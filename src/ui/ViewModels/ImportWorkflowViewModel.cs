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
/// The focused, application-lifetime child VM behind the inline import card
/// under the Mods toolbar. Owns two exclusive modes over one editing form:
/// the ordered batch import of picked/dropped paths (the per-item editing
/// form, the three-state workflow, the per-item import orchestration) and the
/// single-container edit mode (the universal correction surface for a mod's
/// import details: name, source association, release tag, applied through
/// <see cref="IModRepository.EditImportDetails"/>). Emits the narrow
/// <see cref="ItemImported"/> event (carrying the captured target profile id)
/// and <see cref="ImportDetailsEdited"/> (carrying the edited container id)
/// so the mod list can reload when relevant.
/// </summary>
/// <remarks>
/// <para><b>Focused child VM:</b> the card is a distinct concern from the mod
/// list itself, so it lives in its own VM rather than more members on the
/// already large <see cref="ModListViewModel"/>. Constructed once
/// (application-lifetime) and reused across batches + edits; navigating away
/// from Mods preserves an in-flight card because the VM is not tied to the
/// view.</para>
/// <para><b>One form, two kinds:</b> batch + edit share the editing fields
/// and their validation (the shared <see cref="ImportSourceValidator"/>), but
/// never their lifecycle: a <c>WorkflowKind</c> flag selects the mode at
/// activation and the batch-only members (the path queue, the current index,
/// the processing hop, the terminal failure state) are unreachable in edit
/// mode, whose save is synchronous and whose refusals surface inline while
/// the form stays editable. The two modes are mutually exclusive: a batch
/// cannot start while an edit is active and vice versa (both entries check
/// the shared inactive gate first), and the card being active at all gates
/// the Add button + drag-and-drop for either mode.</para>
/// <para><b>Edit mode:</b> prefilled from the container's current facts
/// (name, source choice, the bare mod id, the latest version's tag). A
/// downloaded container never opens the card (any version carrying a FileId
/// or a RemoteUploadedAt grounds it: the row's pencil is hidden and
/// <c>StartEdit</c> refuses; the primitive enforces the same refusal). The
/// policy picker hides (policy is per-row, not import details). The name
/// field is editable only for the Untracked choice (a Nexus mod's name comes
/// from Nexus and the update check's name-sync would revert a user-typed
/// name); the id, version, and source switch stay editable. A multi-version
/// identity change swaps the form for an inline removal confirm (never a
/// nested modal), with the save-time state refresh + the typed
/// <see cref="RemovalConfirmationRequiredException"/> recover path covering a
/// version landing while the card is open.</para>
/// <para><b>States:</b> inactive (no card shown), editing (the current item's
/// metadata form), processing (batch filesystem work in flight; all fields
/// and actions disabled), and failure (a terminal batch error with a Close
/// action). A second batch or an edit cannot start while the card is
/// active.</para>
/// <para><b>Cancellation boundary:</b> Cancel is available only while
/// editing. Once Import is clicked, the current synchronous atomic backend
/// operation finishes; there is no mid-extraction token. Darktide mods are
/// normally too small for mid-extraction cancellation to be useful, and
/// widening the synchronous backend contract for a token that is never
/// honored in practice would be misleading.</para>
/// <para><b>Profile capture:</b> an activated card captures the active
/// profile id at start. If the active profile changes while the card is
/// editing or showing a failure, it resets immediately. If it changes while
/// a batch item is processing, the confirmed item finishes against the
/// captured profile (an imported repository version must keep its profile
/// reference and a confirmed item is never silently redirected), then the
/// remaining queue is aborted and the card resets. A success or a failure
/// (expected or unexpected) that lands after the active profile changed
/// resets rather than showing a failure card or a pending indicator over the
/// newly active profile.</para>
/// <para><b>Threads:</b> only the filesystem-heavy <c>GetBaseName</c> and
/// <c>Import</c> calls run via <see cref="Task.Run"/>; the continuation
/// resumes the captured UI context between them so <c>FindExistingContainer</c>,
/// <c>GetBaseNameCollision</c>, the repository lookup, and the profile
/// mutation (which carry a single-UI-thread assumption) never run on a
/// worker. The edit-mode save is synchronous (manifest-level repository
/// work). No <c>ConfigureAwait(false)</c>.</para>
/// <para><b>Recovery:</b> every batch path after processing begins ends in
/// editing (the next item), inactive (closed/reset), or failure. Expected
/// import/validation failures and collisions show an inline failure card with
/// the actionable detail; an unexpected exception from a dependency shows a
/// generic inline message (technical details are logged, not exposed). Edit
/// refusals (the primitive's guards, the untracked-name conflict, disk
/// failures) surface as inline edit failures with the form still editable;
/// no path strands processing or crashes through a command's calling
/// context.</para>
/// </remarks>
public partial class ImportWorkflowViewModel : LocalizedViewModel
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly ModCardsGate _cards;
    private readonly ILogger<ImportWorkflowViewModel> _logger;

    private WorkflowState _state = WorkflowState.Inactive;
    private WorkflowKind _kind = WorkflowKind.None;
    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private int _currentIndex;
    private Guid? _capturedProfileId;
    // Set when the active profile changes during processing: the current item
    // finishes against the captured profile, then the workflow resets instead
    // of advancing, and the new active profile is never marked pending.
    private bool _abortAfterCurrent;

    // ---- edit-mode state (never touched by the batch path) ------------------
    //
    // The edit mode reuses the observable form fields above; these carry the
    // container being edited + the facts the removal-confirm decision reads.
    // All of it is cleared by Reset() alongside the batch state, so an
    // inactive card is mode-neutral.

    /// <summary>The container being edited (null outside edit mode).</summary>
    private Guid? _editContainerId;

    /// <summary>The container's source at activation, for identity-change detection.</summary>
    private ModSource _editOriginalSource = new UntrackedSource();

    /// <summary>
    /// The container's version count at the last refresh (activation + each
    /// save attempt), driving the removal-confirm decision + its copy.
    /// </summary>
    private int _editVersionCount;

    /// <summary>The edit mode's stage (form vs. the inline removal confirm).</summary>
    private EditStage _editStage = EditStage.Form;

    /// <summary>
    /// The raw failure detail of the last refused edit save, or null. The
    /// localized <see cref="EditFailureMessage"/> derives from it on every
    /// access so a culture change truly re-resolves it.
    /// </summary>
    private string? _editFailureDetail;

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
        ModCardsGate cards,
        LocalizationService localization,
        ILogger<ImportWorkflowViewModel> logger)
        : base(localization)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _session.PropertyChanged += OnSessionPropertyChanged;
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

    /// <summary>
    /// Which mode the active card is in: the batch import (the path queue +
    /// per-item orchestration) or the single-container edit (the correction
    /// surface). None while inactive. The modes share the editing fields but
    /// never their lifecycle (see the class remarks).
    /// </summary>
    private enum WorkflowKind { None, Batch, Edit }

    /// <summary>
    /// The edit mode's two visibility-swapped stages: the editing form and
    /// the inline identity-removal confirm (never a nested modal; the card is
    /// a hosted view, not a window).
    /// </summary>
    private enum EditStage { Form, Confirm }

    // ---- observable editing fields -----------------------------------------

    /// <summary>
    /// The mod name (editable; pre-filled from the folder/archive stem in a
    /// batch, from the container's display name in edit mode). The
    /// mod-store key and on-disk folder name; an edited name becomes the
    /// canonical key (the import service upserts; the edit primitive renames).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditSaveCommand))]
    private string _modName = string.Empty;

    /// <summary>
    /// The chosen source. Drives which conditional fields show (Nexus: Version
    /// + URL; Untracked: nothing) and which validation applies. Defaults to
    /// Nexus for each new batch item (most Darktide mods ship on Nexus); an
    /// edit starts from the container's current source.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(IsPolicyVisible))]
    [NotifyPropertyChangedFor(nameof(IsNameEditable))]
    [NotifyPropertyChangedFor(nameof(SourceChoiceIndex))]
    [NotifyPropertyChangedFor(nameof(UrlLabel))]
    [NotifyPropertyChangedFor(nameof(UrlPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyPropertyChangedFor(nameof(RequiresIdentityConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditSaveCommand))]
    private ImportSource _sourceChoice = ImportSource.Nexus;

    /// <summary>
    /// The raw release tag string (e.g. <c>"1.2"</c>). Required for Nexus (the
    /// user supplies the tag; the workflow does not fetch it from the remote);
    /// recorded as empty for Untracked. Never parsed or normalized here. In
    /// edit mode this is the latest version's tag of an ungrounded container
    /// (a downloaded container never opens the card).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyPropertyChangedFor(nameof(RequiresIdentityConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditSaveCommand))]
    private string _version = string.Empty;

    /// <summary>
    /// The remote source URL or bare mod id (shown for Nexus). Parsed to
    /// canonical identity on confirm. Ignored for Untracked. Edit mode
    /// prefills the bare id form and locks the field when the identity is
    /// FileId-grounded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(RequiresIdentityConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ImportCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditSaveCommand))]
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

            return ImportSourceValidator.TryParseUrl(SourceChoice, url, out _)
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
    /// source additionally needs a non-empty Version + a URL that parses (the
    /// shared <see cref="ImportSourceValidator"/> rules). Untracked needs only
    /// the name. The edit-mode Save uses the same conjunction: both modes
    /// validate identically by design.
    /// </summary>
    public bool CanImport =>
        !string.IsNullOrWhiteSpace(ModName)
        && ImportSourceValidator.IsRemoteSourceValid(SourceChoice, Url ?? string.Empty, Version ?? string.Empty);

    // ---- mode + edit-mode projections ---------------------------------------

    /// <summary>
    /// Whether the active card is in edit mode (the correction surface for one
    /// container's import details) rather than a batch import. False while
    /// inactive (the kind resets with the card).
    /// </summary>
    public bool IsEdit => _kind == WorkflowKind.Edit;

    /// <summary>
    /// Whether the top-of-page card hosts the workflow: true only while a
    /// BATCH is active (editing, processing, or terminal failure). The edit
    /// mode never uses the top card: it renders as an in-row band on the
    /// edited row (the mod list's edit-target projection, keyed on
    /// <see cref="EditTargetContainerId"/>), so the top host binds this
    /// batch-only projection.
    /// </summary>
    public bool IsBatchActive => IsActive && !IsEdit;

    /// <summary>
    /// The container being edited while the edit mode is active, else null.
    /// The mod list's edit-target projection (which row carries the in-row
    /// band) reads this through the shared property-change subscription (the
    /// <c>IsListToolingEnabled</c> propagation shape): re-fired by
    /// <c>SetState</c> on every activation + reset, so a save, cancel, or
    /// profile-switch reset clears it and the parent re-assigns the row flags
    /// at once. A mid-edit reload keeps the target (it is a container id, not
    /// a row instance) and the parent re-attaches the band to the rebuilt row.
    /// </summary>
    public Guid? EditTargetContainerId => IsEdit ? _editContainerId : null;

    /// <summary>
    /// Whether the edit mode is on its inline removal-confirm stage (the
    /// visibility-swapped second step; the form hides while it shows).
    /// </summary>
    public bool IsEditConfirm => IsEdit && _editStage == EditStage.Confirm;

    /// <summary>
    /// Whether the edit mode's editing form is showing (vs. its confirm
    /// stage). Drives the Save + Cancel buttons' visibility.
    /// </summary>
    public bool IsEditForm => IsEdit && !IsEditConfirm;

    /// <summary>
    /// Whether the batch editing form is showing (the per-item form + Cancel
    /// batch + Import). Drives the batch buttons' visibility; always false in
    /// edit mode.
    /// </summary>
    public bool IsBatchEditing => IsEditing && !IsEdit;

    /// <summary>
    /// Whether the editing form grid shows at all: not during the terminal
    /// batch failure, and not while the edit mode's confirm stage owns the
    /// card (the visibility swap).
    /// </summary>
    public bool IsFormVisible => !IsFailure && !IsEditConfirm;

    /// <summary>
    /// Whether the policy picker row shows: Nexus chosen AND batch mode. In
    /// edit mode the picker hides (policy is a per-row profile setting, not
    /// part of a container's import details).
    /// </summary>
    public bool IsPolicyVisible => IsRemote && !IsEdit;

    /// <summary>
    /// Whether the mod name field accepts input: always in batch mode; in
    /// edit mode only for the Untracked CHOICE. The name is the identity for
    /// an untracked container (rename is a real correction), while a Nexus
    /// mod's name comes from Nexus and the update check's name-sync renames
    /// the container when Nexus's name changes, so a user-typed name would be
    /// reverted. The editability follows the chosen source: switching an
    /// Untracked container's choice to Nexus disables the field mid-edit with
    /// its in-memory value (the save keeps the name it had).
    /// </summary>
    public bool IsNameEditable => !IsEdit || SourceChoice == ImportSource.Untracked;

    /// <summary>
    /// Whether the edit form's identity (the source record) differs from the
    /// container's current one: a different Nexus id, or a Nexus/Untracked
    /// swap in either direction. A retag alone is not an identity change.
    /// </summary>
    public bool IsEditIdentityChange
    {
        get
        {
            if (!IsEdit)
            {
                return false;
            }

            ModSource current;
            if (SourceChoice == ImportSource.Untracked)
            {
                current = new UntrackedSource();
            }
            else if (!ImportSourceValidator.TryParseUrl(SourceChoice, Url ?? string.Empty, out var parsed))
            {
                // An unparsable id is not a saveable identity; treat it as a
                // change so a multi-version confirm is never skipped on a
                // technicality (CanImport blocks the save itself).
                return true;
            }
            else
            {
                current = parsed;
            }

            return (_editOriginalSource, current) switch
            {
                (NexusSource a, NexusSource b) => a.ModId != b.ModId,
                (UntrackedSource, UntrackedSource) => false,
                _ => true,
            };
        }
    }

    /// <summary>
    /// Whether saving the edit form requires the explicit removal confirm:
    /// an identity change on a container with more than one version (the
    /// older versions are claims about the old identity and are removed).
    /// </summary>
    public bool RequiresIdentityConfirm => IsEditIdentityChange && _editVersionCount > 1;

    /// <summary>The localized title of the inline removal-confirm stage.</summary>
    public string ConfirmTitle => _localization["EditDetails_ConfirmTitle"];

    /// <summary>
    /// The localized plain-language removal notice, formatted with the number
    /// of older versions the identity change removes. Re-fires on the
    /// save-time state refresh (a version landing while the card is open
    /// changes the count).
    /// </summary>
    public string ConfirmMessage => _localization.Format(
        "EditDetails_ConfirmMessage", Math.Max(0, _editVersionCount - 1));

    /// <summary>
    /// The localized inline failure of the last refused edit save: the framing
    /// plus the actionable detail (the primitive's guards, the untracked-name
    /// conflict, the disk failure families). Empty when the last attempt
    /// succeeded or none ran; the form stays editable for correction. Re-fires
    /// on a culture change (the detail is stored raw, the framing resolves
    /// live).
    /// </summary>
    public string EditFailureMessage => _editFailureDetail is null
        ? string.Empty
        : _localization["EditDetails_FailedMessage"] + " " + _editFailureDetail;

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
    /// The localized header text: "Edit import details" while the edit mode
    /// owns the card, "Import mod {current} of {total}" in a batch. Empty when
    /// inactive. Re-resolves on a culture change.
    /// </summary>
    public string HeaderText
    {
        get
        {
            if (_state == WorkflowState.Inactive)
            {
                return string.Empty;
            }

            return IsEdit
                ? _localization["EditDetails_Title"]
                : _localization.Format("ImportWorkflow_Header", CurrentNumber, TotalCount);
        }
    }

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

    /// <summary>
    /// Raised after a successful edit-mode save, carrying the edited container
    /// id. The mod list reloads from it (the container's name, source, and
    /// version can all have changed). Narrow by design, the
    /// <see cref="ItemImported"/> shape: a notification, not a lifecycle
    /// interface.
    /// </summary>
    public event EventHandler<Guid>? ImportDetailsEdited;

    // ---- start batch -------------------------------------------------------

    /// <summary>
    /// Starts a new batch from the picker/drop paths. Captures an ordered copy
    /// of the paths and the active profile id, loads the first item into the
    /// editing form, and transitions to editing. Rejects (no-op) a second
    /// activation while the card is active (a batch, an edit, a processing
    /// item, or a failure; the view gates the Add button and drop acceptance,
    /// but the VM repeats the gate so a programmatic call or a late-returning
    /// picker cannot start over an active card). No-op with no active profile
    /// or an empty path list.
    /// </summary>
    [RelayCommand]
    private void StartBatch(IReadOnlyList<string>? paths)
    {
        if (_state != WorkflowState.Inactive || _cards.IsAnyOtherCardActive(this))
        {
            _logger.LogWarning(
                "Import batch start rejected: the card is already active ({State}) or another hosted card is.",
                _state);
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
        _kind = WorkflowKind.Batch;
        LoadCurrentItem();
    }

    // ---- edit mode (the correction surface) ---------------------------------

    /// <summary>
    /// Starts an edit of one container's import details: the card activates in
    /// place, prefilled from the container's current facts (name, source
    /// choice, the bare mod id when Nexus, the latest version's tag). Rejects
    /// (no-op) another activation while the card is active (a batch in any
    /// state or an edit already open), for an unknown, linked, or
    /// download-grounded container (a grounded container is not editable at
    /// all: any version carrying a FileId or a RemoteUploadedAt grounds it;
    /// the row's pencil is already disabled, this gate is defense in depth),
    /// or with no active profile (the edited row lives in one). The parent's
    /// row command keeps its own linked/morphed guards; this gate is defense
    /// in depth.
    /// </summary>
    [RelayCommand]
    private void StartEdit(Guid? containerId)
    {
        if (_state != WorkflowState.Inactive || _cards.IsAnyOtherCardActive(this))
        {
            _logger.LogWarning(
                "Edit start rejected: the card is already active ({State}) or another hosted card is.",
                _state);
            return;
        }

        if (containerId is not Guid id)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid profileId)
        {
            _logger.LogWarning("Edit start rejected: no active profile.");
            return;
        }

        var container = _repo.Get(id);
        if (container is null || container.Source is LinkedSource)
        {
            _logger.LogWarning("Edit start rejected: container {Id} is missing or linked.", id);
            return;
        }

        // Downloaded mods are not editable (the row's pencil is hidden for
        // a grounded container; this gate is defense in depth): a version
        // carrying a FileId OR a RemoteUploadedAt (only the download path
        // records either) grounds the whole container.
        if (container.Versions.Any(v => v.FileId is not null || v.RemoteUploadedAt is not null))
        {
            _logger.LogWarning(
                "Edit start rejected: container {Id} was downloaded from Nexus (a version carries download evidence).", id);
            return;
        }

        _kind = WorkflowKind.Edit;
        _editContainerId = id;
        _capturedProfileId = profileId;
        _editStage = EditStage.Form;
        _editFailureDetail = null;
        RefreshEditState(container);

        // Prefill the shared form. Source first (switching to Untracked
        // clears Version), then the tag; the URL field carries the bare id
        // form, not the URL.
        ModName = container.Name;
        SourceChoice = container.Source is NexusSource nexus
            ? ImportSource.Nexus
            : ImportSource.Untracked;
        Url = container.Source is NexusSource n ? n.ModId.ToString() : string.Empty;
        Version = container.Versions.FirstOrDefault(v => v.IsLatest)?.VersionString ?? string.Empty;
        PolicyChoice = ImportPolicyChoice.Latest;
        SetState(WorkflowState.Editing);
        _logger.LogInformation("Started editing the import details of container {Id}.", id);
    }

    /// <summary>
    /// Re-reads the container's version count so the removal-confirm decision
    /// + its copy reflect the live container rather than the activation-time
    /// snapshot (a download for this container completing while the card is
    /// open adds a version the snapshot never saw; its save is then refused by
    /// the primitive's grounding guard, surfaced inline).
    /// </summary>
    private void RefreshEditState()
    {
        if (_editContainerId is not Guid id || _repo.Get(id) is not { } container)
        {
            // A vanished container surfaces through the save (ContainerGone);
            // the projections keep their last values.
            return;
        }

        RefreshEditState(container);
    }

    /// <summary>Applies a freshly read container's confirm-decision facts.</summary>
    private void RefreshEditState(ModContainer container)
    {
        _editOriginalSource = container.Source;
        _editVersionCount = container.Versions.Count;
        OnPropertyChanged(nameof(ConfirmMessage));
        OnPropertyChanged(nameof(RequiresIdentityConfirm));
    }

    /// <summary>
    /// Save (edit mode). When the fields require the removal confirm (an
    /// identity change on a multi-version container) the first click swaps the
    /// form for the inline confirm panel instead of applying; the confirm
    /// decision re-reads the live container first (a version may have landed
    /// while the card was open). No-op when the fields are invalid
    /// (<see cref="CanImport"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private void SaveEdit()
    {
        if (!IsEditForm || !CanImport)
        {
            return;
        }

        RefreshEditState();

        if (RequiresIdentityConfirm && _editStage == EditStage.Form)
        {
            _editStage = EditStage.Confirm;
            FireEditStage();
            return;
        }

        // A single-version identity change removes nothing (there are no
        // older versions), so the confirm flag stays false on this path.
        ApplyEdit(removeOlderVersions: _editStage == EditStage.Confirm && RequiresIdentityConfirm);
    }

    private bool CanSaveEdit => IsEditForm && CanImport;

    /// <summary>
    /// The confirm panel's explicit proceed: applies the save with
    /// older-version removal (the plain-language notice was shown + acted on).
    /// No-op when the fields are invalid.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirmEditSave))]
    private void ConfirmEditSave()
    {
        if (!IsEditConfirm || !CanImport)
        {
            return;
        }

        ApplyEdit(removeOlderVersions: true);
    }

    private bool CanConfirmEditSave => IsEditConfirm && CanImport;

    /// <summary>Back from the confirm panel to the editing form (no save).</summary>
    [RelayCommand]
    private void BackFromEditConfirm()
    {
        if (!IsEditConfirm)
        {
            return;
        }

        _editStage = EditStage.Form;
        FireEditStage();
    }

    /// <summary>
    /// Applies the edited facts through the repository primitive. Builds the
    /// canonical source from the validated fields (Untracked records an empty
    /// tag; Nexus parses the id/URL), calls
    /// <see cref="IModRepository.EditImportDetails"/>, and on success resets
    /// the card + raises <see cref="ImportDetailsEdited"/> so the mod list
    /// reloads. Refused saves surface as the inline edit failure with the form
    /// still editable: the primitive's guards, the untracked-name conflict
    /// check, and the disk failure families (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>; a full disk or an AV lock
    /// mid-save) are all caught the same way, never a crash through the
    /// command's calling context. The typed
    /// <see cref="RemovalConfirmationRequiredException"/> recover path swaps
    /// to the confirm stage over re-read state (the read-to-call race of the
    /// save-time refresh).
    /// </summary>
    private void ApplyEdit(bool removeOlderVersions)
    {
        if (_editContainerId is not Guid containerId)
        {
            return;
        }

        _editFailureDetail = null;
        OnPropertyChanged(nameof(EditFailureMessage));

        ModSource source;
        string tag;
        if (SourceChoice == ImportSource.Untracked)
        {
            source = new UntrackedSource();
            tag = string.Empty;
        }
        else
        {
            ImportSourceValidator.TryParseUrl(SourceChoice, Url ?? string.Empty, out source);
            tag = (Version ?? string.Empty).Trim();
        }

        var name = (ModName ?? string.Empty).Trim();

        // The untracked-name dedupe index is the identity for untracked
        // containers: saving under another untracked container's exact name
        // would silently shadow it in the index (later folder imports of that
        // mod would dedupe onto this one). Refuse inline.
        if (source is UntrackedSource
            && _repo.FindUntrackedByName(name)?.Id is { } conflicting
            && conflicting != containerId)
        {
            _editFailureDetail = _localization.Format("EditDetails_UntrackedNameConflict", name);
            OnPropertyChanged(nameof(EditFailureMessage));
            return;
        }

        try
        {
            var updated = _repo.EditImportDetails(
                containerId, name, source, tag, removeOlderVersions);
            if (updated is null)
            {
                // The container vanished between opening the card + saving.
                _editFailureDetail = _localization["EditDetails_ContainerGone"];
                OnPropertyChanged(nameof(EditFailureMessage));
                return;
            }
        }
        catch (RemovalConfirmationRequiredException)
        {
            // The version count went stale between the save-time refresh and
            // the primitive (a download for this container landed mid-save):
            // recover onto the confirm step over the fresh state instead of a
            // terminal inline failure the user cannot act on.
            RefreshEditState();
            _editStage = EditStage.Confirm;
            FireEditStage();
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            // A refused or failed save (the guards' messages are
            // user-actionable; the IO failures are transient disk state): show
            // the localized framing + the detail inline; the card stays open
            // for correction or cancel.
            _editFailureDetail = ex.Message;
            OnPropertyChanged(nameof(EditFailureMessage));
            return;
        }

        Reset();
        _logger.LogInformation(
            "Edited the import details of container {Id}.", containerId);
        ImportDetailsEdited?.Invoke(this, containerId);
    }

    /// <summary>
    /// Re-fires the stage-driven projections + the edit commands' CanExecute
    /// after an edit-stage transition (the visibility swap).
    /// </summary>
    private void FireEditStage()
    {
        OnPropertyChanged(nameof(IsEditConfirm));
        OnPropertyChanged(nameof(IsEditForm));
        OnPropertyChanged(nameof(IsFormVisible));
        SaveEditCommand.NotifyCanExecuteChanged();
        ConfirmEditSaveCommand.NotifyCanExecuteChanged();
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
        // CanImport through the shared validator).
        ModSource source;
        if (SourceChoice == ImportSource.Untracked)
        {
            source = new UntrackedSource();
            recordedVersion = string.Empty;
        }
        else
        {
            ImportSourceValidator.TryParseUrl(SourceChoice, Url ?? string.Empty, out source);
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

    // Batch-only by construction: the edit mode never enters Editing with a
    // path queue (its save path is SaveEdit), and the mode gate keeps a
    // programmatic ImportCurrentCommand call mid-edit from indexing an empty
    // _paths (defense in depth, matching the file's posture).
    private bool CanImportCurrent => IsBatchEditing && CanImport;

    // ---- cancel (editing only) --------------------------------------------

    /// <summary>
    /// Cancels the active card in either mode while it is editing: a batch
    /// clears its current and remaining paths (items already imported in
    /// earlier positions remain imported); an edit discards its staged fields
    /// with no repository write. Both hide the card. No-op when not editing
    /// (cancel is intentionally unavailable after a batch's Import is clicked;
    /// the edit mode has no post-Save state to cancel).
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
    /// URL label/placeholder, validation messages, failure messages, the edit
    /// mode's hint + confirm copy) without mutating the editing fields or the
    /// queue position.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(HeaderText),
        nameof(ProcessingText),
        nameof(UrlLabel),
        nameof(UrlPlaceholder),
        nameof(UrlValidationMessage),
        nameof(VersionValidationMessage),
        nameof(FailureMessage),
        nameof(ConfirmTitle),
        nameof(ConfirmMessage),
        nameof(EditFailureMessage),
    };

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Switching to Untracked clears the version field in either mode: an
    /// untracked mod carries no release tag (the field is hidden + the save
    /// records the empty tag), and ImportCurrentAsync forces the empty tag for
    /// Untracked regardless, so clearing is consistency, not behavior change.
    /// </summary>
    partial void OnSourceChoiceChanged(ImportSource value)
    {
        if (value == ImportSource.Untracked)
        {
            Version = string.Empty;
        }
    }

    /// <summary>
    /// Loads the current path into the editing form with fresh defaults (the
    /// derived name, Nexus, empty Version/URL, Latest) and transitions to
    /// editing. Called from <see cref="StartBatch"/> (first item) and
    /// <see cref="AdvanceOrClose"/> (next item); the mode flag is Batch by the
    /// time this runs.
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
    /// Resets the card to inactive: clears the batch state (paths, index,
    /// capture, abort flag), the edit state (container, grounding facts,
    /// stage, failure detail), and the mode flag, then re-fires all state
    /// projections. Editing fields are left as-is (the card is hidden), so the
    /// next activation prefills them (batch in <see cref="LoadCurrentItem"/>,
    /// edit in <see cref="StartEdit"/>).
    /// </summary>
    private void Reset()
    {
        _paths = Array.Empty<string>();
        _currentIndex = 0;
        _capturedProfileId = null;
        _abortAfterCurrent = false;
        _failure = null;
        _kind = WorkflowKind.None;
        _editContainerId = null;
        _editOriginalSource = new UntrackedSource();
        _editVersionCount = 0;
        _editStage = EditStage.Form;
        _editFailureDetail = null;
        SetState(WorkflowState.Inactive);
    }

    /// <summary>
    /// Sets the state and re-fires the state projections, the mode/stage
    /// projections, the current-item info (position/path/header), and the
    /// commands' CanExecute so the view and any programmatic caller reflect
    /// the new state at once. Also re-fires <see cref="FailureMessage"/> and
    /// <see cref="EditFailureMessage"/> so the derived getters re-read their
    /// descriptors after they change.
    /// </summary>
    private void SetState(WorkflowState newState)
    {
        _state = newState;
        // The shared hosted-card gate (the toolbar lock + Add disable + the
        // other card's start refusal read it). SetState is the one state
        // authority, so the report cannot drift from IsActive.
        _cards.ReportActive(this, newState != WorkflowState.Inactive);
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(IsEdit));
        OnPropertyChanged(nameof(IsBatchActive));
        OnPropertyChanged(nameof(EditTargetContainerId));
        OnPropertyChanged(nameof(IsEditConfirm));
        OnPropertyChanged(nameof(IsEditForm));
        OnPropertyChanged(nameof(IsBatchEditing));
        OnPropertyChanged(nameof(IsFormVisible));
        OnPropertyChanged(nameof(IsPolicyVisible));
        OnPropertyChanged(nameof(IsNameEditable));
        OnPropertyChanged(nameof(CurrentNumber));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(FailureMessage));
        OnPropertyChanged(nameof(EditFailureMessage));
        ImportCurrentCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        CloseFailureCommand.NotifyCanExecuteChanged();
        SaveEditCommand.NotifyCanExecuteChanged();
        ConfirmEditSaveCommand.NotifyCanExecuteChanged();
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
}
