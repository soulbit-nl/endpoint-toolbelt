using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Platform;

/// <summary>
/// One CMTrace-format log file (the format the IME and ConfigMgr tools read), shared by the
/// service and the engine. Rolls over to ".lo_" at 4 MB. Every write opens the file with the
/// no-follow checks, so a planted link or hard link is never written through, and the file gets
/// a protected ACL (SYSTEM, Administrators and the account this process runs as).
/// </summary>
public sealed class CmTraceFile
{
    public const long MaxBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    // Control characters, NEL, and the Unicode line (Zl) and paragraph (Zp) separators.
    private static readonly Regex ControlCharacters =
        new(@"[\x00-\x1F\x7F\x85\p{Zl}\p{Zp}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private readonly long _maxBytes;
    private readonly object _lock = new();
    // The file's ACL is checked once per file this instance writes (and again after a rollover).
    private bool _protected;

    public CmTraceFile(string path, long maxBytes = MaxBytes)
    {
        FilePath = path;
        _maxBytes = maxBytes;
    }

    public string FilePath { get; }

    /// <summary>
    /// A message as one CMTrace record can carry it. Line breaks and other control characters
    /// (also Unicode line and paragraph separators) become spaces, and the record markers are
    /// defused, so text from outside can never end the record early and add a forged line.
    /// </summary>
    public static string CleanText(string message) =>
        string.IsNullOrEmpty(message) ? "" : ControlCharacters.Replace(message, " ").Replace("]LOG]!>", "] LOG]!>").Replace("<![LOG[", "<! [LOG[");

    /// <summary>One record. CMTrace convention: UTC = local + bias, so UTC+2 is written as -120.</summary>
    public static string FormatLine(string message, int type, string component, DateTimeOffset now, int threadId)
    {
        var bias = -(int)now.Offset.TotalMinutes;
        var time = now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + bias.ToString("+0;-0", CultureInfo.InvariantCulture);
        var date = now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        return "<![LOG[" + CleanText(message) + "]LOG]!><time=\"" + time + "\" date=\"" + date + "\" component=\"" + component +
            "\" context=\"\" type=\"" + type.ToString(CultureInfo.InvariantCulture) + "\" thread=\"" + threadId.ToString(CultureInfo.InvariantCulture) + "\" file=\"\">";
    }

    /// <summary>
    /// Appends one record (type 1 information, 2 warning, 3 error). Throws when the file cannot
    /// be written; protecting its ACL is best effort and never throws.
    /// </summary>
    public void Append(string message, int type, string component, DateTimeOffset now, int threadId)
    {
        var line = FormatLine(message, type, component, now, threadId) + "\r\n";
        var folderPath = Path.GetDirectoryName(FilePath);
        var name = Path.GetFileName(FilePath);
        var oldName = Path.ChangeExtension(name, ".lo_");
        lock (_lock)
        {
            // The folder is pinned by handle (a real directory at its expected path) and the
            // log file is opened, rolled over and protected relative to that handle, so nothing
            // between the folder and the file can be swapped for a link.
            using var folder = NativeFile.OpenFolderNoFollow(folderPath) ?? throw new DirectoryNotFoundException("The log folder does not exist: " + folderPath);
            FileStream stream = null;
            try
            {
                RollOverIfNeeded(folder, name, oldName);
                stream = NativeFile.OpenAppendRelative(folder, name);
                var bom = stream.Length == 0 ? Utf8Bom.GetPreamble() : Array.Empty<byte>();
                var body = Utf8Bom.GetBytes(line);
                stream.Seek(0, SeekOrigin.End);
                if (bom.Length > 0) { stream.Write(bom, 0, bom.Length); }
                stream.Write(body, 0, body.Length);
            }
            finally
            {
                if (!_protected && stream != null)
                {
                    // The log names callers and what they ran; the IME Logs folder is readable by
                    // every interactive user, so the file itself is SYSTEM and Administrators only.
                    try
                    {
                        _protected = FileSystemSafety.ProtectLogFile(stream);
                        // A rolled-over file from before this version was written readable too.
                        var old = NativeFile.OpenFileRelative(folder, oldName, NativeFile.AccessReadControlWriteDac);
                        if (old != null)
                        {
                            using var oldStream = new FileStream(old, FileAccess.Read);
                            FileSystemSafety.ProtectLogFile(oldStream);
                        }
                    }
                    catch (Exception) { }
                }
                stream?.Dispose();
            }
        }
    }

    private void RollOverIfNeeded(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string name, string oldName)
    {
        // Null: the file does not exist or is a link (never rolled over or deleted through).
        var length = NativeFile.GetFileLengthRelative(folder, name);
        if (length == null || length.Value < _maxBytes) { return; }
        NativeFile.DeleteFileRelative(folder, oldName);
        NativeFile.RenameFileRelative(folder, name, oldName);
        // The next write creates a new file: protect that one too.
        _protected = false;
    }
}
