using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EndpointToolbelt.Native;

/// <summary>Named pipe and service control functions that .NET Framework does not expose.</summary>
public static class PipeNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

    public static uint GetServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var id) ? id : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static uint GetServerSessionId(SafePipeHandle pipe) =>
        GetNamedPipeServerSessionId(pipe, out var id) ? id : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static uint GetClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var id) ? id : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static uint GetClientSessionId(SafePipeHandle pipe) =>
        GetNamedPipeClientSessionId(pipe, out var id) ? id : throw new Win32Exception(Marshal.GetLastWin32Error());

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SC_STATUS_PROCESS_INFO = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref SERVICE_STATUS_PROCESS buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>Process ID of a running service, or 0 when it is not running or not installed.</summary>
    public static uint GetServiceProcessId(string serviceName)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) { return 0; }
        try
        {
            var svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) { return 0; }
            try
            {
                var status = new SERVICE_STATUS_PROCESS();
                return QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref status, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _)
                    ? status.dwProcessId
                    : 0;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
