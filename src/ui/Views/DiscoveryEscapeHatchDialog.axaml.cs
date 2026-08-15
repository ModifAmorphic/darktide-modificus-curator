using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The discovery escape-hatch modal window. Its <c>DataContext</c> is a
/// <see cref="DiscoveryEscapeHatchViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). The dialog reads
/// <see cref="DiscoveryEscapeHatchViewModel.Result"/> after <c>ShowDialog</c>
/// returns: <c>true</c> means the user submitted (the entered paths are now
/// persisted), <c>false</c> means they cancelled (no writes). No auto-retry:
/// the user clicks Launch again to retry after submitting.
/// </summary>
/// <remarks>
/// All persistence logic lives in the (unit-tested) VM; this is pure view
/// mechanics. The browse button opens the row's matching picker (folder / file),
/// seeded with the row's current value as the start location, and sets the
/// row's <c>Value</c> directly. Submit + Cancel forward to the VM's matching
/// commands and close.
/// </remarks>
public partial class DiscoveryEscapeHatchDialog : Window
{
    public DiscoveryEscapeHatchDialog()
    {
        InitializeComponent();
    }

    private DiscoveryEscapeHatchViewModel? ViewModel => DataContext as DiscoveryEscapeHatchViewModel;

    /// <summary>
    /// Detaches the VM's subscriptions on close so the short-lived dialog VM is
    /// collectable (the localization service is a singleton that outlives the
    /// dialog).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Detach();
        base.OnClosed(e);
    }

    /// <summary>
    /// Submit: runs the VM's <see cref="DiscoveryEscapeHatchViewModel.SubmitCommand"/>
    /// (which marks <see cref="DiscoveryEscapeHatchViewModel.Result"/> true after
    /// persisting the entered paths) and closes.
    /// </summary>
    private void Submit_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SubmitCommand.Execute(null);
        Close();
    }

    /// <summary>Cancel: runs the VM's Cancel command (marks Result false) and
    /// closes without a write.</summary>
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CancelCommand.Execute(null);
        Close();
    }

    /// <summary>
    /// A row's Browse button. The button carries the field's
    /// <see cref="DiscoveryBrowseKind"/> as its <c>CommandParameter</c>; this
    /// opens the matching picker, takes the first selection, and sets the row's
    /// <c>Value</c>. No-op inside a Steam Deck Gaming Mode session (pickers are
    /// unusable there; the disabled button is the first gate, this covers a
    /// programmatic or stale-click invocation).
    /// </summary>
    /// <remarks>
    /// The picker's <c>SuggestedStartLocation</c> is derived from the row's
    /// current <c>Value</c> so the picker opens where the user already is (for a
    /// file-kind row, the file's parent directory). A null/invalid path falls
    /// back to the system default location, so no error handling is needed.
    /// </remarks>
    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b
            || b.DataContext is not DiscoveryFieldRowViewModel row)
        {
            return;
        }

        if (ViewModel?.IsGamingMode is true)
        {
            return;
        }

        var kind = b.CommandParameter is DiscoveryBrowseKind k
            ? k
            : row.Field.BrowseKind;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        string? picked;
        if (kind == DiscoveryBrowseKind.File)
        {
            var options = new FilePickerOpenOptions { AllowMultiple = false };
            options.SuggestedStartLocation = await ResolveStartLocation(
                topLevel.StorageProvider, row.Value, inputIsFile: true);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
            picked = files is { Count: > 0 } ? files[0].Path.LocalPath : null;
        }
        else
        {
            var options = new FolderPickerOpenOptions { AllowMultiple = false };
            options.SuggestedStartLocation = await ResolveStartLocation(
                topLevel.StorageProvider, row.Value, inputIsFile: false);
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
            picked = folders is { Count: > 0 } ? folders[0].Path.LocalPath : null;
        }

        if (!string.IsNullOrEmpty(picked))
        {
            row.Value = picked;
        }
    }

    /// <summary>
    /// Resolves a storage folder for a picker's <c>SuggestedStartLocation</c>
    /// from the row's current input value. For a file-kind row the input is a
    /// file path, so its parent directory is used. Returns null (picker falls
    /// back to the system default) for an empty, whitespace, or non-existent
    /// path; <see cref="StorageProviderExtensions.TryGetFolderFromPathAsync"/>
    /// is null-safe by construction.
    /// </summary>
    private static async Task<IStorageFolder?> ResolveStartLocation(
        IStorageProvider provider,
        string? input,
        bool inputIsFile)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var startPath = inputIsFile
            ? (Path.GetDirectoryName(input) ?? input)
            : input;
        return await provider.TryGetFolderFromPathAsync(startPath);
    }
}
