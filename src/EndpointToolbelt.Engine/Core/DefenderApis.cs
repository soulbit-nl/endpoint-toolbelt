using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Xml;

namespace EndpointToolbelt.Engine;

/// <summary>One event of the Microsoft Defender operational log: its ID, its time and its named data values.</summary>
public sealed class DefenderEvent
{
    public int Id { get; set; }
    public DateTime TimeCreatedUtc { get; set; }

    /// <summary>The event's data values by their names ("ID", "User", "Path", "Process Name", ...), names compared without case.</summary>
    public IDictionary<string, string> Data { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The Microsoft Defender operational log, and the account lookup its events need.</summary>
public interface IDefenderEventSource
{
    /// <summary>
    /// Events with these IDs since a moment, newest first (at most <paramref name="max"/>); throws an
    /// <see cref="InvalidOperationException"/> or an <see cref="UnauthorizedAccessException"/> when the log cannot be read.
    /// </summary>
    IReadOnlyList<DefenderEvent> Read(IReadOnlyList<int> ids, DateTime sinceUtc, int max);

    /// <summary>When the oldest event still in the log was written, or null when the log is empty or cannot be read.</summary>
    DateTime? OldestUtc();

    /// <summary>The SID of an account name as an event writes it ("DOMAIN\user"), or null when Windows cannot resolve it.</summary>
    string SidOf(string account);

    /// <summary>The account name ("DOMAIN\user") of a SID, or null when Windows cannot resolve it.</summary>
    string NameOf(string sid);
}

public sealed class WindowsDefenderEventSource : IDefenderEventSource
{
    public const string LogName = "Microsoft-Windows-Windows Defender/Operational";

    /// <summary>
    /// The only data values that are read from an event: what the check shows or decides on. Any
    /// other value (the command lines of 1121 and 1122, the update source of 2001 among them) is
    /// never read into memory, and 5007 is never read at all. "Changed Type" and "Value" are read
    /// for tamper protection's 5013, whose value is matched against a fixed list of settings and
    /// never shown; "Feature Name" and "Error Code" for a failed feature (3002) and a failed update
    /// (2001, 2003, 2004), shown only in a fixed form.
    /// </summary>
    public static IReadOnlyCollection<string> ValuesRead => ValueNames;

    private static readonly HashSet<string> ValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID", "User", "Detection User", "Path", "Process Name", "Threat Name", "Severity Name", "Detection ID", "Action Name", "Destination",
        "Changed Type", "Value", "Feature Name", "Error Code",
    };

    /// <summary>A data value longer than this is cut: no field the check uses is longer.</summary>
    internal const int MaxValueLength = 2048;

    public IReadOnlyList<DefenderEvent> Read(IReadOnlyList<int> ids, DateTime sinceUtc, int max)
    {
        var idFilter = string.Join(" or ", System.Linq.Enumerable.Select(ids, i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        var xpath = string.Format(CultureInfo.InvariantCulture, "*[System[({0}) and TimeCreated[@SystemTime>='{1}']]]",
            idFilter, sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        var query = new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true };
        var list = new List<DefenderEvent>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record != null && list.Count < max; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated == null) { continue; }
                    list.Add(new DefenderEvent { Id = record.Id, TimeCreatedUtc = record.TimeCreated.Value.ToUniversalTime(), Data = DataOf(record.ToXml()) });
                }
            }
        }
        catch (EventLogException ex)
        {
            // A missing or disabled log: the caller needs no event log type to say so.
            throw new InvalidOperationException("The Microsoft Defender operational log could not be read (" + ex.GetType().Name + ").", ex);
        }
        return list;
    }

    public DateTime? OldestUtc()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName));
            using var record = reader.ReadEvent();
            return record?.TimeCreated?.ToUniversalTime();
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string SidOf(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) { return null; }
        try
        {
            return ((SecurityIdentifier)new NTAccount(account.Trim()).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string NameOf(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) { return null; }
        try
        {
            return ((NTAccount)new SecurityIdentifier(sid.Trim()).Translate(typeof(NTAccount))).Value;
        }
        catch (SystemException)
        {
            // Not mapped, a malformed SID, or the lookup failed: the caller is then matched by SID only.
            return null;
        }
    }

    /// <summary>
    /// The named data values of an event's XML (<c>&lt;Data Name="..."&gt;text&lt;/Data&gt;</c>), only
    /// those of <see cref="ValuesRead"/>: every other value is skipped unread, so a command line never
    /// reaches the check. The XML comes from Windows; it is still read without DTDs and without a
    /// resolver, and an event whose XML cannot be read has no values.
    /// </summary>
    internal static IDictionary<string, string> DataOf(string xml)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(xml)) { return data; }
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Data") { continue; }
                var name = reader.GetAttribute("Name");
                if (string.IsNullOrEmpty(name) || reader.IsEmptyElement || !ValueNames.Contains(name)) { continue; }
                var value = reader.ReadString();
                if (value.Length > MaxValueLength) { value = value.Substring(0, MaxValueLength); }
                if (!data.ContainsKey(name)) { data[name] = value; }
            }
        }
        catch (XmlException)
        {
            // What was read before the fault stays.
        }
        return data;
    }
}
