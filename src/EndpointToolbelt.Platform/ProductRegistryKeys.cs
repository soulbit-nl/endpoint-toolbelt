using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace EndpointToolbelt.Platform;

/// <summary>
/// The product's registry keys (root, Config, State; 64-bit view) with a protected DACL that gives
/// SYSTEM and Administrators full control and nobody else anything, inherited by subkeys.
/// Enforced by the service at every start, because Windows Installer applies a key's
/// PermissionEx only when that key row creates the key: the root and Config are created while
/// the MSI writes their first value, and then keep the inherited HKLM\SOFTWARE ACL (users can
/// read). The policy key is never touched.
/// </summary>
public static class ProductRegistryKeys
{
    public static readonly string[] Keys = { ProductInfo.RegistryRoot, ProductInfo.ConfigKey, ProductInfo.StateKey };

    public static RegistrySecurity CreateSecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSids.LocalSystem, WellKnownSids.Administrators })
        {
            security.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        security.SetOwner(WellKnownSids.LocalSystem);
        return security;
    }

    /// <summary>True when a key's DACL is protected and allows only SYSTEM and Administrators.</summary>
    public static bool IsProtected(RegistrySecurity security)
    {
        if (!security.AreAccessRulesProtected) { return false; }
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(WellKnownSids.LocalSystem) && !sid.Equals(WellKnownSids.Administrators)) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Creates missing keys and replaces an open DACL on existing ones. Returns what it changed
    /// (empty when everything was already right); throws when it cannot.
    /// </summary>
    public static List<string> Enforce()
    {
        var changed = new List<string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var path in Keys)
        {
            using var existing = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);
            if (existing == null)
            {
                using var created = hklm.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, CreateSecurity());
                changed.Add(path + " (created)");
                continue;
            }
            if (!IsProtected(existing.GetAccessControl(AccessControlSections.Access)))
            {
                existing.SetAccessControl(CreateSecurity());
                changed.Add(path);
            }
        }
        return changed;
    }
}
