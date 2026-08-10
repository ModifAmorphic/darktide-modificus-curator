using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The Nexus Integrations destination content (a <see cref="UserControl"/>).
/// Its <c>DataContext</c> is an <see cref="IntegrationsViewModel"/> (bound from
/// the shell). Nexus-only in v1; the VM owns all auth state + the OAuth/API-key
/// flows. Owns only the API-key help link's browser-open mechanics; everything
/// else binds to the VM.
/// </summary>
/// <remarks>
/// All persistence + network logic lives in the (unit-tested) VM +
/// <c>NexusAuthService</c>; this view is pure mechanics. The shell owns the
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

    /// <summary>
    /// Opens the Nexus API-keys page in the user's default browser. The user
    /// gets their API key from there for the alternative (API-key) auth path.
    /// <c>UseShellExecute = true</c> is correct here (opening a URL via the OS
    /// shell-open), the same pattern the OAuth browser launcher uses.
    /// </summary>
    private void ApiKeyHelp_Click(object? sender, RoutedEventArgs e)
    {
        const string helpUrl = "https://www.nexusmods.com/settings/api-keys";
        try
        {
            Process.Start(new ProcessStartInfo(helpUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: a shell-open failure (no default browser, headless
            // test env) is non-fatal. The button's tooltip carries the URL so
            // the user can copy it manually.
        }
    }
}
