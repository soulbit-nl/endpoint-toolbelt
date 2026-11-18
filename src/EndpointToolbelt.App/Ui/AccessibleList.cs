using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace EndpointToolbelt.App.Ui;

/// <summary>
/// An ItemsControl that UI Automation sees as a named list (the health checks, the result cards,
/// the steps). A plain ItemsControl has no automation peer, so its rows appeared loose under the
/// window and a screen reader could not announce the list by its name. Its rows are
/// <see cref="AccessibleRow"/> list items.
/// </summary>
public sealed class AccessibleList : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleListAutomationPeer(this);

    private sealed class AccessibleListAutomationPeer : FrameworkElementAutomationPeer
    {
        public AccessibleListAutomationPeer(AccessibleList owner) : base(owner) { }

        protected override string GetClassNameCore() => nameof(AccessibleList);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
