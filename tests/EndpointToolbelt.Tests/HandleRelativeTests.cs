using System;
using System.IO;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Rules;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Folders are pinned by handle and everything below them is opened relative to it.</summary>
public class HandleRelativeTests
{
    [Fact]
    public void Deletes_a_tree_with_read_only_files_and_removes_a_junction_inside_as_a_link()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        var root = t.Combine("root");
        Directory.CreateDirectory(Path.Combine(root, "app_1", "sub"));
        File.WriteAllText(Path.Combine(root, "app_1", "a.txt"), "x");
        File.WriteAllText(Path.Combine(root, "app_1", "sub", "b.txt"), "y");
        File.SetAttributes(Path.Combine(root, "app_1", "sub", "b.txt"), FileAttributes.ReadOnly);
        TestFolder.CreateJunction(Path.Combine(root, "app_1", "sub", "link"), outside);

        Assert.True(NativeFile.DeleteTreeBelowNoFollow(root, "app_1"));

        Assert.False(Directory.Exists(Path.Combine(root, "app_1")));
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.False(NativeFile.DeleteTreeBelowNoFollow(root, "app_1"));
        Assert.False(NativeFile.DeleteTreeBelowNoFollow(t.Combine("no-root"), "x"));
    }

    [Fact]
    public void A_root_that_is_a_junction_is_refused_and_nothing_is_removed()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(Path.Combine(outside, "child"));
        File.WriteAllText(Path.Combine(outside, "child", "precious.txt"), "keep");
        var link = t.Combine("link");
        TestFolder.CreateJunction(link, outside);

        var ex = Assert.Throws<IOException>(() => NativeFile.DeleteTreeBelowNoFollow(link, "child"));
        Assert.Contains("reparse point", ex.Message);
        Assert.True(File.Exists(Path.Combine(outside, "child", "precious.txt")));
    }

    [Fact]
    public void A_junction_on_the_relative_path_is_refused()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(Path.Combine(outside, "child"));
        var root = t.Combine("root");
        Directory.CreateDirectory(root);
        TestFolder.CreateJunction(Path.Combine(root, "link"), outside);

        Assert.Throws<IOException>(() => NativeFile.DeleteTreeBelowNoFollow(root, @"link\child"));
        Assert.True(Directory.Exists(Path.Combine(outside, "child")));
        // The link itself as the target is removed as a link.
        Assert.True(NativeFile.DeleteTreeBelowNoFollow(root, "link"));
        Assert.True(Directory.Exists(outside));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"a\..\b")]
    [InlineData(@"a\\b")]
    public void A_relative_path_that_is_not_plain_is_refused(string relative)
    {
        using var t = new TestFolder();
        Assert.Throws<ArgumentException>(() => NativeFile.DeleteTreeBelowNoFollow(t.Path, relative));
    }

    [Fact]
    public void A_folder_is_opened_by_handle_only_when_it_is_a_real_directory_at_its_path()
    {
        using var t = new TestFolder();
        var real = t.Combine("real");
        Directory.CreateDirectory(real);
        using (var handle = NativeFile.OpenFolderNoFollow(real)) { Assert.NotNull(handle); Assert.NotNull(NativeFile.ReadSecurity(handle)); }
        Assert.Null(NativeFile.OpenFolderNoFollow(t.Combine("missing")));
        TestFolder.CreateJunction(t.Combine("link"), real);
        Assert.Throws<IOException>(() => NativeFile.OpenFolderNoFollow(t.Combine("link")));
        File.WriteAllText(t.Combine("file"), "x");
        Assert.Throws<IOException>(() => NativeFile.OpenFolderNoFollow(t.Combine("file")));
    }

    [Fact]
    public void The_foreign_folder_check_refuses_a_junction_and_reads_the_descriptor_through_the_handle()
    {
        using var t = new TestFolder();
        var real = t.Combine("real");
        Directory.CreateDirectory(real);
        TestFolder.CreateJunction(t.Combine("link"), real);
        Assert.Equal("reparse point in path", FileSystemSafety.ValidateForeignFolder(t.Combine("link")));
        Assert.Equal("missing", FileSystemSafety.ValidateForeignFolder(t.Combine("nope")));
        // A temp folder is owned by the developer (not a trusted owner) or, on an elevated build
        // agent, by Administrators with write rights for the user: never trusted.
        Assert.NotNull(FileSystemSafety.ValidateForeignFolder(real));
    }

    [Fact]
    public void The_log_is_written_rolled_over_and_protected_relative_to_its_folder()
    {
        using var t = new TestFolder();
        var folder = t.Combine("logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "test.log");
        var log = new EndpointToolbelt.Service.Logging.CmTraceLog(path, "Test", maxBytes: 400);
        for (var i = 0; i < 6; i++) { log.Info("line " + i + " " + new string('x', 100)); }
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(folder, "test.lo_")));
        Assert.Contains("line 5", File.ReadAllText(path));
        var security = File.GetAccessControl(path);
        Assert.True(security.AreAccessRulesProtected);
        // No folder: nothing is created anywhere else (the service log swallows the error; the file class reports it).
        Assert.ThrowsAny<IOException>(() => new CmTraceFile(t.Combine(@"gone\x.log")).Append("x", 1, "Test", DateTimeOffset.Now, 1));
        new EndpointToolbelt.Service.Logging.CmTraceLog(t.Combine(@"gone\x.log"), "Test").Info("x");
        Assert.False(Directory.Exists(t.Combine("gone")));
    }

    [Fact]
    public void A_log_file_that_is_a_link_is_never_written_through()
    {
        using var t = new TestFolder();
        var folder = t.Combine("logs");
        Directory.CreateDirectory(folder);
        var target = t.Combine("target");
        Directory.CreateDirectory(target);
        // A folder link where the log file name is: refused (not a file), the target stays empty.
        TestFolder.CreateJunction(Path.Combine(folder, "test.log"), target);
        Assert.ThrowsAny<IOException>(() => new CmTraceFile(Path.Combine(folder, "test.log")).Append("x", 1, "Test", DateTimeOffset.Now, 1));
        new EndpointToolbelt.Service.Logging.CmTraceLog(Path.Combine(folder, "test.log"), "Test").Info("x");
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }
}

