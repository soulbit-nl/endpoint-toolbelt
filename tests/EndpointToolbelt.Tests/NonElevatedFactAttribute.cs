using System.Security.Principal;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A test that checks behaviour as a standard user sees it (who owns a new folder, which pipe
/// rights are refused). An elevated administrator owns new objects as Administrators and gets
/// full rights everywhere, so such a test is skipped there (the CI runner runs elevated); it runs
/// on a developer machine and the Windows Sandbox scenarios cover the standard-user view.
/// </summary>
public sealed class NonElevatedFactAttribute : FactAttribute
{
    public NonElevatedFactAttribute()
    {
        if (IsElevated) { Skip = "Runs only without elevation: an elevated administrator owns new objects and has full pipe rights."; }
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
