using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>What the service sets up itself because Windows Installer cannot (see deployment.md).</summary>
public class InstallSupportTests
{
    [Fact]
    public void The_product_keys_get_a_protected_dacl_for_system_and_administrators_only()
    {
        var security = ProductRegistryKeys.CreateSecurity();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(RegistryRights.FullControl, r.RegistryRights);
            Assert.Equal(InheritanceFlags.ContainerInherit, r.InheritanceFlags);
        });
        Assert.Contains(rules, r => r.IdentityReference.Equals(WellKnownSids.LocalSystem));
        Assert.Contains(rules, r => r.IdentityReference.Equals(WellKnownSids.Administrators));
        Assert.True(ProductRegistryKeys.IsProtected(security));
    }

    [Fact]
    public void An_inherited_or_user_readable_dacl_is_not_protected()
    {
        // What Windows Installer leaves on the root and Config keys: inherited, users may read.
        var open = new RegistrySecurity();
        open.SetSecurityDescriptorSddlForm("O:SYG:SYD:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)");
        Assert.False(ProductRegistryKeys.IsProtected(open));

        var protectedButOpen = new RegistrySecurity();
        protectedButOpen.SetSecurityDescriptorSddlForm("O:SYG:SYD:P(A;CI;KA;;;SY)(A;CI;KA;;;BA)(A;CI;KR;;;BU)");
        Assert.False(ProductRegistryKeys.IsProtected(protectedButOpen));

        var right = new RegistrySecurity();
        right.SetSecurityDescriptorSddlForm("O:SYG:SYD:P(A;CI;KA;;;SY)(A;CI;KA;;;BA)");
        Assert.True(ProductRegistryKeys.IsProtected(right));
    }

    [Fact]
    public void Only_the_product_keys_are_enforced_never_the_policy_key()
    {
        Assert.Equal(new[] { ProductInfo.RegistryRoot, ProductInfo.ConfigKey, ProductInfo.StateKey }, ProductRegistryKeys.Keys);
        Assert.DoesNotContain(ProductRegistryKeys.Keys, k => k.Contains("Policies"));
    }

    [Fact]
    public void Remove_state_uses_the_fixed_system_profile_path_and_accepts_it()
    {
        var root = EndpointToolbelt.Service.UninstallCleanup.ServiceWorkingRoot;
        Assert.EndsWith(@"\System32\config\systemprofile\AppData\Local\EndpointToolbelt", root, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(System.IO.Path.IsPathRooted(root));
        Assert.Null(EndpointToolbelt.Service.UninstallCleanup.CheckWorkingRoot(root, EndpointToolbelt.Service.UninstallCleanup.SystemProfileFolder));
        // What an empty folder API answer would have produced: refused, so nothing is deleted.
        Assert.NotNull(EndpointToolbelt.Service.UninstallCleanup.CheckWorkingRoot("EndpointToolbelt", EndpointToolbelt.Service.UninstallCleanup.SystemProfileFolder));
    }

    private static string RepositoryRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public void A_build_of_the_same_version_replaces_the_installed_one_and_an_older_one_is_refused()
    {
        // Every build has its own product code; test builds keep one version, so the major
        // upgrade must also take a build of the same version, or it installs beside the old one.
        var wix = (System.Xml.Linq.XNamespace)"http://wixtoolset.org/schemas/v4/wxs";
        var package = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(RepositoryRoot(), "installer", "Package.wxs")).Root.Element(wix + "Package");
        Assert.NotNull(package);
        // No fixed product code: WiX gives every build a new one.
        Assert.Null(package.Attribute("ProductCode"));
        var upgrade = Assert.Single(package.Elements(wix + "MajorUpgrade"));
        Assert.Equal("yes", (string)upgrade.Attribute("AllowSameVersionUpgrades"));
        Assert.Equal("afterInstallValidate", (string)upgrade.Attribute("Schedule"));
        Assert.False(string.IsNullOrEmpty((string)upgrade.Attribute("DowngradeErrorMessage")));
        Assert.Null(upgrade.Attribute("AllowDowngrades"));
        // Only the ICE61 warning that this causes is suppressed, next to the two per-user shortcut ICEs.
        var project = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(RepositoryRoot(), "installer", "EndpointToolbelt.Installer.wixproj"));
        var ices = project.Descendants().Single(e => e.Name.LocalName == "SuppressIces").Value.Split(';');
        Assert.Equal(new[] { "ICE43", "ICE57", "ICE61" }, ices);
    }

    [Fact]
    public void The_service_restarts_itself_three_times_and_resets_the_count_after_a_day()
    {
        Assert.Equal(new[] { 60000, 60000, 300000 }, ServiceRecovery.RestartDelaysMs);
        Assert.Equal(86400, ServiceRecovery.ResetPeriodSeconds);
    }

    [Fact]
    public void An_install_a_person_starts_shows_progress_a_finish_screen_and_the_chosen_texts()
    {
        // Core WiX elements only, and the chosen texts.
        var wix = (System.Xml.Linq.XNamespace)"http://wixtoolset.org/schemas/v4/wxs";
        var root = RepositoryRoot();
        var ui = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root, "installer", "UI.wxs"));
        var package = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root, "installer", "Package.wxs"));
        Assert.Equal("EtbUI", (string)Assert.Single(package.Descendants(wix + "UIRef")).Attribute("Id"));
        // No extension namespace in any installer source: no WixUI, no licence page, no WiX branding.
        foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "installer"), "*.wxs"))
        {
            var xml = System.Xml.Linq.XDocument.Load(file);
            Assert.All(xml.Descendants().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration), a => Assert.Equal(wix.NamespaceName, a.Value));
        }
        Assert.Empty(ui.Descendants(wix + "UIRef"));

        var dialogs = ui.Descendants(wix + "Dialog").ToDictionary(d => (string)d.Attribute("Id"));
        string Texts(string dialog) => string.Join("|", dialogs[dialog].Elements(wix + "Control").Select(c => (string)c.Attribute("Text") ?? ""));
        Assert.Contains("Installing Endpoint Toolbelt...", Texts("EtbProgressDlg"));
        Assert.Contains("Endpoint Toolbelt is installed.", Texts("EtbExitDlg"));
        // One sentence and Finish: no check box that starts the app.
        Assert.DoesNotContain(dialogs["EtbExitDlg"].Elements(wix + "Control"), c => (string)c.Attribute("Type") == "CheckBox");
        // Before a first install: the check box for the version check, checked; cleared, Install sets
        // UPDATECHECK=0 for the install to write.
        Assert.Contains("Endpoint Toolbelt is ready to install.", Texts("EtbWelcomeDlg"));
        var box = Assert.Single(dialogs["EtbWelcomeDlg"].Elements(wix + "Control"), c => (string)c.Attribute("Type") == "CheckBox");
        Assert.Equal("&Check for new versions at start-up", (string)box.Attribute("Text"));
        Assert.Equal("ETBUPDATECHECK", (string)box.Attribute("Property"));
        Assert.Equal("1", (string)ui.Descendants(wix + "Property").Single(p => (string)p.Attribute("Id") == "ETBUPDATECHECK").Attribute("Value"));
        var install = dialogs["EtbWelcomeDlg"].Elements(wix + "Control").Single(c => (string)c.Attribute("Id") == "Install");
        // Install writes the choice either way (1 or 0), so the device then has it and the screen does not ask again.
        var choice = install.Elements(wix + "Publish").Where(p => (string)p.Attribute("Property") == "UPDATECHECK").ToDictionary(p => (string)p.Attribute("Value"), p => (string)p.Attribute("Condition"));
        Assert.Equal("NOT ETBUPDATECHECK", choice["0"]);
        Assert.Equal("ETBUPDATECHECK", choice["1"]);
        Assert.Contains("Endpoint Toolbelt [ProductVersion] is already installed.", Texts("EtbAlreadyInstalledDlg"));
        Assert.Contains("Endpoint Toolbelt was not installed.", Texts("EtbFatalErrorDlg"));
        Assert.Contains("Your IT support can find the reason in an install log (see the admin guide).", Texts("EtbFatalErrorDlg"));
        Assert.Contains("FilesInUse", dialogs.Keys);
        // The app open during an upgrade: Windows' Restart Manager closes it when the user agrees.
        var rm = dialogs["MsiRMFilesInUse"].Elements(wix + "Control").Single(c => (string)c.Attribute("Id") == "Close");
        Assert.Contains(rm.Elements(wix + "Publish"), p => (string)p.Attribute("Event") == "RMShutdownAndRestart" && (string)p.Attribute("Value") == "0");

        // Bit on every screen in the pose of the moment, from the bitmaps InstallerArtTests draws.
        var binaries = ui.Descendants(wix + "Binary").ToDictionary(b => (string)b.Attribute("Id"), b => System.IO.Path.GetFileName((string)b.Attribute("SourceFile")));
        Assert.Equal(InstallerArtTests.Art.Select(a => a.File).OrderBy(f => f), binaries.Values.OrderBy(f => f));
        string Bit(string dialog) => binaries[(string)dialogs[dialog].Elements(wix + "Control").Single(c => (string)c.Attribute("Type") == "Bitmap").Attribute("Text")];
        Assert.Equal("bit-working.bmp", Bit("EtbProgressDlg"));
        Assert.Equal("bit-done.bmp", Bit("EtbExitDlg"));
        Assert.Equal("bit-idle.bmp", Bit("EtbWelcomeDlg"));
        Assert.Equal("bit-idle.bmp", Bit("EtbAlreadyInstalledDlg"));
        Assert.Equal("bit-error.bmp", Bit("EtbFatalErrorDlg"));
        Assert.Equal("bit-attention.bmp", Bit("EtbUserExitDlg"));

        // The setup never starts the app (from an elevated prompt it would run elevated): no custom
        // action on the screens, and no DoAction on any of them.
        Assert.Empty(ui.Descendants(wix + "CustomAction"));
        Assert.DoesNotContain(ui.Descendants(wix + "Publish"), p => (string)p.Attribute("Event") == "DoAction");

        // UPDATECHECK: the device's value, written only when given and kept on an upgrade, as every
        // value; secure, so the first screen's choice reaches the elevated install.
        var config = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root, "installer", "Config.wxs"));
        var component = config.Descendants(wix + "Component").Single(c => (string)c.Attribute("Id") == "Config_UpdateCheck");
        Assert.Equal("UPDATECHECK", (string)component.Attribute("Condition"));
        var value = component.Element(wix + "RegistryValue");
        Assert.Equal(@"SOFTWARE\EndpointToolbelt\Config", (string)value.Attribute("Key"));
        Assert.Equal("UpdateCheck", (string)value.Attribute("Name"));
        var updateCheck = config.Descendants(wix + "Property").Single(p => (string)p.Attribute("Id") == "UPDATECHECK");
        Assert.NotNull(updateCheck.Element(wix + "RegistrySearch"));
        Assert.Equal("yes", (string)updateCheck.Attribute("Secure"));
        // With it, the mark that the choice was made, in a key every user can read: the screens run
        // without elevation and cannot read the Config key.
        var asked = config.Descendants(wix + "Component").Single(c => (string)c.Attribute("Id") == "Setup_UpdateCheckAsked");
        Assert.Equal("UPDATECHECK", (string)asked.Attribute("Condition"));
        var askedKey = asked.Element(wix + "RegistryKey");
        Assert.Equal(@"SOFTWARE\EndpointToolbelt\Setup", (string)askedKey.Attribute("Key"));
        Assert.Contains("(A;CI;KR;;;BU)", (string)askedKey.Element(wix + "PermissionEx").Attribute("Sddl"));
        Assert.DoesNotContain("KA;;;BU", (string)askedKey.Element(wix + "PermissionEx").Attribute("Sddl"));
        Assert.Equal("UpdateCheckAsked", (string)askedKey.Element(wix + "RegistryValue").Attribute("Name"));
        var askedSearch = config.Descendants(wix + "Property").Single(p => (string)p.Attribute("Id") == "ETBUPDATECHECKASKED").Element(wix + "RegistrySearch");
        Assert.Equal(@"SOFTWARE\EndpointToolbelt\Setup", (string)askedSearch.Attribute("Key"));

        // The order of the install's screens, and "already installed" only for the same package.
        var shows = ui.Descendants(wix + "InstallUISequence").Single().Elements(wix + "Show").ToDictionary(s => (string)s.Attribute("Dialog"));
        Assert.Equal("success", (string)shows["EtbExitDlg"].Attribute("OnExit"));
        Assert.Equal("EtbProgressDlg", (string)shows["EtbWelcomeDlg"].Attribute("Before"));
        // A first install, and an upgrade of a device where nobody chose yet; not a repair, not a device with the mark.
        Assert.Equal("NOT Installed AND NOT UPDATECHECK AND NOT ETBUPDATECHECKASKED", (string)shows["EtbWelcomeDlg"].Attribute("Condition"));
        Assert.Equal("error", (string)shows["EtbFatalErrorDlg"].Attribute("OnExit"));
        Assert.Equal("NOT ETBALREADYSHOWN", (string)shows["EtbUserExitDlg"].Attribute("Condition"));
        Assert.StartsWith("Installed AND NOT REMOVE AND NOT REINSTALL", (string)shows["EtbAlreadyInstalledDlg"].Attribute("Condition"));

        // An administrative install (msiexec /a) installs nothing: it says "unpacked" and offers no app to start.
        var admin = ui.Descendants(wix + "AdminUISequence").Single().Elements(wix + "Show").ToDictionary(s => (string)s.Attribute("Dialog"));
        Assert.Equal("success", (string)admin["EtbAdminExitDlg"].Attribute("OnExit"));
        Assert.DoesNotContain("EtbExitDlg", admin.Keys);
        Assert.Contains("Endpoint Toolbelt is unpacked.", Texts("EtbAdminExitDlg"));
        Assert.Contains("Nothing was installed on this device.", Texts("EtbAdminExitDlg"));
        Assert.DoesNotContain(dialogs["EtbAdminExitDlg"].Elements(wix + "Control"), c => (string)c.Attribute("Type") == "CheckBox");
        Assert.Contains("Unpacking Endpoint Toolbelt...", Texts("EtbProgressDlg"));
    }
}
