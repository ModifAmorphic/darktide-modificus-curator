using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The mod-list content area (a <see cref="UserControl"/>). Its
/// <c>DataContext</c> is a <see cref="ModListViewModel"/> (bound from the shell as
/// <c>{Binding ModList}</c>). Owns the add entry points (the Add split button's
/// archive file picker + folder picker + the link-external-folder picker + the
/// content-area drag-and-drop target) and routes every per-row interaction
/// (toggle / move / lock / policy / remove / open external folder) through
/// code-behind handlers calling the parent VM's commands with the row as the
/// parameter (the established per-row code-behind pattern). All state + service
/// calls stay in the (unit-tested) VM; this is pure view mechanics.
/// </summary>
/// <remarks>
/// <para><b>Add split button:</b> all four flyout items are modes. Each item
/// sets itself as the default on click (the VM's <see cref="ModListViewModel.AddMode"/>
/// is mirrored via <see cref="SetAddMode"/> so the face label updates through
/// <see cref="ModListViewModel.AddModeLabel"/>) and runs its action: NexusMods
/// opens the Darktide Nexus Mods games page, Archive + Folder open their import
/// pickers and forward paths to <see cref="ImportWorkflowViewModel.StartBatchCommand"/>,
/// and LinkExternal opens the link-external-folder picker. NexusMods is the
/// default, so the face first reads "+ Add Nexus Mods"; clicking the face runs
/// the current default's action. Archive + Folder are separate modes because a
/// native picker cannot mix files + folders.</para>
/// <para><b>Workflow gating:</b> while the inline import card is active
/// (editing, processing, or failure), the Add split button is disabled and the
/// archive/folder pickers + drag-and-drop are gated defensively so a second
/// batch cannot start. The VM's <c>StartBatch</c> gate is the final defense.</para>
/// <para><b>External drag-and-drop (file/folder import):</b> the content area
/// has <c>DragDrop.AllowDrop="True"</c> + <c>Drop</c>/<c>DragOver</c> handlers.
/// The drop reads the files (folders AND archives, multi) via the sync
/// <c>TryGetFiles</c> extension on <see cref="DragEventArgs.DataTransfer"/> (an
/// <c>IDataTransfer</c> in Avalonia 12.x, so the async variant is unavailable
/// here), maps each to its local path, and forwards the list to the workflow's
/// start command. <c>DragOver</c> advertises the Copy effect only when files are
/// present AND the workflow is not active. This is native OS drag (external
/// files only) and is structurally separate from the row-reorder pointer
/// gesture below; the two never share a payload or a code path.</para>
/// <para><b>Row reorder gesture:</b> the drag grip at each row's left edge is
/// the ONLY surface that initiates reordering. A press on an unlocked grip
/// calls <see cref="PointerEventArgs.PreventGestureRecognition"/> (so the
/// ScrollViewer's touch-scroll manipulation does not also grab the gesture),
/// captures the pointer to the grip, and starts a reorder only after an 8-DIP
/// movement threshold. Once the threshold is crossed the realized item
/// container (the full-width actual row) is lifted: a render transform follows
/// the pointer while its layout slot stays reserved, ZIndex raises it, and a
/// lifted style adds an opaque surface + corners + a shadow (the lift replaces
/// the old source-dimming). While dragging, the target rank + insertion marker
/// are computed against the OTHER unlocked rows only (locked rows are never
/// destinations), and an edge band auto-scrolls the list while the lifted row
/// is kept under the pointer. A release inside the viewport commits through the
/// VM's
/// <see cref="ModListViewModel.CommitReorderCommand"/>; Escape, capture loss,
/// detachment, a release outside the viewport, or an invalid target all cancel
/// without persistence. Every mutated container property is restored on each
/// finish/cancel path. The pure threshold / target / marker / lift / auto-scroll
/// math lives in <see cref="ReorderGestureMath"/> and is unit-tested separately.
/// Dragging anywhere outside the grip stays ordinary touch scrolling, which
/// matters on the Steam Deck touch list.</para>
/// <para><b>Policy ComboBox guard:</b> <see cref="Policy_Changed"/> skips when the
/// selection already agrees with the row's effective policy, so the binding-init
/// (and post-Reload) <c>SelectionChanged</c> fires do not re-apply + loop. Only a
/// genuine divergence routes to the parent's policy command.</para>
/// </remarks>
public partial class ModListView : UserControl
{
    /// <summary>
    /// The auto-scroll tick interval. Short enough that the list tracks the
    /// pointer smoothly while the pointer lingers in an edge band.
    /// </summary>
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(16);

    public ModListView()
    {
        InitializeComponent();
    }

    private ModListViewModel? ViewModel => DataContext as ModListViewModel;

    /// <summary>
    /// The Add split button's current mode (which action the primary click runs).
    /// Defaults to <see cref="ModAddMode.NexusMods"/>. Kept in sync with the VM's
    /// <see cref="ModListViewModel.AddMode"/> (via <see cref="SetAddMode"/>) so
    /// the split button's label tracks the selected mode.
    /// </summary>
    private ModAddMode _addMode = ModAddMode.NexusMods;

    // ---- row reorder gesture state -------------------------------------------

