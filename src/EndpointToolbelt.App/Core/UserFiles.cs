using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>Where the app keeps the user's own files, and where it puts output for the user.</summary>
public static class UserFiles
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>%LOCALAPPDATA%\EndpointToolbelt: settings, history and the device report.</summary>
    public static string DataFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.WorkingFolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string HistoryFile() => Path.Combine(DataFolder(), "history.log");
    public static string ReportFile() => Path.Combine(DataFolder(), "HealthReport.txt");
    public static string SettingsFile() => Path.Combine(DataFolder(), "settings.json");

    /// <summary>A copy of the run log opened last ("Open full log"), under the name earlier versions used.</summary>
    public static string LastRunLogFile() => Path.Combine(DataFolder(), "last-run.log");

    /// <summary>%LOCALAPPDATA%\EndpointToolbelt\remedies.json: the user's remedies and their post-checks.</summary>
    public static string RemediesFile() => Path.Combine(DataFolder(), RemedyStore.FileName);

    /// <summary>%LOCALAPPDATA%\EndpointToolbelt\runs: the run history, one log per run.</summary>
    public static string RunsFolder()
    {
        var folder = Path.Combine(DataFolder(), RunLogStore.FolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The user's Downloads folder through the Known Folder API, so a redirected Downloads is
    /// honoured. Not the desktop: a RemoteApp session has none, and a desktop redirected to
    /// OneDrive would sync a support package to every device the user signs in to.
    /// </summary>
    public static string Downloads()
    {
        if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path))
        {
            return path;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>A file name in <paramref name="folder"/> that does not exist yet: name, then name_1, name_2.</summary>
    public static string FreeName(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);
        for (var n = 1; File.Exists(path) || Directory.Exists(path); n++)
        {
            path = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}", stem, n, extension));
        }
        return path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}

/// <summary>
/// The user's own action history: one line per action, "date (with UTC offset);user;action;result".
/// Per user, so on a shared device nobody reads another user's history.
/// </summary>
public static class History
{
    public static string Line(DateTimeOffset when, string user, string action, string result)
        => string.Join(";", when.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), Clean(user), Clean(action), Clean(result));

    // The history keeps the most recent lines; it also goes into every support package.
    internal const int KeepLines = 1000;
    internal const long TrimAboveBytes = 256 * 1024;

    public static void Add(string action, string result, string path = null)
    {
        try
        {
            var file = path ?? UserFiles.HistoryFile();
            File.AppendAllText(file, Line(DateTimeOffset.Now, Environment.UserName, action, result) + Environment.NewLine, new UTF8Encoding(false));
            if (new FileInfo(file).Length > TrimAboveBytes)
            {
                var keep = File.ReadAllLines(file).Where(l => l.Length > 0).Reverse().Take(KeepLines).Reverse().ToArray();
                File.WriteAllLines(file, keep, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The history is a convenience; an action never fails because of it.
        }
    }

    /// <summary>
    /// The line of one run, written again when the run has a new result (a safe-mode start and
    /// its answer): the line of the same action whose result carries "|&lt;run ID&gt;|" is replaced in
    /// place, with its own time; without one the line is added. One run stays one line.
    /// </summary>
    public static void Put(string action, string result, string runId, string path = null)
    {
        if (string.IsNullOrEmpty(runId))
        {
            Add(action, result, path);
            return;
        }
        try
        {
            var file = path ?? UserFiles.HistoryFile();
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
            var marker = "|" + runId + "|";
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var parts = lines[i].Split(';');
                if (parts.Length == 4 && parts[2] == Clean(action) && parts[3].Contains(marker))
                {
                    lines[i] = string.Join(";", parts[0], parts[1], parts[2], Clean(result));
                    File.WriteAllLines(file, lines, new UTF8Encoding(false));
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The history is a convenience; an action never fails because of it.
            return;
        }
        Add(action, result, path);
    }

    /// <summary>The last <paramref name="count"/> lines; empty when there is no history, null when it cannot be read.</summary>
    public static IReadOnlyList<string> Tail(int count, string path = null)
    {
        try
        {
            var file = path ?? UserFiles.HistoryFile();
            if (!File.Exists(file)) { return Array.Empty<string>(); }
            return File.ReadAllLines(file).Where(l => l.Length > 0).Reverse().Take(count).Reverse().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Clean(string text) => (text ?? "").Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
}
