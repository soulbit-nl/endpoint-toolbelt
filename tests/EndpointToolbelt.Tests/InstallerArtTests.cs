using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Bit, the app's helper character, on the installer's screens: one still pose
/// per screen, drawn by the app's own <see cref="HelperCharacter"/> in the light theme, so the installer
/// never shows another Bit than the app. When ETB_INSTALLER_ART names a folder (build\build.ps1 sets it
/// to artifacts\staging\installer-art), the bitmaps go there for the MSI; otherwise the test renders them
/// to a temporary folder and checks them.
/// </summary>
public class InstallerArtTests
{
    /// <summary>The installer's screens and Bit's pose on each; the file names UI.wxs reads.</summary>
    internal static readonly (HelperMood Mood, string File)[] Art =
    {
        (HelperMood.Working, "bit-working.bmp"),     // the progress screen
        (HelperMood.Done, "bit-done.bmp"),           // installed, removed
        (HelperMood.Idle, "bit-idle.bmp"),           // already installed
        (HelperMood.Error, "bit-error.bmp"),         // not installed
        (HelperMood.Attention, "bit-attention.bmp"), // cancelled, files in use
    };

    /// <summary>The size of each bitmap; the screens show it at 48 x 48 dialog units.</summary>
    internal const int Size = 96;

    /// <summary>Windows Installer's dialogs have the system's button face colour; the bitmap is opaque on it.</summary>
    private static readonly Color DialogFace = Color.FromRgb(0xF0, 0xF0, 0xF0);

    [Fact]
    public void Bit_is_drawn_for_every_installer_screen_by_the_app_s_own_helper_character()
    {
        var folder = Environment.GetEnvironmentVariable("ETB_INSTALLER_ART");
        var target = string.IsNullOrWhiteSpace(folder) ? Path.Combine(Path.GetTempPath(), "etb-installer-art-" + Guid.NewGuid().ToString("N")) : folder;
        Directory.CreateDirectory(target);
        try
        {
            MainWindowPagesTests.OnSta(() =>
            {
                foreach (var (mood, file) in Art) { Render(mood, Path.Combine(target, file)); }
            });
            using var sha = SHA256.Create();
            var hashes = Art.Select(a =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(target, a.File));
                // A Windows bitmap ("BM"), 24 bits per pixel, Size x Size.
                Assert.Equal((byte)'B', bytes[0]);
                Assert.Equal((byte)'M', bytes[1]);
                Assert.Equal(Size, BitConverter.ToInt32(bytes, 18));
                Assert.Equal(Size, Math.Abs(BitConverter.ToInt32(bytes, 22)));
                Assert.Equal(24, BitConverter.ToInt16(bytes, 28));
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }).ToList();
            // Five different poses, none of them an empty square.
            Assert.Equal(Art.Length, hashes.Distinct().Count());
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(folder)) { Directory.Delete(target, true); }
        }
    }

    private static void Render(HelperMood mood, string file)
    {
        // A still pose: a new helper never animates until the window turns animations on.
        var helper = new HelperCharacter { Mood = mood, Width = Size, Height = Size };
        var host = new Border { Width = Size, Height = Size, Background = new SolidColorBrush(DialogFace), Child = helper };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/Light.xaml", UriKind.Absolute) });
        host.Measure(new Size(Size, Size));
        host.Arrange(new Rect(0, 0, Size, Size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        // Opaque, 24 bits: Windows Installer's Bitmap control draws no transparency.
        var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
