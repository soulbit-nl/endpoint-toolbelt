using System;
using System.Collections.Generic;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The work account rows by join type, and the Windows Hello row, from the dsregcmd /status the
/// health checks already run in the user's context (Microsoft's "Troubleshoot devices by using the
/// dsregcmd command"). Each join type has the checks that apply to it:
/// <list type="bullet">
/// <item>Microsoft Entra joined and hybrid joined: the join and the sign-in token (AzureAdPrt).</item>
/// <item>Microsoft Entra registered (BYOD, WorkplaceJoined): the registration only; the SSO state
/// (AzureAdPrt) does not apply ("You can ignore this section for Microsoft Entra registered devices").</item>
/// <item>Domain joined only (DomainJoined YES, AzureAdJoined NO): the hybrid join did not complete, or the
/// device is not in Microsoft Entra; a Check with a step for IT.</item>
/// <item>Not joined: said calmly; no sign-in token is expected.</item>
/// </list>
/// The rows show the tenant's display name only: never a user name, a device ID or a key ID.
/// </summary>
public static class WorkAccountRows
{
    private static bool Yes(string value) => string.Equals((value ?? "").Trim(), "YES", StringComparison.OrdinalIgnoreCase);

    private static bool No(string value) => string.Equals((value ?? "").Trim(), "NO", StringComparison.OrdinalIgnoreCase);

    private static HealthResult Row(string name, HealthStatus status, string detail, string hint, bool info = false, string support = "", string homeTitle = "", string homeLine = "")
        => new() { Name = name, Status = status, Detail = detail ?? "", Hint = hint ?? "", Info = info, Support = support ?? "", HomeTitle = homeTitle ?? "", HomeLine = homeLine ?? "", FixOverride = HealthFix.None };

    private static string Tenant(string name) => string.IsNullOrWhiteSpace(name) ? "" : " to " + name.Trim();

    /// <summary>The join row: the join type in words, with what it means and, when it is not as expected, IT's step.</summary>
    public static HealthResult Join(DsregInfo dsreg)
    {
        var join = FirstLineFacts.JoinOf(dsreg);
        HealthResult row;
        switch (join)
        {
            case JoinType.EntraJoined:
                row = Row(HealthChecks.EntraJoined, HealthStatus.Pass, "Entra joined" + Tenant(dsreg.TenantName),
                    "This device is joined to Microsoft Entra ID; your work account signs in to Windows and Microsoft 365 with it.");
                break;
            case JoinType.HybridJoined:
                row = Row(HealthChecks.EntraJoined, HealthStatus.Pass, "Hybrid joined" + Tenant(dsreg.TenantName),
                    "This device is joined to your organization's domain and to Microsoft Entra ID (Microsoft Entra hybrid joined).");
                break;
            case JoinType.Registered:
                row = Row(HealthChecks.EntraJoined, HealthStatus.Pass, "Registered (personal device)" + Tenant(dsreg.WorkplaceTenantName),
                    "Your work account is registered on this device (Microsoft Entra registered, a personal or BYOD device). The device itself is not joined, which is normal for it; apps sign in with your work account one by one.");
                break;
            case JoinType.DomainOnly:
                var failed = dsreg.JoinErrorPhase.Length > 0 || dsreg.JoinClientErrorCode.Length > 0;
                row = Row(HealthChecks.EntraJoined, HealthStatus.Warn,
                    failed ? "Domain joined; the Microsoft Entra hybrid join did not complete" : "Domain joined, not in Microsoft Entra",
                    "This device is joined to your organization's domain but not to Microsoft Entra ID, so single sign-on and Intune cannot use it yet. IT support can check the hybrid join.",
                    support: "Microsoft Entra hybrid join did not complete (dsregcmd: DomainJoined YES, AzureAdJoined NO"
                        + (failed ? ", error phase " + Clean(dsreg.JoinErrorPhase) + ", client error " + Clean(dsreg.JoinClientErrorCode) : "")
                        + "). Check that the computer object syncs to Microsoft Entra ID (Microsoft Entra Connect) and the device registration, then sign out and in on the device.",
                    homeTitle: "This PC isn't fully connected to your work account", homeLine: "Your IT support needs to finish setting it up.");
                break;
            case JoinType.NotJoined:
                row = Row(HealthChecks.EntraJoined, HealthStatus.Warn, "Not joined to Microsoft Entra",
                    "This device is not joined to Microsoft Entra ID and has no work account registered. If your organization manages it, IT support can join or register it.",
                    support: "dsregcmd: AzureAdJoined NO, DomainJoined NO, WorkplaceJoined NO. Join the device to Microsoft Entra ID, or register the work account (Settings > Accounts > Access work or school), as your organization does.",
                    homeTitle: "This PC isn't connected to a work account", homeLine: "If your organization manages this PC, your IT support can connect it.");
                break;
            default:
                row = Row(HealthChecks.EntraJoined, HealthStatus.Warn, "Unknown: the join state could not be read",
                    "Windows did not answer the check, so how this device is joined is not known. Check again in a moment.",
                    homeTitle: "Your work account needs a check", homeLine: "Windows did not say how this PC is connected; check again in a moment.");
                break;
        }
        row.ReportDetail = FirstLineFacts.JoinWords(join) + (string.IsNullOrWhiteSpace(dsreg?.TenantName) ? "" : ", tenant " + dsreg.TenantName.Trim())
            + (join == JoinType.DomainOnly && dsreg.JoinErrorPhase.Length > 0 ? ", hybrid join stopped in phase " + Clean(dsreg.JoinErrorPhase) : "");
        return row;
    }