    /// <summary>The row being dragged (set on press, cleared on finish/cancel).</summary>
    private ModItemViewModel? _dragRow;

    /// <summary>The grip Border that captured the pointer (for releasing capture).</summary>
    private Border? _dragGrip;

    /// <summary>The pointer currently captured by the grip, or null.</summary>
    private IPointer? _capturedPointer;

    /// <summary>The press position (DIP) relative to the ScrollViewer, captured at press.</summary>
    private Point _pressPosition;

    /// <summary>
    /// The ScrollViewer's vertical offset at press, captured so the lift
    /// translation can compensate for edge auto-scroll (see
    /// <see cref="ReorderGestureMath.ComputeLiftTranslationY"/>).
    /// </summary>
    private double _pressScrollOffsetY;

    /// <summary>Whether the drag has crossed the movement threshold (lift + marker + commit active).</summary>
    private bool _dragging;

    /// <summary>The last pointer position relative to the ScrollViewer (for auto-scroll).</summary>
    private Point _lastPointerPosition;

    /// <summary>The computed target unlocked rank for the current pointer (for commit).</summary>
    private int _targetUnlockedRank;

    /// <summary>The edge-band auto-scroll timer; running only while dragging.</summary>
    private DispatcherTimer? _autoScrollTimer;

    /// <summary>Whether the TopLevel Escape key handler is subscribed (avoids leaks).</summary>
    private bool _keyHandlerAttached;

    /// <summary>
    /// The lifted row's render transform (mutated on each move/auto-scroll); null
    /// until the threshold is crossed + the container is lifted.
    /// </summary>
    private TranslateTransform? _liftTransform;

    /// <summary>
    /// The snapshot of the lifted container's pre-lift render-transform + z-index
    /// (and the container itself), so every finish/cancel path restores them
    /// exactly. Null when no row is lifted.
    /// </summary>
    private LiftSnapshot? _liftSnapshot;

    /// <summary>
    /// A snapshot of the realized item container's pre-lift render transform +
    /// z-index, so the lifted treatment is restored exactly (not assumed to be
    /// the type defaults) on every finish/cancel path.
    /// </summary>
    /// <param name="Container">The realized ItemsControl container (a
    /// ContentPresenter).</param>
    /// <param name="RenderTransform">The container's render transform before lift.</param>
    /// <param name="ZIndex">The container's z-index before lift.</param>
    private sealed record LiftSnapshot(Control Container, Avalonia.Media.ITransform? RenderTransform, int ZIndex);

    /// <summary>The class applied to the lifted container (drives the lifted-row style).</summary>
    private const string LiftedRowClass = "liftedRow";

    /// <summary>The z-index applied to the lifted container so it renders above siblings.</summary>
    private const int LiftedZIndex = 100;

    // ---- add: split button (archive + folder pickers) --------------------------

