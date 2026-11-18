using System;
using EndpointToolbelt.Service.Configuration;

namespace EndpointToolbelt.Service.Platform;

/// <summary>
/// Whether the device is built for several users at once: Windows Enterprise multi-session
/// (EditionID <c>ServerRdsh</c>, or an edition named "multi-session"), or a Windows Server with
/// the Remote Desktop Session Host role (application compatibility mode on).
/// </summary>
public static class MultiSessionHost
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    public static bool Detect(IRegistryReader registry)
    {
        try
        {
            var edition = registry.GetValue(CurrentVersionKey, "EditionID") as string ?? "";
            if (edition.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase)
                || edition.IndexOf("MultiSession", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            var installation = registry.GetValue(CurrentVersionKey, "InstallationType") as string ?? "";
            // RD Session Host role: Terminal Server application compatibility mode is on.
            return installation.StartsWith("Server", StringComparison.OrdinalIgnoreCase)
                && registry.GetValue(TerminalServerKey, "TSAppCompat") is int appCompat && appCompat == 1;
        }
        catch (Exception)
        {
            // Unknown counts as shared: the only effect is the support package limit.
            return true;
        }
    }
}
