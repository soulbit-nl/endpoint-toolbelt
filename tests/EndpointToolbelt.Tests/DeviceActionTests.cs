using System;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The low-risk actions of 1.0 in the app.</summary>
public class DeviceActionTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Temp_cleanup_removes_only_old_files_and_never_follows_a_link()
    {
        using var t = new TestFolder();
        var profile = t.Combine("profile");
        var temp = Path.Combine(profile, "AppData", "Local", "Temp");
        Directory.CreateDirectory(Path.Combine(temp, "old-folder"));
        var old = Path.Combine(temp, "old.tmp");
        var fresh = Path.Combine(temp, "fresh.tmp");
        var nested = Path.Combine(temp, "old-folder", "nested.tmp");
        File.WriteAllText(old, new string('x', 2048));
        File.WriteAllText(fresh, "keep");
        File.WriteAllText(nested, "x");
        foreach (var f in new[] { old, nested }) { File.SetCreationTimeUtc(f, Now.AddDays(-3)); File.SetLastWriteTimeUtc(f, Now.AddDays(-3)); }
        File.SetLastWriteTimeUtc(fresh, Now.AddHours(-1));
        // A junction to a folder outside: never followed, never removed.
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "keep");
        File.SetCreationTimeUtc(precious, Now.AddDays(-3));
        File.SetLastWriteTimeUtc(precious, Now.AddDays(-3));
        TestFolder.CreateJunction(Path.Combine(temp, "link"), outside);
        Directory.SetLastWriteTimeUtc(Path.Combine(temp, "old-folder"), Now.AddDays(-3));

        var result = UserTempCleanup.Run(temp, profile, Now);

        Assert.Null(result.NotRun);
        Assert.Equal(2, result.FilesRemoved);
        Assert.True(result.BytesFreed >= 2049);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(precious));
        Assert.True(Directory.Exists(Path.Combine(temp, "link")));
        Assert.True(result.Skipped >= 1);
    }

    [Fact]
    public void Temp_cleanup_refuses_a_folder_outside_the_profile()
    {
        using var t = new TestFolder();
        var result = UserTempCleanup.Run(t.Combine("elsewhere"), t.Combine("profile"), Now);
        Assert.Equal("the temporary folder is not in your profile", result.NotRun);
        Assert.Equal("the temporary folder is unknown", UserTempCleanup.Run("", t.Path, Now).NotRun);
    }

    private static StatusInfo Status() => new();

    [Fact]
    public void Rows_link_to_the_new_tools_only_where_they_help()
    {
        var oldSignatures = new DefenderReadout { Mode = "normal", RealTimeProtection = true, SignatureUpdatedUtc = Now.AddDays(-5).ToString("o") };
        Assert.Equal(HealthFix.UpdateDefender, DeviceSettingRows.Defender(Status(), oldSignatures, Now).Fix);
        var realTimeOff = new DefenderReadout { Mode = "normal", RealTimeProtection = false, SignatureUpdatedUtc = Now.AddDays(-5).ToString("o") };
        Assert.Equal(HealthFix.None, DeviceSettingRows.Defender(Status(), realTimeOff, Now).Fix);
        Assert.Equal(HealthFix.None, DeviceSettingRows.Defender(Status(), new DefenderReadout { Mode = "missing" }, Now).Fix);

        Assert.Equal(HealthFix.SyncClock, DeviceSettingRows.Clock(Status(), new ClockReadout { OffsetSeconds = 400 }).Fix);
        Assert.Equal(HealthFix.SyncClock, DeviceSettingRows.Clock(Status(), new ClockReadout { Unavailable = "not-running" }).Fix);
        Assert.Equal(HealthFix.None, DeviceSettingRows.Clock(Status(), new ClockReadout { Unavailable = "no-source" }).Fix);
        Assert.Equal(HealthFix.None, DeviceSettingRows.Clock(Status(), new ClockReadout { OffsetSeconds = 2 }).Fix);

        Assert.Equal(HealthFix.FreeDiskSpace, new HealthResult { Name = HealthChecks.FreeSpace, Status = HealthStatus.Warn }.Fix);
        // Nothing on the device shows whether the key is in Entra ID: the hint names the tool instead.
        Assert.Equal(HealthFix.None, new HealthResult { Name = HealthChecks.Compliance, Status = HealthStatus.Warn }.Fix);
        Assert.Contains("Back up recovery key", HealthChecks.Hint(HealthChecks.Compliance, false));
        Assert.Contains("Sync clock", HealthChecks.Hint(HealthChecks.Clock, false));
        Assert.Contains("Update Defender definitions", HealthChecks.Hint(HealthChecks.Defender, false));
        Assert.Contains("Free up disk space", HealthChecks.Hint(HealthChecks.FreeSpace, false));
    }

    [Fact]
    public void The_new_actions_have_names_texts_and_the_service_rules_of_the_decisions()
    {
        var added = new[] { ToolbeltAction.BackupRecoveryKey, ToolbeltAction.SyncClock, ToolbeltAction.UpdateDefender, ToolbeltAction.FreeDiskSpace };
        foreach (var action in added) { Assert.NotEqual(action.ToString(), ActionText.Name(action)); }
        Assert.Equal(new[] { "Back up recovery key", "Sync clock", "Update Defender definitions", "Free up disk space" }, added.Select(ActionText.Name));
        // Only the disk cleanup slows everyone down: it asks and acknowledges other sessions.
        Assert.True(ActionCatalog.Get(ToolbeltAction.FreeDiskSpace).RequiresConfirmation);
        Assert.True(ActionCatalog.Get(ToolbeltAction.FreeDiskSpace).AffectsAllSessions);
        foreach (var action in added.Take(3))
        {
            Assert.False(ActionCatalog.Get(action).RequiresConfirmation);
            Assert.False(ActionCatalog.Get(action).AffectsAllSessions);
        }
        Assert.All(added, a => Assert.True(ActionCatalog.Get(a).CountsTowardRateLimit));
        Assert.Contains("Old versions of Windows components", Confirmations.Text(ToolbeltAction.FreeDiskSpace));
        Assert.Contains("slows the device for everyone", Confirmations.OtherSessionsWarning(ToolbeltAction.FreeDiskSpace, 1));
        Assert.True(ActionText.RunsInBackground(ToolbeltAction.FreeDiskSpace));
        Assert.False(ActionText.RunsInBackground(ToolbeltAction.SyncClock));
        // "Nothing to repair" is Repair Windows Update's sentence; the new actions say what they found.
        var outcome = new RunOutcome { Action = ToolbeltAction.UpdateDefender, Result = ResultCode.Completed, ExitCode = EngineExitCode.NothingToRepair };
        outcome.Records.Add(new EngineRecord { Type = "finding", Severity = "INFO", Title = "Another antivirus is in charge on this device", Detail = "", NextStep = "" });
        Assert.Equal("Update Defender definitions: Another antivirus is in charge on this device. Nothing was changed.", ResultPresenter.Final(outcome).Status);        foreach (var fix in new[] { HealthFix.SyncClock, HealthFix.UpdateDefender, HealthFix.FreeDiskSpace })
        {
            Assert.Contains(Words.FixLink(fix), added.Select(ActionText.Name));
        }
    }
}
