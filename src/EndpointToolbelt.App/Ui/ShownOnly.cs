using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace EndpointToolbelt.App.Ui;

/// <summary>
/// UI Automation children of only what is shown. WPF's automation peers walk the visual tree
/// and also report the controls of a collapsed element (they are then "not control elements"),
/// so the controls of a page that is not shown stayed in the raw tree: a screen reader could meet a
/// control it cannot see, and Axe.Windows reported them (IsControlElementTrueRequiredButtonWPF)
/// when it read the tree while a page was switched. These peers leave out every element that is
/// not Visible, with everything below it.
/// </summary>
internal static class ShownPeers
{
    /// <summary>The peers of <paramref name="owner"/>'s visual subtree as WPF finds them, without collapsed or hidden parts; null when none.</summary>
    public static List<AutomationPeer> Children(UIElement owner)
    {
        var result = new List<AutomationPeer>();
        Collect(owner, result);
        return result.Count == 0 ? null : result;
    }

    /// <summary>The peers of <paramref name="peers"/> whose element is shown within <paramref name="root"/>; null when none.</summary>
    public static List<AutomationPeer> Filter(List<AutomationPeer> peers, UIElement root)
    {
        var shown = (peers ?? new List<AutomationPeer>()).Where(p => p is not UIElementAutomationPeer element || IsShownWithin(element.Owner, root)).ToList();
        return shown.Count == 0 ? null : shown;
    }

    /// <summary>The element and every element between it and <paramref name="root"/> are Visible.</summary>
    public static bool IsShownWithin(UIElement element, UIElement root)
    {
        for (DependencyObject current = element; current != null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) { return false; }
        }
        return true;
    }

    private static void Collect(DependencyObject parent, List<AutomationPeer> result)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement element)
            {
                if (element.Visibility != Visibility.Visible) { continue; }
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer != null)
                {
                    result.Add(peer);
                    continue;
                }
            }
            Collect(child, result);
        }
    }
}

/// <summary>The main window's peer: its children are only what is shown.</summary>
internal sealed class ShownOnlyWindowAutomationPeer : WindowAutomationPeer
{
    public ShownOnlyWindowAutomationPeer(Window owner) : base(owner) { }

    protected override List<AutomationPeer> GetChildrenCore() => ShownPeers.Children(Owner);
}

/// <summary>
/// A scroll viewer whose peer reports only what is shown: the main page area, the app list
/// and the details drawer hold parts that are collapsed while another page or state is shown.
/// </summary>
public sealed class ShownOnlyScrollViewer : ScrollViewer
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ShownOnlyScrollViewerAutomationPeer(this);

    private sealed class ShownOnlyScrollViewerAutomationPeer : ScrollViewerAutomationPeer
    {
        public ShownOnlyScrollViewerAutomationPeer(ScrollViewer owner) : base(owner) { }

        protected override List<AutomationPeer> GetChildrenCore() => ShownPeers.Filter(base.GetChildrenCore(), Owner);
    }
}
