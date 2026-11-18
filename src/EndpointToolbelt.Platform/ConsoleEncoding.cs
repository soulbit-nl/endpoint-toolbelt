using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EndpointToolbelt.Platform;

/// <summary>
/// The encoding a console tool (dsregcmd and the like) writes in when its output is redirected:
/// the system's OEM code page, the code page of the console Windows gives the tool. It is never
/// derived from a culture: in the service's SYSTEM context a culture's OEMCodePage can be a
/// placeholder such as 1 (CP_OEMCP), which Encoding.GetEncoding refuses. Never throws: a code page
/// that is not usable falls back to 437 (the OEM code page of the US English system locale), then
/// to UTF-8.
/// </summary>
public static class ConsoleEncoding
{
    /// <summary>The code page used when the system's OEM code page is not usable.</summary>
    public const int FallbackCodePage = 437;

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>The system's OEM code page, or 0 when it cannot be read.</summary>
    public static int SystemOemCodePage()
    {
        try
        {
            return unchecked((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>The encoding of the system's OEM code page, or a fallback; never null.</summary>
    public static Encoding Oem() => FromCodePage(SystemOemCodePage());

    /// <summary>The encoding of <paramref name="codePage"/> when it is a real code page; otherwise 437, then UTF-8. Never null, never throws.</summary>
    public static Encoding FromCodePage(int codePage) => Resolve(codePage, FallbackCodePage);

    /// <summary>The encoding of <paramref name="codePage"/>, else of <paramref name="fallback"/>, else UTF-8.</summary>
    internal static Encoding Resolve(int codePage, int fallback) => TryGet(codePage) ?? TryGet(fallback) ?? new UTF8Encoding(false);

    /// <summary>The encoding of a real code page; null for a placeholder or a code page this system does not support.</summary>
    internal static Encoding TryGet(int codePage)
    {
        // 0 CP_ACP, 1 CP_OEMCP, 2 CP_MACCP, 3 CP_THREAD_ACP and 42 CP_SYMBOL stand for "the code
        // page of ..." or for no text encoding at all: not a code page to decode output with.
        if (codePage <= 3 || codePage == 42 || codePage > 65535) { return null; }
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