    /// <summary>
    /// The sign-in token (PRT) row, only where Windows gives one: on an Entra joined or hybrid joined
    /// device it is expected; on a registered or not joined device (and before a hybrid join completes)
    /// it does not apply, which is OK (Info), never a Check.
    /// </summary>
    public static HealthResult Prt(DsregInfo dsreg)
    {
        var join = FirstLineFacts.JoinOf(dsreg);
        if (join is JoinType.EntraJoined or JoinType.HybridJoined)
        {
            if (Yes(dsreg.AzureAdPrt)) { return HealthChecks.Make(HealthChecks.Prt, HealthStatus.Pass, "Present"); }
            var missing = HealthChecks.Make(HealthChecks.Prt, HealthStatus.Warn, No(dsreg.AzureAdPrt) ? "Missing: lock and unlock, or sign out and in" : "Unknown");
            missing.FixOverride = HealthFix.RefreshSignIn;
            return missing;
        }
        var why = join switch
        {
            JoinType.Registered => "Not used on a registered device: each app signs in with your work account",
            JoinType.DomainOnly => "Not given until the hybrid join completes",
            JoinType.NotJoined => "Not used: the device is not joined",
            _ => "Unknown: the join state could not be read",
        };
        if (join == JoinType.Unknown)
        {
            return HealthChecks.Make(HealthChecks.Prt, HealthStatus.Warn, "Unknown");
        }
        return Row(HealthChecks.Prt, HealthStatus.Pass, why,
            "Windows gives a single sign-on token (PRT) only on a device joined to Microsoft Entra ID (Entra joined or hybrid joined).", info: true);
    }

