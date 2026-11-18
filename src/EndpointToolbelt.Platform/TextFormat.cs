using System.Globalization;

namespace EndpointToolbelt.Platform;

/// <summary>Numbers in text for the user or the log, the same on every system locale (SYSTEM's locale is not the user's).</summary>
public static class TextFormat
{
    public static string Number(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A count with the right noun: "1 profile", "3 profiles".</summary>
    public static string Count(long count, string singular, string plural = null)
    {
        if (string.IsNullOrEmpty(plural)) { plural = singular + "s"; }
        return count == 1 ? "1 " + singular : count.ToString(CultureInfo.InvariantCulture) + " " + plural;
    }

    /// <summary>
    /// A tool's exit code in the form its documentation uses: decimal for 0 to 99999 ("3010",
    /// "87"), otherwise 0x plus eight upper-case hex digits, a negative code as its unsigned
    /// 32-bit value ("0x800F081F").
    /// </summary>
    public static string ExitCode(long code)
    {
        if (code >= 0 && code <= 99999) { return code.ToString(CultureInfo.InvariantCulture); }
        return "0x" + (code & 0xFFFFFFFFL).ToString("X8", CultureInfo.InvariantCulture);
    }
}
