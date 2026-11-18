using System.Collections.Generic;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Every reset's confirmation says which connection it needs afterwards
/// and what the user is without until then; with the Network to Intune row not OK a warning line
/// comes before the button. It warns and never refuses.
/// </summary>
public class ResetConnectionWarningTests
{
    private static List<HealthResult> Rows(HealthStatus network)
        => HealthView.Rows(new[] { new HealthResult { Name = HealthChecks.Connectivity, Status = network, Detail = "" } });

    [Fact]
    public void Every_reset_confirmation_says_which_connection_it_needs_afterwards_and_what_the_user_is_without()
    {
        var bodies = new Dictionary<ResetTool, string>
        {
            [ResetTool.SignInCache] = Confirmations.SignInCacheReset,
            [ResetTool.TeamsCache] = Confirmations.TeamsCache,
            [ResetTool.CompanyPortal] = Confirmations.CompanyPortalReset,
            [ResetTool.OneDrive] = OneDriveReset.ConfirmText,
            [ResetTool.AppContent] = Confirmations.Text(ToolbeltAction.ClearAppCache),
            [ResetTool.FullImeReset] = Confirmations.Text(ToolbeltAction.FullReset),
        };
        foreach (var pair in bodies)
        {
            var sentence = ConnectionNeeds.After(pair.Key);
            Assert.EndsWith(" " + sentence, pair.Value);
            Assert.Contains("needs a connection", sentence);
            Assert.Contains("without one, ", sentence);
            Assert.EndsWith("until it is back.", sentence);
        }
        Assert.Equal(6, new HashSet<string>(bodies.Values).Count);
    }

    [Fact]
    public void The_warning_line_comes_only_when_the_Network_to_Intune_row_is_not_OK()
    {
        Assert.False(ConnectionNeeds.NetworkNotOk(Rows(HealthStatus.Pass)));
        Assert.True(ConnectionNeeds.NetworkNotOk(Rows(HealthStatus.Warn)));
        Assert.True(ConnectionNeeds.NetworkNotOk(Rows(HealthStatus.Fail)));
        // Not checked yet: no warning, no guess.
        Assert.False(ConnectionNeeds.NetworkNotOk(new List<HealthResult>()));
        Assert.False(ConnectionNeeds.NetworkNotOk(null));
        foreach (ResetTool tool in System.Enum.GetValues(typeof(ResetTool)))
        {
            Assert.Equal("", ConnectionNeeds.Warning(tool, Rows(HealthStatus.Pass)));
            var warning = ConnectionNeeds.Warning(tool, Rows(HealthStatus.Fail));
            Assert.StartsWith("Connection to work services is not OK right now: ", warning);
            Assert.EndsWith(" until the connection is back.", warning);
        }
        Assert.Null(ConnectionNeeds.Join("", null));
        Assert.Equal("a" + System.Environment.NewLine + "b", ConnectionNeeds.Join("a", "", "b"));
    }

    [Fact]
    public void Acceptance_4_with_the_network_row_not_OK_Reset_sign_in_cache_says_before_it_runs_that_signing_in_again_needs_a_connection()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.SetField(window, "_healthRows", Rows(HealthStatus.Fail));
            Assert.Contains("Signing in again needs a connection to Microsoft", Confirmations.SignInCacheReset);
            Assert.Equal("Connection to work services is not OK right now: signing in again may not work until the connection is back.", window.NetworkWarning(ResetTool.SignInCache));
            // With the row OK there is no warning block; the body still says it.
            MainWindowPagesTests.SetField(window, "_healthRows", Rows(HealthStatus.Pass));
            Assert.Null(window.NetworkWarning(ResetTool.SignInCache));
        });
    }
}
