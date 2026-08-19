using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.Dialogs;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The game-dir conflict modal: a foreign entry occupies the game-dir
/// <c>mods</c> slot, and the user decides between letting Curator take over
/// (rename aside, nothing deleted), keeping their current setup (the external
/// hosting preference), or cancelling the launch. Caller sets the message via
/// <see cref="SetMessage"/> before awaiting
/// <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>; the title is
/// supplied by <c>DialogService</c>. <see cref="GameDirConflictChoice.Cancel"/>
/// is the default, so ESC (via <c>EscapeClosesBehavior</c>), the title-bar
/// close button, and a window close all behave the same as the explicit Cancel
/// button.
/// </summary>
public partial class GameDirConflictDialog : Window
{
    /// <summary>
    /// The user's choice. Defaults to <see cref="GameDirConflictChoice.Cancel"/>
    /// so ESC / title-bar close / window close (which never run a Click
    /// handler) return Cancel via the field's initial value.
    /// </summary>
    public GameDirConflictChoice Result { get; private set; } = GameDirConflictChoice.Cancel;

    public GameDirConflictDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory prompt body above the buttons.</summary>
    public void SetMessage(string message) => MessageText.Text = message;

    private void Proceed_Click(object? sender, RoutedEventArgs e)
    {
        Result = GameDirConflictChoice.Proceed;
        Close();
    }

    private void KeepSetup_Click(object? sender, RoutedEventArgs e)
    {
        Result = GameDirConflictChoice.KeepCurrentSetup;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Already the field default; set explicitly for clarity.
        Result = GameDirConflictChoice.Cancel;
        Close();
    }
}
