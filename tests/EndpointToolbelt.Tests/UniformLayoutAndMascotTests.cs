using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;
using Xunit.Abstractions;
using Timeline = System.Windows.Media.Animation.Timeline;

namespace EndpointToolbelt.Tests;

/// <summary>
/// One calm, uniform app: one layout for the three tabs at every size (the
/// tabs at the top, one centered column, the mascot at the same place), a calmer Home and at most one
/// dark button per page, and a mascot that lives calmly: its state machine (idle, reactions first,
/// still without animation effects or in high contrast, paused while hidden), tested as logic.
/// </summary>
public class UniformLayoutAndMascotTests
{
    private readonly ITestOutputHelper _output;

    public UniformLayoutAndMascotTests(ITestOutputHelper output) => _output = output;

    private static readonly DateTime T0 = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    // --- The mascot's state machine ------------------------------------------------------------

    private static MascotBrain Moving(int seed = 1)
    {
        var brain = new MascotBrain(new Random(seed));
        Assert.True(brain.SetConditions(windowsAnimations: true, highContrast: false, shown: true, T0));
        return brain;
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void The_mascot_is_still_with_nothing_scheduled_without_animation_effects_in_high_contrast_or_hidden(bool animations, bool highContrast, bool shown)
    {
        var brain = new MascotBrain(new Random(1));
        brain.SetConditions(animations, highContrast, shown, T0);
        Assert.False(brain.Moving);
        Assert.Null(brain.NextDueUtc);
        // Nothing ever becomes due, and done starts no bounce.
        for (var t = T0; t < T0.AddMinutes(5); t = t.AddSeconds(1)) { Assert.Equal((false, MascotAction.None), brain.Tick(t)); }
        Assert.Equal(MascotAction.None, brain.SetMood(HelperMood.Done, T0.AddMinutes(5)));
        Assert.Equal(MascotAction.None, brain.Action);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Idle_it_blinks_every_3_to_6_seconds_and_makes_one_small_action_every_3_to_6_seconds_never_two_at_once(int seed)
    {
        var brain = Moving(seed);
        var blinks = new List<DateTime>();
        var actions = new List<(DateTime At, MascotAction Action)>();
        for (var t = T0; t < T0.AddMinutes(20); t = t.AddMilliseconds(100))
        {
            var before = brain.Action;
            var (blink, start) = brain.Tick(t);
            if (blink) { blinks.Add(t); }
            if (start != MascotAction.None)
            {
                // Never more than one action at a time: a new one only starts when none runs.
                Assert.True(before == MascotAction.None || t >= brain.ActionEndsUtc - MascotBrain.ActionLength);
                actions.Add((t, start));
            }
        }
        var gaps = blinks.Zip(blinks.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
        Assert.NotEmpty(gaps);
        Assert.All(gaps, g => Assert.InRange(g, 2.95, 6.15));
        // Each action starts 3 to 6 seconds after the previous one ended.
        var starts = actions.Select(a => a.At).ToList();
        Assert.InRange((starts[0] - T0).TotalSeconds, 2.95, 6.15);
        Assert.All(starts.Zip(starts.Skip(1), (a, b) => (b - a - MascotBrain.ActionLength).TotalSeconds), g => Assert.InRange(g, 2.95, 6.15));
        // Only the eight idle actions (look left or right, a wave, a tilt, a hop, the bit jumping, a smile, the squash jump), and over 20 minutes each of them.
        Assert.All(actions, a => Assert.Contains(a.Action, new[] { MascotAction.LookLeft, MascotAction.LookRight, MascotAction.Wave, MascotAction.Tilt, MascotAction.Hop, MascotAction.BitHop, MascotAction.Smile, MascotAction.SquashJump }));
        Assert.Equal(8, actions.Select(a => a.Action).Distinct().Count());
    }

    [Fact]
    public void A_click_makes_it_answer_only_when_it_may_move_and_never_on_top_of_another_action()
    {
        // Still (animations off): a click does nothing.
        var still = new MascotBrain(new Random(1));
        Assert.Equal(MascotAction.None, still.Poke(T0));
        // At rest: a hop with happy eyes; a second click during it is ignored.
        var brain = Moving();
        Assert.Equal(MascotAction.Poke, brain.Poke(T0));
        Assert.Equal(MascotAction.None, brain.Poke(T0.AddSeconds(1)));
        // After it ended the next idle action comes a calm pause later, not at once.
        brain.Tick(T0 + MascotBrain.ActionLength);
        Assert.True(brain.NextActionUtc >= T0 + MascotBrain.ActionLength + MascotBrain.ActionMin - TimeSpan.FromMilliseconds(1));
        Assert.Equal(MascotAction.Poke, brain.Poke(T0.AddSeconds(2)));
        // While it works, a click does nothing.
        var busy = Moving();
        busy.SetMood(HelperMood.Working, T0);
        Assert.Equal(MascotAction.None, busy.Poke(T0.AddSeconds(1)));
        // While it asks for attention or shows an error it still answers a click (a puzzled tilt);
        // its mood stays and a second click during the tilt is ignored.
        foreach (var mood in new[] { HelperMood.Attention, HelperMood.Error })
        {
            var worried = Moving();
            worried.SetMood(mood, T0);
            Assert.Equal(MascotAction.Poke, worried.Poke(T0.AddSeconds(1)));
            Assert.Equal(mood, worried.Mood);
            Assert.Equal(MascotAction.None, worried.Poke(T0.AddSeconds(1.5)));
            worried.Tick(T0.AddSeconds(1) + MascotBrain.ActionLength);
            Assert.Equal(MascotAction.None, worried.Action);
        }
        // Done: after its own bounce, a click makes it hop again.
        var done = Moving();
        Assert.Equal(MascotAction.Bounce, done.SetMood(HelperMood.Done, T0));
        Assert.Equal(MascotAction.None, done.Poke(T0.AddMilliseconds(100)));
        done.Tick(T0 + MascotBrain.BounceLength);
        Assert.Equal(MascotAction.Poke, done.Poke(T0 + MascotBrain.BounceLength + TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void A_reaction_stops_an_idle_action_and_only_calm_ones_follow_while_something_is_wrong()
    {
        var brain = Moving();
        // Run until an idle action is going.
        var t = T0;
        while (brain.Action == MascotAction.None) { t = t.AddMilliseconds(100); brain.Tick(t); }
        // Something runs: the working look takes over at once.
        Assert.Equal(MascotAction.None, brain.SetMood(HelperMood.Working, t));
        Assert.Equal(MascotAction.None, brain.Action);
        Assert.Null(brain.NextActionUtc);
        for (var end = t.AddMinutes(2); t < end; t = t.AddMilliseconds(250)) { Assert.Equal(MascotAction.None, brain.Tick(t).Start); }
        // It ended OK: the happy bounce, which is the one action now.
        Assert.Equal(MascotAction.Bounce, brain.SetMood(HelperMood.Done, t));
        Assert.Equal(MascotAction.Bounce, brain.Action);
        brain.Tick(t.AddSeconds(1));
        Assert.Equal(MascotAction.None, brain.Action);
        // The concerned look while the page shows a problem: only calm actions (a look aside, a tilt),
        // never a hop, wave or smile; the same for an error.
        foreach (var worried in new[] { HelperMood.Attention, HelperMood.Error })
        {
            t = t.AddSeconds(2);
            brain.SetMood(worried, t);
            Assert.InRange((brain.NextActionUtc.Value - t).TotalSeconds, 2.99, 6.01);
            var calm = new List<MascotAction>();
            for (var end = t.AddMinutes(2); t < end; t = t.AddMilliseconds(250))
            {
                var start = brain.Tick(t).Start;
                if (start != MascotAction.None) { calm.Add(start); }
            }
            Assert.NotEmpty(calm);
            Assert.All(calm, a => Assert.Contains(a, new[] { MascotAction.LookLeft, MascotAction.LookRight, MascotAction.Tilt }));
            brain.SetMood(HelperMood.Idle, t);
        }
        // Idle again: the next action comes after a calm pause, never at once.
        t = t.AddSeconds(2);
        brain.SetMood(HelperMood.Working, t);
        brain.SetMood(HelperMood.Idle, t);
        Assert.Equal(MascotAction.None, brain.Action);
        Assert.InRange((brain.NextActionUtc.Value - t).TotalSeconds, 2.99, 6.01);
        Assert.Equal(MascotAction.None, brain.Tick(t.AddSeconds(2.9)).Start);
    }

    [Fact]
    public void Hidden_it_pauses_with_nothing_scheduled_and_shown_again_it_starts_over_calmly()
    {
        var brain = Moving();
        var t = T0;
        while (brain.Action == MascotAction.None) { t = t.AddMilliseconds(100); brain.Tick(t); }
        // Minimized: still at once, the action stopped, nothing due.
        Assert.True(brain.SetConditions(true, false, shown: false, t));
        Assert.False(brain.Moving);
        Assert.Equal(MascotAction.None, brain.Action);
        Assert.Null(brain.NextDueUtc);
        // Shown again: a blink and an action are scheduled at their calm distances.
        Assert.True(brain.SetConditions(true, false, shown: true, t.AddMinutes(10)));
        Assert.InRange((brain.NextBlinkUtc.Value - t.AddMinutes(10)).TotalSeconds, 2.99, 6.01);
        Assert.InRange((brain.NextActionUtc.Value - t.AddMinutes(10)).TotalSeconds, 2.99, 6.01);
        // The same conditions again change nothing.
        Assert.False(brain.SetConditions(true, false, true, t.AddMinutes(11)));
    }

    // --- The mascot control: storyboards, never a redraw timer -----------------------------------

    private static IEnumerable<System.Windows.Media.Animation.Timeline> All(System.Windows.Media.Animation.Timeline timeline)
    {
        yield return timeline;
        if (timeline is TimelineGroup group)
        {
            foreach (var child in group.Children.SelectMany(All)) { yield return child; }
        }
    }

    [Fact]
    public void The_mascot_moves_through_storyboards_at_a_low_frame_rate_and_never_flashes()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var mascot = new HelperCharacter(new Random(3)) { Name = "Mascot" };
            Assert.Empty(mascot.Running);
            mascot.AnimationsEnabled = true;
            Assert.True(mascot.IsAnimating);
            // Breathing runs; the next due moment has a one-shot timer, seconds away (not a redraw timer).
            Assert.NotEmpty(mascot.Running);
            Assert.True(mascot.Timer.IsEnabled);
            Assert.True(mascot.Timer.Interval >= MascotBrain.BlinkMin - TimeSpan.FromMilliseconds(100), mascot.Timer.Interval.ToString());
            foreach (var mood in new[] { HelperMood.Working, HelperMood.Done, HelperMood.Attention, HelperMood.Error, HelperMood.Idle })
            {
                mascot.Mood = mood;
                foreach (var board in mascot.Running)
                {
                    // A low frame rate: the window redraws little.
                    Assert.InRange(Timeline.GetDesiredFrameRate(board) ?? 60, 1, HelperCharacter.ActionFrameRate);
                    // WCAG 2.3.1: nothing repeats faster than three times a second.
                    foreach (var part in All(board).Where(p => p.RepeatBehavior == RepeatBehavior.Forever))
                    {
                        Assert.True(part.Duration.HasTimeSpan && part.Duration.TimeSpan.TotalSeconds * (part.AutoReverse ? 2 : 1) >= 0.34, mood + ": " + part.Duration);
                    }
                }
            }
            // Back at rest after every mood: the breathing and the idle bit's float run (the working dots and a reaction ended with their mood).
            Assert.Equal(2, mascot.Running.Count);
            var drift = mascot.Running.SelectMany(All).OfType<DoubleAnimation>().Single(a => a.RepeatBehavior == RepeatBehavior.Forever && a.From == 0);
            // Big enough to see at the size the mascot is shown (48 of its 120 units): at least 2 px.
            Assert.True(Math.Abs(drift.To.Value) * 48 / 120 >= 2, drift.To.Value.ToString());
            // The breathing scale is 1 to 2 %.
            var breath = mascot.Running.SelectMany(All).OfType<DoubleAnimation>().Where(a => a.RepeatBehavior == RepeatBehavior.Forever && a.From == 1).ToList();
            Assert.Equal(2, breath.Count);
            Assert.All(breath, b => Assert.InRange(Math.Abs(b.To.Value - 1), 0.01, 0.02));
            // Each action ends where it started, so a stopped action leaves the still pose.
            foreach (var action in new[] { MascotAction.LookLeft, MascotAction.LookRight, MascotAction.Wave, MascotAction.Tilt, MascotAction.Bounce, MascotAction.Hop, MascotAction.BitHop, MascotAction.Smile, MascotAction.SquashJump, MascotAction.Poke })
            {
                var board = mascot.ActionStoryboard(action);
                Assert.NotEmpty(board.Children);
                // A move or turn ends at 0, a scale (the squash jump) at 1.
                Assert.All(board.Children.OfType<DoubleAnimationUsingKeyFrames>(), k => Assert.Equal(
                    Storyboard.GetTargetProperty(k).PathParameters.OfType<DependencyProperty>().Any(p => p.Name.StartsWith("Scale", StringComparison.Ordinal)) ? 1 : 0, k.KeyFrames[k.KeyFrames.Count - 1].Value));
                Assert.Equal(action == MascotAction.Bounce ? MascotBrain.BounceLength : MascotBrain.ActionLength, board.Children[0].Duration.TimeSpan);
            }
            // Big enough to see at 48 DIP (times 48/120): a look at least 6 DIP, a hop at least 10, a tilt at
            // least 12 degrees, reached within 0.25 s; the calm set (attention, error) about two thirds of that.
            double Peak(MascotAction action) => mascot.ActionStoryboard(action).Children.OfType<DoubleAnimationUsingKeyFrames>()
                .SelectMany(k => k.KeyFrames.Cast<DoubleKeyFrame>()).Max(f => Math.Abs(f.Value));
            Assert.True(Peak(MascotAction.LookLeft) * 48 / 120 >= 6);
            Assert.True(Peak(MascotAction.Hop) * 48 / 120 >= 10);
            Assert.True(Peak(MascotAction.Tilt) >= 12);
            Assert.All(new[] { MascotAction.LookLeft, MascotAction.Tilt, MascotAction.Hop },
                a => Assert.True(mascot.ActionStoryboard(a).Children.OfType<DoubleAnimationUsingKeyFrames>().First().KeyFrames[0].KeyTime.TimeSpan <= TimeSpan.FromSeconds(0.25)));
            Assert.InRange(HelperCharacter.LookCalm / HelperCharacter.LookIdle, 0.5, 0.75);
            Assert.InRange(HelperCharacter.TiltCalm / HelperCharacter.TiltIdle, 0.5, 0.75);
            // The bit sits 8 units below the figure's top; floating and jumping together it must stay inside the
            // mascot's 12 DIP top margin, or it is cut off.
            Assert.True((Peak(MascotAction.BitHop) + Math.Abs(drift.To.Value) - 8) * 48 / 120 <= 12);
            // Done starts the bounce at once (a reaction).
            mascot.Mood = HelperMood.Done;
            Assert.Equal(MascotAction.Bounce, mascot.Brain.Action);
            // Paused (the window minimized or hidden): every storyboard stops and nothing is scheduled.
            mascot.Paused = true;
            Assert.False(mascot.IsAnimating);
            Assert.Empty(mascot.Running);
            Assert.False(mascot.Timer.IsEnabled);
            mascot.Paused = false;
            Assert.True(mascot.IsAnimating);
            // High contrast: still too.
            mascot.HighContrast = true;
            Assert.False(mascot.IsAnimating);
            Assert.Empty(mascot.Running);
            Assert.False(mascot.Timer.IsEnabled);
            mascot.HighContrast = false;
            mascot.AnimationsEnabled = false;
            Assert.Empty(mascot.Running);
        });
    }

    [Fact]
    public void With_Windows_animations_off_a_click_shows_a_still_pose_for_a_moment()
    {
        // A remote session turns Windows animations off by default: a click still answers, without movement.
        MainWindowPagesTests.OnSta(() =>
        {
            var mascot = new HelperCharacter(new Random(5)) { AnimationsEnabled = false, Mood = HelperMood.Attention };
            mascot.Poke();
            Assert.True(mascot.StillPoseShown);
            Assert.Equal(-8, mascot.TiltAngle);
            Assert.Empty(mascot.Running);
            mascot.EndStillPose();
            Assert.False(mascot.StillPoseShown);
            Assert.Equal(0, mascot.TiltAngle);
            // At rest it smiles for a moment; a new mood ends the still pose.
            mascot.Mood = HelperMood.Idle;
            mascot.Poke();
            Assert.True(mascot.StillPoseShown);
            mascot.Mood = HelperMood.Working;
            Assert.False(mascot.StillPoseShown);
            // While it works, a click does nothing.
            mascot.Poke();
            Assert.False(mascot.StillPoseShown);
            Assert.Empty(mascot.Running);
        });
    }

    [Fact]
    public void A_tick_with_a_test_clock_starts_one_action_storyboard_and_ends_it()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var mascot = new HelperCharacter(new Random(5)) { AnimationsEnabled = true };
            var brain = mascot.Brain;
            var t = DateTime.UtcNow;
            while (brain.Action == MascotAction.None) { t = t.AddMilliseconds(250); mascot.Tick(t); }
            var action = mascot.CurrentAction;
            Assert.NotNull(action);
            Assert.Contains(action, mascot.Running);
            mascot.Tick(t.AddSeconds(2));
            Assert.Equal(MascotAction.None, brain.Action);
            Assert.Null(mascot.CurrentAction);
            Assert.DoesNotContain(action, mascot.Running);
            mascot.AnimationsEnabled = false;
        });
    }

