using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// For IT's six tiles each have an icon in a colour of their own: never a result colour (green, amber,
/// red), at least 3:1 against the tile in the light and the dark theme, and the system's text colour in
/// high contrast. The colour only adds to the tile's name; nothing is said by colour alone.
/// </summary>
public class TileColourTests
{
    internal static readonly string[] Keys = { "TileIconApps", "TileIconSignIn", "TileIconMicrosoft365", "TileIconWindows", "TileIconDefender", "TileIconNetwork" };

    private static ResourceDictionary Theme(string name)
    {
        MainWindowPagesTests.NewWindow();
        return new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/" + name + ".xaml", UriKind.Absolute) };
    }

    private static Color ColorOf(ResourceDictionary theme, string key) => ((SolidColorBrush)theme[key]).Color;

    /// <summary>The WCAG contrast ratio of two colours.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The hue of a colour in degrees (0 red, 120 green, 240 blue).</summary>
    internal static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0) { return 0; }
        var h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Each_tile_icon_has_a_colour_of_its_own_never_a_result_colour_and_at_least_3_to_1_against_the_tile(string name)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var theme = Theme(name);
            var tile = ColorOf(theme, "SurfaceBackground");
            var colours = Keys.Select(k => ColorOf(theme, k)).ToList();
            Assert.Equal(Keys.Length, colours.Distinct().Count());
            foreach (var tone in new[] { "ToneSuccess", "ToneWarning", "ToneError" }) { Assert.DoesNotContain(ColorOf(theme, tone), colours); }
            foreach (var key in Keys)
            {
                var ratio = Contrast(ColorOf(theme, key), tile);
                Assert.True(ratio >= 3.0, name + " " + key + ": " + ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":1 against the tile");
                // Hue far from green, amber and red: blue to pink only (180 to 340 degrees).
                Assert.InRange(Hue(ColorOf(theme, key)), 180.0, 340.0);
            }
        });
    }

    [Fact]
    public void High_contrast_keeps_the_systems_text_colour_for_the_tile_icons()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var theme = Theme("HighContrast");
            Assert.All(Keys, k => Assert.Equal(SystemColors.WindowTextColor, ColorOf(theme, k)));
        });
    }
}
