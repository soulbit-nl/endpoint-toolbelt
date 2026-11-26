using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// One record of an engine run, as the service forwards it (a JSON object per line). Only the
/// fields the app uses are declared; anything else in a record is ignored.
/// </summary>
[DataContract]
public sealed class EngineRecord
{
    [DataMember(Name = "type")] public string Type { get; set; }
    [DataMember(Name = "run")] public string Run { get; set; }

    // When the engine wrote the record (UTC, yyyy-MM-ddTHH:mm:ss.fffZ). Read as text, so a
    // record with a missing or unreadable time is still shown, only without its time.
    [DataMember(Name = "ts")] private string TsText { get; set; }

    /// <summary>When the engine wrote the record (UTC); null when the record has no readable time.</summary>
    public DateTime? Utc { get; set; }

    // progress, warning, change: the text; finding: severity, id, title, detail, next, support.
    [DataMember(Name = "severity")] public string Severity { get; set; }
    [DataMember(Name = "text")] public string Text { get; set; }
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "title")] public string Title { get; set; }
    [DataMember(Name = "detail")] public string Detail { get; set; }
    [DataMember(Name = "next")] public string NextStep { get; set; }

    /// <summary>What only IT support can do about a finding; empty when there is nothing.</summary>
    [DataMember(Name = "support")] public string Support { get; set; }

    /// <summary>The app's own warning that it cut the run's records here (not an engine record; never a step's point).</summary>
    public bool IsCut { get; set; }

    // summary
    [DataMember(Name = "exit")] public int? Exit { get; set; }
    [DataMember(Name = "changes")] public int? Changes { get; set; }
    [DataMember(Name = "restart")] public bool? Restart { get; set; }

    // mdm-queued: when Windows retries the sync (UTC)
    [DataMember(Name = "at")] public string At { get; set; }

    // file
    [DataMember(Name = "name")] public string Name { get; set; }

    /// <summary>An <c>app-diagnosis</c> record's app; null for other records or one that could not be read.</summary>
    public AppEvidence App { get; set; }

    /// <summary>An <c>apps-diagnosis</c> record's counts; null for other records.</summary>
    public DiagnosisClose Closing { get; set; }

    /// <summary>An <c>ip-renew</c> record: the result of Get a new IP address; null for other records.</summary>
    public IpRenewRecord Renewal { get; set; }

    /// <summary>A <c>wu-check</c> record: the verdict of Check Windows Update and its facts; null for other records.</summary>
    public WuCheckRecord WuCheck { get; set; }

    /// <summary>A <c>defender-check</c> record: the verdict of Check Defender blocks and its counts; null for other records.</summary>
    public DefenderCheckRecord DefenderCheck { get; set; }

    /// <summary>Parses one record; null when the text is not a record this app can read.</summary>
    public static EngineRecord Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var record = (EngineRecord)new DataContractJsonSerializer(typeof(EngineRecord)).ReadObject(stream);
            if (record == null) { return null; }
            record.Severity = Clean(record.Severity);
            record.Text = Clean(record.Text);
            record.Id = Clean(record.Id);
            record.Title = Clean(record.Title);
            record.Detail = Clean(record.Detail);
            record.NextStep = Clean(record.NextStep);
            record.Name = Clean(record.Name);
            record.Support = Clean(record.Support);
            record.Utc = ParseTime(record.TsText);
            if (record.Type == "app-diagnosis") { record.App = ReadApp(json); }
            else if (record.Type == "apps-diagnosis") { record.Closing = Read<DiagnosisClose>(json); }
            else if (record.Type == "ip-renew") { record.Renewal = IpRenewRecord.Clean(Read<IpRenewRecord>(json)); }
            else if (record.Type == "wu-check") { record.WuCheck = WuCheckRecord.Clean(Read<WuCheckRecord>(json)); }
            else if (record.Type == "defender-check") { record.DefenderCheck = DefenderCheckRecord.Clean(Read<DefenderCheckRecord>(json)); }
            return record;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidCastException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The same JSON read as another contract; null when it does not fit.</summary>
    private static T Read<T>(string json) where T : class
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
        }
        catch (Exception ex) when (ex is SerializationException or InvalidCastException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>An app of the diagnosis, its device-sourced texts cleaned like every other record text.</summary>
    private static AppEvidence ReadApp(string json)
    {
        var app = Read<AppEvidence>(json);
        if (app == null || string.IsNullOrWhiteSpace(app.AppId)) { return null; }
        app.DiagnosisId = Clean(app.DiagnosisId) ?? "";
        app.AppId = Clean(app.AppId) ?? "";
        app.Name = Clean(app.Name) ?? "";
        app.Scope = Clean(app.Scope) ?? "";
        app.Intent = Clean(app.Intent) ?? "";
        app.Verdict = Clean(app.Verdict) ?? "";
        app.State = Clean(app.State) ?? "";
        app.Code = Clean(app.Code) ?? "";
        app.Installed = Clean(app.Installed) ?? "";
        app.ObservedText = Clean(app.ObservedText) ?? "";
        app.Phase = Clean(app.Phase) ?? "";
        app.Attempt = Clean(app.Attempt) ?? "";
        app.AttemptText = Clean(app.AttemptText) ?? "";
        app.NextRetryText = Clean(app.NextRetryText) ?? "";
        // The kind is one of three tokens; anything else (also no kind, from an older service) is a Win32 app.
        app.Kind = (Clean(app.Kind) ?? "") is "msi" or "msix" or "store" ? app.Kind : "";
        app.CodeMeaning = Clean(app.CodeMeaning) ?? "";
        app.CodeStep = Clean(app.CodeStep) ?? "";
        app.CodeSupport = Clean(app.CodeSupport) ?? "";
        app.CodeNext = (Clean(app.CodeNext) ?? "") is "connectivity" or "retry" or "it" or "restart" or "wait" or "none" ? app.CodeNext : "";
        app.Version = Clean(app.Version) ?? "";
        // A host name only: a URL, a path or anything else is dropped.
        var host = Clean(app.DownloadHost) ?? "";
        app.DownloadHost = host.Length <= 253 && host.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-') ? host : "";
        return app;
    }

    private static readonly string[] TimeFormats = { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ" };

    /// <summary>
    /// A record's time as UTC, in exactly the engine's format (with or without milliseconds);
    /// null when it is missing or anything else, so a stray value never becomes a wrong time.
    /// </summary>
    internal static DateTime? ParseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        return DateTime.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;
    }

    /// <summary>
    /// Record text partly comes from the device (app names, event text). Control and formatting
    /// characters (such as bidirectional overrides) are removed, so text cannot pose as something
    /// else on screen; line breaks and tabs stay.
    /// </summary>
    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) { return text; }
        StringBuilder builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var drop = c != '\n' && c != '\r' && c != '\t' &&
                (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
            if (drop && builder == null) { builder = new StringBuilder(text, 0, i, text.Length); }
            if (!drop) { builder?.Append(c); }
        }
        return builder?.ToString() ?? text;
    }
}

