using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EndpointToolbelt.Service.Platform;

public interface ISessionCounter
{
    /// <summary>
    /// Number of signed-in user sessions other than <paramref name="callerSessionId"/>, or -1
    /// when they could not be counted (callers must then treat other users as possibly present).
    /// </summary>
    int CountOtherUserSessions(int callerSessionId);
}

/// <summary>
/// Counts user sessions through the Terminal Services API. Active and disconnected sessions
/// with a signed-in user both count: an IME restart or a Company Portal close affects a
/// disconnected user as well. RemoteApp sessions are sessions here too (they have no
/// explorer.exe, which is why process-based counting is not used).
/// </summary>
public sealed class WtsSessionCounter : ISessionCounter
{
    private enum WtsConnectState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    private const int WTSUserName = 5;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    public int CountOtherUserSessions(int callerSessionId)
    {
        var sessions = ListUserSessions();
        // Fail closed: if the sessions cannot be listed, report "unknown".
        if (sessions == null) { return -1; }
        var others = 0;
        foreach (var id in sessions) { if (id != callerSessionId) { others++; } }
        return others;
    }

    /// <summary>
    /// Sessions other than 0 with a signed-in user (active or disconnected), or null when they
    /// cannot be listed. A session whose user cannot be read counts as a signed-in user (fail closed).
    /// </summary>
    public static IReadOnlyList<int> ListUserSessions()
    {
        var sessions = new SortedSet<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) { return null; }
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(new IntPtr(buffer.ToInt64() + (long)i * size));
                if (info.SessionId == 0) { continue; }
                if (info.State != WtsConnectState.Active && info.State != WtsConnectState.Disconnected) { continue; }
                var user = GetUserName(info.SessionId, out var readable);
                if (!readable || !string.IsNullOrEmpty(user)) { sessions.Add(info.SessionId); }
            }
        }
        finally { WTSFreeMemory(buffer); }
        return new List<int>(sessions);
    }

    private static string GetUserName(int sessionId, out bool readable)
    {
        readable = WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSUserName, out var buffer, out _);
        if (!readable) { return null; }
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }
}
