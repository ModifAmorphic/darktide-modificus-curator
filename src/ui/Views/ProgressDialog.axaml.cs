using Avalonia.Controls;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// A button-less modal spinner shown while a short async operation is in
/// flight. The caller sets the message via <see cref="SetMessage"/> then
/// awaits <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>; the
/// caller also closes the window from the work's continuation (there is no
/// user affordance to close it: a partial result is useless, so the work
/// cannot be cancelled mid-flight).
/// </summary>
/// <remarks>
/// Reuses the shared <c>DialogTitleBar</c> chrome with its close button hidden
/// (the title bar is the drag region; the user cannot dismiss the spinner).
/// Used by <c>DialogService.ShowProgressAsync</c> (the app self-update
/// download). Mod downloads do not use this dialog: they render as rows on
/// the mod list through the download queue.
/// </remarks>
public partial class ProgressDialog : Window
{
    public ProgressDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory message shown above the spinner.</summary>
    public void SetMessage(string message) => MessageText.Text = message;
}
