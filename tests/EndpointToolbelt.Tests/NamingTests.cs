using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// People read "Endpoint Toolbelt", the spelling the installed names always had;
/// "for Microsoft Intune" is no longer next to the name in a title, and "Toolbelt" alone is never a
/// name (docs/naming.md).
/// </summary>
public class NamingTests
{
    /// <summary>The retired capital-B spelling.</summary>
    private const string RetiredSpelling = "ToolBelt";

    [Fact]
    public void Display_names_carry_the_spelling_and_the_publisher_line()
    {
        Assert.Equal("Endpoint Toolbelt", ProductInfo.DisplayName);
        Assert.Equal("Endpoint Toolbelt", ProductInfo.ShortDisplayName);
        Assert.Equal("Endpoint Toolbelt by SoulBit", ProductInfo.NameWithPublisher);
        Assert.Equal("Self-service repair for Intune-managed Windows devices", ProductInfo.Description);
        Assert.Equal("Endpoint Toolbelt Service", ProductInfo.ServiceDisplayName);
        Assert.Equal("Microsoft, Intune and Windows are trademarks of the Microsoft group of companies.", ProductInfo.TrademarkNotice);
        Assert.Equal("SoulBit", ProductInfo.Publisher);
        Assert.DoesNotContain("Intune", ProductInfo.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SoulBit", ProductInfo.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_About_says_what_the_product_is_for_without_a_compatibility_sentence()
    {
        // No "Works with Microsoft Intune" (it read as if the product operated Intune) and no IME jargon.
        var body = EndpointToolbelt.App.Ui.MainWindow.AboutBody("1.0.0");
        Assert.StartsWith("Endpoint Toolbelt by SoulBit, version 1.0.0" + Environment.NewLine + "Self-service repair for Intune-managed Windows devices." + Environment.NewLine, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Works with", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Management Extension", body, StringComparison.Ordinal);
        Assert.Contains("is not affiliated with or endorsed by Microsoft.", body);
        Assert.EndsWith(ProductInfo.TrademarkNotice, body, StringComparison.Ordinal);
        // The title bar adds the name once: "Endpoint Toolbelt - About".
        Assert.Equal("About", EndpointToolbelt.App.Ui.MainWindow.AboutHeading);
        // Two links beside OK: the publisher's site (its own redirect for this product) and the source
        // code, both https; they open only on a click. The addresses carry no version or device data.
        var links = EndpointToolbelt.App.Ui.MainWindow.AboutLinks;
        Assert.Equal(new[] { "soulbit.app", "GitHub" }, links.Select(l => l.Text));
        Assert.Equal(new[] { "https://soulbit.app/go/endpoint-toolbelt", "https://github.com/soulbit-nl/endpoint-toolbelt" }, links.Select(l => l.Url));
        Assert.All(links, l => Assert.True(new Uri(l.Url).Query.Length == 0 && new Uri(l.Url).Fragment.Length == 0));
        Assert.All(links, l => Assert.False(string.IsNullOrEmpty(l.Name)));
    }

    [Fact]
    public void Installed_names_did_not_change()
    {
        Assert.Equal("EndpointToolbeltService", ProductInfo.ServiceName);
        Assert.Equal("Endpoint Toolbelt", ProductInfo.EventLogName);
        Assert.Equal("Endpoint Toolbelt", ProductInfo.EventSource);
        Assert.Equal("Endpoint Toolbelt", ProductInfo.InstallFolderName);
        Assert.Equal("EndpointToolbelt", ProductInfo.PipeName);
        Assert.Equal("EndpointToolbelt.Caller", ProductInfo.CallerPipeName);
        Assert.Equal(@"SOFTWARE\EndpointToolbelt", ProductInfo.RegistryRoot);
        Assert.Equal(@"SOFTWARE\Policies\EndpointToolbelt", ProductInfo.PolicyKey);
        Assert.Equal("EndpointToolbelt", ProductInfo.WorkingFolderName);
        Assert.Equal("EndpointToolbelt.Engine.exe", ProductInfo.EngineExecutable);
        Assert.Equal("EndpointToolbelt.log", ProductInfo.EngineLogFileName);
        Assert.Equal("EndpointToolbeltService.log", ProductInfo.ServiceLogFileName);
    }

    [Fact]
    public void The_installer_says_the_name_with_its_publisher_and_keeps_the_installed_names()
    {
        var wxs = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Package.wxs"));
        Assert.Contains(@"<Package Name=""Endpoint Toolbelt""", wxs);
        Assert.Contains(@"Manufacturer=""SoulBit""", wxs);
        Assert.Contains(@"<Property Id=""ARPCOMMENTS"" Value=""Endpoint Toolbelt by SoulBit: self-service repair for Intune-managed Windows devices""", wxs);
        // Programs and Features has the About dialog's two links (packaging tools read them too).
        Assert.Contains($@"<Property Id=""ARPURLINFOABOUT"" Value=""{ProductInfo.ProductsUrl}"" />", wxs);
        Assert.Contains($@"<Property Id=""ARPHELPLINK"" Value=""{ProductInfo.SourceUrl}"" />", wxs);
        Assert.DoesNotContain("for Microsoft Intune", wxs);
        Assert.Contains(@"DisplayName=""Endpoint Toolbelt Service""", wxs);
        Assert.Contains(@"Name=""EndpointToolbeltService""", wxs);
        Assert.Contains(@"<Directory Id=""InstallFolder"" Name=""Endpoint Toolbelt"">", wxs);
        Assert.Contains(@"Services\EventLog\Endpoint Toolbelt""", wxs);
        Assert.Contains(@"<RegistryKey Key=""Endpoint Toolbelt""", wxs);
        Assert.Contains(@"Directory=""ProgramMenuFolder"" Name=""Endpoint Toolbelt""", wxs);
    }

    [Fact]
    public void No_text_uses_the_retired_spelling_or_the_tagline()
    {
        var root = RepositoryRoot();
        // The Sandbox tests are not in every checkout: a folder that does not exist is skipped.
        var files = new[] { "src", "installer", Path.Combine("tools", "sandbox", "e2e") }
            .Where(d => Directory.Exists(Path.Combine(root, d)))
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0
                     && f.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".wxs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase));
        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(RetiredSpelling, StringComparison.Ordinal) >= 0 || lines[i].IndexOf("for Microsoft Intune", StringComparison.Ordinal) >= 0)
                {
                    hits.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }
        Assert.True(hits.Count == 0, "Retired spelling or tagline: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_product_name_is_written_in_ProductInfo_only()
    {
        var root = RepositoryRoot();
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("ProductInfo.cs", StringComparison.Ordinal)
                     && f.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (file: Path.GetFileName(f), line, i)))
            .Where(t => !t.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && (t.line.IndexOf("\"Endpoint Toolbelt", StringComparison.Ordinal) >= 0 || t.line.IndexOf("Endpoint Toolbelt\"", StringComparison.Ordinal) >= 0
                         || t.line.IndexOf(" Endpoint Toolbelt ", StringComparison.Ordinal) >= 0))
            .Select(t => $"{t.file}:{t.i + 1}")
            .ToList();
        Assert.True(hits.Count == 0, "Product-name literal outside ProductInfo: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_README_starts_with_the_name_and_carries_the_notice()
    {
        var readme = File.ReadAllLines(Path.Combine(RepositoryRoot(), "README.md"));
        Assert.Equal("# " + ProductInfo.DisplayName, readme[0]);
        Assert.Contains(readme, l => l.IndexOf(ProductInfo.TrademarkNotice, StringComparison.Ordinal) >= 0);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), "docs", "naming.md")));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.True(dir != null, "Repository root not found.");
        return dir.FullName;
    }
}