    // --- One layout at every size -----------------------------------------------------------------

    private static Rect Box(FrameworkElement element, FrameworkElement root)
        => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>The views of the three tabs (and For IT support's list and detail, and a problem path).</summary>
    private static (string Label, Action Go)[] Views(MainWindow window) => new (string, Action)[]
    {
        ("Home", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false)),
        ("Choose a problem", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false)),
        ("a problem path", () => MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false)),
        ("the app path", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false)),
        ("For IT support", () => MainWindowPagesTests.Call(window, "OnNavIt", window, new RoutedEventArgs())),
        ("For IT support, a group", () => MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<Button>(window, "SignInTile"), new RoutedEventArgs())),
        ("For IT support, a tool", () => MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton("TeamsCacheButton"), true)),
    };

    [Theory]
    [InlineData(540)]
    [InlineData(900)]
    [InlineData(1600)]
    public void Every_tab_has_one_centered_column_the_tabs_on_top_and_the_mascot_at_the_same_place(double width)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.WindowShownOverride = true;
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.Clock), new DsregInfo(), false);
            var outcome = MainWindowPagesTests.Diagnosis("diag0001", DateTime.UtcNow.AddMinutes(-1), "RETRY-HELPS", DateTime.UtcNow.AddHours(-1), "0x80070643");
            MainWindowPagesTests.Call(window, "SetDiagnosis", AppDecisions.From(outcome, DateTime.UtcNow), outcome);
            var root = (FrameworkElement)window.Content;
            var mascot = MainWindowPagesTests.Get<HelperCharacter>(window, "Mascot");
            var column = MainWindowPagesTests.Get<Grid>(window, "ContentGrid");
            var tabs = MainWindowPagesTests.Get<StackPanel>(window, "NavBar");
            var scroll = MainWindowPagesTests.Get<ScrollViewer>(window, "MainScroll");
            Rect? first = null;
            foreach (var (label, go) in Views(window))
            {
                go();
                CompactLayoutTests.Fit(window, width, 800);
                var where = width + " px, " + label;
                // The tabs in one row above the page, at every width.
                Assert.Equal(Orientation.Horizontal, tabs.Orientation);
                Assert.True(Box(tabs, root).Bottom <= Box(scroll, root).Top + 0.5, where + ": the tabs are not above the page");
                var home = Box(MainWindowPagesTests.Get<Button>(window, "HomeNav"), root);
                var it = Box(MainWindowPagesTests.Get<Button>(window, "ItNav"), root);
                Assert.True(Math.Abs(home.Top - it.Top) < 0.5 && it.Left > home.Left, where + ": the tabs are not in one row with For IT support last");
                // One column of at most 900, centered: equal room on both sides of a wide window.
                var box = Box(column, scroll);
                Assert.True(box.Width <= 900.5, where + ": the column is " + box.Width + " wide");
                Assert.True(Math.Abs(box.Left - (scroll.ViewportWidth - box.Right)) <= 1, where + ": the column is not centered");
                // No page shows two panes side by side: the parts of For IT support and the app path stack.
                foreach (var (a, b) in new[] { ("ToolListPanel", "ToolDetail"), ("CategoryTiles", "ToolDetail"), ("AppsListPanel", "AppDetail") })
                {
                    var one = (FrameworkElement)window.FindName(a);
                    var two = (FrameworkElement)window.FindName(b);
                    if (one.AllShown() && two.AllShown()) { Assert.True(Box(two, root).Top >= Box(one, root).Bottom - 0.5, where + ": " + a + " and " + b + " are side by side"); }
                }
                // The mascot: the same box (place and size) on every page, at the top right of the column.
                var m = Box(mascot, root);
                Assert.Equal(HelperRules.Size, m.Width, 1);
                Assert.True(Math.Abs(Box(column, root).Right - 24 - m.Right) <= 1, where + ": the mascot is not at the right of the column");
                first ??= m;
                Assert.True(Math.Abs(first.Value.X - m.X) <= 0.5 && Math.Abs(first.Value.Y - m.Y) <= 0.5, where + ": the mascot moved from " + first + " to " + m);
            }
        });
    }

    [Fact]
    public void Every_page_fits_the_compact_size_at_100_125_and_150_percent()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo { SupportText = "Contoso IT Service Desk - ext. 5555", SupportUrl = "https://support.contoso.example/" }, false,
                CompactLayoutTests.Health(warn: HealthChecks.ImeCheckIn), new DsregInfo(), false);
            foreach (var (label, go) in Views(window).Where(v => v.Label != "the app path"))
            {
                go();
                CompactLayoutTests.AssertFits(window, label);
            }
        });
    }

    // --- A calmer app -----------------------------------------------------------------------------

    /// <summary>The buttons a page shows, by kind: dark (filled), plain (outlined) and links.</summary>
    internal static (int Dark, int Plain, int Link, List<string> Names) CountButtons(MainWindow window)
    {
        var dark = new List<Color>();
        foreach (var key in new[] { "AccentBackground", "DangerBackground" })
        {
            if (window.TryFindResource(key) is SolidColorBrush brush) { dark.Add(brush.Color); }
        }
        var link = window.TryFindResource("LinkButton") as Style;
        int d = 0, p = 0, l = 0;
        var names = new List<string>();
        foreach (var button in Shown<Button>((FrameworkElement)window.Content))
        {
            var isLink = false;
            for (var style = button.Style; style != null; style = style.BasedOn) { isLink |= ReferenceEquals(style, link); }
            string kind;
            if (isLink) { l++; kind = "link"; }
            else if (button.Background is SolidColorBrush fill && dark.Contains(fill.Color)) { d++; kind = "dark"; }
            else { p++; kind = "plain"; }
            var name = AutomationProperties.GetName(button);
            names.Add(kind + ": " + (string.IsNullOrEmpty(name) ? button.Content?.ToString() ?? button.Name : name));
        }
        return (d, p, l, names);
    }

    /// <summary>The elements of a type whose every ancestor is shown.</summary>
    internal static IEnumerable<T> Shown<T>(DependencyObject root) where T : UIElement
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) { continue; }
            if (current is T match) { yield return match; }
            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) { stack.Push(VisualTreeHelper.GetChild(current, i)); }
        }
    }

    /// <summary>The pages of the window in their usual states, each laid out at the compact size.</summary>
    internal static IEnumerable<(string Page, Action Go)> Pages(MainWindow window) => new (string, Action)[]
    {
        ("Home, one finding", () =>
        {
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.ImeCheckIn), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
        }),
        ("Home, all OK", () =>
        {
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
        }),
        ("Choose a problem", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false)),
        ("Sign-in path", () => MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false)),
        ("App path", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false)),
        ("For IT tiles", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false)),
        ("For IT list", () => MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<Button>(window, "SignInTile"), new RoutedEventArgs())),
        ("For IT detail", () => MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton("TeamsCacheButton"), true)),
    };

    [Fact]
    public void Every_page_has_at_most_one_dark_button()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            foreach (var (page, go) in Pages(window))
            {
                go();
                CompactLayoutTests.Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
                var (dark, plain, link, names) = CountButtons(window);
                _output.WriteLine("{0}: {1} buttons ({2} dark, {3} plain, {4} links): {5}", page, dark + plain + link, dark, plain, link, string.Join("; ", names));
                Assert.True(dark <= 1, page + ": " + dark + " dark buttons: " + string.Join("; ", names.Where(n => n.StartsWith("dark", StringComparison.Ordinal))));
            }
        });
    }

    [Theory]
    [InlineData(HealthChecks.ImeService, true)]
    [InlineData(HealthChecks.ImeCheckIn, false)]
    [InlineData(HealthChecks.Enrollment, true)]
    [InlineData(HealthChecks.LastSync, false)]
    [InlineData(HealthChecks.MdmCertificate, true)]
    [InlineData(HealthChecks.Prt, true)]
    [InlineData(HealthChecks.EntraJoined, false)]
    [InlineData(HealthChecks.WindowsHello, false)]
    [InlineData(HealthChecks.Connectivity, true)]
    [InlineData(HealthChecks.Profiles, true)]
    [InlineData(HealthChecks.FreeSpace, false)]
    [InlineData(HealthChecks.PendingRestart, false)]
    [InlineData(HealthChecks.Clock, true)]
    [InlineData(HealthChecks.Defender, false)]
    [InlineData(null, false)]
    public void Homes_first_screen_has_no_technical_words(string check, bool fail)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var health = CompactLayoutTests.Health(warn: fail ? null : check, fail: fail ? check : null);
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, health, new DsregInfo(), false);
            AssertPlainHome(window, check ?? "all OK");
        });
    }

    [Fact]
    public void Homes_first_screen_has_no_technical_words_for_an_app_or_without_the_service()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var now = DateTime.UtcNow;
            foreach (var verdict in new[] { "RETRY-HELPS", "PACKAGING", "UNKNOWN", "DEPENDENCY", "RESTART-FIRST" })
            {
                var outcome = MainWindowPagesTests.Diagnosis("diag0002", now.AddMinutes(-1), verdict, now.AddHours(-1), "0x80070643");
                MainWindowPagesTests.Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
                AssertPlainHome(window, verdict);
            }
            MainWindowPagesTests.Invoke(window, "ApplyHealth", null, true, CompactLayoutTests.Health(), new DsregInfo(), false);
            AssertPlainHome(window, "no service");
        });
    }

    /// <summary>Every text Home shows at the compact size before Show all (its first screen) is free of technical words.</summary>
    private static void AssertPlainHome(MainWindow window, string what)
    {
        CompactLayoutTests.Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
        var home = MainWindowPagesTests.Get<StackPanel>(window, "HomePage");
        Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").Visibility);
        var texts = Shown<TextBlock>(home).Select(t => t.Text).Concat(Shown<AccessText>(home).Select(t => t.Text)).Where(t => !string.IsNullOrEmpty(t)).ToList();
        Assert.Contains(MainWindowPagesTests.Get<TextBlock>(window, "MainFindingTitle").Text, texts);
        var technical = texts.Where(HomeSummary.HasTechnicalWords).ToList();
        Assert.True(technical.Count == 0, what + ": " + string.Join(" | ", technical));
    }

    [Fact]
    public void The_technical_word_check_finds_whole_words_only()
    {
        Assert.True(HomeSummary.HasTechnicalWords("App installer (IME): Problem"));
        Assert.True(HomeSummary.HasTechnicalWords("Intune connection"));
        Assert.True(HomeSummary.HasTechnicalWords("error 0x80070643"));
        Assert.False(HomeSummary.HasTechnicalWords("Your PC needs a restart"));
        Assert.False(HomeSummary.HasTechnicalWords("TIME and PRINTER"));
        Assert.Equal(HomeSummary.CleanTitle, HomeSummary.Build(HealthView.Rows(CompactLayoutTests.Health()), null, false).Title);
    }

    [Fact]
    public void Home_shows_the_health_as_a_count_until_Show_all()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.Clock, fail: HealthChecks.FreeSpace), new DsregInfo(), false);
            var list = MainWindowPagesTests.Get<ItemsControl>(window, "HealthList");
            var toggle = MainWindowPagesTests.Get<Button>(window, "HealthToggle");
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            Assert.Equal("1 problem, 1 warning, 8 OK.", MainWindowPagesTests.Get<TextBlock>(window, "HealthVerdict").Text);
            Assert.Equal("Show all 10", toggle.Content);
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, list.Visibility);
            Assert.Equal(10, list.Items.Count);
            // Show all opens the health view under Home's tab: Back instead of the link, the rest of Home hidden.
            Assert.Equal(Visibility.Collapsed, toggle.Visibility);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "HealthViewHeader").Visibility);
            foreach (var part in new[] { "StatusCard", "StatusButtons", "HomeProblemsHeading", "HomeTiles", "HomePersonLine" })
            {
                Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName(part)).Visibility);
            }
            Assert.Equal("Back to Home", AutomationProperties.GetName(MainWindowPagesTests.Get<Button>(window, "HealthBackButton")));
            // The view's title says Device health; the card shows only its verdict there.
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "HealthCardTitle").Visibility);
            Assert.Equal("Selected", MainWindowPagesTests.Get<Button>(window, "HomeNav").Tag);
            // Back (and Alt+Left or Backspace) shows Home again, with the count.
            Assert.True((bool)MainWindowPagesTests.Invoke(window, "GoBack"));
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Primitives.UniformGrid>(window, "HomeTiles").Visibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<StackPanel>(window, "HealthViewHeader").Visibility);
            // The Home tab from the health view opens Home itself.
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            MainWindowPagesTests.Call(window, "OnNavHome", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Primitives.UniformGrid>(window, "HomeTiles").Visibility);
        });
    }

    [Fact]
    public void A_path_shows_its_checks_first_then_at_most_three_tools_except_one_row_per_Office_app()
    {
        foreach (var path in ProblemPaths.All.Where(p => p.Problem != Problem.OfficeCloses))
        {
            Assert.True(path.Tools.Count <= ProblemPaths.MaxTools, path.Title + ": " + path.Tools.Count + " tools");
        }
        Assert.All(ProblemPaths.Office.Tools, b => Assert.NotNull(ToolCatalog.ByButton(b).Office));
        // The search still finds what the sign-in row's words found before (Sync clock, the connection check).
        Assert.Contains("SyncClockButton", ProblemPaths.Get(Problem.CantSignIn).SearchTools);
        Assert.Contains("ConnectivityButton", ProblemPaths.Get(Problem.CantSignIn).SearchTools);
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            var page = MainWindowPagesTests.Get<StackPanel>(window, "ProblemPage");
            var children = page.Children.Cast<FrameworkElement>().ToList();
            Assert.True(children.IndexOf(MainWindowPagesTests.Get<Border>(window, "ProblemChecksBox")) < children.IndexOf(MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools")));
            Assert.Equal(3, MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").Items.Count);
        });
    }

    [Fact]
    public void Try_every_failed_app_again_is_one_line_with_Why_for_the_explanation()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            var why = MainWindowPagesTests.Get<Button>(window, "RetryWhyLink");
            var caveat = MainWindowPagesTests.Get<TextBlock>(window, "RetryCaveatText");
            Assert.Equal(Visibility.Collapsed, caveat.Visibility);
            Assert.Equal(AppDecisions.RetryCaveat, caveat.Text);
            Assert.Equal("Why?", why.Content);
            MainWindowPagesTests.Call(window, "OnRetryWhy", why, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, caveat.Visibility);
            Assert.StartsWith("Hide why", AutomationProperties.GetName(why));
            MainWindowPagesTests.Call(window, "OnRetryWhy", why, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, caveat.Visibility);
            Assert.Equal("Why: Retry work apps and settings", AutomationProperties.GetName(why));
        });
    }

    [Fact]
    public void For_IT_support_is_the_last_tab_and_says_who_its_tools_are_for()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var tabs = MainWindowPagesTests.Get<StackPanel>(window, "NavBar").Children.Cast<Button>().ToList();
            Assert.Equal("ItNav", tabs.Last().Name);
            Assert.Equal("For IT support", AutomationProperties.GetName(tabs.Last()));
            Assert.Equal(ToolCatalog.ItIntro, MainWindowPagesTests.Get<TextBlock>(window, "ItIntro").Text);
            // A tool's detail opens in place with Back, at every size.
            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton("TeamsCacheButton"), true);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<DockPanel>(window, "ItBackBar").Visibility);
            Assert.Equal("Back to Microsoft 365", AutomationProperties.GetName(MainWindowPagesTests.Get<Button>(window, "ItBackButton")));
            Assert.Equal("For IT support · Microsoft 365", MainWindowPagesTests.Get<TextBlock>(window, "ToolDetailGroup").Text);
            // Opened from a problem page, the detail says which problem suggested it instead of the intro.
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            var row = ((System.Collections.Generic.IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").ItemsSource).First();
            MainWindowPagesTests.Call(window, "OnToolRow", new Button { DataContext = row }, new RoutedEventArgs());
            Assert.Equal("Suggested for: " + ProblemPaths.Get(Problem.CantSignIn).Title + ".", MainWindowPagesTests.Get<TextBlock>(window, "ItIntro").Text);
            // Back to the list: the intro again.
            MainWindowPagesTests.Call(window, "ItBack");
            Assert.Equal(ToolCatalog.ItIntro, MainWindowPagesTests.Get<TextBlock>(window, "ItIntro").Text);
        });
    }

    [Fact]
    public void Only_the_tools_no_problem_page_offers_say_to_use_them_when_IT_support_asks()
    {
        Assert.Equal(new[] { "ClearCacheButton", "LogsButton", "FullResetButton", "PortalButton", "CheckDefenderButton", "BackupKeyButton", "RepairWuButton" },
            ToolCatalog.All.Where(ToolCatalog.ForItOnly).Select(t => t.Button));
        Assert.EndsWith(ToolCatalog.ItOnlyNote, ToolCatalog.ByButton("FullResetButton").Line);
        Assert.DoesNotContain("IT support asks", ToolCatalog.ByButton("TeamsCacheButton").Line);
    }

    // --- One visual language ----------------------------------------------------------------------

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        return File.ReadAllText(Path.Combine(new[] { dir.FullName, "src", "EndpointToolbelt.App", "Ui" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Every_tool_rows_button_label_fits_the_one_fixed_button_column()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            MainWindowPagesTests.NewWindow();
            var app = Application.Current;
            var column = (GridLength)app.FindResource("ToolRunColumnWidth");
            Assert.True(column.IsAbsolute);
            // The column holds the button and its 8 left margin.
            var room = column.Value - 8;
            foreach (var tool in ToolCatalog.All)
            {
                foreach (var key in new[] { "RunButton", "DangerRunButton" })
                {
                    var button = new Button { Style = (Style)app.FindResource(key), Content = tool.ButtonText, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
                    button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    _output.WriteLine(tool.ButtonText + ": " + button.DesiredSize.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(button.DesiredSize.Width <= room, tool.ButtonText + " is " + button.DesiredSize.Width + " wide, the column holds " + room);
                }
            }
        });
    }

    [Fact]
    public void The_shared_values_live_in_the_theme_not_per_page()
    {
        var window = Source("MainWindow.xaml");
        // No corner radius or title size of its own on any page: the theme's values.
        Assert.DoesNotMatch(new Regex("CornerRadius=\"[0-9]", RegexOptions.None, TimeSpan.FromSeconds(1)), window);
        Assert.DoesNotMatch(new Regex("FontSize=\"1[5-9]\"", RegexOptions.None, TimeSpan.FromSeconds(1)), window);
        var styles = Source("Themes", "Styles.xaml");
        Assert.DoesNotContain("x:Key=\"DangerButton\"", styles);
        // Every link to a web page (the support link) looks like the link buttons: underlined only while pointed at.
        Assert.Equal(Regex.Matches(window, "<Hyperlink ").Count, Regex.Matches(window, "<Hyperlink [^>]*Style=\"{StaticResource QuietHyperlink}\"").Count);
        // Home's rows are the same card, only lower (a sized TileButton).
        Assert.Matches(new Regex("x:Key=\"HomeTileButton\"[^>]*BasedOn=\"\\{StaticResource TileButton\\}\"", RegexOptions.None, TimeSpan.FromSeconds(1)), styles);
        // One primary, one secondary and one link style; the rest only size or outline them.
        foreach (var key in new[] { "PrimaryRunButton", "DangerPrimaryRunButton" }) { Assert.Matches(new Regex("x:Key=\"" + key + "\"[^>]*BasedOn=\"\\{StaticResource (PrimaryButton|PrimaryRunButton)\\}\"", RegexOptions.None, TimeSpan.FromSeconds(1)), styles); }
        foreach (var key in new[] { "DangerOutlineButton", "ChoiceButton", "CompactButton", "RunButton" }) { Assert.Matches(new Regex("x:Key=\"" + key + "\"[^>]*BasedOn=\"\\{StaticResource SecondaryButton\\}\"", RegexOptions.None, TimeSpan.FromSeconds(1)), styles); }
        MainWindowPagesTests.OnSta(() =>
        {
            MainWindowPagesTests.NewWindow();
            var app = Application.Current;
            Assert.Equal(900, (double)app.FindResource("ContentMaxWidth"));
            Assert.Equal(HelperRules.Size, (double)app.FindResource("MascotSize"));
            Assert.Equal(new[] { 4.0, 8, 12, 16, 24 }, new[] { "SpaceXS", "SpaceS", "SpaceM", "SpaceL", "SpaceXL" }.Select(k => (double)app.FindResource(k)));
            Assert.Equal(new Thickness(0, 0, (double)app.FindResource("MascotSize") + (double)app.FindResource("SpaceM"), 0), (Thickness)app.FindResource("BesideMascot"));
            Assert.Equal(app.FindResource("CardRadius"), ((Style)app.FindResource("Card")).Setters.OfType<Setter>().Single(s => s.Property == Border.CornerRadiusProperty).Value);
        });
    }

    [Fact]
    public void Every_check_box_takes_the_themes_text_colour()
    {
        // WPF's own check box style sets the system text colour: without a theme brush the label is dark
        // grey on the dark theme's background.
        var folder = Path.Combine(EngineHarness.RepositoryRoot(), "src", "EndpointToolbelt.App");
        var boxes = Directory.GetFiles(folder, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"<CheckBox\b[^>]*>").Cast<Match>().Select(m => (file, tag: m.Value)))
            .ToList();
        Assert.NotEmpty(boxes);
        Assert.All(boxes, b => Assert.Matches(@"Foreground=""\{DynamicResource \w+\}""", b.tag));
    }
}

internal static class VisibilityExtensions
{
    /// <summary>The element and every ancestor are Visible (a window that is never shown has IsVisible false).</summary>
    public static bool AllShown(this FrameworkElement element)
    {
        for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) { return false; }
        }
        return true;
    }
}
