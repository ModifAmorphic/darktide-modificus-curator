using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The Profiles destination content (a <see cref="UserControl"/>). Its
/// <c>DataContext</c> is a <see cref="ProfilesViewModel"/> (bound from the
/// shell). Owns only view mechanics: closing the picker flyout after a
/// selection. All state + service calls stay in the (unit-tested) VM; the
/// action-row + footer buttons bind directly to the VM's commands (Avalonia's
/// <c>Button</c> auto-wires <c>ICommand.CanExecute</c>).
/// </summary>
/// <remarks>
/// <para><b>Flyout mechanics:</b> the banner button + the Select-a-profile
/// affordance each host an Avalonia <c>Flyout</c> (<c>Button.Flyout</c> opens it
/// on click automatically, no code-behind needed to open). Selecting a profile
/// row inside a flyout routes through <see cref="PickerRow_Click"/> to the VM's
/// <c>SelectProfileCommand</c> (awaited so the authoritative reload lands before
/// the flyout closes), then hides the hosting flyout so the user sees the new
/// active profile immediately. Closing is the only flyout concern here; opening
/// is the framework's.</para>
/// </remarks>
public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private ProfilesViewModel? ViewModel => DataContext as ProfilesViewModel;

    /// <summary>
    /// Selects a persisted profile from the picker: routes the row's
    /// <see cref="ProfileChoice"/> to the VM's
    /// <c>SelectProfileCommand</c>, then hides the hosting flyout so the user
    /// sees the new active profile immediately. The command is awaited so the
    /// authoritative reload (and any discard confirmation) lands before the
    /// flyout closes.
    /// </summary>
    private async void PickerRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ProfileChoice choice && ViewModel is { } vm)
        {
            await vm.SelectProfileCommand.ExecuteAsync(choice);
        }

        // Hide whichever host button's flyout is showing. The banner + the
        // Select affordance are mutually exclusive (banner XOR affordance), so
        // at most one is open; calling Hide on the other's flyout is a harmless
        // no-op when not open.
        BannerButton?.Flyout?.Hide();
        SelectAffordanceButton?.Flyout?.Hide();
    }
}
