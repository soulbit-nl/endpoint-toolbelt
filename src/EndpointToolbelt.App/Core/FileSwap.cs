using System;
using System.IO;
using System.Threading;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// Swaps a fully written temporary file in for its target. Another program can hold the
/// target or the new file for a moment, for example an antivirus scan right after the write;
/// Windows then refuses the swap with a sharing violation. The swap is tried again a few times,
/// at most about <see cref="RetryBudget"/> in all, so such a short hold no longer costs the
/// write. A hold that lasts longer still fails, and the previous file stays as it was.
/// </summary>
public static class FileSwap
{
    /// <summary>The waits before the second and later attempts.</summary>
    internal static readonly TimeSpan[] Waits =
    {
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    };

    /// <summary>The longest the swap waits in all (750 ms).</summary>
    public static readonly TimeSpan RetryBudget = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Moves <paramref name="temp"/> to <paramref name="target"/>, replacing it when it exists.
    /// <paramref name="wait"/> replaces the pause between attempts (tests); null sleeps.
    /// Throws the last error when every attempt failed; any error other than another program's
    /// hold is thrown at once.
    /// </summary>
    public static void Into(string temp, string target, Action<TimeSpan> wait = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target)) { File.Replace(temp, target, null); } else { File.Move(temp, target); }
                return;
            }
            catch (IOException ex) when (IsHeldByAnother(ex) && attempt < Waits.Length)
            {
                (wait ?? Thread.Sleep)(Waits[attempt]);
            }
        }
    }

    /// <summary>
    /// A sharing or lock violation, or ReplaceFile's "could not remove or move" errors, which it
    /// returns when another program has one of the two files open.
    /// </summary>
    internal static bool IsHeldByAnother(IOException ex)
    {
        var hresult = unchecked((uint)ex.HResult);
        if (hresult >> 16 != 0x8007) { return false; } // a Win32 error
        switch (hresult & 0xFFFF)
        {
            case 32:   // ERROR_SHARING_VIOLATION
            case 33:   // ERROR_LOCK_VIOLATION
            case 1175: // ERROR_UNABLE_TO_REMOVE_REPLACED
            case 1176: // ERROR_UNABLE_TO_MOVE_REPLACEMENT
            case 1177: // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2
                return true;
            default:
                return false;
        }
    }
}
