using System;
using System.Diagnostics;
using System.IO;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Tests;

/// <summary>A temporary folder that is removed (without following links) after the test.</summary>
internal sealed class TestFolder : IDisposable
{
    public TestFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "etb-test-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts));

    public void Dispose()
    {
        try { FileSystemSafety.DeleteTreeNoFollow(Path); } catch (Exception) { }
    }

    /// <summary>Creates a directory junction (no administrator rights needed).</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p.WaitForExit();
        if (p.ExitCode != 0) { throw new InvalidOperationException("mklink failed: " + p.StandardError.ReadToEnd()); }
    }
}

internal static class EnumerableExtensions
{
    public static T[] Concat<T>(this T[] first, T[] second)
    {
        var result = new T[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
