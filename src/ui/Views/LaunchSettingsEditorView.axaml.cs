using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The reusable launch-settings editor content (a <see cref="UserControl"/>).
/// Its <c>DataContext</c> is a <see cref="LaunchSettingsEditorViewModel"/>
/// (bound from the host: the Profiles destination binds it to its editor VM).
/// Owns the per-row remove-button forwarding (the established code-behind
/// pattern); all edit state, validation, and value construction live in the
/// (unit-tested) editor VM.
/// </summary>
public partial class LaunchSettingsEditorView : UserControl
{
    public LaunchSettingsEditorView()
    {
        InitializeComponent();
    }

    private LaunchSettingsEditorViewModel? ViewModel => DataContext as LaunchSettingsEditorViewModel;

    // ---- per-row remove buttons -------------------------------------------

    private void RemoveEnv_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is EnvVarRow row)
        {
            ViewModel?.RemoveEnvVarCommand.Execute(row);
        }
    }

    private void RemoveArg_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is GameArgRow row)
        {
            ViewModel?.RemoveGameArgCommand.Execute(row);
        }
    }
}
