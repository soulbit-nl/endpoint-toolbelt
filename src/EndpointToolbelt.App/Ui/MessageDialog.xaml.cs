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

    private Func<System.Threading.Tasks.Task<string>> _action;
    private string _runningText = "";
    private string _linkUrl;
    private Func<bool> _linkShown;

    private void OnActionLink(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_linkUrl)) { WebLink.Open(_linkUrl); }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_action == null) { return; }
        ActionButton.IsEnabled = false;
        // Hidden while empty: an empty text has no size, which UI Automation reports.
        ActionResult.Visibility = Visibility.Visible;
        ActionResult.Text = _runningText;
        string answer;
        try { answer = await _action(); }
        catch (Exception) { answer = "That did not work. Try again."; }
        ActionResult.Text = answer ?? "";
        ActionButton.IsEnabled = true;
        ActionLink.Visibility = _linkShown != null && _linkShown() ? Visibility.Visible : Visibility.Collapsed;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(ActionResult) ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ActionResult);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

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
        => Show(owner, heading, body, links, null, false);

    /// <summary>
    /// Shows a message with one button, web links, and one check box when <paramref name="option"/> is
    /// given; returns the check box's state when the dialog closes (the given state without one).
    /// </summary>
    public static bool Show(Window owner, string heading, string body, IReadOnlyList<WebLink> links, string option, bool optionChecked)
        => Show(owner, heading, body, links, option, optionChecked, null, null, null);

    /// <summary>
    /// As above, with one action button when <paramref name="actionText"/> is given (About: Check now):
    /// while it runs the button is off and <paramref name="runningText"/> shows; its answer then shows
    /// beside it and is read out once.
    /// </summary>
    public static bool Show(Window owner, string heading, string body, IReadOnlyList<WebLink> links, string option, bool optionChecked,
        string actionText, string runningText, Func<System.Threading.Tasks.Task<string>> action)
        => Show(owner, heading, body, links, option, optionChecked, actionText, runningText, action, null, null, null);

    /// <summary>
    /// As above, with a link beside the action's answer when <paramref name="linkShown"/> says so after
    /// the action (About: Release notes once Check now found a newer version); it opens the browser.
    /// </summary>
    public static bool Show(Window owner, string heading, string body, IReadOnlyList<WebLink> links, string option, bool optionChecked,
        string actionText, string runningText, Func<System.Threading.Tasks.Task<string>> action, string linkText, string linkUrl, Func<bool> linkShown)
    {
        var dialog = new MessageDialog(owner, ProductInfo.ShortDisplayName + " - " + heading, heading, body, null, "_OK", null, false);
        if (!string.IsNullOrEmpty(actionText) && action != null)
        {
            dialog.ActionButton.Content = actionText;
            AutomationProperties.SetName(dialog.ActionButton, actionText.Replace("_", ""));
            dialog._action = action;
            dialog._runningText = runningText ?? "";
            if (!string.IsNullOrEmpty(linkText) && !string.IsNullOrEmpty(linkUrl) && linkShown != null)
            {
                dialog.ActionLink.Content = linkText;
                dialog.ActionLink.ToolTip = linkUrl;
                AutomationProperties.SetName(dialog.ActionLink, linkText);
                AutomationProperties.SetHelpText(dialog.ActionLink, "Opens " + linkUrl + " in your browser.");
                dialog._linkUrl = linkUrl;
                dialog._linkShown = linkShown;
            }
            dialog.ActionPanel.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(option))
        {
            dialog.OptionCheck.Content = option;
            AutomationProperties.SetName(dialog.OptionCheck, option.Replace("_", ""));
            dialog.OptionCheck.IsChecked = optionChecked;
            dialog.OptionCheck.Visibility = Visibility.Visible;
        }
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
        return dialog.OptionCheck.Visibility == Visibility.Visible ? dialog.OptionCheck.IsChecked == true : optionChecked;
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
