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
}
