using Avalonia.Controls;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The Preferences destination content (a <see cref="UserControl"/>). Its
/// <c>DataContext</c> is a <see cref="ViewModels.PreferencesViewModel"/> (bound
/// from the shell). Each control applies + persists immediately through the VM;
/// the shell owns the surrounding chrome (header, navigation rail, status
/// strip).
/// </summary>
public partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();
    }
}
