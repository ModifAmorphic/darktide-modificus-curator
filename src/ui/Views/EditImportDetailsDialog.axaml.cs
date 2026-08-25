using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The edit-import-details modal window. Its <c>DataContext</c> is an
/// <see cref="EditImportDetailsViewModel"/> (built by
/// <see cref="Dialogs.IEditImportDetailsFactory"/> and set by
/// <see cref="Dialogs.DialogService"/>). The service reads
/// <see cref="EditImportDetailsViewModel.Result"/> after <c>ShowDialog</c>
/// returns: <c>true</c> means the user saved (the edits were applied through
/// the repository), <c>false</c> means they cancelled (ESC, title-bar close,
/// window close, or the Cancel button; no writes).
/// </summary>
/// <remarks>
/// All edit + validation logic lives in the (unit-tested) VM; this is pure
/// view mechanics. The Save + confirm buttons forward to the VM's commands
/// and close only on a saved result (a refused save keeps the dialog open so
/// the inline failure is readable + correctable). Cancel forwards to the VM's
/// Cancel command (marking the result false) and closes.
/// </remarks>
public partial class EditImportDetailsDialog : Window
{
    public EditImportDetailsDialog()
    {
        InitializeComponent();
    }

    private EditImportDetailsViewModel? ViewModel => DataContext as EditImportDetailsViewModel;

    /// <summary>
    /// Detaches the VM's culture subscription on close so the short-lived
    /// dialog VM is collectable (the localization service is a singleton that
    /// outlives the dialog).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Detach();
        base.OnClosed(e);
    }

    /// <summary>
    /// Save: runs the VM's <see cref="EditImportDetailsViewModel.SaveCommand"/>
    /// (which may swap to the inline confirm step instead of applying) and
    /// closes only when a save actually applied (Result true).
    /// </summary>
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.SaveCommand.Execute(null);
        if (vm.Result)
        {
            Close();
        }
    }

    /// <summary>
    /// The confirm panel's explicit proceed: runs the VM's
    /// <see cref="EditImportDetailsViewModel.ConfirmSaveCommand"/> (applying
    /// the save with older-version removal) and closes only on a saved
    /// result.
    /// </summary>
    private void ConfirmSave_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.ConfirmSaveCommand.Execute(null);
        if (vm.Result)
        {
            Close();
        }
    }

    /// <summary>
    /// Cancel: runs the VM's Cancel command (marks Result false) and closes
    /// without a write.
    /// </summary>
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CancelCommand.Execute(null);
        Close();
    }
}
