using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using System.Linq;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The load-order import workspace. Its <c>DataContext</c> is a
/// <see cref="LoadOrderImportViewModel"/> (set by the hosting wrapper in
/// ModListView, which also gates the workspace's visibility to
/// <c>LoadOrder.IsActive</c>). All workflow logic lives in the (unit-tested)
/// VM; this is pure view mechanics: the per-row actions route through
/// code-behind to the VM's commands (the established per-row pattern, so the
/// row templates need no parent-context bindings).
/// </summary>
public partial class LoadOrderImportView : UserControl
{
    public LoadOrderImportView()
    {
        InitializeComponent();
    }

    private LoadOrderImportViewModel? ViewModel => DataContext as LoadOrderImportViewModel;

    /// <summary>
    /// A row's Skip/Undo toggle (the exceptional opt-out).
    /// </summary>
    private void Skip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LoadOrderRowViewModel row })
        {
            ViewModel?.ToggleSkipCommand.Execute(row);
        }
    }

    /// <summary>
    /// The inline magnifier Find icon button: runs the VM's shared manual
    /// lookup (the command owns the id/URL/name classification, the busy
    /// state, and inline errors).
    /// </summary>
    private void Find_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LoadOrderRowViewModel row })
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.FindNexusModCommand.Execute(row);
        }
    }

    /// <summary>
    /// Enter in the manual id/URL/name field invokes the SAME Find command as
    /// the magnifier icon, for that row, and marks the key handled. No
    /// classification or search policy lives here; the command owns all of
    /// it.
    /// </summary>
    private void ManualId_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is TextBox { DataContext: LoadOrderRowViewModel row })
        {
            e.Handled = true;
            ViewModel?.FindNexusModCommand.Execute(row);
        }
    }

    /// <summary>
    /// The identified row's Change action: returns the row to the
    /// candidate/manual identification state.
    /// </summary>
    private void ChangeIdentity_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LoadOrderRowViewModel row })
        {
            ViewModel?.ChangeIdentityCommand.Execute(row);
        }
    }

    /// <summary>
    /// The top candidate's Accept: identifies the row with its best
    /// candidate.
    /// </summary>
    private void AcceptCandidate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LoadOrderRowViewModel row })
        {
            ViewModel?.AcceptCandidateCommand.Execute(row);
        }
    }

    /// <summary>
    /// The expand affordance: toggles the row's alternates panel.
    /// </summary>
    private void ToggleAlternates_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LoadOrderRowViewModel row })
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    /// <summary>
    /// An alternate candidate's Accept (inside the alternates panel, whose
    /// DataContext is the candidate): resolves the owning row by walking the
    /// visual ancestors' content presenters and taking the first DataContext
    /// OF THE ROW TYPE. The nearest presenter is not the row's: the
    /// alternates ItemsControl wraps each candidate in its own
    /// ContentPresenter, so a first-ancestor cast would read the candidate
    /// itself and yield null.
    /// </summary>
    private void AcceptAlternate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NexusSearchCandidate candidate })
        {
            return;
        }

        var row = (sender as Control)?
            .GetVisualAncestors()
            .OfType<ContentPresenter>()
            .Select(p => p.DataContext)
            .OfType<LoadOrderRowViewModel>()
            .FirstOrDefault();
        if (row is null)
        {
            return;
        }

        ViewModel?.AcceptAlternateCommand.Execute((row, candidate));
    }
}