/// <summary>A confirmation is bound to a status the caller was shown.</summary>
public class ConfirmationNonceTests
{
    private static readonly CallerContext Caller = new("S-1-5-21-100", 2, "user");

    [Fact]
    public void A_nonce_is_valid_for_its_caller_once_and_within_its_lifetime()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var nonces = new ConfirmationNonces(() => now);
        var nonce = nonces.Issue("S-1-5-21-1");
        Assert.Equal(32, nonce.Length);
        Assert.True(nonces.IsValid("S-1-5-21-1", nonce));
        Assert.False(nonces.IsValid("S-1-5-21-2", nonce));
        Assert.False(nonces.IsValid("S-1-5-21-1", null));
        Assert.False(nonces.IsValid("S-1-5-21-1", nonce.ToUpperInvariant() + "x"));
        Assert.True(nonces.Consume("S-1-5-21-1", nonce));
        Assert.False(nonces.Consume("S-1-5-21-1", nonce));
        Assert.False(nonces.IsValid("S-1-5-21-1", nonce));

        var old = nonces.Issue("S-1-5-21-1");
        now = now.Add(ConfirmationNonces.Lifetime).AddSeconds(1);
        Assert.False(nonces.IsValid("S-1-5-21-1", old));
    }

    [Fact]
    public void Only_the_newest_nonces_of_a_caller_are_kept()
    {
        var nonces = new ConfirmationNonces();
        var first = nonces.Issue("S-1-5-21-1");
        for (var i = 0; i < ConfirmationNonces.PerCaller; i++) { nonces.Issue("S-1-5-21-1"); }
        Assert.False(nonces.IsValid("S-1-5-21-1", first));
    }

    [Theory]
    [InlineData(ToolbeltAction.ClearAppCache, 0)]
    [InlineData(ToolbeltAction.FullReset, 0)]
    [InlineData(ToolbeltAction.RepairWU, 2)]
    public void A_confirmed_action_needs_a_nonce_of_a_status_shown_to_the_caller(ToolbeltAction action, int otherSessions)
    {
        var nonces = new ConfirmationNonces();
        var config = new ServiceConfig { AllowMachineWideOnMultiSession = true };
        Request Run(string nonce) => new() { Type = RequestType.RunAction, Action = action, Confirmed = true, ConfirmedOtherSessions = otherSessions, ConfirmationNonce = nonce };

        var without = RequestPolicy.EvaluateRunAction(Run(null), Caller, otherSessions, config, null, new RateLimiter(TimeSpan.FromHours(1)), nonces: nonces);
        Assert.Equal(ResultCode.ConfirmationRequired, without.Result);
        Assert.Contains("status", without.Message);

        var other = nonces.Issue("S-1-5-21-999");
        Assert.Equal(ResultCode.ConfirmationRequired, RequestPolicy.EvaluateRunAction(Run(other), Caller, otherSessions, config, null, new RateLimiter(TimeSpan.FromHours(1)), nonces: nonces).Result);

        var mine = nonces.Issue(Caller.Sid);
        Assert.True(RequestPolicy.EvaluateRunAction(Run(mine), Caller, otherSessions, config, null, new RateLimiter(TimeSpan.FromHours(1)), nonces: nonces).IsAccepted);
        // The policy only checks; the coordinator spends the nonce once the run is accepted.
        Assert.True(nonces.IsValid(Caller.Sid, mine));
    }

    [Fact]
    public void An_action_without_a_warning_needs_no_nonce()
    {
        var nonces = new ConfirmationNonces();
        var request = new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock };
        Assert.True(RequestPolicy.EvaluateRunAction(request, Caller, 0, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)), nonces: nonces).IsAccepted);
        // A machine-wide action with nobody else signed in shows no other-sessions warning either.
        var alone = new Request { Type = RequestType.RunAction, Action = ToolbeltAction.RepairWU, Confirmed = true, ConfirmationNonce = nonces.Issue(Caller.Sid) };
        Assert.True(RequestPolicy.EvaluateRunAction(alone, Caller, 0, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)), nonces: nonces).IsAccepted);
    }

    [Fact]
    public void The_nonce_fields_travel_in_the_protocol_and_an_older_request_without_them_still_reads()
    {
        using var ms = new MemoryStream();
        MessageFraming.Write(ms, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true, ConfirmationNonce = "abc" });
        ms.Position = 0;
        Assert.Equal("abc", MessageFraming.Read<Request>(ms, ProtocolInfo.MaxRequestBytes).ConfirmationNonce);

        var older = System.Text.Encoding.UTF8.GetBytes("{\"ProtocolVersion\":1,\"Type\":2,\"Action\":3,\"Confirmed\":true}");
        var request = MessageFraming.Deserialize<Request>(older);
        Assert.True(request.Confirmed);
        Assert.Null(request.ConfirmationNonce);

        using var status = new MemoryStream();
        MessageFraming.Write(status, new Response { Type = ResponseType.Status, Status = new StatusInfo { ConfirmationNonce = "n1" } });
        status.Position = 0;
        Assert.Equal("n1", MessageFraming.Read<Response>(status, ProtocolInfo.MaxResponseBytes).Status.ConfirmationNonce);
    }
}