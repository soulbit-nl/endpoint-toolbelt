using System;
using System.IO;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Windows PowerShell as a plain child process for the tests of the Job Object runner (process
/// trees, pipes, time limits). The product itself starts it only through the in-box cmdlet runner.
/// </summary>
internal static class TestShell
{
    public static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