    /// <summary>A short code-like token for IT's line: letters, digits, '-' and '_', at most 40 characters.</summary>
    private static string Clean(string value)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in value ?? "")
        {
            if (sb.Length >= 40) { break; }
            if (ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) { sb.Append(ch); }
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    /// <summary>The user step when Windows Hello is required but not set up.</summary>
    public const string HelloStep = "Set up a PIN: Settings > Accounts > Sign-in options > PIN (Windows Hello).";

    /// <summary>
    /// Windows Hello for this user, read-only (it never starts the setup and reads no biometric
    /// data). The order matters: (1) a virtual machine or a multi-session host: not available,
    /// Info (in a remote session the hint says that first, the machine kind second); (2) a remote session (dsregcmd SessionIsNotRemote NO): Info, because a physical PC used over
    /// Remote Desktop also reports DeviceEligible NO, and an older service sends no machine kind; (3)
    /// set up (NgcSet YES): OK; (4) required by policy (dsregcmd PolicyEnabled, or the Group Policy "Use
    /// Windows Hello for Business") on a device that cannot use it (DeviceEligible NO): a Check with a
    /// step for IT; (5) required and not set up: a Check with the user's step. Not required and not set
    /// up: Info.
    /// </summary>
    public static HealthResult Hello(DsregInfo dsreg, MachineReadout machine, bool? groupPolicy)
    {
        dsreg ??= new DsregInfo();
        var facts = HelloFacts(dsreg, machine, groupPolicy);
        HealthResult row;
        if (machine?.MultiSession == true || machine?.VirtualMachine == true)
        {
            var kind = machine?.MultiSession == true ? "a multi-session host" : "a virtual machine";
            var onKind = "Windows Hello for Business cannot be set up on " + kind + "; sign in with your password or another method your organization allows.";
            // Signed in remotely, the remote session is the first reason, the machine kind the second.
            row = Row(HealthChecks.WindowsHello, HealthStatus.Pass, "Not available on this device",
                No(dsreg.NgcSessionIsNotRemote) ? RemoteSessionHint + " Besides, " + onKind : onKind, info: true);
        }
        else if (dsreg.Lines.Length == 0)
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Pass, "Unknown: could not be read",
                "Windows did not answer the check, so whether Windows Hello is set up is not known.", info: true);
        }
        else if (No(dsreg.NgcSessionIsNotRemote))
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Pass, "Not checked in a remote session",
                "Windows Hello for Business is set up and used at the device itself; in a remote session (such as Remote Desktop) Windows does not say whether it can be used.", info: true);
        }
        else if (Yes(dsreg.NgcSet))
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Pass, Required(dsreg, groupPolicy) ? "Set up for you (required by your organization)" : "Set up for you",
                "Windows Hello (a PIN, and a fingerprint or face where the device has one) is set up for your account on this device.");
        }
        else if (Required(dsreg, groupPolicy) && No(dsreg.NgcDeviceEligible))
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Warn, "Required, but this device cannot use it",
                "Your organization requires Windows Hello for Business, and this device does not meet its hardware requirement. Contact your IT support.",
                support: "Windows Hello for Business is required by policy, and dsregcmd says DeviceEligible NO (the device does not meet the hardware requirement, usually the TPM). Check the TPM, or leave this device out of the policy.",
                homeTitle: "Windows Hello can't be used on this PC", homeLine: "Your organization requires it, so your IT support needs to look at this PC.");
        }
        else if (Required(dsreg, groupPolicy))
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Warn, "Required by your organization, not set up for you",
                HelloStep,
                support: "Windows Hello for Business is required by policy and not set up for this user (dsregcmd: NgcSet NO"
                    + (dsreg.NgcPreReqResult.Length > 0 ? ", PreReqResult " + Clean(dsreg.NgcPreReqResult) : "") + "). If the PIN setup is not offered, check the policy's scope and the prerequisites dsregcmd lists.",
                homeTitle: "Set up a PIN for Windows Hello", homeLine: "Your organization requires it: Settings > Accounts > Sign-in options > PIN.");
        }
        else
        {
            row = Row(HealthChecks.WindowsHello, HealthStatus.Pass, "Not set up; not required",
                "Windows Hello is not set up for your account, and your organization does not require it here.", info: true);
        }
        row.ReportDetail = row.Detail + " (" + facts + ")";
        return row;
    }

    /// <summary>The first reason in a remote session (dsregcmd SessionIsNotRemote NO).</summary>
    public const string RemoteSessionHint =
        "Windows Hello is not set up in a remote session (such as Remote Desktop): it is set up when you sign in at the device itself.";

    /// <summary>Required by policy: dsregcmd's PolicyEnabled, or the Group Policy (device or user).</summary>
    private static bool Required(DsregInfo dsreg, bool? groupPolicy) => Yes(dsreg.NgcPolicyEnabled) || groupPolicy == true;

    /// <summary>
    /// The facts for the report, in words (dsregcmd's NgcSet, PolicyEnabled, DeviceEligible and
    /// SessionIsNotRemote, said the right way round): yes or no only, never the key ID.
    /// </summary>
    private static string HelloFacts(DsregInfo dsreg, MachineReadout machine, bool? groupPolicy)
    {
        string Word(string value) => Yes(value) ? "yes" : No(value) ? "no" : "unknown";
        string Inverse(string value) => Yes(value) ? "no" : No(value) ? "yes" : "unknown";
        var parts = new List<string>
        {
            "set up for this user: " + Word(dsreg.NgcSet),
            "key: " + (dsreg.NgcKeyPresent ? "present" : "none"),
            "required by policy: " + Word(dsreg.NgcPolicyEnabled),
            "Group Policy: " + (groupPolicy == true ? "on" : groupPolicy == false ? "off" : "not set"),
            "device meets the requirements: " + Word(dsreg.NgcDeviceEligible),
            "remote session: " + Inverse(dsreg.NgcSessionIsNotRemote),
        };
        if (dsreg.NgcPreReqResult.Length > 0) { parts.Add("prerequisite check: " + Clean(dsreg.NgcPreReqResult)); }
        if (machine?.VirtualMachine == true) { parts.Add("virtual machine"); }
        if (machine?.MultiSession == true) { parts.Add("multi-session host"); }
        return string.Join("; ", parts);
    }
}
