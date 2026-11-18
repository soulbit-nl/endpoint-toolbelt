using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EndpointToolbelt.Service.Platform;

/// <summary>
/// The service's recovery actions: restart after 1, 1 and 5 minutes, failure count reset after a
/// day. Set by the service itself at every start, because Windows Installer cannot set restart
/// actions: MsiServiceConfigFailureActions opens the service without SERVICE_START, which
/// ChangeServiceConfig2 requires for SC_ACTION_RESTART, and fails with access denied (WiX warns
/// about this as WIX1149). The MSI still sets delayed start and "also on a stop with an error"
/// (FailureActionsWhen), which work. A repair or a manual change is corrected at the next start.
/// </summary>
public static class ServiceRecovery
{
    public const int ResetPeriodSeconds = 86400;
    public static readonly int[] RestartDelaysMs = { 60000, 60000, 300000 };

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    private const int SC_ACTION_RESTART = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public string RebootMessage;
        public string Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActionsFlag
    {
        public int FailureActionsOnNonCrashFailures;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    private static extern bool ChangeFailureActions(IntPtr service, uint level, ref ServiceFailureActions info);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    private static extern bool ChangeFailureActionsFlag(IntPtr service, uint level, ref ServiceFailureActionsFlag info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>Sets the recovery actions on the named service; null on success, otherwise why not.</summary>
    public static string Apply(string serviceName)
    {
        var manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) { return "the service control manager could not be opened: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; }
        try
        {
            var service = OpenServiceW(manager, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_START);
            if (service == IntPtr.Zero) { return "the service could not be opened: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; }
            var actions = Marshal.AllocHGlobal(Marshal.SizeOf<ScAction>() * RestartDelaysMs.Length);
            try
            {
                for (var i = 0; i < RestartDelaysMs.Length; i++)
                {
                    Marshal.StructureToPtr(new ScAction { Type = SC_ACTION_RESTART, Delay = (uint)RestartDelaysMs[i] },
                        actions + (i * Marshal.SizeOf<ScAction>()), false);
                }
                // A null command and reboot message leave those settings alone.
                var info = new ServiceFailureActions { ResetPeriod = ResetPeriodSeconds, ActionCount = (uint)RestartDelaysMs.Length, Actions = actions };
                if (!ChangeFailureActions(service, SERVICE_CONFIG_FAILURE_ACTIONS, ref info))
                {
                    return "the recovery actions could not be set: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                var flag = new ServiceFailureActionsFlag { FailureActionsOnNonCrashFailures = 1 };
                if (!ChangeFailureActionsFlag(service, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref flag))
                {
                    return "the recovery actions are set, but not for a stop with an error: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(actions);
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