    /// <summary>
    /// The Add split button's primary click: runs the current mode's action.
    /// NexusMods opens the Darktide Nexus Mods games page; Archive + Folder open
    /// their import pickers; LinkExternal opens the link-external-folder picker.
    /// Archive + Folder are separate modes because a native picker cannot mix
    /// files + folders. A top-level <see cref="ImportWorkflowViewModel.IsActive"/>
    /// guard skips the action entirely while the inline card is active (the
    /// SplitButton is also disabled, but this covers a flyout click that opened
    /// before the state changed).
    /// </summary>
    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && vm.ImportWorkflow.IsActive)
        {
            return;
        }

        switch (_addMode)
        {
            case ModAddMode.NexusMods:
                ViewModel?.AddNexusModsCommand.Execute(null);
                break;
            case ModAddMode.Archive:
                await OpenArchivePickerAsync();
                break;
            case ModAddMode.Folder:
                await OpenFolderPickerAsync();
                break;
            case ModAddMode.LinkExternal:
                await OpenLinkFolderPickerAsync();
                break;
        }
    }

    /// <summary>
    /// The "Add Nexus Mods" flyout item (the first item on the Add split button):
    /// sets the mode to NexusMods (so subsequent primary clicks reopen the games
    /// page) and opens the Darktide Nexus Mods games page in the user's default
    /// browser. The command owns the external-launch + fallback alert (the
    /// established forwarder pattern).
    /// </summary>
    private void AddNexusMods_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.NexusMods);
        ViewModel?.AddNexusModsCommand.Execute(null);
    }

    /// <summary>
    /// The "Add Mod (archive)" flyout item: switches the mode to archive (so
    /// subsequent primary clicks open the archive picker) and opens the archive
    /// picker immediately (one-click import).
    /// </summary>
    private async void AddArchive_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.Archive);
        await OpenArchivePickerAsync();
    }

    /// <summary>
    /// The "Add Mod (folder)" flyout item: switches the mode to folder (so
    /// subsequent primary clicks open the folder picker) and opens the folder
    /// picker immediately (one-click import). Folders get a picker path because
    /// a native picker cannot mix files + folders.
    /// </summary>
    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.Folder);
        await OpenFolderPickerAsync();
    }

    /// <summary>
    /// The "Link external folder" flyout item: sets the mode to LinkExternal (so
    /// subsequent primary clicks reopen the link picker) and opens a folder
    /// picker, forwarding the selected folder paths to the linked-mods child
    /// VM's link command (records each folder as a metadata-only linked
    /// container, no copy).
    /// </summary>
    private async void LinkFolder_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.LinkExternal);
        await OpenLinkFolderPickerAsync();
    }

    /// <summary>
    /// Opens a multi-select folder picker and forwards the selected folder paths
    /// to the linked-mods child VM's link command. The picker call mirrors
    /// <see cref="OpenFolderPickerAsync"/> exactly (the same
    /// <c>StorageProvider.OpenFolderPickerAsync</c> path); only the target
    /// command differs. Gated on <see cref="ImportWorkflowViewModel.IsActive"/>
    /// at entry (flyout item handlers call this directly, so a pre-open
    /// MenuFlyout click should not open the link picker once the workflow is
    /// active) AND rechecked after the picker returns so a linked-folder
    /// mutation does not proceed if an import workflow became active while the
    /// picker was open (e.g. a drag-and-drop landed in the meantime). Also
    /// gated on Gaming Mode (pickers are unusable in a Steam Deck Gaming Mode
    /// session; the disabled split button is the first gate).
    /// </summary>
    private async Task OpenLinkFolderPickerAsync()
    {
        if (ViewModel is not { } vm || vm.ImportWorkflow.IsActive || vm.IsGamingMode)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = true,
        };

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result is null || result.Count == 0)
        {
            return;
        }

        // Recheck after the picker returns: a native picker is async, so the
        // import workflow could have become active while it was open. A
        // linked-folder mutation must not proceed in that window.
        if (vm.ImportWorkflow.IsActive)
        {
            return;
        }

        var paths = result.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0)
        {
            await vm.LinkedMods.LinkModsCommand.ExecuteAsync(paths);
        }
    }

    /// <summary>
    /// Sets the current add mode + mirrors it on the VM (so the split button's
    /// <c>AddModeLabel</c> binding refreshes). Centralized so the field + the VM
    /// property never drift apart.
    /// </summary>
    private void SetAddMode(ModAddMode mode)
    {
        _addMode = mode;
        if (ViewModel is { } vm)
        {
            vm.AddMode = mode;
        }
    }

    /// <summary>
    /// Opens a multi-select archive file picker and forwards the selected paths
    /// to the inline import workflow's start command. Skipped defensively when
    /// the workflow is already active at entry AND rechecked after the picker
    /// returns (a native picker is async; the workflow could have become active
    /// while it was open). The SplitButton is also disabled, but a late-returning
    /// picker or a programmatic call could race; StartBatch's VM gate is the
    /// final defense. Also gated on Gaming Mode (pickers are unusable in a
    /// Steam Deck Gaming Mode session). The filter offers a curated "Archives"
    /// entry (zip/7z/rar) plus the built-in "All files" entry, so
    /// unsupported-but-real archives (and edge cases) are still reachable. The
    /// import backend detects the format from the file contents, so the filter
    /// is a convenience, not a gate.
    /// </summary>
    private async Task OpenArchivePickerAsync()
    {
        if (ViewModel is not { } vm || vm.ImportWorkflow.IsActive || vm.IsGamingMode)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var archives = new FilePickerFileType("Archives")
        {
            Patterns = new[] { "*.zip", "*.7z", "*.rar" },
        };
        var options = new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[] { archives, FilePickerFileTypes.All },
        };

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (result is null || result.Count == 0)
        {
            return;
        }

        // Recheck after the picker returns: a native picker is async, so the
        // import workflow could have become active while it was open.
        if (vm.ImportWorkflow.IsActive)
        {
            return;
        }

        var paths = result.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0)
        {
            vm.ImportWorkflow.StartBatchCommand.Execute(paths);
        }
    }

    /// <summary>
    /// Opens a multi-select folder picker and forwards the selected folder paths
    /// to the inline import workflow's start command. Skipped defensively when
    /// the workflow is already active at entry AND rechecked after the picker
    /// returns (same late-return contract as the archive picker). Also gated on
    /// Gaming Mode (pickers are unusable in a Steam Deck Gaming Mode session).
    /// The cross-platform path for folder import via picker (a native picker
    /// cannot mix files + folders).
    /// </summary>
    private async Task OpenFolderPickerAsync()
    {
        if (ViewModel is not { } vm || vm.ImportWorkflow.IsActive || vm.IsGamingMode)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = true,
        };

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result is null || result.Count == 0)
        {
            return;
        }

        // Recheck after the picker returns: a native picker is async, so the
        // import workflow could have become active while it was open.
        if (vm.ImportWorkflow.IsActive)
        {
            return;
        }

        var paths = result.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0)
        {
            vm.ImportWorkflow.StartBatchCommand.Execute(paths);
        }
    }

    // ---- add: external drag-and-drop (file/folder import) -------------------

    /// <summary>
    /// Advertises the Copy effect when the dragged payload carries files (folders
    /// or archives) AND the import workflow is not active; otherwise None, so
    /// non-file drops and drops while a batch is in progress are not accepted.
    /// Gate on the actual file retrieval (the same call OnDrop uses), not on
    /// Contains(DataFormat.File): that format-name check can be unreliable for
    /// external file-manager drags. This is native OS drag for external files
    /// only; it is unrelated to the row-reorder grip gesture.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var workflowActive = ViewModel?.ImportWorkflow.IsActive is true;
        e.DragEffects = !workflowActive && e.DataTransfer.TryGetFiles() is { Length: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// Collects the dropped files' local paths (folders or archives, multi) via
    /// the sync <c>TryGetFiles</c> extension on <see cref="DragEventArgs.DataTransfer"/>
    /// and forwards them to the import workflow's start command. Ignored when the
    /// workflow is already active (the drag-over advertised None, but a defensive
    /// check here is the final gate before the VM's own StartBatch defense).
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || vm.ImportWorkflow.IsActive)
        {
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        var paths = files.Select(f => f.Path.LocalPath).ToArray();
        e.Handled = true;

        if (paths.Length > 0)
        {
            vm.ImportWorkflow.StartBatchCommand.Execute(paths);
        }
    }

    // ---- per-row interactions ----------------------------------------------

    /// <summary>
    /// Applies a row's enabled toggle. The CheckBox two-way bound
    /// <see cref="ModItemViewModel.Enabled"/> already flipped; this persists it via
    /// the parent's <c>ToggleEnabledCommand</c>.
    /// </summary>
    private void Enabled_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ModItemViewModel row)
        {
            ViewModel?.ToggleEnabledCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a policy-ComboBox change to the parent's Latest / Pinned command.
    /// Skips when the selection already agrees with the row's effective policy, so
    /// binding-init + post-Reload <c>SelectionChanged</c> fires (which would
    /// otherwise re-apply + reload infinitely) are harmless. Only a genuine
    /// divergence proceeds.
    /// </summary>
    private void Policy_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.DataContext is not ModItemViewModel row)
        {
            return;
        }

        // Skip the init / programmatic fire: when the ComboBox's selection matches
        // the row's effective policy there is nothing to apply. Reloads recreate
        // rows + re-init their ComboBoxes; without this guard each would re-apply.
        var wantsPinned = cb.SelectedIndex == ModItemViewModel.PolicyPinned;
        var isPinned = row.Policy is PinnedPolicy;
        if (wantsPinned == isPinned)
        {
            return;
        }

        if (wantsPinned)
        {
            ViewModel?.SetPolicyPinnedCommand.Execute(row);
        }
        else
        {
            ViewModel?.SetPolicyLatestCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a version-dropdown selection change to the parent's
    /// <c>SetPolicyPinned</c> command (with the newly selected versionId).
    /// Skips when the selection already agrees with the row's effective pinned
    /// versionId, so binding-init + post-Reload <c>SelectionChanged</c> fires
    /// (which would otherwise re-apply + reload infinitely) are harmless. Only a
    /// genuine divergence proceeds. No-op when the row's effective policy is not
    /// Pinned (the policy ComboBox's own change drives the switch-to-Pinned).
    /// </summary>
    private void PinnedVersion_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.DataContext is not ModItemViewModel row)
        {
            return;
        }

        // Only relevant when the row is already Pinned: the Latest->Pinned switch
        // is driven by Policy_Changed (which calls SetPolicyPinned itself). This
        // handler covers a re-pin to a different version while already Pinned.
        if (row.Policy is not PinnedPolicy pinned)
        {
            return;
        }

        if (cb.SelectedItem is not VersionOption selected)
        {
            return;
        }

        // Skip the init / programmatic fire: when the dropdown's selection matches
        // the row's effective pinned versionId there is nothing to apply. Reloads
        // recreate rows + re-init their dropdowns; without this guard each would
        // re-apply.
        if (string.Equals(selected.VersionId, pinned.VersionId, StringComparison.Ordinal))
        {
            return;
        }

        ViewModel?.SetPolicyPinnedCommand.Execute(row);
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            ViewModel?.MoveUpCommand.Execute(row);
        }
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            ViewModel?.MoveDownCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes an order-lock toggle button click to the parent's
    /// <c>ToggleOrderLockCommand</c>. The command owns the no-active-profile
    /// guard + the reload; the view is pure mechanics.
    /// </summary>
    private void ToggleOrderLock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            ViewModel?.ToggleOrderLockCommand.Execute(row);
        }
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.RemoveCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a row's edit-import-details button click to the parent's
    /// <c>EditImportDetailsCommand</c>, which starts the import card's edit
    /// mode for the row's container (the child workflow VM owns the card +
    /// the save; the parent reloads on the child's edited event). The command
    /// owns the linked / download-morphed guards (the button is hidden for
    /// both inside its always-laid-out slot, the update-action-cell pattern);
    /// the view is pure mechanics.
    /// </summary>
    private void EditImportDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.EditImportDetailsCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a per-row Update button click to the parent's
    /// <c>UpdateCommand</c>. The command owns the defenses (premium, Nexus +
    /// Latest, update flagged, one-at-a-time) + the acquire + reload + alert
    /// flow; the view is pure mechanics (the established row-interaction
    /// pattern). The row is passed as the command parameter.
    /// </summary>
    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.UpdateCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a linked row's badge click to the linked-mods child's
    /// <c>OpenFolderCommand</c>, which opens the OS file manager at the row's
    /// external folder. No-op for non-linked or broken rows (the command guards
    /// on both). The row is passed as the command parameter; the view is pure
    /// mechanics (the shared badge template routes here from both row roots).
    /// </summary>
    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton hb && hb.DataContext is ModItemViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.LinkedMods.OpenFolderCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes the header "check for updates now" button to the VM's
    /// <c>CheckForUpdatesNowCommand</c> (an AsyncRelayCommand). The command
    /// owns the thorough check + the <c>IsCheckingNow</c> affordance + the
    /// no-active-profile no-op; the view is pure mechanics. The button's
    /// <c>IsEnabled</c> is bound to <c>!IsCheckingNow</c> so a second click
    /// while a thorough check is running is disabled at the control level too.
    /// </summary>
    private async void RefreshUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        }
    }

    // ---- row reorder pointer gesture ----------------------------------------
    //
    // The grip Border (one per row template) is the only surface that captures
    // these handlers. A locked row's grip has IsHitTestVisible=False (bound to
    // IsGripEnabled), so it never receives the press and its area falls through
    // to the ScrollViewer's touch scrolling. The pure threshold / target-rank /
    // marker / auto-scroll rules live in ReorderGestureMath.

    /// <summary>
    /// A primary press on an unlocked grip: prevents the ScrollViewer's
    /// touch-scroll gesture from also engaging, marks the event handled, records
    /// the press position, and captures the pointer to the grip. Immediate
    /// capture is intentional: the grip is reserved for reorder, so capture
    /// ensures every move/release is retained even if the pointer leaves the
    /// grip's bounds. A sub-threshold move (a tap) performs no reorder.
    /// </summary>
    /// <remarks>
    /// <b>Multi-pointer:</b> the gesture is single-pointer. If a row gesture is
    /// already armed (a press captured but not yet released, dragging or not), a
    /// second pointer's press is ignored outright, before it can call
    /// <see cref="PointerEventArgs.PreventGestureRecognition"/>, capture, or
    /// overwrite the shared state. <see cref="Grip_PointerMoved"/> +
    /// <see cref="Grip_PointerReleased"/> + <see cref="Grip_PointerCaptureLost"/>
    /// likewise process only the active captured pointer (by reference), so an
    /// unrelated pointer cannot move, commit, cancel, or release the gesture.
    /// </remarks>
    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Multi-pointer defense (first): if a row gesture is already armed, a
        // second pointer press is ignored before it can claim the gesture.
        if (_dragRow is not null)
        {
            return;
        }

        if (sender is not Border grip || grip.DataContext is not ModItemViewModel row)
        {
            return;
        }

        // Defense: a locked grip has IsHitTestVisible=False and should not
        // receive the press, but guard anyway so a programmatic path can't
        // start a reorder on a locked row.
        if (!row.IsGripEnabled)
        {
            return;
        }

        var pointer = e.Pointer;
        var current = e.GetCurrentPoint(null);
        if (!current.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var scroll = ModListScroll;
        if (scroll is null)
        {
            return;
        }

        // Claim the gesture: prevent the ScrollViewer's scroll/pan recognizer
        // from also handling this press (the grip is reserved for reorder), mark
        // handled, and capture the pointer so moves/releases stay with the grip.
        e.PreventGestureRecognition();
        e.Handled = true;
        pointer.Capture(grip);

        _dragRow = row;
        _dragGrip = grip;
        _capturedPointer = pointer;
        _pressPosition = e.GetPosition(scroll);
        _pressScrollOffsetY = scroll.Offset.Y;
        _lastPointerPosition = _pressPosition;
        _dragging = false;
        _targetUnlockedRank = -1;

        EnsureKeyHandlerAttached();
    }

    /// <summary>
    /// While the grip holds capture: once the 8-DIP threshold is crossed, enters
    /// the dragging state (lifting the realized row so it follows the pointer);
    /// on every move while
    /// dragging, recomputes the target unlocked rank, updates the insertion
    /// marker, and feeds the pointer position to the auto-scroll timer. Only the
    /// active captured pointer is processed; an unrelated pointer's move is
    /// ignored.
    /// </summary>
    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null || _capturedPointer is null
            || !ReferenceEquals(e.Pointer, _capturedPointer))
        {
            return;
        }

        var scroll = ModListScroll;
        if (scroll is null)
        {
            return;
        }

        var position = e.GetPosition(scroll);
        _lastPointerPosition = position;

        if (!_dragging)
        {
            var delta = position - _pressPosition;
            if (!ReorderGestureMath.ExceedsThreshold(delta.X, delta.Y))
            {
                return;
            }

            // Threshold crossed: enter the drag + lift the actual realized row.
            // If the container cannot be resolved, cancel safely (no persistence,
            // no stale state).
            if (!BeginLift())
            {
                CancelDragCore();
                return;
            }

            _dragging = true;
        }

        UpdateLiftTranslation(position.Y, scroll.Offset.Y);
        UpdateTargetAndMarker(position);
    }

    /// <summary>
    /// A release of the active pointer while a drag is active recomputes the
    /// target from the final release position (so the committed rank reflects
    /// the layout at release, closing the one-tick auto-scroll/layout lag),
    /// snapshots the rank, releases capture, and commits only when the release
    /// is inside the viewport AND the target is a real order change. A release
    /// without a started drag (a tap) just clears the press state. A release
    /// from an unrelated pointer is ignored. Capture is released BEFORE the VM
    /// command runs because Reload rebuilds the row containers.
    /// </summary>
    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null || _capturedPointer is null
            || !ReferenceEquals(e.Pointer, _capturedPointer))
        {
            return;
        }

        var scroll = ModListScroll;
        var wasDragging = _dragging;
        var row = _dragRow;

        // Recompute the target + marker from the final release position so the
        // committed rank reflects the layout at release, then snapshot the rank
        // before releasing capture. Only meaningful once the drag engaged; a tap
        // has no target to commit.
        var targetRank = _targetUnlockedRank;
        var insideViewport = false;
        if (wasDragging && scroll is not null)
        {
            var releasePosition = e.GetPosition(scroll);
            insideViewport = IsInsideViewport(releasePosition, scroll);
            UpdateTargetAndMarker(releasePosition);
            targetRank = _targetUnlockedRank;
        }

        // Always release capture + clear state before the commit; Reload
        // rebuilds containers so the captured grip may be gone.
        CancelDragCore();

        if (!wasDragging || scroll is null || ViewModel is not { } vm)
        {
            return;
        }

        if (!insideViewport || targetRank < 0)
        {
            return;
        }

        // Commit only a real order change. The planner rejects a no-op target
        // (source rank == target rank) inside the VM, so a same-position release
        // makes no service call.
        vm.CommitReorderCommand.Execute(new ReorderRequest(row.ContainerId, targetRank));
    }

    /// <summary>
    /// Capture was lost (another capture, window deactivation, etc.) for the
    /// ACTIVE pointer. Cancel the in-flight drag without persistence. A
    /// capture-loss for an unrelated pointer (we hold no capture on it) is
    /// ignored, so it cannot cancel an active gesture.
    /// </summary>
    private void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_capturedPointer is null || !ReferenceEquals(e.Pointer, _capturedPointer))
        {
            return;
        }

        CancelDragCore();
    }

    /// <summary>
    /// Escape while dragging cancels. Subscribed on the TopLevel while a press
    /// is active and unsubscribed on finish/detach so this application-lifetime
    /// view does not leak the handler.
    /// </summary>
    private void TopLevel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelDragCore();
        }
    }

    /// <summary>Attaches the TopLevel Escape handler once per drag lifecycle.</summary>
    private void EnsureKeyHandlerAttached()
    {
        if (_keyHandlerAttached)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        // Generic KeyDown add: bubble from the focused element to the TopLevel.
        // During a drag the window keeps focus, so Escape reaches this handler.
        topLevel.AddHandler(KeyDownEvent, TopLevel_KeyDown);
        _keyHandlerAttached = true;
    }

    /// <summary>Detaches the TopLevel Escape handler (finish, cancel, or detach).</summary>
    private void DetachKeyHandler()
    {
        if (!_keyHandlerAttached)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            topLevel.RemoveHandler(KeyDownEvent, TopLevel_KeyDown);
        }

        _keyHandlerAttached = false;
    }

    /// <summary>
    /// Clears the insertion marker on every row, so a cancel leaves no stale
    /// destination line. The lifted container is restored separately by
    /// <see cref="RestoreLiftedContainer"/> (called from <see cref="CancelDragCore"/>).
    /// </summary>
    private void ClearMarkerAndSource()
    {
        if (ViewModel is { } vm)
        {
            foreach (var row in vm.Mods)
            {
                row.ShowReorderMarkerBefore = false;
                row.ShowReorderMarkerAfter = false;
            }
        }
    }

    /// <summary>
    /// The shared finish / cancel path: stops the auto-scroll timer, restores the
    /// lifted container (BEFORE any VM Reload on a valid drop), releases pointer
    /// capture, clears the insertion marker, and drops the TopLevel key handler.
    /// Makes no service call (the commit, when it happens, runs after this).
    /// </summary>
    private void CancelDragCore()
    {
        if (_autoScrollTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnAutoScrollTick;
            _autoScrollTimer = null;
        }

        // Restore the lifted container before clearing state/Reload so the row's
        // render transform + z-index + style are exactly back (the VM Reload on a
        // valid drop rebuilds containers, so the captured container may be gone).
        RestoreLiftedContainer();

        if (_capturedPointer is not null && _dragGrip is not null)
        {
            _capturedPointer.Capture(null);
        }

        DetachKeyHandler();
        ClearMarkerAndSource();

        _dragRow = null;
        _dragGrip = null;
        _capturedPointer = null;
        _dragging = false;
        _targetUnlockedRank = -1;
    }

    /// <summary>
    /// Recomputes the target unlocked rank for the current pointer position +
    /// applies the insertion marker to the right row. Target rank is computed
    /// against the centers of the OTHER visible unlocked row containers (the
    /// source excluded, locked excluded, filter-hidden rows excluded: the
    /// ItemsControl realizes exactly the visible projection, so only visible
    /// rows are possible destinations). Clears the marker for a no-op target.
    /// </summary>
    private void UpdateTargetAndMarker(Point pointer)
    {
        if (ViewModel is not { } vm || _dragRow is null || ModListItems is null)
        {
            return;
        }

        var (othersCenters, sourceRank) = CollectUnlockedCenterY(vm);
        if (sourceRank < 0)
        {
            return;
        }

        _targetUnlockedRank = ReorderGestureMath.ComputeTargetUnlockedRank(othersCenters, pointer.Y);
        ApplyMarker(vm, sourceRank, _targetUnlockedRank);
    }

    /// <summary>
    /// Builds the ascending center-Y list of OTHER visible unlocked row
    /// containers (the source excluded, locked excluded, hidden excluded) for
    /// the current scroll position, and resolves the source's rank among the
    /// visible unlocked rows. Walks <see cref="ModListViewModel.VisibleMods"/>
    /// (the ItemsControl's ItemsSource) so each item index aligns with its
    /// realized container. Returns the source rank (or -1 when the source is
    /// no longer visible + unlocked / present).
    /// </summary>
    private (List<double> OthersCenters, int SourceRank) CollectUnlockedCenterY(ModListViewModel vm)
    {
        var scroll = ModListScroll;
        if (scroll is null || ModListItems is null)
        {
            return (new List<double>(), -1);
        }

        var othersCenters = new List<double>();
        var sourceRank = -1;
        var unlockedSeen = 0;

        for (var i = 0; i < vm.VisibleMods.Count; i++)
        {
            var row = vm.VisibleMods[i];
            if (row.OrderLocked)
            {
                continue;
            }

            if (ReferenceEquals(row, _dragRow))
            {
                sourceRank = unlockedSeen;
                unlockedSeen++;
                continue;
            }

            var center = ContainerCenterY(i, scroll);
            if (center is { } cy)
            {
                othersCenters.Add(cy);
            }

            unlockedSeen++;
        }

        return (othersCenters, sourceRank);
    }

    /// <summary>
    /// Resolves the vertical center (DIP) of the row container at
    /// <paramref name="itemIndex"/> relative to the ScrollViewer, or null when
    /// the container is not realized / not in the same visual tree.
    /// </summary>
    private double? ContainerCenterY(int itemIndex, ScrollViewer scroll)
    {
        if (ModListItems is null)
        {
            return null;
        }

        var container = ModListItems.ContainerFromIndex(itemIndex);
        if (container is null)
        {
            return null;
        }

        var transform = container.TransformToVisual(scroll);
        if (transform is null)
        {
            return null;
        }

        var origin = transform.Value.Transform(new Point(0, 0));
        return origin.Y + container.Bounds.Height / 2.0;
    }

    /// <summary>
    /// Applies the insertion marker for the current source rank + target rank.
    /// The marker anchors to the visible unlocked row currently occupying the
    /// target rank, drawn before it for an upward move, after it for a downward
    /// move. Clears every row's marker for a no-op target.
    /// </summary>
    /// <param name="vm">The mod-list VM (owns the row collections).</param>
    /// <param name="sourceRank">The source's rank among the visible unlocked
    /// rows.</param>
    /// <param name="targetRank">The computed target rank among the visible
    /// unlocked rows.</param>
    private void ApplyMarker(
        ModListViewModel vm,
        int sourceRank,
        int targetRank)
    {
        // Clear every row first so at most one carries a marker.
        foreach (var row in vm.Mods)
        {
            row.ShowReorderMarkerBefore = false;
            row.ShowReorderMarkerAfter = false;
        }

        var marker = ReorderGestureMath.ComputeMarker(sourceRank, targetRank);
        if (marker is null)
        {
            return;
        }

        var m = marker.Value;
        // The anchor is the visible unlocked row currently occupying the target
        // rank (hidden rows are never destinations, so the anchor is always
        // rendered). Walk the visible unlocked rows (in display order) to that
        // rank.
        var unlockedRank = 0;
        ModItemViewModel? anchor = null;
        foreach (var row in vm.VisibleMods)
        {
            if (row.OrderLocked)
            {
                continue;
            }

            if (unlockedRank == m.AnchorUnlockedRank)
            {
                anchor = row;
                break;
            }

            unlockedRank++;
        }

        if (anchor is null)
        {
            return;
        }

        if (m.Before)
        {
            anchor.ShowReorderMarkerBefore = true;
        }
        else
        {
            anchor.ShowReorderMarkerAfter = true;
        }
    }

    /// <summary>
    /// Starts (or reuses) the auto-scroll timer. Each tick scrolls one step when
    /// the last pointer position sits in an edge band, then recomputes the target
    /// + marker against the new scroll offset.
    /// </summary>
    private void StartAutoScrollTimer()
    {
        if (_autoScrollTimer is not null)
        {
            return;
        }

        _autoScrollTimer = new DispatcherTimer
        {
            Interval = AutoScrollInterval,
        };
        _autoScrollTimer.Tick += OnAutoScrollTick;
        _autoScrollTimer.Start();
    }

    /// <summary>One auto-scroll tick: nudge the offset, keep the lifted row under
    /// the pointer, + recompute the marker.</summary>
    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (ModListScroll is not { } scroll)
        {
            return;
        }

        var delta = ReorderGestureMath.ComputeAutoScrollDelta(
            _lastPointerPosition.Y, scroll.Viewport.Height);
        if (delta == 0)
        {
            return;
        }

        var max = scroll.ScrollBarMaximum;
        var newY = ReorderGestureMath.ClampOffset(scroll.Offset.Y + delta, max.Y);
        scroll.Offset = new Vector(scroll.Offset.X, newY);

        // Keep the lifted row under the (stationary) pointer after the scroll
        // moved normal content, then recompute target + marker against the
        // scrolled layout.
        UpdateLiftTranslation(_lastPointerPosition.Y, scroll.Offset.Y);
        UpdateTargetAndMarker(_lastPointerPosition);
    }

    // ---- lifted-row treatment ----------------------------------------------
    //
    // Once the reorder threshold is crossed, the realized ItemsControl item
    // container (a ContentPresenter) is lifted: a render transform follows the
    // pointer so the full-width actual row moves, its layout slot stays reserved
    // (rows do not jump), ZIndex raises it above siblings, and the liftedRow
    // class adds an opaque surface + rounded corners + a shadow. Horizontal
    // translation stays zero (vertical reorder only). Every mutated property is
    // restored on each finish/cancel path from a snapshot.

    /// <summary>
    /// Lifts the source row's realized container: resolves the container,
    /// snapshots its render transform + z-index, applies the liftedRow class +
    /// z-index + a fresh translate transform, and starts the auto-scroll timer.
    /// Returns false (so the caller cancels safely) if the container cannot be
    /// resolved. Idempotent guard: a no-op if a lift is already in flight.
    /// </summary>
    /// <returns><c>true</c> if the container was lifted; <c>false</c> if lookup
    /// failed (the caller should cancel with no persistence + no stale
    /// state).</returns>
    private bool BeginLift()
    {
        if (_liftSnapshot is not null)
        {
            return true;
        }

        if (ViewModel is not { } vm || _dragRow is null || ModListItems is null)
        {
            return false;
        }

        // The container lookup indexes the ItemsControl's ItemsSource, which is
        // the visible projection (the dragged grip is only reachable on a
        // rendered, i.e. visible, row).
        var index = vm.VisibleMods.IndexOf(_dragRow);
        if (index < 0 || ModListItems.ContainerFromIndex(index) is not { } container)
        {
            return false;
        }

        _liftSnapshot = new LiftSnapshot(container, container.RenderTransform, container.ZIndex);
        _liftTransform = new TranslateTransform();
        container.RenderTransform = _liftTransform;
        container.ZIndex = LiftedZIndex;
        container.Classes.Add(LiftedRowClass);

        var scroll = ModListScroll;
        UpdateLiftTranslation(_lastPointerPosition.Y, scroll?.Offset.Y ?? _pressScrollOffsetY);

        StartAutoScrollTimer();
        return true;
    }

    /// <summary>
    /// Moves the lifted row to follow the pointer: sets the translate transform's
    /// Y from the pure lift formula (pointer delta + scroll-offset delta). X stays
    /// zero. No-op when no row is lifted.
    /// </summary>
    /// <param name="pointerY">The pointer's current Y in the ScrollViewer
    /// viewport space.</param>
    /// <param name="scrollOffsetY">The ScrollViewer's current vertical offset.
    /// </param>
    private void UpdateLiftTranslation(double pointerY, double scrollOffsetY)
    {
        if (_liftTransform is null)
        {
            return;
        }

        _liftTransform.Y = ReorderGestureMath.ComputeLiftTranslationY(
            pointerY, _pressPosition.Y, scrollOffsetY, _pressScrollOffsetY);
    }

    /// <summary>
    /// Restores the lifted container's render transform, z-index, and drops the
    /// liftedRow class from the snapshotted values, so styles/bindings return
    /// exactly (the snapshot is used rather than assuming the defaults). No-op
    /// when no row is lifted. Runs on every finish/cancel path, before any VM
    /// Reload on a valid drop.
    /// </summary>
    private void RestoreLiftedContainer()
    {
        if (_liftSnapshot is not { } snap)
        {
            return;
        }

        snap.Container.Classes.Remove(LiftedRowClass);
        snap.Container.RenderTransform = snap.RenderTransform;
        snap.Container.ZIndex = snap.ZIndex;
        _liftSnapshot = null;
        _liftTransform = null;
    }

    /// <summary>
    /// Whether a pointer position (relative to the ScrollViewer) sits inside the
    /// viewport's visible bounds. A release outside the viewport cancels.
    /// </summary>
    private static bool IsInsideViewport(Point position, ScrollViewer scroll)
    {
        var viewport = scroll.Viewport;
        return position.X >= 0 && position.X <= viewport.Width
            && position.Y >= 0 && position.Y <= viewport.Height;
    }

    /// <summary>
    /// On detach, cancel any in-flight drag + drop the TopLevel key handler so a
    /// detachment mid-drag (view switch, window close) leaves no dangling state
    /// or handler.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelDragCore();
        base.OnDetachedFromVisualTree(e);
    }
}
