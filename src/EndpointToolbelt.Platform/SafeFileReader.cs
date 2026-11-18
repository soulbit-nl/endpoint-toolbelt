using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndpointToolbelt.Platform;

/// <summary>
/// Reading and copying files that SYSTEM did not write, always through
/// <see cref="NativeFile.OpenReadNoFollow"/>: a link, a folder or a file with more than one hard
/// link is refused, and the checks and the read use the same handle.
/// </summary>
public static class SafeFileReader
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The last <paramref name="maxBytes"/> of a text file as a string (UTF-8; a BOM is honoured
    /// at the start of the file only). Null when it cannot be read.
    /// </summary>
    public static string ReadTail(string path, long maxBytes)
    {
        try
        {
            using var stream = NativeFile.OpenReadNoFollow(path);
            var length = stream.Length;
            var take = Math.Min(length, maxBytes);
            if (take < length) { stream.Seek(-take, SeekOrigin.End); }
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = stream.Read(buffer, read, (int)(take - read));
                if (n <= 0) { break; }
                read += n;
            }
            var offset = take == length && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(buffer, offset, read - offset);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines of a text file that contain a text (ordinal, ignoring case), streamed, so a large
    /// log is never held in memory whole. Bounded: only the last <paramref name="maxBytes"/> of
    /// the file are read (the newest lines matter; the first line there is cut and skipped), and
    /// only the newest <paramref name="maxLines"/> matching lines are kept.
    /// </summary>
    public static IReadOnlyList<string> FindLines(string path, string contains, long maxBytes = 32L * 1024 * 1024, int maxLines = 500)
    {
        using var stream = NativeFile.OpenReadNoFollow(path);
        var skipFirst = false;
        if (stream.Length > maxBytes)
        {
            stream.Seek(stream.Length - maxBytes, SeekOrigin.Begin);
            skipFirst = true;
        }
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: !skipFirst);
        var lines = new Queue<string>();
        if (skipFirst) { reader.ReadLine(); }
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
            lines.Enqueue(line);
            if (lines.Count > maxLines) { lines.Dequeue(); }
        }
        return lines.ToArray();
    }

    /// <summary>The lines of a text file (for logs the engine parses).</summary>
    public static IReadOnlyList<string> ReadLines(string path)
    {
        using var stream = NativeFile.OpenReadNoFollow(path);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        string line;
        while ((line = reader.ReadLine()) != null) { lines.Add(line); }
        return lines;
    }

    /// <summary>
    /// Copies one file into a folder of the product's own; the destination must not exist yet.
    /// The copy keeps the original's time, which is what a reader of logs goes by. Returns the
    /// number of bytes copied.
    /// </summary>
    public static long CopyFile(string source, string destination)
    {
        long length;
        using (var input = NativeFile.OpenReadNoFollow(source))
        using (var output = new FileStream(NativeFile.ToExtendedPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            length = output.Length;
        }
        try { File.SetLastWriteTimeUtc(destination, new FileInfo(source).LastWriteTimeUtc); }
        catch (Exception) { }
        return length;
    }
}
