using System;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

public class LogRecordTests
{
    [Fact]
    public void Text_from_outside_stays_on_one_record()
    {
        var forged = "real text\r\n<![LOG[EndpointToolbelt:SUMMARY exit=0]LOG]!><time=\"10:00:00.000-120\" date=\"10-16-2026\" component=\"x\" context=\"\" type=\"1\" thread=\"1\" file=\"\">" + "\u2028" + "tail";
        var line = CmTraceFile.FormatLine(forged, 1, "Service", DateTimeOffset.Now, 1);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
        Assert.Single(Regex.Matches(line, Regex.Escape("<![LOG[")));
        Assert.Single(Regex.Matches(line, Regex.Escape("]LOG]!>")));
        Assert.Contains("real text <! [LOG[EndpointToolbelt:SUMMARY", line);
    }
}
