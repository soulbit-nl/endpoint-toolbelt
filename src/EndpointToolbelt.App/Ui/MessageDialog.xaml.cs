using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace EndpointToolbelt.App.Ui;

/// <summary>A confirmation or a message: Enter confirms, Esc cancels.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(Window owner, string title, string heading, string body, string warning, string confirmText, string cancelText, bool destructive)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        // Never taller than the screen, so the buttons stay reachable on a small display.
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 40);
        HeadingText.Text = heading;
        BodyText.Text = body;
        if (!string.IsNullOrEmpty(warning))
        {
            WarningText.Text = warning;
            WarningBlock.Visibility = Visibility.Visible;
        }
        ConfirmButton.Content = confirmText;
        AutomationProperties.SetName(ConfirmButton, confirmText.Replace("_", ""));
        if (cancelText == null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.IsCancel = true;
            ConfirmButton.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            CancelButton.Content = cancelText;
        }
        // For an action that changes the whole device, Enter cancels: it must be chosen on purpose.
        var focus = destructive && cancelText != null ? CancelButton : ConfirmButton;
        if (destructive && cancelText != null)
        {
            ConfirmButton.IsDefault = false;
            CancelButton.IsDefault = true;
        }
        AutomationProperties.SetHelpText(this, body ?? "");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        // Opened with the mouse, the button that gets the focus draws no ring until a key is pressed;
        // the dialog's last input (a key or a click) goes back to the window when it closes.
        var main = owner as MainWindow;
        _keyboard = main?.KeyboardCues ?? true;
        if (!_keyboard) { QuietFocus(focus); }
        AddHandler(PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
        {
            if (!MainWindow.IsNavigationKey(e)) { return; }
            _keyboard = true;
            RestoreFocus();
        }), handledEventsToo: true);
        PreviewMouseDown += (_, _) => _keyboard = false;
        Closed += (_, _) => main?.SetKeyboardCues(_keyboard);
        Loaded += (_, _) => focus.Focus();
    }

    private bool _keyboard;

    private FrameworkElement _quiet;

    /// <summary>The element keeps the focus without its ring until a key is pressed.</summary>
    internal void QuietFocus(FrameworkElement element)
    {
        _quiet = element;
        element.FocusVisualStyle = null;
    }

    private void RestoreFocus()
    {
        _quiet?.ClearValue(FocusVisualStyleProperty);
        _quiet = null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private bool _alternate;

    private void OnAlternate(object sender, RoutedEventArgs e)
    {
        _alternate = true;
        DialogResult = true;
    }

    /// <summary>
    /// A three-way question: the confirm button (the default, Enter), a second way forward, and
    /// Cancel (Esc). Returns 1 for confirm, 2 for the second way, 0 for Cancel or a closed window.
    /// </summary>
    public static int Choose(Window owner, string heading, string body, string warning, string confirmText, string alternateText)
    {
        var dialog = new MessageDialog(owner, ProductInfo.ShortDisplayName + " - " + heading, heading, body, warning, confirmText, "_Cancel", false);
        dialog.AlternateButton.Content = alternateText;
        AutomationProperties.SetName(dialog.AlternateButton, alternateText.Replace("_", ""));
        dialog.AlternateButton.Visibility = Visibility.Visible;
        if (dialog.ShowDialog() != true) { return 0; }
        return dialog._alternate ? 2 : 1;
    }

    /// <summary>Asks to confirm; true when the user confirmed. A destructive confirmation has Cancel as its default.</summary>
    public static bool Confirm(Window owner, string heading, string body, string warning, string confirmText, bool destructive = false)
        => new MessageDialog(owner, ProductInfo.ShortDisplayName + " - " + heading, heading, body, warning, confirmText, "_Cancel", destructive).ShowDialog() == true;

    /// <summary>Shows a message with one button, and web links beside it when given (the About dialog).</summary>
    public static void Show(Window owner, string heading, string body, IReadOnlyList<WebLink> links = null)
    {
        var dialog = new MessageDialog(owner, ProductInfo.ShortDisplayName + " - " + heading, heading, body, null, "_OK", null, false);
        foreach (var link in links ?? Array.Empty<WebLink>())
        {
            var button = new System.Windows.Controls.Button { Content = link.Text, Margin = new Thickness(0, 0, 16, 8), VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(button, link.Name);
            AutomationProperties.SetHelpText(button, "Opens " + link.Url + " in your browser.");
            button.ToolTip = link.Url;
            button.Click += (_, _) => WebLink.Open(link.Url);
            dialog.LinksPanel.Children.Add(button);
        }
        dialog.LinksPanel.Visibility = dialog.LinksPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        dialog.ShowDialog();
    }
}

/// <summary>A web link in a dialog: its visible text, its name for assistive technology and its https address.</summary>
public sealed class WebLink
{
    public WebLink(string text, string name, string url)
    {
        Text = text;
        Name = name;
        Url = url;
    }

    public string Text { get; }
    public string Name { get; }
    public string Url { get; }

    /// <summary>Opens an https address in the default browser; anything else is ignored.</summary>
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) { return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
