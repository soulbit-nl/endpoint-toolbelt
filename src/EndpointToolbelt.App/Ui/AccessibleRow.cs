using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EndpointToolbelt.App.Ui;

/// <summary>
/// A border that UI Automation sees: a health row, a result card or a step. A plain Border has
/// no automation peer, so its AutomationProperties.Name never reached Narrator or other
/// assistive technology, and tabbing to a row announced nothing. This one appears as a list item
/// with its name, help text, keyboard focus, its position in the list ("3 of 10") and
/// scroll-into-view support. A row whose item can open (<see cref="IExpandableRow"/>, a
/// health row) opens and closes with a click, Enter or Space, and is an expandable list item.
/// </summary>
public sealed class AccessibleRow : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleRowAutomationPeer(this);

    private IExpandableRow Expandable => DataContext as IExpandableRow;

    /// <summary>Opens or closes the row's item and tells assistive technology.</summary>
    internal void ToggleOpen()
    {
        if (Expandable is not { } item) { return; }
        var was = item.IsOpen;
        item.Toggle();
        if (UIElementAutomationPeer.FromElement(this) is AutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                was ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                item.IsOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        // A click on the row's own link is handled by the link and never reaches here.
        if (e.Handled || Expandable == null) { return; }
        Focus();
        ToggleOpen();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Only on the row itself, not on its link.
        if (e.Handled || !ReferenceEquals(e.OriginalSource, this) || Expandable == null) { return; }
        if (e.Key is Key.Enter or Key.Space)
        {
            ToggleOpen();
            e.Handled = true;
        }
    }

    /// <summary>The list this row is an item of, and the container that holds the row.</summary>
    internal (ItemsControl List, DependencyObject Container) FindList()
    {
        DependencyObject current = this;
        while (current != null)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent is Panel panel && panel.IsItemsHost)
            {
                return (ItemsControl.GetItemsOwner(panel), current);
            }
            current = parent;
        }
        return (null, null);
    }

    private sealed class AccessibleRowAutomationPeer : FrameworkElementAutomationPeer, IScrollItemProvider, IExpandCollapseProvider
    {
        public AccessibleRowAutomationPeer(AccessibleRow owner) : base(owner) { }

        private AccessibleRow Row => (AccessibleRow)Owner;

        protected override string GetClassNameCore() => nameof(AccessibleRow);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override bool IsKeyboardFocusableCore() => Owner.Focusable;

        // Only what is shown: a row's collapsed fix link or hint is not in the tree.
        protected override System.Collections.Generic.List<AutomationPeer> GetChildrenCore() => ShownPeers.Children(Owner);

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public override object GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.ScrollItem => this,
            PatternInterface.ExpandCollapse when Row.Expandable != null => this,
            _ => base.GetPattern(patternInterface),
        };

        void IScrollItemProvider.ScrollIntoView() => Row.BringIntoView();

        ExpandCollapseState IExpandCollapseProvider.ExpandCollapseState
            => Row.Expandable?.IsOpen == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        void IExpandCollapseProvider.Expand()
        {
            if (Row.Expandable is { IsOpen: false }) { Row.ToggleOpen(); }
        }

        void IExpandCollapseProvider.Collapse()
        {
            if (Row.Expandable is { IsOpen: true }) { Row.ToggleOpen(); }
        }

        protected override int GetPositionInSetCore()
        {
            var (list, container) = Row.FindList();
            if (list == null) { return base.GetPositionInSetCore(); }
            var index = list.ItemContainerGenerator.IndexFromContainer(container);
            return index < 0 ? base.GetPositionInSetCore() : index + 1;
        }

        protected override int GetSizeOfSetCore()
        {
            var (list, _) = Row.FindList();
            return list == null ? base.GetSizeOfSetCore() : list.Items.Count;
        }
    }
}
