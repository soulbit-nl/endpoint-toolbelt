using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The screenshots of the user guide and the IT support guide (docs/images), rendered from the real
/// window with example data: SoulBit as the organization (its support text and a reserved .example link)
/// and Contoso, Fabrikam and Litware as the apps and windows. Nothing of a real device or person.
/// Runs only when ETB_DOC_SHOTS names a folder; the normal test run skips it at once.
/// </summary>
public class DocScreenshotTests
{
    private const string SupportText = "SoulBit Service Desk - ext. 5555";
    private const string SupportUrl = "https://support.soulbit.example/";

    // The window as it opens, and a larger one for the pages with a lot on them.
    private static readonly double SmallWidth = WindowLayout.DefaultClientWidth;
    private static readonly double SmallHeight = WindowLayout.CompactClientHeight;
    private const double LargeWidth = 900;
    private const double LargeHeight = 720;

    [Fact]
    public void Docs_screenshots_are_made_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("ETB_DOC_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) { return; }
        Directory.CreateDirectory(folder);
        MainWindowPagesTests.OnSta(() =>
        {
            Home(folder);
            Health(folder);
            ChooseProblem(folder);
            Apps(folder);
            Confirm(folder);
            Result(folder);
            Windows(folder);
            ForIt(folder);
            Report(folder);
            Dark(folder);
        });
    }

