using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The Settings destination content (a <see cref="UserControl"/>). Its
/// <c>DataContext</c> is a <see cref="SettingsViewModel"/> (bound from the
/// shell). Owns the discovery-row Browse picker mechanics (the picker is a view
/// concern, needing the live <c>TopLevel</c>); the Storage buttons + the
/// Updates controls bind directly to the VM.
/// </summary>
/// <remarks>
/// All persistence logic lives in the (unit-tested) VM; this is pure view
/// mechanics. The <see cref="BrowseDiscovery_Click"/> path resolves the
/// <c>TopLevel</c> from this <see cref="UserControl"/> (a valid Avalonia visual
/// in the main shell).
/// </remarks>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A discovery row's Browse button. The button carries the field's
    /// <see cref="DiscoveryBrowseKind"/> as its <c>CommandParameter</c>; this
    /// opens the matching picker, takes the first selection (single-select), and
    /// sets the row's <c>Value</c> (which triggers the VM's write-through).
    /// Multi-select is not meaningful for a single path field. No-op inside a
    /// Steam Deck Gaming Mode session (pickers are unusable there; the disabled
    /// button is the first gate, this covers a programmatic or stale-click
    /// invocation).
    /// </summary>
    /// <remarks>
    /// The picker's <c>SuggestedStartLocation</c> is derived from the row's
    /// current <c>Value</c> so the picker opens where the user already is (for a
    /// file-kind row, the file's parent directory). A null/invalid path falls
    /// back to the system default location, so no error handling is needed.
    /// </remarks>
    private async void BrowseDiscovery_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b
            || b.DataContext is not DiscoveryFieldRowViewModel row)
        {
            return;
        }

        if ((DataContext as SettingsViewModel)?.IsGamingMode is true)
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
