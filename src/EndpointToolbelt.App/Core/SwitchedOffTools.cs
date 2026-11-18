using System;
using System.Collections.Generic;
using System.Security;
using EndpointToolbelt.Protocol;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The tools the organization switched off, for the app to hide. The service's status says
/// which; while the service cannot be reached, or from an older service (no list in its
/// status), the app reads the same policy value itself.
/// </summary>
public static class SwitchedOffTools
{
    /// <summary>The list from the status, or <paramref name="readPolicy"/> when the status carries none.</summary>
    public static IReadOnlyList<string> From(StatusInfo status, Func<IReadOnlyList<string>> readPolicy)
        => status?.DisabledTools ?? readPolicy?.Invoke() ?? Array.Empty<string>();

    /// <summary>The policy value read by the app, as the signed-in user (HKLM can be read by every user); empty when it cannot be read.</summary>
    public static IReadOnlyList<string> ReadPolicy()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ProductInfo.PolicyKey, writable: false);
            return ToolPolicy.Parse(key?.GetValue(ToolPolicy.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), null);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return Array.Empty<string>();
        }
    }
}
