using Avalonia.Controls;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The Nexus destination content (a <see cref="UserControl"/>).
/// Its <c>DataContext</c> is an <see cref="IntegrationsViewModel"/> (bound from
/// the shell). Nexus-only in v1; the VM owns all auth state + the OAuth/API-key
/// flows + every interaction, including the API-key help link's browser-open
/// (<see cref="IntegrationsViewModel.OpenApiKeyHelpCommand"/>).
/// </summary>
/// <remarks>
/// All persistence + network logic lives in the (unit-tested) VM +
/// <c>NexusAuthService</c>; this view is pure layout. The shell owns the
/// surrounding chrome and the activation lifecycle
/// (<see cref="IntegrationsViewModel.RefreshAsync"/> on enter,
/// <see cref="IntegrationsViewModel.Deactivate"/> on leave).
/// </remarks>
public partial class IntegrationsView : UserControl
{
    public IntegrationsView()
    {
        InitializeComponent();
    }
}
