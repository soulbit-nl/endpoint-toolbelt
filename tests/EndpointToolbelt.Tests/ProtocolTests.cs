using System;
using System.IO;
using System.Runtime.Serialization;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Engine;
using Xunit;

namespace EndpointToolbelt.Tests;

public class MessageFramingTests
{
    [Fact]
    public void Round_trips_a_request()
    {
        var ms = new MemoryStream();
        MessageFraming.Write(ms, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true, ConfirmedOtherSessions = 2 });
        ms.Position = 0;
        var r = MessageFraming.Read<Request>(ms, ProtocolInfo.MaxRequestBytes);
        Assert.Equal(ProtocolInfo.Version, r.ProtocolVersion);
        Assert.Equal(RequestType.RunAction, r.Type);
        Assert.Equal(ToolbeltAction.FullReset, r.Action);
        Assert.True(r.Confirmed);
        Assert.Equal(2, r.ConfirmedOtherSessions);
    }

    [Fact]
    public void Clean_end_of_stream_returns_null()
    {
        Assert.Null(MessageFraming.Read<Request>(new MemoryStream(), 100));
    }

    [Fact]
    public void Rejects_oversized_and_empty_frames_before_reading_the_body()
    {
        var big = new MemoryStream(BitConverter.GetBytes(ProtocolInfo.MaxRequestBytes + 1));
        Assert.Throws<InvalidDataException>(() => MessageFraming.Read<Request>(big, ProtocolInfo.MaxRequestBytes));
        var zero = new MemoryStream(BitConverter.GetBytes(0));
        Assert.Throws<InvalidDataException>(() => MessageFraming.Read<Request>(zero, ProtocolInfo.MaxRequestBytes));
    }

    [Fact]
    public void Truncated_frame_throws()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(50), 0, 4);
        ms.Write(new byte[10], 0, 10);
        ms.Position = 0;
        Assert.Throws<EndOfStreamException>(() => MessageFraming.Read<Request>(ms, 100));
    }

    [Fact]
    public void Invalid_json_throws()
    {
        var ms = new MemoryStream();
        var body = new byte[] { 0x7B, 0xFF, 0xFE, 0x7D };
        ms.Write(BitConverter.GetBytes(body.Length), 0, 4);
        ms.Write(body, 0, body.Length);
        ms.Position = 0;
        Assert.ThrowsAny<Exception>(() => MessageFraming.Read<Request>(ms, 100));
    }

    [Fact]
    public void Engine_record_header_ignores_other_fields()
    {
        var header = MessageFraming.Deserialize<EngineRecordHeader>(System.Text.Encoding.UTF8.GetBytes(
            "{\"type\":\"file\",\"name\":\"EndpointToolbelt_2026-10-15_1200.zip\",\"extra\":[1,2,3]}"));
        Assert.Equal("file", header.Type);
        Assert.Equal("EndpointToolbelt_2026-10-15_1200.zip", header.Name);
    }
}

public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\Program Files\SoulBit\Endpoint Toolbelt\", @"""C:\Program Files\SoulBit\Endpoint Toolbelt\\""")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("", "\"\"")]
    public void Quotes_like_CommandLineToArgvW(string input, string expected)
    {
        Assert.Equal(expected, NativeProcess.QuoteArgument(input));
    }

    [Fact]
    public void Engine_launch_uses_the_full_path_fixed_arguments_and_a_clean_environment()
    {
        var info = EngineLaunch.Create(@"C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.Engine.exe", ToolbeltAction.RepairWU, @"C:\work\request.json", @"C:\work\temp", TimeSpan.FromMinutes(1));
        Assert.Equal(@"C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.Engine.exe", info.FileName);
        Assert.Equal(new[] { "-Action", "RepairWU", "-RequestFile", @"C:\work\request.json" }, info.Arguments);
        Assert.Equal(@"C:\Program Files\SoulBit\Endpoint Toolbelt", info.WorkingDirectory);
        Assert.False(info.Environment.ContainsKey("PSModulePath"));
        Assert.DoesNotContain("WindowsPowerShell", info.Environment["PATH"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\work\temp", info.Environment["TEMP"]);
        Assert.True(EngineLaunch.IsPolicyBlock(577));
        Assert.True(EngineLaunch.IsPolicyBlock(1260));
        Assert.True(EngineLaunch.IsPolicyBlock(5));
        Assert.False(EngineLaunch.IsPolicyBlock(2));
    }
}
