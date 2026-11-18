using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.WindowsUpdate;

/// <summary>
/// One Windows Update API operation in its own process: <c>EndpointToolbelt.Engine.exe --wu-worker
/// Search|Install|Busy [UpdateId]</c>. Started only by the engine, as SYSTEM: the
/// Windows Update API has no time
/// limit of its own, and a call that hangs can only be ended by stopping the process that made it.
/// Writes exactly one JSON line to standard output and exits 0; a failed operation is reported in
/// that line, not through the exit code. The calls run late bound through IDispatch on an STA
/// thread, as Windows PowerShell made them.
///
/// Search  : what this device is offered (quality and security updates to install, other offered
///           updates and hidden updates by title).
/// Install : downloads and installs one update, identified by its update ID.
/// Busy    : whether the update client is installing right now (UpdateInstaller.IsBusy), asked
///           before the engine stops the update services. Changes nothing.
/// </summary>
public static class WuWorker
{
    public const string Switch = "--wu-worker";

    private static readonly Regex UpdateIdPattern = new("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Classifications this repair installs, by ID so the check works in every display language:
    // the monthly security and quality updates. Never upgrades (feature updates), drivers,
    // definition updates or optional previews (BrowseOnly=0 already leaves the previews out).
    private static readonly string[] InstallClassifications =
    {
        "0fa1201d-4330-4fa8-8ae9-b877473b6441", // Security Updates
        "e6cf1350-c01b-414d-a61f-263d14d133b4", // Critical Updates
        "28bc880e-0592-4cbf-8f95-c79b17911d5f", // Update Rollups
        "cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83", // Updates
    };

    private static readonly string[] Operations = { "Search", "Install", "Busy" };

    /// <summary>
    /// Runs the worker for its arguments (after the switch). An operation that is not one of the
    /// three, or an update ID that is not a GUID, is refused before anything runs, without a
    /// result line (exit 1), as the PowerShell worker's parameter validation did.
    /// </summary>
    public static int Run(IReadOnlyList<string> args, TextWriter output)
    {
        if (args.Count < 1 || args.Count > 2 || !Operations.Contains(args[0], StringComparer.OrdinalIgnoreCase)) { return 1; }
        var operation = Operations.First(o => string.Equals(o, args[0], StringComparison.OrdinalIgnoreCase));
        var updateId = args.Count == 2 ? args[1] : "";
        if (updateId.Length > 0 && !UpdateIdPattern.IsMatch(updateId)) { return 1; }
        string line;
        try
        {
            line = StaThread.Run(() => operation switch
            {
                "Search" => Search(),
                "Busy" => BusyCheck(),
                _ => updateId.Length == 0 ? Failure("Install needs -UpdateId.") : Install(updateId),
            });
        }
        catch (Exception ex)
        {
            line = Failure(ex.Message);
        }
        output.WriteLine(line);
        return 0;
    }

    private static string Failure(string error) => Json(Field("ok", false), Field("errorCode", ""), Field("error", error));

    /// <summary>A 32-bit result as 0x........ (negative HRESULTs included).</summary>
    internal static string HexCode(long value) => "0x" + (value & 0xFFFFFFFFL).ToString("x8", CultureInfo.InvariantCulture);

    private static object NewSession()
    {
        var session = LateBound.Create("Microsoft.Update.Session");
        LateBound.Set(session, "ClientApplicationID", ProductInfo.DisplayName);
        return session;
    }

    /// <summary>
    /// The code that explains one update's download or install: the update's own when it has one,
    /// otherwise the operation's. Either can be 0 while the other is not.
    /// </summary>
    private static string OperationCode(object operation)
    {
        long updateCode;
        long operationCode;
        try { updateCode = Convert.ToInt64(LateBound.Get(LateBound.Call(operation, "GetUpdateResult", 0), "HResult"), CultureInfo.InvariantCulture); }
        catch (Exception) { updateCode = 0; }
        try { operationCode = Convert.ToInt64(LateBound.Get(operation, "HResult"), CultureInfo.InvariantCulture); }
        catch (Exception) { operationCode = 0; }
        return HexCode(updateCode != 0 ? updateCode : operationCode);
    }

    private static string Search()
    {
        object searcher;
        object search;
        try
        {
            searcher = LateBound.Call(NewSession(), "CreateUpdateSearcher");
            search = LateBound.Call(searcher, "Search", "IsInstalled=0 and IsHidden=0 and BrowseOnly=0 and Type='Software'");
        }
        catch (Exception ex)
        {
            return Json(Field("ok", false), Field("errorCode", HexCode(LateBound.HResult(ex))), Field("error", Innermost(ex)),
                Field("install", Array.Empty<string>()), Field("other", Array.Empty<string>()), Field("hidden", Array.Empty<string>()));
        }
        // 2 = succeeded, 3 = succeeded with errors: both return a usable list.
        var resultCode = LateBound.ToInt(LateBound.Get(search, "ResultCode"));
        if (resultCode != 2 && resultCode != 3)
        {
            return Json(Field("ok", false), Field("errorCode", "result " + resultCode.ToString(CultureInfo.InvariantCulture)), Field("error", "the search did not complete"),
                Field("install", Array.Empty<string>()), Field("other", Array.Empty<string>()), Field("hidden", Array.Empty<string>()));
        }
        var install = new List<string>();
        var other = new List<string>();
        foreach (var update in LateBound.Items(LateBound.Get(search, "Updates")))
        {
            var installable = LateBound.Items(LateBound.Get(update, "Categories"))
                .Any(c => InstallClassifications.Contains(LateBound.ToText(LateBound.Get(c, "CategoryID")).ToLowerInvariant()));
            var title = LateBound.ToText(LateBound.Get(update, "Title"));
            if (installable)
            {
                install.Add("{" + Pair("id", LateBound.ToText(LateBound.Get(LateBound.Get(update, "Identity"), "UpdateID"))) + "," + Pair("title", title) + "}");
            }
            else
            {
                other.Add(title);
            }
        }

        // Hidden updates, for the log: a hidden cumulative update looks exactly like one the ring
        // has not released. Read from the local cache the search above has just refreshed; a
        // failure here changes nothing about the result.
        var hidden = new List<string>();
        try
        {
            LateBound.Set(searcher, "Online", false);
            var found = LateBound.Call(searcher, "Search", "IsInstalled=0 and IsHidden=1 and Type='Software'");
            hidden.AddRange(LateBound.Items(LateBound.Get(found, "Updates")).Select(u => LateBound.ToText(LateBound.Get(u, "Title"))));
        }
        catch (Exception)
        {
            hidden.Clear();
        }
        return "{" + Pair("ok", true) + "," + Pair("errorCode", "") + "," + Pair("error", "") +
            ",\"install\":[" + string.Join(",", install) + "]" +
            ",\"other\":[" + string.Join(",", other.Select(RecordJson.Quote)) + "]" +
            ",\"hidden\":[" + string.Join(",", hidden.Select(RecordJson.Quote)) + "]}";
    }

    private static string Install(string updateId)
    {
        var reply = new InstallReply();
        try
        {
            var session = NewSession();
            var searcher = LateBound.Call(session, "CreateUpdateSearcher");
            // Only while it is still offered: installed or hidden since the engine's search means
            // there is nothing to do for it.
            var criteria = "UpdateID='" + updateId + "' and IsInstalled=0 and IsHidden=0";
            // The engine's search has just refreshed the local cache; ask it first.
            object found = null;
            try
            {
                LateBound.Set(searcher, "Online", false);
                found = LateBound.Call(searcher, "Search", criteria);
            }
            catch (Exception)
            {
                found = null;
            }
            if (found == null || LateBound.ToInt(LateBound.Get(LateBound.Get(found, "Updates"), "Count")) == 0)
            {
                LateBound.Set(searcher, "Online", true);
                found = LateBound.Call(searcher, "Search", criteria);
            }
            var updates = LateBound.Get(found, "Updates");
            if (LateBound.ToInt(LateBound.Get(updates, "Count")) == 0)
            {
                reply.NotOffered = true;
                reply.Error = "the update is no longer offered";
                return reply.ToJson();
            }
            var update = LateBound.Get(updates, "Item", 0);
            reply.Title = LateBound.ToText(LateBound.Get(update, "Title"));

            var installer = LateBound.Call(session, "CreateUpdateInstaller");
            bool busy;
            try { busy = Convert.ToBoolean(LateBound.Get(installer, "IsBusy"), CultureInfo.InvariantCulture); }
            catch (Exception) { busy = false; }
            if (busy)
            {
                reply.Busy = true;
                reply.Error = "another installation is running";
                return reply.ToJson();
            }
            bool restartFirst;
            try { restartFirst = Convert.ToBoolean(LateBound.Get(installer, "RebootRequiredBeforeInstallation"), CultureInfo.InvariantCulture); }
            catch (Exception) { restartFirst = false; }
            if (restartFirst)
            {
                reply.RestartFirst = true;
                return reply.ToJson();
            }

            // An update whose licence was not accepted fails to install without saying why.
            if (!Convert.ToBoolean(LateBound.Get(update, "EulaAccepted"), CultureInfo.InvariantCulture)) { LateBound.Call(update, "AcceptEula"); }
            var single = LateBound.Create("Microsoft.Update.UpdateColl");
            LateBound.Call(single, "Add", update);

            var downloader = LateBound.Call(session, "CreateUpdateDownloader");
            LateBound.Set(downloader, "Updates", single);
            var download = LateBound.Call(downloader, "Download");
            reply.DownloadResult = LateBound.ToInt(LateBound.Get(download, "ResultCode"));
            reply.DownloadCode = OperationCode(download);
            if (reply.DownloadResult != 2 && reply.DownloadResult != 3) { return reply.ToJson(); }

            LateBound.Set(installer, "Updates", single);
            var installation = LateBound.Call(installer, "Install");
            reply.InstallResult = LateBound.ToInt(LateBound.Get(installation, "ResultCode"));
            reply.InstallCode = OperationCode(installation);
            bool reboot;
            try { reboot = Convert.ToBoolean(LateBound.Get(LateBound.Call(installation, "GetUpdateResult", 0), "RebootRequired"), CultureInfo.InvariantCulture); }
            catch (Exception) { reboot = false; }
            reply.RebootRequired = reboot || Convert.ToBoolean(LateBound.Get(installation, "RebootRequired"), CultureInfo.InvariantCulture);
            reply.Ok = true;
        }
        catch (Exception ex)
        {
            reply.ErrorCode = HexCode(LateBound.HResult(ex));
            reply.Error = Innermost(ex);
        }
        return reply.ToJson();
    }

    /// <summary>Read-only: creating an installer object starts nothing.</summary>
    private static string BusyCheck()
    {
        try
        {
            var installer = LateBound.Call(NewSession(), "CreateUpdateInstaller");
            var busy = Convert.ToBoolean(LateBound.Get(installer, "IsBusy"), CultureInfo.InvariantCulture);
            return Json(Field("ok", true), Field("errorCode", ""), Field("error", ""), Field("busy", busy));
        }
        catch (Exception ex)
        {
            return Json(Field("ok", false), Field("errorCode", HexCode(LateBound.HResult(ex))), Field("error", Innermost(ex)), Field("busy", false));
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException != null) { ex = ex.InnerException; }
        return ex.Message;
    }

    private sealed class InstallReply
    {
        public bool Ok;
        public string ErrorCode = "";
        public string Error = "";
        public string Title = "";
        public bool NotOffered;
        public bool Busy;
        public bool RestartFirst;
        public int DownloadResult = -1;
        public string DownloadCode = "";
        public int InstallResult = -1;
        public string InstallCode = "";
        public bool RebootRequired;

        public string ToJson() => Json(Field("ok", Ok), Field("errorCode", ErrorCode), Field("error", Error), Field("title", Title),
            Field("notOffered", NotOffered), Field("busy", Busy), Field("restartFirst", RestartFirst),
            Field("downloadResult", DownloadResult), Field("downloadCode", DownloadCode), Field("installResult", InstallResult),
            Field("installCode", InstallCode), Field("rebootRequired", RebootRequired));
    }

    private static KeyValuePair<string, object> Field(string name, object value) => new(name, value);

    private static string Pair(string name, object value) => RecordJson.Quote(name) + ":" + RecordJson.Value(value);

    private static string Json(params KeyValuePair<string, object>[] fields)
    {
        var sb = new StringBuilder("{");
        foreach (var field in fields)
        {
            if (sb.Length > 1) { sb.Append(','); }
            sb.Append(RecordJson.Quote(field.Key)).Append(':');
            sb.Append(field.Value is string[] list ? "[" + string.Join(",", list.Select(RecordJson.Quote)) + "]" : RecordJson.Value(field.Value));
        }
        return sb.Append('}').ToString();
    }
}
