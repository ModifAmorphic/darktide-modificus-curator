using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.Dialogs;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The dedicated unsaved-changes modal. Caller sets the message via
/// <see cref="SetMessage"/> and the save-enabled flag via <see cref="CanSave"/>
/// before awaiting <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>;
/// <see cref="Result"/> holds the outcome. <see cref="UnsavedChangesChoice.Cancel"/>
/// is the default, so ESC (via <c>EscapeClosesBehavior</c>), the title-bar close
/// button, and a window close all behave the same as the explicit Cancel button.
/// </summary>
/// <remarks>
/// <para>
/// Not a generic N-button dialog: the three choices have distinct semantics
/// (Save runs the caller's save core, Don't save reloads authority, Cancel
/// preserves the staged state), and the optional disabled-Save explanation is
/// specific to this prompt. Parameterizing <c>ConfirmAsync</c> into a generic
/// button list would force every binary caller to ignore unrelated parameters
/// and would couple the prompt's tailored framing to a kitchen-sink API.</para>
/// <para>
/// <see cref="CanSave"/> gates the Save button's <c>IsEnabled</c> and shows a
/// concise localized explanation beneath the buttons when false (so the disabled
/// action is not mysterious). Applied in <see cref="OnOpened"/> after the named
/// controls are realized.</para>
/// </remarks>
public partial class UnsavedChangesDialog : Window
{
    /// <summary>
    /// The user's choice. Defaults to <see cref="UnsavedChangesChoice.Cancel"/>
    /// so ESC / title-bar close / window close (which never run a Click handler)
    /// return Cancel via the field's initial value.
    /// </summary>
    public UnsavedChangesChoice Result { get; private set; } = UnsavedChangesChoice.Cancel;

    /// <summary>
    /// Whether the Save button may be enabled. Default <c>true</c>. When
    /// <c>false</c> the Save button is disabled and the concise
    /// <c>Unsaved_SaveUnavailable</c> explanation is shown beneath the buttons
    /// so the disabled action is not mysterious. Set before
    /// <c>ShowDialog</c>; applied to the button + explanation on
    /// <see cref="OnOpened"/>.
    /// </summary>
    public bool CanSave { get; set; } = true;

    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory prompt body above the buttons.</summary>
    public void SetMessage(string message) => MessageText.Text = message;

    /// <summary>
    /// Applies <see cref="CanSave"/> to the Save button + the unavailable
    /// explanation once the window is open + its content is realized. The named
    /// controls are part of the XAML content, so they are resolved here rather
    /// than in the constructor before the dialog is shown.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (this.GetControl<Button>("SaveButton") is { } save)
        {
            save.IsEnabled = CanSave;
        }
        if (this.GetControl<TextBlock>("SaveUnavailableText") is { } unavailable)
        {
            unavailable.IsVisible = !CanSave;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Result = UnsavedChangesChoice.Save;
        Close();
    }

    private void DontSave_Click(object? sender, RoutedEventArgs e)
    {
        Result = UnsavedChangesChoice.DontSave;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Already the field default; set explicitly for clarity.
        Result = UnsavedChangesChoice.Cancel;
        Close();
    }
}
