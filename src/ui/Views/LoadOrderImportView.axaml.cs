using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
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

    /// <summary>
    /// The top candidate's Accept: identifies the row with its best
    /// candidate. The button's DataContext is the row (the workspace lives in
    /// the row's template).
    /// </summary>
    private void AcceptCandidate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is LoadOrderRowViewModel row)
        {
            ViewModel?.AcceptCandidateCommand.Execute(row);
        }
    }

    /// <summary>
    /// The expand affordance: toggles the row's alternates panel.
    /// </summary>
    private void ToggleAlternates_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is LoadOrderRowViewModel row)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    /// <summary>
    /// The manual-identification Apply: commits the row's typed id/URL.
    /// </summary>
    private void ApplyManualId_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is LoadOrderRowViewModel row)
        {
            ViewModel?.ApplyManualIdCommand.Execute(row);
        }
    }

    /// <summary>
    /// An alternate candidate's Accept (inside the alternates panel, whose
    /// DataContext is the candidate): resolves the owning row by walking the
    /// visual tree to the first ancestor carrying a row DataContext, then
    /// identifies it with the clicked candidate.
    /// </summary>
    private void AcceptAlternate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NexusSearchCandidate candidate })
        {
            return;
        }

        var row = (sender as Control)?.FindAncestorOfType<ContentPresenter>()
            ?.DataContext as LoadOrderRowViewModel
            ?? throw new InvalidOperationException("The alternate accept lost its row context.");
        ViewModel?.AcceptAlternateCommand.Execute((row, candidate));
    }
}