/// <summary>The closing record of an app diagnosis (<c>apps-diagnosis</c>).</summary>
[DataContract]
public sealed class DiagnosisClose
{
    [DataMember(Name = "diagnosis")] public string DiagnosisId { get; set; }
    [DataMember(Name = "present")] public bool Present { get; set; }
    [DataMember(Name = "records")] public int Records { get; set; }
    [DataMember(Name = "listed")] public int Listed { get; set; }
    [DataMember(Name = "scopes")] public int Scopes { get; set; }
    [DataMember(Name = "otherscopes")] public int OtherScopes { get; set; }
    [DataMember(Name = "userscope")] public bool UserScope { get; set; }
    [DataMember(Name = "logevidence")] public bool LogEvidence { get; set; }
}

/// <summary>Severity of a finding, in the order the result panel lists them.</summary>
public enum FindingSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2,
    Ok = 3,
}

public static class FindingSeverityText
{
    public static FindingSeverity Parse(string text) => (text ?? "").ToUpperInvariant() switch
    {
        "ERROR" => FindingSeverity.Error,
        "WARNING" => FindingSeverity.Warning,
        "OK" => FindingSeverity.Ok,
        _ => FindingSeverity.Info,
    };

    /// <summary>Sort order of the result panel: problems first; a severity this app does not know goes last.</summary>
    public static int Rank(string text) => (text ?? "").ToUpperInvariant() switch
    {
        "ERROR" => 0,
        "WARNING" => 1,
        "INFO" => 2,
        "OK" => 3,
        _ => 4,
    };

    /// <summary>The severity in words, so the meaning never depends on colour alone.</summary>
    public static string Word(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => "PROBLEM",
        FindingSeverity.Warning => Words.WarningBadge,
        FindingSeverity.Ok => "OK",
        _ => "INFO",
    };
}
