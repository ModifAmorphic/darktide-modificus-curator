using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The load-order review table card. Its <c>DataContext</c> is a
/// <see cref="LoadOrderImportViewModel"/> (set by the hosting wrapper Panel
/// in ModListView, which also gates the visibility to the card's IsActive).
/// All review + apply logic lives in the (unit-tested) VM; this is pure view
/// mechanics: the per-row open-on-Nexus link routes through code-behind to
/// the VM's command (the established per-row code-behind pattern, so the row
/// template needs no parent-context binding).
/// </summary>
public partial class LoadOrderImportView : UserControl
{
    public LoadOrderImportView()
    {
        InitializeComponent();
    }

    private LoadOrderImportViewModel? ViewModel => DataContext as LoadOrderImportViewModel;

    /// <summary>
    /// An unresolved row's open-on-Nexus link: runs the VM's
    /// <see cref="LoadOrderImportViewModel.OpenOnNexusCommand"/> (which owns
    /// the external launch + the fallback alert).
    /// </summary>
    private void OpenOnNexus_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton b && b.DataContext is LoadOrderRowViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.OpenOnNexusCommand.Execute(row);
        }
    }
}
