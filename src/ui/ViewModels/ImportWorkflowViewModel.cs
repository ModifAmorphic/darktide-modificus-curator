using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The focused, application-lifetime child VM behind the inline local-import
/// card under the Mods toolbar. Owns the ordered batch of picked/dropped
/// paths, the editing form for the current item, the three-state workflow
/// (editing, processing, terminal failure), the per-item import orchestration,
/// and the active-profile-change contract. Emits a narrow
/// <see cref="ItemImported"/> event carrying the captured target profile id so
/// the mod list can reload when relevant.
/// </summary>
/// <remarks>
/// <para><b>Focused child VM:</b> the import workflow is a distinct concern
/// from the mod list itself, so it lives in its own VM rather than more members
/// on the already large <see cref="ModListViewModel"/>. Constructed once
/// (application-lifetime) and reused across batches; navigating away from Mods
/// preserves an in-flight card because the VM is not tied to the view.</para>
/// <para><b>States:</b> inactive (no card shown), editing (the current item's
/// metadata form), processing (filesystem work in flight; all fields and
/// actions disabled), and failure (a terminal inline error with a Close
/// action). A second batch cannot start while the workflow is active.</para>
/// <para><b>Cancellation boundary:</b> Cancel is available only while editing.
/// Once Import is clicked, the current synchronous atomic backend operation
/// finishes; there is no mid-extraction token. Darktide mods are normally too
/// small for mid-extraction cancellation to be useful, and widening the
/// synchronous backend contract for a token that is never honored in practice
/// would be misleading.</para>
/// <para><b>Profile capture:</b> a batch captures the active profile id at
/// <see cref="StartBatch"/> time. If the active profile changes while editing
/// or showing a failure, the workflow resets immediately. If it changes while
/// an item is processing, the confirmed item finishes against the captured
/// profile (an imported repository version must keep its profile reference and
/// a confirmed item is never silently redirected), then the remaining queue is
/// aborted and the workflow resets. A success or a failure (expected or
/// unexpected) that lands after the active profile changed resets rather than
/// showing a failure card or a pending indicator over the newly active
/// profile.</para>
/// <para><b>Threads:</b> only the filesystem-heavy <c>GetBaseName</c> and
/// <c>Import</c> calls run via <see cref="Task.Run"/>; the continuation resumes
/// the captured UI context between them so <c>FindExistingContainer</c>,
/// <c>GetBaseNameCollision</c>, the repository lookup, and the profile mutation
/// (which carry a single-UI-thread assumption) never run on a worker. No
/// <c>ConfigureAwait(false)</c>.</para>
/// <para><b>Recovery:</b> every exception path after processing begins ends in
/// editing (the next item), inactive (closed/reset), or failure. Expected
/// import/validation failures and collisions show an inline failure card with
/// the actionable detail; an unexpected exception from a dependency shows a
/// generic inline message (technical details are logged, not exposed). No path
/// strands processing or crashes through the command's calling context.</para>
/// </remarks>
public partial class ImportWorkflowViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly LocalizationService _localization;
    private readonly ILogger<ImportWorkflowViewModel> _logger;

    private WorkflowState _state = WorkflowState.Inactive;
    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private int _currentIndex;
    private Guid? _capturedProfileId;
    // Set when the active profile changes during processing: the current item
    // finishes against the captured profile, then the workflow resets instead
    // of advancing, and the new active profile is never marked pending.
    private bool _abortAfterCurrent;

    /// <summary>
    /// Creates the workflow VM, inactive, and subscribes to the session (reset
    /// on active-profile change) and localization (refresh derived labels on a
    /// culture change).
    /// </summary>
    public ImportWorkflowViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        LocalizationService localization,
        ILogger<ImportWorkflowViewModel> logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _session.PropertyChanged += OnSessionPropertyChanged;
        _localization.PropertyChanged += OnCultureChanged;
    }

    /// <summary>
    /// The source provenance options offered by the editing form's ComboBox
    /// (Untracked / Nexus), offered by the editing form's ComboBox.
    /// </summary>
    public enum ImportSource
    {
        /// <summary>Untracked import: no remote identity, no version.</summary>
        Untracked,

        /// <summary>Nexus Mods: collects a URL or bare mod id parsed to a mod id.</summary>
        Nexus,
    }

    /// <summary>
    /// The version-policy options offered by the editing form's ComboBox
    /// (Latest / Pinned), offered by the editing form's ComboBox. Pinned freezes the
    /// profile entry to the version being imported; the opaque version id is
    /// substituted from the import result.
    /// </summary>
    public enum ImportPolicyChoice
    {
        /// <summary>Track the container's newest release (the default).</summary>
        Latest,

        /// <summary>Pin the profile entry to the version being imported.</summary>
        Pinned,
    }

    /// <summary>Internal lifecycle state driving the observable projections.</summary>
    private enum WorkflowState { Inactive, Editing, Processing, Failure }

    // ---- observable editing fields -----------------------------------------

    /// <summary>
    /// The mod name (editable; pre-filled from the folder/archive stem). The
    /// mod-store key and on-disk folder name; an edited name becomes the
    /// canonical key (the import service upserts).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    private string _modName = string.Empty;

    /// <summary>
    /// The chosen source. Drives which conditional fields show (Nexus: Version
    /// + URL; Untracked: nothing) and which validation applies. Defaults to
    /// Nexus for each new item (most Darktide mods ship on Nexus).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(SourceChoiceIndex))]
    [NotifyPropertyChangedFor(nameof(UrlLabel))]
    [NotifyPropertyChangedFor(nameof(UrlPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    private ImportSource _sourceChoice = ImportSource.Nexus;

    /// <summary>
    /// The raw release tag string (e.g. <c>"1.2"</c>). Required for Nexus (the
    /// user supplies the tag; the workflow does not fetch it from the remote);
    /// recorded as empty for Untracked. Never parsed or normalized here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    private string _version = string.Empty;

    /// <summary>
    /// The remote source URL or bare mod id (shown for Nexus). Parsed to
    /// canonical identity on confirm. Ignored for Untracked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    private string _url = string.Empty;

    /// <summary>
    /// The version-policy choice (Latest or Pinned). Default Latest. For
    /// Pinned, the import result's opaque version id is substituted into the
    /// <see cref="PinnedPolicy"/> fed to <c>AddMod</c>.
    /// </summary>
    [ObservableProperty]
    private ImportPolicyChoice _policyChoice = ImportPolicyChoice.Latest;

    /// <summary>
    /// The current terminal-failure descriptor, or null when there is no
    /// failure. <see cref="FailureMessage"/> is derived from this through the
    /// live LocalizationService on every access so a culture change truly
    /// re-resolves it (the descriptor, not a frozen string, is what Reset
    /// clears and what a new failure replaces).
    /// </summary>
    private FailureDescriptor? _failure;

    // ---- ComboBox index adapters ------------------------------------------

    /// <summary>
    /// Integer adapter for the source ComboBox's <c>SelectedIndex</c>
    /// (0 = Untracked, 1 = Nexus), so the ComboBox binds two-way without a
    /// converter or view code-behind. Maps to/from <see cref="SourceChoice"/>.
    /// </summary>
    public int SourceChoiceIndex
    {
        get => (int)SourceChoice;
        set
        {
            var choice = (ImportSource)value;
            if (choice != SourceChoice)
            {
                SourceChoice = choice;
            }
        }
    }

    /// <summary>
    /// Integer adapter for the policy ComboBox's <c>SelectedIndex</c>
    /// (0 = Latest, 1 = Pinned). Maps to/from <see cref="PolicyChoice"/>.
    /// </summary>
    public int PolicyChoiceIndex
    {
        get => (int)PolicyChoice;
        set
        {
            var choice = (ImportPolicyChoice)value;
            if (choice != PolicyChoice)
            {
                PolicyChoice = choice;
            }
        }
    }

    // ---- derived editing projections --------------------------------------

    /// <summary>Whether a remote source (Nexus) is chosen, driving the Version
    /// + URL fields' visibility.</summary>
    public bool IsRemote => SourceChoice != ImportSource.Untracked;

    /// <summary>Whether the Version field is visible (Nexus). The field is
    /// required for the remote source.</summary>
    public bool IsVersionVisible => IsRemote;

    /// <summary>The localized label for the URL field (Nexus).</summary>
    public string UrlLabel => _localization["Import_NexusUrlLabel"];

    /// <summary>The localized placeholder for the URL field (Nexus).</summary>
    public string UrlPlaceholder => _localization["Import_UrlPlaceholderNexus"];

    /// <summary>
    /// The localized validation message for the URL field when the input is
    /// non-empty but does not parse, or the required message when empty for a
    /// remote source. Empty when there is nothing to show (Untracked, or a
    /// valid remote URL). Never throws.
    /// </summary>
    public string UrlValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            var url = Url?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                return _localization["Import_UrlRequired"];
            }

            return TryParseUrl(SourceChoice, url, out _)
                ? string.Empty
                : _localization["Import_UrlInvalid"];
        }
    }

    /// <summary>
    /// The localized validation message for the Version field when it is empty
    /// or whitespace for a remote source. Empty when there is nothing to show.
    /// Never throws.
    /// </summary>
    public string VersionValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(Version)
                ? _localization["Import_VersionRequired"]
                : string.Empty;
        }
    }

    /// <summary>
    /// Whether Import may be enabled. The mod name must be non-empty; a remote
    /// source additionally needs a non-empty Version + a URL that parses.
    /// Untracked needs only the name.
    /// </summary>
    public bool CanImport =>
        !string.IsNullOrWhiteSpace(ModName)
        && (!IsRemote
            || (!string.IsNullOrWhiteSpace(Version)
                && TryParseUrl(SourceChoice, Url ?? string.Empty, out _)));

    // ---- state projections -------------------------------------------------

    /// <summary>
    /// Whether the workflow card should be visible: true across editing,
    /// processing, and failure. Drives the Add split-button gate and the
    /// drag-and-drop acceptance in the view (the VM repeats the gate as defense
    /// in depth).
    /// </summary>
    public bool IsActive => _state != WorkflowState.Inactive;

    /// <summary>Whether the editing form is showing (fields + Cancel + Import).</summary>
    public bool IsEditing => _state == WorkflowState.Editing;

    /// <summary>Whether an import is in flight (all fields and actions
    /// disabled; an indeterminate ProgressBar + status text show).</summary>
    public bool IsProcessing => _state == WorkflowState.Processing;

    /// <summary>Whether a terminal inline failure is showing (header + error +
    /// Close only).</summary>
    public bool IsFailure => _state == WorkflowState.Failure;

    // ---- current-item info -------------------------------------------------

    /// <summary>The one-based position of the current item in the batch, or
    /// zero when inactive (no current item).</summary>
    public int CurrentNumber => _state == WorkflowState.Inactive ? 0 : _currentIndex + 1;

    /// <summary>The total number of paths in the captured batch.</summary>
    public int TotalCount => _paths.Count;

    /// <summary>
    /// The current item's source path (full path retained for the tooltip and
    /// automation name; the view character-ellipsizes the display). Empty when
    /// inactive.
    /// </summary>
    public string CurrentPath =>
        _state != WorkflowState.Inactive && _currentIndex < _paths.Count
            ? _paths[_currentIndex]
            : string.Empty;

    /// <summary>
    /// The localized header text: "Import mod {current} of {total}". Empty when
    /// inactive. Re-resolves on a culture change.
    /// </summary>
    public string HeaderText =>
        _state == WorkflowState.Inactive
            ? string.Empty
            : _localization.Format("ImportWorkflow_Header", CurrentNumber, TotalCount);

    /// <summary>
    /// The localized status text shown beside the progress bar while
    /// processing. Re-resolves on a culture change.
    /// </summary>
    public string ProcessingText => _localization["ImportWorkflow_Processing"];

    /// <summary>
    /// The localized terminal-failure message shown inline, derived from
    /// <see cref="_failure"/> through the live LocalizationService on every
    /// access so a culture change truly re-resolves it. Empty unless there is
    /// a current failure. An expected import/validation failure shows the
    /// localized framing plus the actionable detail; a collision shows the
    /// localized collision explanation; an unexpected exception shows a generic
    /// message without technical details.
    /// </summary>
    public string FailureMessage
    {
        get
        {
            if (_failure is not { } f)
            {
                return string.Empty;
            }

            return f.Kind switch
            {
                FailureKind.Import =>
                    _localization.Format("Import_FailedMessage", f.Path) + " " + f.Detail,
                FailureKind.Collision =>
                    _localization.Format("Import_CollisionMessage", f.Path, f.BaseName, f.ConflictingName),
                FailureKind.Unexpected =>
                    _localization["ImportWorkflow_UnexpectedFailure"],
                _ => string.Empty,
            };
        }
    }

    // ---- the narrow success event -----------------------------------------

    /// <summary>
    /// Raised after a successful per-item import + profile add, carrying the
    /// captured target profile id. The mod list reloads when the event's id is
    /// the active profile (otherwise the user is looking at a different profile
    /// and no reload is needed). Narrow by design: a notification, not a page
    /// lifecycle or import-coordinator interface.
    /// </summary>
    public event EventHandler<Guid>? ItemImported;

    // ---- start batch -------------------------------------------------------

    /// <summary>
    /// Starts a new batch from the picker/drop paths. Captures an ordered copy
    /// of the paths and the active profile id, loads the first item into the
    /// editing form, and transitions to editing. Rejects (no-op) a second batch
    /// while the workflow is active (the view gates the Add button and drop
    /// acceptance, but the VM repeats the gate so a programmatic call or a
    /// late-returning picker cannot start a second batch). No-op with no active
    /// profile or an empty path list.
    /// </summary>
    [RelayCommand]
    private void StartBatch(IReadOnlyList<string>? paths)
    {
        if (_state != WorkflowState.Inactive)
        {
            _logger.LogWarning("Import batch start rejected: workflow is already active ({State}).", _state);
            return;
        }

        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid profileId)
        {
            _logger.LogWarning("Import batch start rejected: no active profile.");
            return;
        }

        _paths = paths.ToArray();
        _currentIndex = 0;
        _capturedProfileId = profileId;
        _abortAfterCurrent = false;
        LoadCurrentItem();
    }

    // ---- import current ----------------------------------------------------

    /// <summary>
    /// Imports the current item. Transitions to processing, runs the
    /// filesystem-heavy <c>GetBaseName</c> and <c>Import</c> calls via
    /// <see cref="Task.Run"/> (resuming the captured UI context between them
    /// for the single-UI-thread queries and the profile mutation), then on
    /// success builds the policy, adds the container to the captured profile,
    /// marks pending changes when that profile is still active, raises
    /// <see cref="ItemImported"/>, and advances to the next item (or closes
    /// after the last). On a base-name collision or an expected validation/I/O
    /// failure, transitions to the terminal failure state with an inline
    /// message and aborts the remaining queue. An unexpected exception from a
    /// dependency recovers to a generic inline failure (technical details are
    /// logged, not exposed); if the profile changed mid-processing, any failure
    /// or collision resets instead of showing a card over the new profile. No
    /// path strands processing. No-op when not editing or when the fields are
    /// invalid.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportCurrent))]
    private async Task ImportCurrentAsync()
    {
        if (_state != WorkflowState.Editing || !CanImport)
        {
            return;
        }

        if (_capturedProfileId is not Guid profileId)
        {
            // Unreachable (captured at StartBatch), but defensive.
            return;
        }

        var path = _paths[_currentIndex];
        var name = ModName.Trim();
        var recordedVersion = (Version ?? string.Empty).Trim();
        var policyChoice = PolicyChoice;

        // Build the canonical source from the validated fields. Untracked
        // records an empty version; Nexus parses the URL (guaranteed by
        // CanImport).
        ModSource source;
        if (SourceChoice == ImportSource.Untracked)
        {
            source = new UntrackedSource();
            recordedVersion = string.Empty;
        }
        else
        {
            TryParseUrl(SourceChoice, Url ?? string.Empty, out source);
        }

        SetState(WorkflowState.Processing);

        // The import body runs under a broad recovery guard: every path ends in
        // Editing (advance), Inactive (close/reset), or Failure. No path strands
        // Processing or crashes through the AsyncRelayCommand calling context.
        try
        {
            // (1) Get and validate the base folder name on the worker
            // (filesystem-heavy). No ConfigureAwait(false): the continuation
            // resumes the captured UI context for the queries below.
            string baseName;
            try
            {
                baseName = await Task.Run(() => _importService.GetBaseName(path));
            }
            catch (Exception ex) when (IsExpectedImportException(ex))
            {
                FailImport(path, ex);
                return;
            }

            // (2-3) Resolve the would-be container and check the captured
            // profile for a base-name collision (excluding a re-add) on the
            // captured UI context. ProfileService has a single-UI-thread
            // assumption and must not be read from a worker while row/profile
            // writes may occur.
            var existing = _importService.FindExistingContainer(source, name);
            var collision = _profiles.GetBaseNameCollision(profileId, baseName, existing?.Id);
            if (collision is not null)
            {
                var conflictingName = _repo.Get(collision.ContainerId)?.Name ?? baseName;
                FailCollision(path, baseName, conflictingName);
                return;
            }

            // (4) Import/extract/copy into the repository on the worker
            // (filesystem-heavy). Resume the captured UI context for the
            // profile mutation below.
            Guid containerId;
            string versionId;
            try
            {
                (containerId, versionId) = await Task.Run(() =>
                    _importService.Import(path, name, source, recordedVersion));
            }
            catch (Exception ex) when (IsExpectedImportException(ex))
            {
                FailImport(path, ex);
                return;
            }

            // (5-6) Convert the Pinned choice to a real PinnedPolicy (using the
            // imported version's opaque id) and add the container to the
            // captured target profile, on the captured UI context.
            var policy = policyChoice == ImportPolicyChoice.Pinned
                ? new PinnedPolicy(versionId)
                : ModVersionPolicy.Latest;

            try
            {
                _profiles.AddMod(profileId, containerId, policy);
            }
            catch (Exception ex)
            {
                // The import succeeded in the repository but the profile
                // reference failed. The repo copy is unreferenced (the startup
                // prune reclaims it); do NOT emit ItemImported or mark pending.
                _logger.LogError(ex,
                    "AddMod for container {Container} on profile {Profile} failed after import.",
                    containerId, profileId);
                FailWith(FailureDescriptor.Unexpected(path));
                return;
            }

            _logger.LogInformation(
                "Imported {Mod} from {Path} (source={Source}, version={Version}, policy={Policy}) onto container {Container} for profile {Profile}.",
                name, path, source, recordedVersion, policy, containerId, profileId);
        }
        catch (Exception ex)
        {
            // Unexpected: not an expected import/validation failure (caught by
            // the inner guards) and not an AddMod failure (caught above).
            // Recover to a terminal inline failure (or a reset if the profile
            // changed mid-processing). Never strand Processing.
            _logger.LogError(ex, "Import of {Path} failed unexpectedly.", path);
            FailWith(FailureDescriptor.Unexpected(path));
            return;
        }

        // (7) Success: mark pending when the captured profile is still active
        // and the batch was not aborted, then notify. A subscriber exception
        // here must not strand Processing or show a failure card over a
        // successful import.
        try
        {
            if (!_abortAfterCurrent && _session.ActiveProfileId == profileId)
            {
                _session.HasPendingChanges = true;
            }

            // The notification always fires: it carries the captured profile id
            // so the mod list can decide whether a reload is relevant.
            ItemImported?.Invoke(this, profileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Success notification for {Path} threw; the import landed.", path);
        }

        if (_abortAfterCurrent)
        {
            // Profile changed during processing: the confirmed item finished
            // against the captured profile; abort the remaining queue and reset.
            Reset();
            return;
        }

        AdvanceOrClose();
    }

    private bool CanImportCurrent => _state == WorkflowState.Editing && CanImport;

    // ---- cancel (editing only) --------------------------------------------

    /// <summary>
    /// Cancels the batch while editing: clears the current and remaining paths
    /// and hides the card. No-op when not editing (cancel is intentionally
    /// unavailable after Import is clicked). Items already imported in earlier
    /// positions remain imported.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelBatch))]
    private void CancelBatch() => Reset();

    private bool CanCancelBatch => _state == WorkflowState.Editing;

    // ---- close failure -----------------------------------------------------

    /// <summary>
    /// Closes the terminal failure and resets the workflow so a new picker/drop
    /// batch may start. No-op when not in the failure state.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseFailure))]
    private void CloseFailure() => Reset();

    private bool CanCloseFailure => _state == WorkflowState.Failure;

    // ---- profile-change + culture-change handling -------------------------

    /// <summary>
    /// Session-driven: the active profile changed. While editing or showing a
    /// failure, reset the workflow immediately. While an item is processing,
    /// let the confirmed item finish against the captured profile, then abort
    /// the remaining queue and reset (the import's continuation checks
    /// <see cref="_abortAfterCurrent"/>). No-op when inactive or when the id
    /// did not actually change away from the captured profile.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId))
        {
            return;
        }

        // Only act on a real change away from the captured profile. A batch
        // that captured profile X stays put if the session is still on X.
        if (_capturedProfileId is not Guid captured
            || _session.ActiveProfileId == captured)
        {
            return;
        }

        switch (_state)
        {
            case WorkflowState.Editing:
            case WorkflowState.Failure:
                _logger.LogInformation(
                    "Active profile changed to {NewId} during {State}; resetting the import workflow.",
                    _session.ActiveProfileId, _state);
                Reset();
                break;
            case WorkflowState.Processing:
                _logger.LogInformation(
                    "Active profile changed to {NewId} during processing; finishing the current item against {Captured}, then resetting.",
                    _session.ActiveProfileId, captured);
                _abortAfterCurrent = true;
                break;
        }
    }

    /// <summary>
    /// Culture changed: re-fire the localized derived strings (header, status,
    /// URL label/placeholder, validation messages, failure message) without
    /// mutating the editing fields or the queue position.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)
            && e.PropertyName != "Item[]")
        {
            return;
        }

        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ProcessingText));
        OnPropertyChanged(nameof(UrlLabel));
        OnPropertyChanged(nameof(UrlPlaceholder));
        OnPropertyChanged(nameof(UrlValidationMessage));
        OnPropertyChanged(nameof(VersionValidationMessage));
        OnPropertyChanged(nameof(FailureMessage));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Loads the current path into the editing form with fresh defaults (the
    /// derived name, Nexus, empty Version/URL, Latest) and transitions to
    /// editing. Called from <see cref="StartBatch"/> (first item) and
    /// <see cref="AdvanceOrClose"/> (next item).
    /// </summary>
    private void LoadCurrentItem()
    {
        var path = _paths[_currentIndex];
        ModName = DeriveModName(path);
        SourceChoice = ImportSource.Nexus;
        Version = string.Empty;
        Url = string.Empty;
        PolicyChoice = ImportPolicyChoice.Latest;
        _failure = null;
        SetState(WorkflowState.Editing);
    }

    /// <summary>
    /// Advances to the next path (editing) or closes the workflow after the
    /// last item (inactive). Called only on a non-aborted success.
    /// </summary>
    private void AdvanceOrClose()
    {
        _currentIndex++;
        if (_currentIndex >= _paths.Count)
        {
            Reset();
            return;
        }

        LoadCurrentItem();
    }

    /// <summary>
    /// Resets the workflow to inactive: clears the paths, index, capture, and
    /// abort flag, and re-fires all state projections. Editing fields are left
    /// as-is (the card is hidden), so the next batch resets them in
    /// <see cref="LoadCurrentItem"/>.
    /// </summary>
    private void Reset()
    {
        _paths = Array.Empty<string>();
        _currentIndex = 0;
        _capturedProfileId = null;
        _abortAfterCurrent = false;
        _failure = null;
        SetState(WorkflowState.Inactive);
    }

    /// <summary>
    /// Sets the state and re-fires the state projections, the current-item info
    /// (position/path/header), and the commands' CanExecute so the view and any
    /// programmatic caller reflect the new state at once. Also re-fires
    /// <see cref="FailureMessage"/> so the derived getter re-reads the
    /// descriptor after it changes.
    /// </summary>
    private void SetState(WorkflowState newState)
    {
        _state = newState;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(CurrentNumber));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(FailureMessage));
        ImportCurrentCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        CloseFailureCommand.NotifyCanExecuteChanged();
    }

    // ---- failure recording -------------------------------------------------

    /// <summary>
    /// Records an expected import/source failure: logs the exception and routes
    /// through <see cref="FailWith"/>. If the profile changed mid-processing,
    /// resets instead of showing a failure card.
    /// </summary>
    private void FailImport(string path, Exception ex)
    {
        _logger.LogError(ex, "Import of {Path} failed.", path);
        FailWith(FailureDescriptor.Import(path, ex.Message));
    }

    /// <summary>
    /// Records a base-name collision: logs the conflict and routes through
    /// <see cref="FailWith"/>. If the profile changed mid-processing, resets
    /// instead of showing a failure card.
    /// </summary>
    private void FailCollision(string path, string baseName, string conflictingName)
    {
        _logger.LogWarning(
            "Import blocked at {Path}: base folder '{Base}' collides with existing mod '{Conflicting}'.",
            path, baseName, conflictingName);
        FailWith(FailureDescriptor.Collision(path, baseName, conflictingName));
    }

    /// <summary>
    /// Applies a failure descriptor: if the profile changed mid-processing
    /// (<see cref="_abortAfterCurrent"/>), logs and resets so no failure card
    /// appears over the newly active profile; otherwise stores the descriptor
    /// and transitions to the terminal failure state (the header/current path
    /// are retained, the editor and processing controls hidden, only Close
    /// shows). Earlier successes remain imported.
    /// </summary>
    private void FailWith(FailureDescriptor descriptor)
    {
        if (_abortAfterCurrent)
        {
            _logger.LogInformation(
                "Active profile changed during processing; resetting after the failure ({Kind}).",
                descriptor.Kind);
            Reset();
            return;
        }

        _failure = descriptor;
        SetState(WorkflowState.Failure);
    }

    /// <summary>
    /// The expected exception families for a base-name peek or an import
    /// failure, mirroring the existing add flow. Caught and surfaced as an
    /// actionable inline failure; any other exception is unexpected and recovers
    /// to a generic message (technical details are logged, not exposed).
    /// </summary>
    private static bool IsExpectedImportException(Exception ex) =>
        ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException
            or System.IO.InvalidDataException;

    /// <summary>
    /// The kind of terminal failure recorded by the workflow. Drives the
    /// <see cref="FailureMessage"/> derived getter's formatting.
    /// </summary>
    private enum FailureKind { Import, Collision, Unexpected }

    /// <summary>
    /// A durable failure descriptor: the kind plus the raw arguments needed to
    /// format the localized message on every <see cref="FailureMessage"/>
    /// access. Stored (not preformatted) so a culture change truly re-resolves
    /// the message.
    /// </summary>
    private sealed record FailureDescriptor(
        FailureKind Kind,
        string Path,
        string BaseName,
        string ConflictingName,
        string Detail)
    {
        public static FailureDescriptor Import(string path, string detail) =>
            new(FailureKind.Import, path, string.Empty, string.Empty, detail);

        public static FailureDescriptor Collision(string path, string baseName, string conflictingName) =>
            new(FailureKind.Collision, path, baseName, conflictingName, string.Empty);

        public static FailureDescriptor Unexpected(string path) =>
            new(FailureKind.Unexpected, path, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// Derives the default mod name from a path: the folder name, or the
    /// archive stem (any extension stripped). Falls back to the raw path when
    /// the stem is empty. Mirrors the existing add flow.
    /// </summary>
    private static string DeriveModName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>
    /// Parses the URL/id for the chosen source into a canonical
    /// <see cref="ModSource"/>. Never throws. Mirrors the editing form's field semantics.
    /// </summary>
    private static bool TryParseUrl(ImportSource source, string url, out ModSource parsed)
    {
        parsed = new UntrackedSource();
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        switch (source)
        {
            case ImportSource.Nexus:
                if (ModSourceParser.TryParseNexus(url, out var nexus))
                {
                    parsed = nexus;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }
}
