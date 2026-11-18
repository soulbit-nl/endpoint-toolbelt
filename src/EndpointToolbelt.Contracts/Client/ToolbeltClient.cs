using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using EndpointToolbelt.Native;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.Client;

/// <summary>Decides whether the process behind a pipe is the real service.</summary>
public interface IServerVerifier
{
    /// <summary>Returns null when the server is trusted, otherwise a reason.</summary>
    string Verify(NamedPipeClientStream pipe);
}

/// <summary>
/// Trusts the pipe server only if the pipe is owned by SYSTEM or Administrators, the service is
/// running, and the server process is that service's process in session 0. This blocks a user
/// who created the pipe name first (squatting) from feeding results or files to another user.
/// </summary>
public sealed class ServiceProcessVerifier : IServerVerifier
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public string Verify(NamedPipeClientStream pipe)
    {
        // A pipe created by a standard user is owned by that user.
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !(owner.Equals(LocalSystem) || owner.Equals(Administrators)))
        {
            return "The pipe is not owned by the system.";
        }

        var serverPid = PipeNative.GetServerProcessId(pipe.SafePipeHandle);
        var serverSession = PipeNative.GetServerSessionId(pipe.SafePipeHandle);
        if (serverSession != 0) { return "The pipe server does not run in session 0."; }

        using var sc = new ServiceController(ProductInfo.ServiceName);
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running) { return "The service is not running."; }
        var servicePid = PipeNative.GetServiceProcessId(ProductInfo.ServiceName);
        if (servicePid == 0 || serverPid != servicePid) { return "The pipe is not served by the " + ProductInfo.DisplayName + " service."; }
        return null;
    }
}

public sealed class ServerIdentityException : Exception
{
    public ServerIdentityException(string message) : base(message) { }
}

/// <summary>One connection to the service. Not thread-safe; use one instance per request.</summary>
public sealed class ToolbeltClient : IDisposable
{
    // Only the rights the pipe DACL grants to interactive users. GENERIC_WRITE would include
    // FILE_APPEND_DATA (= create pipe instance), which the service deliberately denies.
    // ReadPermissions lets the client check the pipe owner.
    private const PipeAccessRights ClientRights =
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions;

    /// <summary>The access the client requests when it opens the pipe.</summary>
    public static PipeAccessRights RequestedRights => ClientRights;

    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

    private readonly NamedPipeClientStream _pipe;

    private ToolbeltClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>
    /// Starts the service if it is stopped (interactive users have SERVICE_START on it) and
    /// waits until it runs. Throws <see cref="InvalidOperationException"/> when the service is
    /// not installed, disabled, or cannot be started.
    /// </summary>
    public static void EnsureServiceRunning(TimeSpan timeout)
    {
        using var sc = new ServiceController(ProductInfo.ServiceName);
        sc.Refresh();
        var status = sc.Status; // throws when the service is not installed
        if (status == ServiceControllerStatus.Running) { return; }
        if (status == ServiceControllerStatus.StopPending)
        {
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            status = ServiceControllerStatus.Stopped;
        }
        if (status == ServiceControllerStatus.Stopped)
        {
            try { sc.Start(); }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception w && w.NativeErrorCode == ERROR_SERVICE_ALREADY_RUNNING)
            {
                // Started concurrently by someone else.
            }
        }
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static ToolbeltClient Connect(TimeSpan timeout, string pipeName = ProductInfo.PipeName, IServerVerifier verifier = null)
    {
        verifier ??= new ServiceProcessVerifier();
        var pipe = new NamedPipeClientStream(".", pipeName, ClientRights, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            var reason = verifier.Verify(pipe);
            if (reason != null) { throw new ServerIdentityException(reason); }
            return new ToolbeltClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public void Send(Request request) => MessageFraming.Write(_pipe, request);

    /// <summary>Reads the next response, or null when the service closed the connection.</summary>
    public Response Receive() => MessageFraming.Read<Response>(_pipe, ProtocolInfo.MaxResponseBytes);

    public Stream Stream => _pipe;

    public void Dispose() => _pipe.Dispose();
}