    private static MainWindow Window(List<HealthResult> health = null)
    {
        var window = MainWindowPagesTests.NewWindow();
        var status = new Protocol.StatusInfo { SupportText = SupportText, SupportUrl = SupportUrl };
        MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, health ?? CompactLayoutTests.Health(), new DsregInfo(), false);
        return window;
    }

    private static void Home(string folder)
    {
        var ok = Window();
        Shot(ok, Path.Combine(folder, "home-ok.png"), SmallWidth, SmallHeight);
        var problem = Window(CompactLayoutTests.Health(warn: HealthChecks.ImeCheckIn));
        Shot(problem, Path.Combine(folder, "home-problem.png"), SmallWidth, SmallHeight);
    }

    private static void Health(string folder)
    {
        var window = Window();
        MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
        Shot(window, Path.Combine(folder, "health-all.png"), SmallWidth, SmallHeight);
    }

    private static void ChooseProblem(string folder)
    {
        var window = Window();
        MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
        Shot(window, Path.Combine(folder, "choose-problem.png"), SmallWidth, SmallHeight);
    }

    /// <summary>Eight apps, two that need attention; the first one opened; then a search.</summary>
    private static void Apps(string folder)
    {
        var window = Window();
        var now = DateTime.UtcNow;
        var apps = new[]
        {
            ("Contoso Agent", "PACKAGING", "0x87D1041C"),
            ("Contoso Vault", "RETRY-HELPS", "0x80070643"),
            ("Contoso Drive Mappings", "OK", ""),
            ("Fabrikam Notes", "OK", ""),
            ("Fabrikam Planner", "OK", ""),
            ("Litware Reader", "OK", ""),
            ("Litware Writer", "OK", ""),
            ("Northwind Viewer", "OK", ""),
        };
        var records = apps.Select((app, i) => new EngineRecord
        {
            Type = "app-diagnosis",
            Utc = now,
            App = new AppEvidence
            {
                DiagnosisId = "d0c50001", AppId = (i + 1).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-2222-3333-4444-555555555555", Name = app.Item1,
                Scope = "device", Intent = "required", Revision = 1, Verdict = app.Item2, Code = app.Item3,
                ObservedText = now.AddMinutes(-20).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            },
        }).ToList();
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = now });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = now });
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "d0c50001", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
        MainWindowPagesTests.Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);
        // As a user gets there: from Choose a problem.
        MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
        MainWindowPagesTests.Call(window, "OpenAppPath");
        Shot(window, Path.Combine(folder, "problem-app.png"), LargeWidth, LargeHeight);

        MainWindowPagesTests.Call(window, "OnAppFind", window, new RoutedEventArgs());
        MainWindowPagesTests.Get<TextBox>(window, "AppSearch").Text = "contoso";
        Shot(window, Path.Combine(folder, "apps-search.png"), SmallWidth, SmallHeight);
    }

    /// <summary>The confirmation of Free up disk space, as the window asks it.</summary>
    private static void Confirm(string folder)
    {
        var dialog = (Window)Activator.CreateInstance(typeof(MessageDialog), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object[] { null, ProductInfo.ShortDisplayName + " - Free up disk space", ActionText.Name(ToolbeltAction.FreeDiskSpace), Confirmations.Text(ToolbeltAction.FreeDiskSpace), null, "_Free up disk space", "_Cancel", false }, null);
        var content = (FrameworkElement)dialog.Content;
        Save(content, Path.Combine(folder, "confirm.png"), 480, double.NaN, dialog.Background);
    }

    /// <summary>A repair's result with View details open: the status, Copy details for IT and the cards.</summary>
    private static void Result(string folder)
    {
        var window = Window();
        MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
        MainWindowPagesTests.Call(window, "StartCheck", "Clear Teams cache");
        MainWindowPagesTests.Call(window, "ShowToolOutcome", UserCaches.ClearTeamsCacheOutcome(TeamsClose.QuitByUser, 4275, 58, 833_754_316L), "ClearTeamsCache");
        MainWindowPagesTests.Call(window, "OpenDrawer");
        Shot(window, Path.Combine(folder, "result.png"), 1180, LargeHeight);
    }

    /// <summary>A window off the screen and three others, all shown.</summary>
    private static void Windows(string folder)
    {
        var window = Window();
        var api = new WindowRescueTests.FakeWindows();
        api.Add(41, "Notepad", "Meeting notes - Notepad", ScreenRect.FromSize(-6000, -6000, 800, 600));
        api.Add(42, "Contoso Editor", "Quarterly plan", ScreenRect.FromSize(100, 100, 900, 700));
        api.Add(43, "Microsoft Edge", "SoulBit intranet - Microsoft Edge", ScreenRect.FromSize(200, 120, 1200, 800));
        api.Add(44, "Windows Explorer", "Downloads - File Explorer", ScreenRect.FromSize(300, 200, 900, 600));
        window.WindowApi = api;
        MainWindowPagesTests.Call(window, "OnProblemWindow", window, new RoutedEventArgs());
        MainWindowPagesTests.Call(window, "OnWindowsToggle", window, new RoutedEventArgs());
        Shot(window, Path.Combine(folder, "window-list.png"), SmallWidth, SmallHeight);
    }

    /// <summary>For IT support with the group Windows & security open.</summary>
    private static void ForIt(string folder)
    {
        var window = Window();
        MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
        var tiles = (System.Collections.IDictionary)MainWindowPagesTests.Field(window, "_tiles");
        var tile = tiles.Keys.Cast<Button>().First(b => (ToolGroup)tiles[b] == ToolGroup.WindowsSecurity);
        MainWindowPagesTests.Call(window, "OnCategoryTile", tile, new RoutedEventArgs());
        Shot(window, Path.Combine(folder, "for-it.png"), SmallWidth, SmallHeight);
    }

    /// <summary>The device report as a text window shows it, for an example device of SoulBit.</summary>
    private static void Report(string folder)
    {
        var facts = new DeviceFacts { ComputerName = "SOULBIT-0042", Serial = "SB0042", Model = "Contoso Laptop 14", OperatingSystem = "Windows 11 Enterprise 25H2 (build 26200.7171)", User = "soulbit.user@soulbit.example", ImeVersion = "1.97.104.0" };
        var dsreg = new DsregInfo { TenantName = "SoulBit", TenantId = "00000000-0000-0000-0000-000000000042", DeviceId = "00000000-0000-0000-0000-000000004200" };
        var history = new[]
        {
            "2026-11-17 09:27:10 +01:00;soulbit.user;ClearTeamsCache;0",
            "2026-11-17 10:02:44 +01:00;soulbit.user;DiagnoseApps;0",
            "2026-11-17 10:05:31 +01:00;soulbit.user;RetryAppsAndPolicies;2|8a2a9a11|info",
            "2026-11-17 10:06:02 +01:00;soulbit.user;SyncClock;0",
        };
        HealthResult R(string name, string detail) => new() { Name = name, Status = HealthStatus.Pass, Detail = detail, Hint = "" };
        var health = new List<HealthResult>
        {
            R("IME service", "Running"),
            R("MDM enrollment", "Enrolled in Intune"),
            R("Last MDM sync", "2026-11-17 09:58 (12 min ago, MDM session log)"),
            R("Last IME check-in", "2026-11-17 09:40 (30 min ago) (IME log)"),
            R("Join type (dsregcmd)", "Microsoft Entra joined, tenant SoulBit"),
            R("Sign-in token (PRT)", "Present"),
            R("Windows Hello for Business (NGC)", "Set up for you"),
            R("MDM certificate", "Expires 2027-08-26 (282 days)"),
            R("Intune connectivity", "manage.microsoft.com answered (HTTP 405)"),
            R("Free disk space", "153.4 GB free on C:"),
            R("Pending restart", "No restart pending"),
            R("Compliance settings (local)", "BitLocker on, Secure Boot on, TPM ready 2.0, firewall on for every network"),
            R("MDM policy errors", "No failed settings in the last 7 days (MDM admin log)"),
            R("Time sync", "Offset +0.207 s to the time server (w32tm)"),
            R("Defender Antivirus", "Running mode normal, real-time protection on"),
        };
        var status = new Protocol.StatusInfo
        {
            SupportText = SupportText, SupportUrl = SupportUrl,
            Readouts = new Protocol.DeviceReadouts
            {
                Machine = new Protocol.MachineReadout { VirtualMachine = false, MultiSession = false },
                IntuneConnection = new Protocol.IntuneConnectionReadout { Tasks = 16, TasksEnabled = 16, LastRunUtc = "2026-11-17T08:26:00Z", LastResult = "0x00000000" },
            },
        };
        var text = DeviceReport.Build(facts, dsreg, health, history, new DateTimeOffset(2026, 11, 17, 10, 10, 0, TimeSpan.FromHours(1)), "1.0.0",
            status, TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));
        var page = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(16),
            Child = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap },
        };
        Save(page, Path.Combine(folder, "device-report.png"), 1000, double.NaN, Brushes.White);
    }

    /// <summary>Home in the dark theme: the same window with the dark brushes.</summary>
    private static void Dark(string folder)
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        var light = resources[0];
        resources[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/Dark.xaml", UriKind.Absolute) };
        try
        {
            var window = Window();
            Shot(window, Path.Combine(folder, "home-dark.png"), SmallWidth, SmallHeight);
        }
        finally
        {
            resources[0] = light;
        }
    }

    /// <summary>The window's content laid out at a size and saved as a PNG.</summary>
    private static void Shot(MainWindow window, string file, double width, double height)
    {
        CompactLayoutTests.Fit(window, width, height);
        Save((FrameworkElement)window.Content, file, width, height, window.Background);
    }

    /// <summary>An element at a width (and a height, or its own), on a background, as a PNG.</summary>
    private static void Save(FrameworkElement element, string file, double width, double height, Brush background)
    {
        element.Measure(new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height));
        var h = double.IsNaN(height) ? Math.Ceiling(element.DesiredSize.Height) : height;
        element.Arrange(new Rect(0, 0, width, h));
        element.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(background ?? (Application.Current.TryFindResource("WindowBackground") as Brush) ?? Brushes.White, null, new Rect(0, 0, width, h));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        // The element itself, at its own place (a brush would crop the margin around what it draws).
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.On };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
