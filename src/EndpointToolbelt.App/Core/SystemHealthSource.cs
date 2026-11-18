using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using EndpointToolbelt.Platform;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>The health facts as the signed-in user can read them. Read-only.</summary>
public sealed class SystemHealthSource : IHealthSource, IProxyRoutes, IBootTime, IDirectRoute, IHelloPolicySource
{
    /// <summary>
    /// The Group Policy "Use Windows Hello for Business" (Software\Policies\Microsoft\PassportForWork,
    /// value Enabled), the device's or the user's, read-only. On (1) wins; off (0) when only off is set;
    /// null when neither sets it or it cannot be read.
    /// </summary>
    public bool? WindowsHelloPolicy()
    {
        bool? Read(RegistryHive hive)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\PassportForWork", writable: false);
                return key?.GetValue("Enabled") is int value ? value != 0 : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
        var machine = Read(RegistryHive.LocalMachine);
        var user = Read(RegistryHive.CurrentUser);
        if (machine == true || user == true) { return true; }
        return machine == false || user == false ? false : null;
    }

    private readonly SystemProxyRoutes _proxies = new();

    public Uri BrowserProxy(Uri target) => _proxies.BrowserProxy(target);

    public (Uri Proxy, string Bypass)? MachineProxy() => _proxies.MachineProxy();

    public HttpsObservation Https(HttpsRequest request) => _proxies.Https(request);

    private static readonly string[] LineBreaks = { "\r\n", "\n" };

    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>The device's last start: now minus the time Windows has been running.</summary>
    public DateTime? BootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public string ImeServiceStatus()
    {
        try
        {
            using var sc = new ServiceController("IntuneManagementExtension");
            return sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }

    public DsregInfo Dsreg() => DsregInfo.Parse(RunDsregcmd());

    /// <summary>dsregcmd /refreshprt in the user's context: its exit code and output.</summary>
    public static (int ExitCode, string Output) RunDsregcmdRefresh()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dsregcmd.exe");
        var psi = new ProcessStartInfo(exe, "/refreshprt")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // dsregcmd writes in the system's OEM code page, not in this window's (it has no console).
            StandardOutputEncoding = ConsoleEncoding.Oem(),
        };
        using var process = Process.Start(psi);
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return (-1, "did not finish within 30 seconds");
        }
        process.WaitForExit();
        return (process.ExitCode, (output.Result + " " + errors.Result).Trim());
    }

    /// <summary>dsregcmd /status in the user's context: their own view, also used in the support package.</summary>
    public static string[] RunDsregcmd()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dsregcmd.exe");
        var psi = new ProcessStartInfo(exe, "/status")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // dsregcmd writes in the system's OEM code page, not in this window's (it has no console).
            StandardOutputEncoding = ConsoleEncoding.Oem(),
        };
        using var process = Process.Start(psi);
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return Array.Empty<string>();
        }
        _ = errorTask;
        return output.Result.Split(LineBreaks, StringSplitOptions.None);
    }

    public DateTime? MdmCertificateExpiryUtc()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates.Cast<X509Certificate2>().ToList();
        try
        {
            return certificates
                .Where(c => c.Issuer.IndexOf("Microsoft Intune MDM Device CA", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(c => (DateTime?)c.NotAfter.ToUniversalTime())
                .OrderByDescending(d => d)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var certificate in certificates) { certificate.Dispose(); }
        }
    }

    public bool CanReach(string host, int port, int timeoutMs) => Connect(host, port, timeoutMs).Outcome == RouteOutcome.TcpConnected;

    /// <summary>A TCP connection to the host: connected, or why not (name, refused, time-out, other).</summary>
    public ProbeAnswer Connect(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (!WaitObserved(connect, timeoutMs)) { return ProbeAnswer.Failed(RouteOutcome.TimedOut); }
            return client.Connected ? ProbeAnswer.Connected : ProbeAnswer.Failed(RouteOutcome.Unknown);
        }
        catch (AggregateException ex) when (ex.GetBaseException() is SocketException socket)
        {
            return ProbeAnswer.Failed(RouteRules.FromSocketError(socket.SocketErrorCode));
        }
        catch (SocketException ex)
        {
            return ProbeAnswer.Failed(RouteRules.FromSocketError(ex.SocketErrorCode));
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            return ProbeAnswer.Failed(RouteOutcome.Unknown);
        }
    }

    /// <summary>
    /// Waits for a task at most <paramref name="timeoutMs"/>; false on a time-out. A task left
    /// running then (a connect whose client is disposed right after) faults later; its exception is
    /// observed here, so it never surfaces as an unobserved task exception.
    /// </summary>
    internal static bool WaitObserved(System.Threading.Tasks.Task task, int timeoutMs) => WaitObserved(task, timeoutMs, out _);

    /// <summary>As above; <paramref name="observer"/> is the continuation that reads the late fault (null when the task finished in time), for tests.</summary>
    internal static bool WaitObserved(System.Threading.Tasks.Task task, int timeoutMs, out System.Threading.Tasks.Task observer)
    {
        observer = null;
        if (task.Wait(timeoutMs)) { return true; }
        observer = task.ContinueWith(t => _ = t.Exception, System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);
        return false;
    }

    public double FreeSystemDriveGB()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
    }

    public bool ServicingRestartPending()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                 })
        {
            using var key = hklm.OpenSubKey(path, writable: false);
            if (key != null) { return true; }
        }
        return false;
    }
}
