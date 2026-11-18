using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EndpointToolbelt.Engine;

/// <summary>
/// The JSON of the stdout records, written by hand so every record has the same shape:
/// fields in order, plain ASCII (everything outside printable ASCII
/// as \uXXXX), and no line break inside a record.
/// </summary>
public static class RecordJson
{
    /// <summary>Longest text field; longer text is cut, which keeps a record well below the service's 64 KB line limit.</summary>
    public const int MaxTextLength = 8000;

    public static string Quote(string text)
    {
        if (text == null) { return "null"; }
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            int code = ch;
            if (ch == '"') { sb.Append("\\\""); }
            else if (ch == '\\') { sb.Append("\\\\"); }
            else if (code == 10) { sb.Append("\\n"); }
            else if (code == 13) { sb.Append("\\r"); }
            else if (code == 9) { sb.Append("\\t"); }
            // Plain ASCII whatever encoding the console of the engine process ends up with.
            else if (code < 32 || code >= 0x7F) { sb.Append("\\u").Append(code.ToString("x4", CultureInfo.InvariantCulture)); }
            else { sb.Append(ch); }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Value(object value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int or long or double => string.Format(CultureInfo.InvariantCulture, "{0}", value),
        _ => Quote(System.Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>One record: {"type":..., "run":..., "ts":..., fields...}.</summary>
    public static string Record(string type, string runId, string timestamp, IEnumerable<KeyValuePair<string, object>> fields)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":").Append(Quote(type));
        sb.Append(",\"run\":").Append(Quote(runId));
        sb.Append(",\"ts\":").Append(Quote(timestamp));
        if (fields != null)
        {
            foreach (var pair in fields)
            {
                var value = pair.Value;
                if (value is string s && s.Length > MaxTextLength) { value = s.Substring(0, MaxTextLength) + " [cut]"; }
                sb.Append(',').Append(Quote(pair.Key)).Append(':').Append(Value(value));
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
}
