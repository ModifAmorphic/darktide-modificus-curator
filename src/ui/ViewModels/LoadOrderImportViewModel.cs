using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Modificus.Curator.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One search candidate proposed for an unresolved row: the mod's canonical
/// title + its Nexus mod id. The candidate area renders the title under Match
/// and the <c>#id</c> under Mod ID; accepting records both on the row.
/// </summary>
/// <param name="ModId">The Nexus mod id.</param>
/// <param name="Name">The mod's canonical title.</param>
public sealed record NexusSearchCandidate(int ModId, string Name);

/// <summary>
/// Which operation the user chose for the picked load-order file. The choice
/// is made once, before any Nexus traffic: reorder-only performs no network
/// work at all; reorder-and-import adds the sibling scan, the anonymous
/// search queue, and (for Premium) the download enqueues.
/// </summary>
public enum LoadOrderImportMode
{
    /// <summary>Reorder only: profile matches move to their file positions;
    /// every other line stays visible as skipped.</summary>
    Reorder,

    /// <summary>Reorder plus acquisition: library matches are added, sibling
    /// folders imported, and missing mods identified and (for Premium)
    /// downloaded.</summary>
    ReorderAndImport,
}

/// <summary>
/// The workspace's current stage: no session, the mode choice, or the review.
/// The single state source every workspace projection derives from.
/// </summary>
public enum LoadOrderStage
{
    /// <summary>No session; the Mods destination shows its normal content.</summary>
    Inactive,

    /// <summary>The file is picked; the two mode tiles are offered.</summary>
    ChoosingMode,

    /// <summary>A mode was chosen; the review list is showing.</summary>
    Reviewing,
}

/// <summary>
/// One review row: a single parsed file line's reconciliation result. Plain
/// state (the folder name, the reconciliation outcome, the skip flag, the
/// identification state); the parent
/// <see cref="LoadOrderImportViewModel"/> owns every action. Localized text
/// resolves through the injected <see cref="LocalizationService"/> and
/// re-fires via <see cref="Refresh"/> (the parent's culture hook).
/// </summary>
/// <remarks>
/// The row knows its session's <see cref="LoadOrderImportMode"/> + whether a
/// Premium account was available when the review was built (both fixed for
/// the session; the parent re-checks Premium at apply). Everything the row
/// renders derives from that trio plus its own observable state.
/// </remarks>
public partial class LoadOrderRowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly LoadOrderImportMode _mode;
    private readonly bool _premiumAvailable;

    /// <summary>
    /// Creates a row from a plan line. Rows are never included/excluded by a
    /// checkbox: in import mode every actionable row is included by default,
    /// and <see cref="IsSkipped"/> is the exceptional opt-out.
    /// </summary>
    public LoadOrderRowViewModel(
        LoadOrderLine line,
        LoadOrderImportMode mode,
        bool premiumAvailable,
        LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _mode = mode;
        _premiumAvailable = premiumAvailable;
        Name = line.Name;
        Outcome = line.Outcome;
        ContainerId = line.ContainerId;
        KnownModId = line.NexusModId;
        KnownVersion = line.Version;
        _repoMatchText = line.DisplayName ?? "-";
    }

    /// <summary>The file's folder name (the parsed line, trimmed).</summary>
    public string Name { get; }

    /// <summary>The reconciliation outcome driving the row's projections.</summary>
    public LoadOrderLineOutcome Outcome { get; }

    /// <summary>The session's import mode (fixed for the row's lifetime).</summary>
    public LoadOrderImportMode Mode => _mode;

    /// <summary>
    /// Whether the row renders the simpler reorder-only projection (the Mod
    /// ID + Version columns collapse; no lookup or skip affordances).
    /// </summary>
    public bool IsReorderProjection => _mode == LoadOrderImportMode.Reorder;

    /// <summary>
    /// The matched container, or null when unmatched. A sibling-import row
    /// receives the imported container's id at apply time.
    /// </summary>
    public Guid? ContainerId { get; internal set; }

    /// <summary>
    /// The Nexus mod id Curator already knows for a MATCHED container (a
    /// Nexus-sourced profile or library line), or null. A read-only fact the
    /// Mod ID column shows without Change/manual controls; unmatched lines
    /// carry null and identify through the review's lookup surface instead.
    /// </summary>
    public int? KnownModId { get; }

    /// <summary>
    /// The version tag this operation will use for a MATCHED container
    /// (policy-resolved for a profile entry, the resolved latest for a
    /// library add), or null when Curator knows none (linked rows, or an
    /// unresolvable resolution). An empty string is an honestly empty
    /// (unknown) tag, rendered blank.
    /// </summary>
    public string? KnownVersion { get; }

    /// <summary>
    /// The sibling mod folder this line imports from (the txt's own directory
    /// carries the content), or null for every other line kind.
    /// </summary>
    public string? SiblingPath { get; internal set; }

    private readonly string _repoMatchText;

    /// <summary>
    /// The Match column: the resolved mod's display name; the canonical title
    /// once an unresolved row is identified (the identification IS the match,
    /// rendered exactly once); the sibling folder's name for imports; or the
    /// localized "not found" for an unidentified missing line.
    /// </summary>
    public string MatchText =>
        Outcome == LoadOrderLineOutcome.Unresolved
            ? (IsIdentified ? IdentifiedName! : _localization["LoadOrder_OutcomeUnresolved"])
            : _repoMatchText;

    /// <summary>Whether the line resolved to nothing locally.</summary>
    public bool IsUnresolved => Outcome == LoadOrderLineOutcome.Unresolved;

    /// <summary>
    /// Whether this row participates in the identification surface (import
    /// mode only), gated by account capability for remote-only lines: a
    /// sibling-import line always offers lookups (local content exists +
    /// a Nexus association is useful at every tier), while an unresolved
    /// line (no local match anywhere) offers them only on the Premium
    /// account read at import-mode entry, because only Premium can act on a
    /// remote-only identification. Non-Premium, signed-out, and
    /// unknown-capability accounts see those rows as visibly non-actionable
    /// instead of review work that Apply would discard. Reorder mode performs
    /// no lookups, so no row offers them there.
    /// </summary>
    public bool IsLookupRow => _mode == LoadOrderImportMode.ReorderAndImport
        && (Outcome == LoadOrderLineOutcome.SiblingImport
            || (Outcome == LoadOrderLineOutcome.Unresolved && _premiumAvailable));

    /// <summary>
    /// The Action column: the plan as if applied, in consistent past tense:
    /// the operation each acted-on row receives ("reordered" / "added" /
    /// "imported" / "downloaded"), or "skipped" for every row the apply
    /// leaves untouched (opted-out rows, non-actionable remote lines on any
    /// account tier, and unidentified missing lines). The reasons live in
    /// the upfront capability notice + the mode tile, not the cell; live
    /// transient status (the search spinner) is separate.
    /// </summary>
    public string? ActionText
    {
        get
        {
            if (_mode == LoadOrderImportMode.Reorder)
            {
                return Outcome == LoadOrderLineOutcome.Reorder
                    ? _localization["LoadOrder_OutcomeReorder"]
                    : _localization["LoadOrder_ActionSkipped"];
            }

            return Outcome switch
            {
                LoadOrderLineOutcome.Reorder => _localization["LoadOrder_OutcomeReorder"],
                LoadOrderLineOutcome.LibraryAdd => IsSkipped
                    ? _localization["LoadOrder_ActionSkipped"]
                    : _localization["LoadOrder_OutcomeAdd"],
                LoadOrderLineOutcome.SiblingImport => IsSkipped
                    ? _localization["LoadOrder_ActionSkipped"]
                    : _localization["LoadOrder_OutcomeImport"],
                LoadOrderLineOutcome.Unresolved when IsIdentified && !IsSkipped && _premiumAvailable =>
                    _localization["LoadOrder_ActionDownload"],
                _ => _localization["LoadOrder_ActionSkipped"],
            };
        }
    }

    /// <summary>
    /// The Skip/Undo text action's label, describing what the click does.
    /// </summary>
    public string SkipActionText => IsSkipped
        ? _localization["LoadOrder_UndoSkipAction"]
        : _localization["LoadOrder_SkipAction"];

    /// <summary>
    /// Whether the row offers the Skip/Undo opt-out: import mode only, for
    /// optional adds/imports and actionable downloads. Profile matches are
    /// never skippable (reordering is the chosen operation), and a missing
    /// mod without Premium is not actionable, so neither offers it.
    /// </summary>
    public bool CanSkip => _mode == LoadOrderImportMode.ReorderAndImport
        && !IsApplyingRow
        && (Outcome is LoadOrderLineOutcome.LibraryAdd or LoadOrderLineOutcome.SiblingImport
            || (Outcome == LoadOrderLineOutcome.Unresolved && IsIdentified && _premiumAvailable));

    /// <summary>
    /// Placeholder consumed by the parent while an apply runs (the view
    /// disables row interaction through the items host's IsEnabled; this flag
    /// is the row-level mirror the projections read).
    /// </summary>
    [ObservableProperty]
    private bool _isApplyingRow;

    /// <summary>
    /// The exceptional opt-out: a skipped row is visible but untouched by the
    /// apply (no add, no import, no enqueue, no order slot).
    /// </summary>
    [ObservableProperty]
    private bool _isSkipped;

    // ---- identification ---------------------------------------------------

    /// <summary>
    /// The search candidates for this row, best first (empty until the search
    /// queue ran + found something).
    /// </summary>
    public IReadOnlyList<NexusSearchCandidate> Candidates { get; private set; } =
        Array.Empty<NexusSearchCandidate>();

    /// <summary>Whether any search candidate arrived.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The best candidate (the inline top slot), when one exists.</summary>
    public NexusSearchCandidate? TopCandidate => Candidates.FirstOrDefault();

    /// <summary>The candidates beyond the top slot (the expandable alternates).</summary>
    public IReadOnlyList<NexusSearchCandidate> AlternateCandidates => Candidates.Skip(1).ToArray();

    /// <summary>Whether any alternate candidate exists (the expand affordance).</summary>
    public bool HasAlternateCandidates => AlternateCandidates.Count > 0;

    /// <summary>Whether the alternates panel is expanded.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Whether the candidate area renders: an unidentified lookup row with at
    /// least one candidate. The area shares the row grid's column layout, so
    /// the proposed title lands under Match, the <c>#id</c> under Mod ID, and
    /// the Accept action under Action.
    /// </summary>
    public bool ShowCandidateArea => IsLookupRow && !IsIdentified && HasCandidates;

    /// <summary>The identified mod id, once a candidate is accepted or a
    /// manual entry is remotely verified. Null while unidentified.</summary>
    public int? IdentifiedModId { get; private set; }

    /// <summary>Whether this row has been identified.</summary>
    public bool IsIdentified => IdentifiedModId is not null;

    /// <summary>
    /// The canonical title of the identification (the accepted candidate's
    /// title, or the title Nexus returned for a verified manual id).
    /// </summary>
    public string? IdentifiedName { get; private set; }

    /// <summary>
    /// The identified fact for the Mod ID column: the actual numeric id
    /// (<c>#42</c>), never a repeat of the title. Falls back to the
    /// reconciliation-known id of an already-matched local row (read-only,
    /// no Change action).
    /// </summary>
    public string? ModIdText =>
        IsIdentified ? "#" + IdentifiedModId
        : KnownModId is { } known ? "#" + known
        : null;

    /// <summary>
    /// Whether the manual id/URL/name entry + Find action render (the Mod ID
    /// cell of an unidentified lookup row). Identified rows show the fact +
    /// the subtle Change action instead.
    /// </summary>
    public bool ShowManualEntry => IsLookupRow && !IsIdentified;

    /// <summary>
    /// Whether the identified fact + Change action render in the Mod ID cell.
    /// </summary>
    public bool ShowIdentifiedFact => IsIdentified;

    /// <summary>
    /// Whether the read-only known-id fact renders for an already-matched
    /// local row (the reconciliation's <see cref="KnownModId"/>; never
    /// combined with Change/manual controls, which only lookup rows get).
    /// </summary>
    public bool ShowKnownModId => !IsIdentified && KnownModId is not null;

    /// <summary>
    /// The manually entered mod id / URL / name text (two-way bound to the
    /// Mod ID cell's field). An id or URL is verified remotely through the
    /// client's exact-identity lookup; a name runs the anonymous search; a
    /// syntactically valid id alone never identifies the row.
    /// </summary>
    [ObservableProperty]
    private string _manualId = string.Empty;

    /// <summary>Whether the manual entry's remote lookup (exact id or name
    /// search) is in flight.</summary>
    [ObservableProperty]
    private bool _isFinding;

    /// <summary>Whether the Find action accepts input: not while a lookup is
    /// in flight and not while the row's automatic search turn is active
    /// (their results must not interleave).</summary>
    public bool CanFind => !IsFinding && !IsSearching;

    /// <summary>
    /// The inline lookup failure under the entry (an unparsable value, a
    /// missing id, or a failed lookup/search), or null. The input stays
    /// editable.
    /// </summary>
    [ObservableProperty]
    private string? _manualError;

    /// <summary>
    /// The OPTIONAL release tag typed for an IDENTIFIED sibling-import row:
    /// it tags the content imported from disk (never auto-populated from the
    /// Nexus page version, which may not describe the disk content). Blank is
    /// valid: the import lands on the version-unknown path (an empty latest
    /// version string, which the Mods row surfaces through its ordinary
    /// download/update action). Nonblank values are trimmed + preserved.
    /// </summary>
    [ObservableProperty]
    private string _version = string.Empty;

    /// <summary>
    /// Whether the version input renders: an identified, non-skipped sibling
    /// import only. Every other row shape shows no version input at all (a
    /// remote download resolves the real version; local rows have no Nexus
    /// version to tag).
    /// </summary>
    public bool ShowVersionInput =>
        _mode == LoadOrderImportMode.ReorderAndImport
        && Outcome == LoadOrderLineOutcome.SiblingImport
        && IsIdentified && !IsSkipped;

    /// <summary>
    /// Whether the "version comes from the download" note renders in the
    /// Version column: an identified, non-skipped missing mod on a Premium
    /// account (its download resolves the real version).
    /// </summary>
    public bool ShowVersionFromDownloadNote =>
        _mode == LoadOrderImportMode.ReorderAndImport
        && Outcome == LoadOrderLineOutcome.Unresolved
        && IsIdentified && !IsSkipped && _premiumAvailable;

    /// <summary>
    /// The read-only version fact for an already-matched local row in the
    /// import review: the reconciliation's <see cref="KnownVersion"/> when
    /// Curator knows a non-empty one (policy-resolved for a profile entry,
    /// the resolved latest for a library add). An empty (unknown) tag
    /// normalizes to null, rendering blank; linked rows and unknowns render
    /// nothing.
    /// </summary>
    public string? KnownVersionText =>
        _mode == LoadOrderImportMode.ReorderAndImport
        && !IsIdentified
        && Outcome is LoadOrderLineOutcome.Reorder or LoadOrderLineOutcome.LibraryAdd
        && KnownVersion is { Length: > 0 } version
            ? version
            : null;

    /// <summary>Whether the row's search is running (a spinner affordance).</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Set once the row's search completed with zero candidates.</summary>
    [ObservableProperty]
    private bool _searchedNoResults;

    /// <summary>
    /// Whether the no-results hint renders: the search ran, found nothing,
    /// and the row is still unidentified (identification is the answer; the
    /// hint hides once it lands).
    /// </summary>
    public bool ShowNoResultsHint => SearchedNoResults && IsLookupRow && !IsIdentified;

    /// <summary>
    /// The localized per-line failure of the apply's import or enqueue for
    /// this row, or null. The apply continues past failed lines; the message
    /// states what did not land so a re-run can finish it.
    /// </summary>
    public string? LineFailure
    {
        get => _lineFailure;
        internal set
        {
            _lineFailure = value;
            OnPropertyChanged();
        }
    }

    private string? _lineFailure;

    /// <summary>
    /// Applies the search queue's candidates to the row (best first).
    /// Candidates are proposals; the row stays unidentified until one is
    /// accepted or a manual entry is verified.
    /// </summary>
    public void ApplyCandidates(IReadOnlyList<NexusSearchCandidate> candidates)
    {
        Candidates = candidates;
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(TopCandidate));
        OnPropertyChanged(nameof(AlternateCandidates));
        OnPropertyChanged(nameof(HasAlternateCandidates));
        OnPropertyChanged(nameof(ShowCandidateArea));
    }

    /// <summary>
    /// Marks the row identified by an accepted candidate (records the id +
    /// the candidate's canonical title; collapses the candidate area).
    /// </summary>
    public void IdentifyFromCandidate(NexusSearchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IdentifiedModId = candidate.ModId;
        IdentifiedName = candidate.Name;
        OnIdentified();
    }

    /// <summary>
    /// Marks the row identified by a remotely verified manual entry (the id
    /// the user typed + the canonical title Nexus returned for it).
    /// </summary>
    public void IdentifyVerified(int modId, string canonicalName)
    {
        IdentifiedModId = modId;
        IdentifiedName = canonicalName;
        OnIdentified();
    }

    /// <summary>
    /// Returns the row to unidentified: the candidate area / manual entry
    /// come back (arrived candidates are retained) and every
    /// identity-specific validation state clears.
    /// </summary>
    public void ClearIdentity()
    {
        IdentifiedModId = null;
        IdentifiedName = null;
        ManualError = null;
        OnIdentified();
    }

    /// <summary>
    /// Re-fires every projection that reads the identification state (the
    /// fact, the Mod ID cell's two mutually exclusive states, the Match
    /// column, the Action column, the skip availability, the version
    /// surfaces, the candidate area, and the no-results hint). Anything new
    /// that keys off IsIdentified joins this list.
    /// </summary>
    private void OnIdentified()
    {
        OnPropertyChanged(nameof(IsIdentified));
        OnPropertyChanged(nameof(ShowManualEntry));
        OnPropertyChanged(nameof(ShowIdentifiedFact));
        OnPropertyChanged(nameof(ShowKnownModId));
        OnPropertyChanged(nameof(ModIdText));
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(SkipActionText));
        OnPropertyChanged(nameof(ShowVersionInput));
        OnPropertyChanged(nameof(ShowVersionFromDownloadNote));
        OnPropertyChanged(nameof(KnownVersionText));
        OnPropertyChanged(nameof(ShowCandidateArea));
        OnPropertyChanged(nameof(ShowNoResultsHint));
        IsExpanded = false;
    }

    partial void OnIsSkippedChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(SkipActionText));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(ShowVersionInput));
        OnPropertyChanged(nameof(ShowVersionFromDownloadNote));
    }

    partial void OnIsFindingChanged(bool value) =>
        OnPropertyChanged(nameof(CanFind));

    partial void OnIsSearchingChanged(bool value) =>
        OnPropertyChanged(nameof(CanFind));

    /// <summary>
    /// Re-fires the localized getters after a culture change (called by the
    /// parent; the row never subscribes to the application-lifetime
    /// localization service itself).
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(SkipActionText));
    }
}

/// <summary>
/// The load-order import workspace's VM: the focused, in-page workflow for a
/// picked <c>mod_load_order.txt</c> inside the Mods destination. An
/// application-lifetime singleton child VM (the
/// <see cref="ImportWorkflowViewModel"/> pattern: registered before
/// <see cref="ModListViewModel"/>, inactive until started) that owns the file
/// read + parse, the reconciliation, the mode choice, the review rows, and
/// the apply; the parent reloads the mod list on the raised
/// <see cref="OrderApplied"/> event.
/// </summary>
/// <remarks>
/// <para><b>The stage machine:</b> <see cref="Stage"/> is the one state
/// source (Inactive, ChoosingMode, Reviewing) plus the session's
/// <see cref="Mode"/> once chosen. Every workspace projection derives from
/// those two values, so there is no boolean matrix to keep coherent:
/// <c>IsActive</c> is stage-derived, the view states are stage-derived, and
/// the rows carry the mode they were built under.</para>
/// <para><b>Mode choice before any Nexus traffic:</b>
/// <see cref="StartImportCommand"/> only reads, parses, and reconciles
/// locally (plus the account-capability read for honest row messaging happens
/// at import-mode choice). Reorder-only performs zero Nexus/auth calls and
/// writes only profile-match order. Reorder-and-import adds the sibling scan
/// and starts the serial, human-paced search queue over the capability-gated
/// lookup rows;
/// there are no per-row include controls, automatic operations are included
/// by choosing the mode, and the subtle Skip/Undo text action is the
/// exceptional opt-out.</para>
/// <para><b>Coexistence:</b> the workspace refuses to start while any other
/// hosted card is active and reports its own activity through the shared
/// <see cref="ModCardsGate"/>.</para>
/// <para><b>Remote convergence:</b> the apply records a profile-scoped
/// pending placement plan (see <see cref="LoadOrderDownloadPlacements"/>) so
/// enqueued downloads land at their file positions as they complete.</para>
/// </remarks>
public partial class LoadOrderImportViewModel : LocalizedViewModel
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly ILoadOrderReconciler _reconciler;
    private readonly INexusClient _nexus;
    private readonly IModImportService _imports;
    private readonly INexusAuthService _auth;
    private readonly IModAcquisitionService _acquisition;
    private readonly IModDownloadQueue _downloadQueue;
    private readonly LoadOrderDownloadPlacements _placements;
    private readonly ModCardsGate _cards;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LoadOrderImportViewModel> _logger;

    private Guid? _capturedProfileId;
    private string? _sourcePath;
    private IReadOnlyList<LoadOrderLine> _planLines = Array.Empty<LoadOrderLine>();
    private bool _premiumAtEntry;
    private bool _resetPendingAfterApply;

    /// <summary>
    /// How many downloads the last apply admitted (drives the
    /// queued-downloads notice when a failure keeps the workspace open).
    /// </summary>
    private int _admittedDownloads;

    private CancellationTokenSource? _searchCancellation;

    /// <summary>
    /// Creates the workspace VM, inactive, and subscribes to the session
    /// (reset on active-profile change) and localization (refresh the row
    /// labels on a culture change).
    /// </summary>
    /// <param name="placements">The profile-scoped pending placement plans
    /// recorded at apply time for enqueued downloads.</param>
    /// <param name="invokeOnUi">The marshal-to-UI-thread seam: the search
    /// queue + verify continuations resume on a background context (the
    /// clients are awaited directly), so every row mutation funnels through
    /// here (the download queue's seam pattern).</param>
    public LoadOrderImportViewModel(
        IProfileService profiles,
        IProfileSession session,
        ILoadOrderReconciler reconciler,
        INexusClient nexus,
        IModImportService imports,
        INexusAuthService auth,
        IModAcquisitionService acquisition,
        IModDownloadQueue downloadQueue,
        LoadOrderDownloadPlacements placements,
        ModCardsGate cards,
        IDialogService dialogs,
        LocalizationService localization,
        Action<Action> invokeOnUi,
        ILogger<LoadOrderImportViewModel> logger)
        : base(localization)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _nexus = nexus ?? throw new ArgumentNullException(nameof(nexus));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _placements = placements ?? throw new ArgumentNullException(nameof(placements));
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private readonly Action<Action> _invokeOnUi;

    /// <summary>
    /// The pause between the search queue's queries (the Cloudflare posture:
    /// the anonymous endpoint sits behind bot protection, so the queue stays
    /// human-paced rather than back-to-back). Tests shrink it to zero through
    /// the internal setter.
    /// </summary>
    internal static TimeSpan SearchQueueDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The per-row candidate cap (the inline top slot + the alternates).</summary>
    private const int MaxCandidatesPerRow = 5;

    // ---- stage + mode -------------------------------------------------------

    /// <summary>The workflow's current stage (the one state source).</summary>
    [ObservableProperty]
    private LoadOrderStage _stage;

    /// <summary>The chosen operation for this session.</summary>
    [ObservableProperty]
    private LoadOrderImportMode _mode;

    partial void OnStageChanged(LoadOrderStage value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsChoosingMode));
        OnPropertyChanged(nameof(IsReviewing));
        OnPropertyChanged(nameof(IsImportMode));
        OnPropertyChanged(nameof(IsReorderMode));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmptyNotice));
        OnPropertyChanged(nameof(ShowRemoteUnavailableNotice));
        OnPropertyChanged(nameof(RemoteUnavailableNoticeText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanApplyNow));
        OnPropertyChanged(nameof(CanCancelNow));
    }

    partial void OnModeChanged(LoadOrderImportMode value)
    {
        OnPropertyChanged(nameof(IsImportMode));
        OnPropertyChanged(nameof(IsReorderMode));
    }

    /// <summary>Whether a session is open (any non-inactive stage).</summary>
    public bool IsActive => Stage != LoadOrderStage.Inactive;

    /// <summary>Whether the mode-choice state is showing.</summary>
    public bool IsChoosingMode => Stage == LoadOrderStage.ChoosingMode;

    /// <summary>Whether the review state is showing.</summary>
    public bool IsReviewing => Stage == LoadOrderStage.Reviewing;

    /// <summary>Whether the session is the reorder-and-import mode.</summary>
    public bool IsImportMode => Stage == LoadOrderStage.Reviewing
        && Mode == LoadOrderImportMode.ReorderAndImport;

    /// <summary>Whether the session is the reorder-only mode.</summary>
    public bool IsReorderMode => Stage == LoadOrderStage.Reviewing
        && Mode == LoadOrderImportMode.Reorder;

    /// <summary>The picked file's full path (tooltip + automation name; the header ellipsizes it).</summary>
    public string SourcePath => _sourcePath ?? string.Empty;

    /// <summary>The review rows, one per parsed file line, in file order.</summary>
    public ObservableCollection<LoadOrderRowViewModel> Rows { get; } = new();

    /// <summary>Whether the review holds any row (false only for an empty/comment-only file).</summary>
    public bool HasRows => IsReviewing && Rows.Count > 0;

    /// <summary>
    /// Whether the localized empty-file notice shows: the picked file parsed
    /// to nothing (only comments or blank lines). The choice state shows the
    /// notice instead of the tiles; there is nothing to choose.
    /// </summary>
    public bool ShowEmptyNotice => IsChoosingMode && _planLines.Count == 0;

    /// <summary>
    /// The choice state's one-line summary of the reconciled file: how many
    /// entries it holds and how many are already in this profile.
    /// </summary>
    public string ChoiceSummaryText => _localization.Format(
        "LoadOrder_ChoiceSummary",
        _planLines.Count,
        _planLines.Count(l => l.Outcome == LoadOrderLineOutcome.Reorder));

    /// <summary>
    /// Whether the upfront remote-unavailable notice shows: an import review
    /// built without Premium (signed out, a regular account, or a capability
    /// read failure) that holds remote-only missing lines. Those lines are
    /// visibly non-actionable (no lookups, no manual identification, nothing
    /// for Apply to download); the notice states the count + the path forward
    /// before the user scrolls into the rows.
    /// </summary>
    public bool ShowRemoteUnavailableNotice => Stage == LoadOrderStage.Reviewing
        && Mode == LoadOrderImportMode.ReorderAndImport
        && !_premiumAtEntry
        && Rows.Any(r => r.Outcome == LoadOrderLineOutcome.Unresolved);

    /// <summary>The localized upfront remote-unavailable notice text.</summary>
    public string RemoteUnavailableNoticeText => _localization.Format(
        "LoadOrder_RemoteUnavailableNotice",
        Rows.Count(r => r.Outcome == LoadOrderLineOutcome.Unresolved));

    /// <summary>
    /// A row's skip, identification, or verification state changed: re-fire
    /// the apply-button projections (the rows are transient, so they push up
    /// instead of the workspace polling; identification can make an import
    /// row actionable, so it counts).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoadOrderRowViewModel.IsSkipped)
            or nameof(LoadOrderRowViewModel.IsIdentified)
            or nameof(LoadOrderRowViewModel.IsFinding))
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanApplyNow));
        }
    }

    /// <summary>
    /// Raised after a successful apply (UI thread). The mod list reloads from
    /// it: the order changed and included mods joined the profile.
    /// </summary>
    public event EventHandler? OrderApplied;

    /// <summary>
    /// Starts a session from a picked <c>mod_load_order.txt</c>: reads +
    /// parses the file, reconciles it against the active profile and
    /// repository (all local; NO Nexus or auth call happens here), and opens
    /// the mode-choice state. Refuses (no-op, logged) while this or any other
    /// hosted card is active, with no active profile, or for a null/blank
    /// path; a read or reconciliation failure surfaces the localized alert
    /// and leaves the workspace inactive.
    /// </summary>
    [RelayCommand]
    private async Task StartImport(string? path)
    {
        if (IsActive)
        {
            _logger.LogWarning("Load-order start rejected: the workspace is already active.");
            return;
        }

        if (_cards.IsAnyOtherCardActive(this))
        {
            _logger.LogWarning("Load-order start rejected: another hosted card is active.");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid profileId)
        {
            _logger.LogWarning("Load-order start rejected: no active profile.");
            return;
        }

        // The file is a tiny text file; the synchronous read + in-memory
        // reconcile keep the flow simple (no Task.Run hop, no
        // ConfigureAwait). A failure surfaces the alert and stays inactive.
        LoadOrderPlan plan;
        try
        {
            var names = ModLoadOrderParser.Parse(await File.ReadAllTextAsync(path));
            plan = _reconciler.Reconcile(profileId, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Reading or reconciling the load-order file {Path} failed.", path);
            await _dialogs.ShowAlertAsync(
                _localization["LoadOrder_ReadFailedTitle"],
                _localization.Format("LoadOrder_ReadFailedMessage", path));
            return;
        }

        // The card-gate re-check after the async read: the picker's return is
        // not the last async seam before activation, so another hosted card
        // (a drag-and-drop batch, an edit band) could have started while the
        // file was loading. The mutual exclusion must hold at activation.
        if (_cards.IsAnyOtherCardActive(this))
        {
            _logger.LogWarning(
                "Load-order start rejected: another hosted card became active while the file was read.");
            return;
        }

        _capturedProfileId = profileId;
        _sourcePath = path;
        _planLines = plan.Lines;
        _premiumAtEntry = false;
        Stage = LoadOrderStage.ChoosingMode;
        _cards.ReportActive(this, active: true);
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(ChoiceSummaryText));
        OnPropertyChanged(nameof(ShowEmptyNotice));
        _logger.LogInformation(
            "Started a load-order import of {Path}: {Lines} line(s), {Matches} profile match(es).",
            path, plan.Lines.Count, plan.Lines.Count(l => l.Outcome == LoadOrderLineOutcome.Reorder));
    }

    /// <summary>
    /// Chooses reorder-only: builds the lightweight review from the stored
    /// plan and makes ZERO Nexus/auth calls. The review offers no per-row
    /// controls at all: profile matches will be reordered; library, sibling,
    /// and missing lines are visible as skipped.
    /// </summary>
    [RelayCommand]
    private void ChooseReorder()
    {
        if (Stage != LoadOrderStage.ChoosingMode || _planLines.Count == 0)
        {
            return;
        }

        Mode = LoadOrderImportMode.Reorder;
        BuildRows(_planLines.Select(l => (l, (string?)null)));
        Stage = LoadOrderStage.Reviewing;
    }

    /// <summary>
    /// Chooses reorder-and-import: reads the account capability once for
    /// honest row messaging (a Premium account unlocks the in-app download
    /// action; anything else visibly is not actionable), scans the txt's own
    /// directory for sibling mod folders, builds the review with every
    /// automatic operation included by default, and starts the serial,
    /// human-paced search queue over the capability-gated lookup rows
    /// (sibling lines at every account tier; remote-only lines on Premium).
    /// </summary>
    [RelayCommand]
    private async Task ChooseImport()
    {
        if (Stage != LoadOrderStage.ChoosingMode || _planLines.Count == 0)
        {
            return;
        }

        try
        {
            var state = await _auth.GetCurrentStateAsync();
            _premiumAtEntry = state?.IsPremium == true;
        }
        catch (Exception ex)
        {
            // An unreadable account state degrades to the non-premium row
            // messaging (visible, honest); the apply re-checks before any
            // enqueue.
            _logger.LogInformation(ex, "Reading the Nexus account state for the load-order import failed.");
            _premiumAtEntry = false;
        }

        // The auth read is the seam: a reset (profile switch) landing while
        // it was in flight must win, not be resurrected by this continuation.
        if (Stage != LoadOrderStage.ChoosingMode)
        {
            return;
        }

        var siblings = _sourcePath is { } path ? ScanSiblingModFolders(path) : new Dictionary<string, string>();
        Mode = LoadOrderImportMode.ReorderAndImport;
        BuildRows(_planLines.Select(l => UpgradeSibling(l, siblings)));
        Stage = LoadOrderStage.Reviewing;

        // The search queue: fire-and-forget over the lookup rows (the queue
        // owns its failures: logged, never alerted + stops when the workspace
        // closes). Stopped by the user it retains arrived candidates.
        _ = RunSearchQueueAsync();
    }

    /// <summary>
    /// Upgrades an unresolved line whose folder sits beside the txt to a
    /// sibling-import line (the migration path). Resolved lines are never
    /// upgraded: the profile/library match wins.
    /// </summary>
    private static (LoadOrderLine Line, string? SiblingPath) UpgradeSibling(
        LoadOrderLine line, Dictionary<string, string> siblings)
    {
        if (line.Outcome == LoadOrderLineOutcome.Unresolved
            && siblings.TryGetValue(line.Name, out var siblingPath))
        {
            return (new LoadOrderLine(
                line.Name,
                LoadOrderLineOutcome.SiblingImport,
                ContainerId: null,
                MatchedBaseName: null,
                DisplayName: line.Name), siblingPath);
        }

        return (line, null);
    }

    private void BuildRows(IEnumerable<(LoadOrderLine Line, string? SiblingPath)> rows)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.Clear();
        foreach (var (line, siblingPath) in rows)
        {
            var row = new LoadOrderRowViewModel(line, Mode, _premiumAtEntry, _localization)
            {
                SiblingPath = siblingPath,
            };
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
    }

    /// <summary>
    /// Scans the picked txt's own directory for sibling mod folders: a
    /// directory directly beside the txt that contains a descriptor named
    /// <c>&lt;dirName&gt;/&lt;dirName&gt;.mod</c>. Skips <c>base</c> (the old
    /// DML loader runtime, never a mod) + any directory named like the txt
    /// itself. Returns the mod folders keyed by name (case-insensitive
    /// ordinal, matching the planner); IO failures are logged + yield an
    /// empty map (the review degrades to the plain unresolved rows, the
    /// manual + search paths remain).
    /// </summary>
    private Dictionary<string, string> ScanSiblingModFolders(string txtPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(txtPath));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return map;
            }

            var txtName = Path.GetFileName(txtPath);
            foreach (var dir in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(dir.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // The old loader runtime is not a mod; a directory named like
                // the txt itself is skipped too (defensive; the txt is a
                // file, so this only catches a same-named directory).
                if (string.Equals(name, "base", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, txtName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(dir, name + ".mod")))
                {
                    map.TryAdd(name, dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(
                ex, "Scanning the load-order file's directory for sibling mod folders failed.");
        }

        return map;
    }

    // ---- the search queue ---------------------------------------------------

    /// <summary>Whether the search queue is running (the progress area + Stop
    /// action show; Apply waits).</summary>
    [ObservableProperty]
    private bool _isSearchRunning;

    /// <summary>Whether the user stopped the search before it finished.</summary>
    [ObservableProperty]
    private bool _searchStopped;

    /// <summary>The number of lookup rows the queue covers.</summary>
    [ObservableProperty]
    private int _searchTotal;

    /// <summary>How many lookup rows the queue has finished.</summary>
    [ObservableProperty]
    private int _searchCompletedCount;

    /// <summary>The folder name the queue is currently searching, or null.</summary>
    [ObservableProperty]
    private string? _currentSearchName;

    /// <summary>
    /// The header's search status: the live progress while running, the
    /// stopped notice after a stop, the finished notice after a natural
    /// completion, and nothing before the queue starts.
    /// </summary>
    public string? SearchStatusText
    {
        get
        {
            if (IsSearchRunning)
            {
                return _localization.Format(
                    "LoadOrder_SearchProgress",
                    SearchCompletedCount,
                    SearchTotal,
                    CurrentSearchName ?? string.Empty);
            }

            if (SearchTotal == 0)
            {
                return null;
            }

            return SearchStopped
                ? _localization["LoadOrder_SearchStopped"]
                : _localization["LoadOrder_SearchComplete"];
        }
    }

    partial void OnIsSearchRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchStatusText));
        // The queue finishing (or starting) flips the Apply button's
        // race-guard projection.
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanApplyNow));
    }

    partial void OnSearchStoppedChanged(bool value) =>
        OnPropertyChanged(nameof(SearchStatusText));

    partial void OnSearchTotalChanged(int value) =>
        OnPropertyChanged(nameof(SearchStatusText));

    partial void OnSearchCompletedCountChanged(int value) =>
        OnPropertyChanged(nameof(SearchStatusText));

    partial void OnCurrentSearchNameChanged(string? value) =>
        OnPropertyChanged(nameof(SearchStatusText));

    /// <summary>
    /// The serial search queue: one lookup row at a time, in file order,
    /// firing the anonymous Nexus search with the folder name normalized into
    /// search terms and applying the top candidates to the row. Human-paced
    /// (serial awaits with the <see cref="SearchQueueDelay"/> pause between
    /// queries; the Cloudflare posture); no retries on failure (a failed
    /// search leaves the row unidentified with the manual path available). A
    /// row the user identified before its turn is skipped without a call. Row
    /// mutations marshal to the UI thread through the injected seam.
    /// </summary>
    private async Task RunSearchQueueAsync()
    {
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        var lookups = Rows.Where(r => r.IsLookupRow).ToArray();
        SearchStopped = false;
        SearchTotal = lookups.Length;
        SearchCompletedCount = 0;
        IsSearchRunning = true;
        try
        {
            var first = true;
            foreach (var row in lookups)
            {
                if (token.IsCancellationRequested)
                {
                    SearchStopped = true;
                    return;
                }

                // Identified rows need no search; a row whose MANUAL lookup
                // is in flight is skipped so the automatic completion can
                // never overwrite the user's fresher results (the manual
                // action is likewise refused while a row's automatic turn is
                // active: the two searches never interleave on one row).
                if (row.IsIdentified || row.IsFinding)
                {
                    SearchCompletedCount++;
                    continue;
                }

                // Human-paced: pause between queries (not before the first:
                // the user just chose the mode). A stopped queue exits the
                // delay immediately.
                if (!first)
                {
                    try
                    {
                        await Task.Delay(SearchQueueDelay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        SearchStopped = true;
                        return;
                    }
                }

                first = false;
                CurrentSearchName = row.Name;
                _invokeOnUi(() => row.IsSearching = true);
                try
                {
                    // Awaited on the calling context; the continuation below
                    // hops to the UI thread explicitly through the seam (no
                    // ConfigureAwait(false); the UI-layer convention).
                    var response = await _nexus.SearchModsAsync(
                        NexusGameIdentity.DarktideDomain,
                        NormalizeSearchTerms(row.Name),
                        MaxCandidatesPerRow,
                        token);

                    _invokeOnUi(() =>
                    {
                        if (response.Data.Length == 0)
                        {
                            row.SearchedNoResults = true;
                        }

                        var candidates = RankCandidatesExactFirst(row.Name, response.Data);
                        row.ApplyCandidates(candidates);

                        // A UNIQUE normalized-exact result is already a
                        // remote Nexus identity: identify immediately with
                        // the canonical title + mod id (no redundant
                        // GetModByIdAsync verify call). Nexus's wildcard
                        // search routinely returns non-exact hits too, so a
                        // single non-exact hit stays a child proposal and
                        // multiple hits always stay proposals: never silently
                        // choose.
                        if (candidates.Length == 1
                            && IsNormalizedExact(row.Name, candidates[0].Name))
                        {
                            row.IdentifyFromCandidate(candidates[0]);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    SearchStopped = true;
                    return;
                }
                catch (Exception ex)
                {
                    // A failed search is a proposal gap, not an error state:
                    // log, leave the row unidentified (the manual path
                    // remains), keep draining the queue.
                    _logger.LogInformation(
                        ex, "The search for load-order line '{Name}' failed; leaving it unidentified.", row.Name);
                }
                finally
                {
                    _invokeOnUi(() => row.IsSearching = false);
                    SearchCompletedCount++;
                }
            }
        }
        finally
        {
            IsSearchRunning = false;
            CurrentSearchName = null;
        }
    }

    /// <summary>
    /// Normalizes a mod folder identifier into a word-separated search
    /// phrase for the Nexus wildcard name search: case boundaries split
    /// first (a lowercase letter or digit followed by an uppercase letter
    /// gains a space, so fused PascalCase names like SimpleAudio become
    /// simple audio), then underscores + hyphens to spaces, lowercase, and
    /// whitespace collapsed to single spaces, trimmed. A folder name whose
    /// canonical title still diverges simply lands as untracked content with
    /// manual entry; there is deliberately no retry with a different phrase.
    /// </summary>
    internal static string NormalizeSearchTerms(string folderName)
    {
        // Split case boundaries BEFORE lowercasing (the boundary is the
        // uppercase letter, which lowercasing erases).
        var spaced = CaseBoundaryRegex().Replace(folderName, "$1 $2");
        var lowered = spaced.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        return string.Join(' ', lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Whether a canonical name is a normalized spelling of the phrase: both
    /// sides run through <see cref="NormalizeSearchTerms"/> and compare
    /// ordinally.
    /// </summary>
    private static bool IsNormalizedExact(string phrase, string canonicalName) =>
        string.Equals(
            NormalizeSearchTerms(phrase),
            NormalizeSearchTerms(canonicalName),
            StringComparison.Ordinal);

    /// <summary>
    /// Builds a row's capped candidate set from a search's results,
    /// normalized-exact matches first: server ordering alone cannot be relied
    /// on to surface the exact title inside a small result page, so the
    /// exact-first precedence is deterministic client policy. Each partition
    /// (exact, then non-exact) keeps the service's relative order; the
    /// display cap applies after the promotion so an exact hit is never the
    /// candidate the cap drops.
    /// </summary>
    private static NexusSearchCandidate[] RankCandidatesExactFirst(
        string phrase, NexusSearchResult[] results) =>
        results
            .Select(r => new NexusSearchCandidate(r.ModId, r.Name))
            .OrderByDescending(c => IsNormalizedExact(phrase, c.Name))
            .Take(MaxCandidatesPerRow)
            .ToArray();

    [GeneratedRegex(@"(\p{Ll}|\d)(\p{Lu})")]
    private static partial Regex CaseBoundaryRegex();

    /// <summary>
    /// Stops the search queue: the remaining lookups are not searched, and
    /// arrived candidates stay on their rows. Apply becomes available.
    /// </summary>
    [RelayCommand]
    private void StopSearch() => _searchCancellation?.Cancel();

    // ---- identification actions ----------------------------------------------

    /// <summary>
    /// Accepts a search candidate for a row: records the identification (the
    /// canonical title lands in Match, the id in Mod ID). Accepting implies
    /// inclusion; the exceptional opt-out is the row's Skip.
    /// </summary>
    [RelayCommand]
    private void AcceptCandidate(LoadOrderRowViewModel? row)
    {
        if (row?.TopCandidate is { } candidate)
        {
            row.IdentifyFromCandidate(candidate);
        }
    }

    /// <summary>Accepts one of the alternates (the expand panel's rows).</summary>
    [RelayCommand]
    private void AcceptAlternate((LoadOrderRowViewModel Row, NexusSearchCandidate Candidate)? arg)
    {
        if (arg is { } pair)
        {
            pair.Row.IdentifyFromCandidate(pair.Candidate);
        }
    }

    /// <summary>
    /// Returns an identified row to the candidate/manual identification state
    /// (arrived candidates retained) without restarting the workflow.
    /// </summary>
    [RelayCommand]
    private void ChangeIdentity(LoadOrderRowViewModel? row) => row?.ClearIdentity();

    /// <summary>
    /// The manual entry's one shared lookup action (the magnifier icon +
    /// Enter both route here). Classifies the trimmed input:
    /// <list type="number">
    /// <item>a valid Nexus id or supported Nexus URL runs the anonymous
    /// exact-identity lookup (<see cref="INexusClient.GetModByIdAsync"/>) and
    /// identifies the row with the canonical title Nexus returned (a
    /// syntactically valid id alone is never accepted);</item>
    /// <item>input that clearly intends an id or URL but is malformed (an
    /// all-numeric invalid value, or an http(s) prefix that is not a valid
    /// supported Nexus URL) shows inline validation and is never
    /// reinterpreted as a name search;</item>
    /// <item>any other nonblank text is user-supplied mod-NAME criteria: it
    /// is normalized with the same search-terms normalization and runs the
    /// anonymous <see cref="INexusClient.SearchModsAsync"/> (the same
    /// candidate cap), replacing the row's current proposals. Every
    /// user-entered name result requires explicit acceptance, even a single
    /// normalized-exact one (the auto-identification rule belongs to the
    /// automatic folder-name search queue only); the typed criteria are
    /// retained.</item>
    /// </list>
    /// No results and request failures stay editable with clear inline
    /// feedback and identify nothing. Refused for rows without lookup
    /// capability (the view renders no entry there; the guard is the
    /// programmatic defense) and while the row's automatic search turn is
    /// active, so a later manual search can never be overwritten by a stale
    /// automatic completion (the queue likewise skips a row whose manual
    /// lookup is in flight).
    /// </summary>
    [RelayCommand]
    private async Task FindNexusMod(LoadOrderRowViewModel? row)
    {
        if (row is null || !row.IsLookupRow || row.IsIdentified
            || row.IsFinding || row.IsSearching || IsApplying)
        {
            return;
        }

        var input = (row.ManualId ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            row.ManualError = _localization["LoadOrder_ManualInvalidError"];
            return;
        }

        if (ImportSourceValidator.TryParseUrl(ImportSource.Nexus, input, out var parsed)
            && parsed is NexusSource nexus)
        {
            await FindByIdAsync(row, nexus.ModId);
            return;
        }

        if (LooksLikeIdOrUrl(input))
        {
            // Clearly intended as an id or URL but malformed: validation, not
            // a name search.
            row.ManualError = _localization["LoadOrder_ManualInvalidError"];
            return;
        }

        await FindByNameAsync(row, input);
    }

    /// <summary>
    /// Whether the input clearly intends a numeric id or a URL: all digits
    /// (an invalid numeric value such as 0 or an overflow), or an http(s)
    /// prefix (an unsupported/malformed URL). Never treated as name
    /// criteria.
    /// </summary>
    private static bool LooksLikeIdOrUrl(string input) =>
        input.All(char.IsDigit)
        || input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The exact-identity leg: looks the id up anonymously and identifies the
    /// row with the canonical title Nexus returned; a missing id leaves the
    /// input editable with an inline error.
    /// </summary>
    private async Task FindByIdAsync(LoadOrderRowViewModel row, int modId)
    {
        row.ManualError = null;
        row.IsFinding = true;
        try
        {
            var response = await _nexus.GetModByIdAsync(
                NexusGameIdentity.DarktideDomain, modId);

            if (response.Data is { } identity)
            {
                row.IdentifyVerified(modId, identity.Name);
            }
            else
            {
                row.ManualError = _localization["LoadOrder_ManualNotFoundError"];
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex, "Looking up the manual mod id {Mod} for '{Name}' failed.", modId, row.Name);
            _invokeOnUi(() => row.ManualError =
                _localization.Format("LoadOrder_ManualLookupFailedError", ex.Message));
        }
        finally
        {
            _invokeOnUi(() => row.IsFinding = false);
        }
    }

    /// <summary>
    /// The name-search leg: normalizes the user's criteria with the same
    /// normalization the automatic queue uses and runs the anonymous search
    /// with the same exact-first candidate ranking + cap, REPLACING the row's
    /// current proposals (never auto-identifying: every user-entered result
    /// needs an explicit Accept). No results surface the no-results hint;
    /// failures stay editable with an inline error. Nothing is identified on
    /// failure.
    /// </summary>
    private async Task FindByNameAsync(LoadOrderRowViewModel row, string criteria)
    {
        row.ManualError = null;
        row.IsFinding = true;
        try
        {
            var response = await _nexus.SearchModsAsync(
                NexusGameIdentity.DarktideDomain,
                NormalizeSearchTerms(criteria),
                MaxCandidatesPerRow);

            _invokeOnUi(() =>
            {
                if (response.Data.Length == 0)
                {
                    row.SearchedNoResults = true;
                }

                row.ApplyCandidates(RankCandidatesExactFirst(criteria, response.Data));
            });
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex, "Searching Nexus by name '{Criteria}' for '{Name}' failed.", criteria, row.Name);
            _invokeOnUi(() => row.ManualError =
                _localization.Format("LoadOrder_ManualLookupFailedError", ex.Message));
        }
        finally
        {
            _invokeOnUi(() => row.IsFinding = false);
        }
    }

    /// <summary>
    /// Toggles the row's exceptional opt-out (Skip / Undo): a skipped row is
    /// visible but untouched by the apply.
    /// </summary>
    [RelayCommand]
    private void ToggleSkip(LoadOrderRowViewModel? row)
    {
        if (row?.CanSkip == true)
        {
            row.IsSkipped = !row.IsSkipped;
        }
    }

    // ---- apply ----------------------------------------------------------------

    /// <summary>
    /// Whether any apply is running: the Apply + Cancel + Back buttons and
    /// the row interactions disable for the duration (the workspace stays
    /// active, holding the card gate + the toolbar lock through the apply).
    /// Double-apply is a no-op.
    /// </summary>
    [ObservableProperty]
    private bool _isApplying;

    /// <summary>
    /// Whether the apply may run at all: a review with at least one row the
    /// apply would act on. Reorder mode needs a profile match; import mode
    /// needs a non-skipped reorder/add/import row or an actionable identified
    /// download.
    /// </summary>
    public bool CanApply => Stage == LoadOrderStage.Reviewing && Mode switch
    {
        LoadOrderImportMode.Reorder => Rows.Any(r => r.Outcome == LoadOrderLineOutcome.Reorder),
        LoadOrderImportMode.ReorderAndImport => Rows.Any(IsActionable),
        _ => false,
    };

    /// <summary>
    /// Whether an import-mode row is acted on by the apply: profile matches
    /// always (reordering is the chosen operation), optional adds/imports
    /// unless skipped, and identified missing mods when (and only when) a
    /// Premium account unlocks the download.
    /// </summary>
    private bool IsActionable(LoadOrderRowViewModel row) => row.Outcome switch
    {
        LoadOrderLineOutcome.Reorder => true,
        LoadOrderLineOutcome.LibraryAdd or LoadOrderLineOutcome.SiblingImport => !row.IsSkipped,
        LoadOrderLineOutcome.Unresolved => row.IsIdentified && !row.IsSkipped && _premiumAtEntry,
        _ => false,
    };

    /// <summary>
    /// The Apply button's enabled projection: <see cref="CanApply"/>, not
    /// mid-apply, not racing the live search queue (Apply enables once the
    /// search finishes or the user stops it), and not while any row's manual
    /// verification is in flight.
    /// </summary>
    public bool CanApplyNow => CanApply && !IsApplying && !IsSearchRunning
        && !Rows.Any(r => r.IsFinding);

    /// <summary>The Cancel + Back buttons' enabled projection: not mid-apply.</summary>
    public bool CanCancelNow => !IsApplying;

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyNow));
        OnPropertyChanged(nameof(CanCancelNow));
        foreach (var row in Rows)
        {
            row.IsApplyingRow = value;
        }
    }

    /// <summary>
    /// Applies the review. The mode decides the shape:
    /// <list type="bullet">
    /// <item><b>Reorder:</b> ONE <see cref="IProfileService.SetModOrder"/>
    /// over the active-profile match ids in file order (locks remain
    /// governed by SetModOrder's own projection). Zero Nexus/auth/import/add
    /// calls; every other line was visible as skipped and is not
    /// written.</item>
    /// <item><b>Reorder and import:</b> see <see cref="ApplyImportAsync"/>.
    /// </item>
    /// </list>
    /// On success the session is marked pending, the workspace deactivates,
    /// and <see cref="OrderApplied"/> reloads the mod list.
    /// </summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApplyNow || _capturedProfileId is not Guid profileId)
        {
            return;
        }

        IsApplying = true;
        ApplyFailure = null;
        OnPropertyChanged(nameof(ApplyFailure));
        _admittedDownloads = 0;
        QueuedDownloadsNotice = null;
        OnPropertyChanged(nameof(QueuedDownloadsNotice));
        try
        {
            if (Mode == LoadOrderImportMode.Reorder)
            {
                await ApplyReorderAsync(profileId);
            }
            else
            {
                await ApplyImportAsync(profileId);
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>The reorder-only apply: one order write, nothing else.</summary>
    private async Task ApplyReorderAsync(Guid profileId)
    {
        var order = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.Reorder)
            .Select(r => r.ContainerId!.Value)
            .ToArray();
        try
        {
            if (order.Length > 0)
            {
                await Task.Run(() => _profiles.SetModOrder(profileId, order));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Applying the load-order reorder for {Profile} failed.", profileId);
            ShowInlineFailure(ex.Message);
            return;
        }

        _logger.LogInformation(
            "Applied a load-order reorder: {Ordered} container(s) ordered.", order.Length);
        FinishApply();
    }

    /// <summary>
    /// The reorder-and-import apply. Sequencing (membership before order: the
    /// order write cannot place ids that are not profile members yet):
    /// <list type="number">
    /// <item><b>Premium re-check</b> (before any write, only when remote
    /// downloads were promised): a lost or unreadable Premium state is a
    /// visible failure that keeps the workspace open (never a silent
    /// no-op). The sibling release tag is OPTIONAL: a nonblank value is
    /// trimmed + preserved, and a blank one imports the Nexus-associated
    /// sibling with the empty version-unknown tag.</item>
    /// <item><b>Imports</b>: every non-skipped sibling folder imports through
    /// <see cref="IModImportService.Import"/> (source =
    /// <see cref="NexusSource"/> of the identified id with the trimmed typed
    /// version, or the empty version-unknown tag when the identified row's
    /// version is blank, else <see cref="UntrackedSource"/> + the empty
    /// version-unknown tag). A per-line import failure is recorded on the
    /// line and the apply continues.</item>
    /// <item><b>Adds</b>: an <see cref="IProfileService.AddMod"/> (Latest
    /// policy) for every non-skipped library match and successful sibling
    /// import.</item>
    /// <item><b>Order</b>: ONE <see cref="IProfileService.SetModOrder"/> over
    /// the profile matches plus the successfully added/imported containers in
    /// file order, omitting skipped/failed rows; SetModOrder's own lock
    /// projection keeps locked entries at their exact slots.</item>
    /// <item><b>Enqueues</b>: each non-skipped, remotely identified,
    /// not-in-Curator row gets its head file resolved and a ProfileAdd
    /// download enqueued onto the shared queue (the rows' download morphs own
    /// progress + the completion owns the add + the reload; the pending
    /// placement plan below converges the order as they land). A
    /// <see cref="NexusRateLimitException"/> aborts the remaining enqueues
    /// with everything before it standing.</item>
    /// </list>
    /// </summary>
    private async Task ApplyImportAsync(Guid profileId)
    {
        // (0) The Premium re-check, only when an enqueue was promised: rows
        // messaged "will be downloaded" on the strength of the entry-time
        // read. A stale/failed state is a visible failure; nothing is written.
        var enqueueRows = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.Unresolved
                && r.IsIdentified && !r.IsSkipped && _premiumAtEntry)
            .ToArray();
        if (enqueueRows.Length > 0)
        {
            NexusAuthState? fresh = null;
            try
            {
                fresh = await _auth.GetCurrentStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The load-order apply's Premium verification failed.");
            }

            if (fresh?.IsPremium != true)
            {
                ShowInlineFailure(_localization["LoadOrder_PremiumLostFailure"]);
                return;
            }
        }

        // (a) Imports: the non-skipped sibling folders, on the worker;
        // per-line failures are recorded on their rows and the rest continue.
        // The import results are returned as DATA; the row-side assignments
        // below are display-only.
        var importedContainers = new List<(LoadOrderRowViewModel Row, Guid ContainerId)>();
        var siblingRows = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.SiblingImport && !r.IsSkipped)
            .ToArray();
        if (siblingRows.Length > 0)
        {
            var results = await Task.Run(() =>
            {
                var imported = new List<(LoadOrderRowViewModel Row, Guid ContainerId)>();
                foreach (var row in siblingRows)
                {
                    try
                    {
                        ModSource source = row.IdentifiedModId is { } modId
                            ? new NexusSource { ModId = modId }
                            : new UntrackedSource();
                        // The version is the OPTIONAL tag for the content
                        // imported from disk: only an identified sibling row
                        // carries one, blank imports land on the
                        // version-unknown path, and every other shape imports
                        // with the empty tag as well.
                        var version = row.IsIdentified && !string.IsNullOrWhiteSpace(row.Version)
                            ? row.Version.Trim()
                            : string.Empty;
                        var (containerId, _) = _imports.Import(row.SiblingPath!, row.Name, source, version);
                        imported.Add((row, containerId));
                    }
                    catch (Exception ex) when (IsExpectedImportException(ex))
                    {
                        _logger.LogError(ex, "Importing the sibling mod '{Name}' failed.", row.Name);
                        var detail = ex.Message;
                        _invokeOnUi(() => row.LineFailure =
                            _localization.Format("LoadOrder_LineImportFailed", detail));
                    }
                }

                return imported;
            });
            importedContainers.AddRange(results);

            // Display-only: reflect the landed ids on the rows (the add +
            // order phases below consume the returned results, not these).
            foreach (var (row, containerId) in importedContainers)
            {
                _invokeOnUi(() => row.ContainerId = containerId);
            }
        }

        try
        {
            // (b) Adds: every non-skipped library match + every successful
            // sibling import (either channel, never a cross-thread guess).
            var importedByRow = importedContainers.ToDictionary(p => p.Row, p => p.ContainerId);
            var adds = Rows
                .Where(r => !r.IsSkipped
                    && (r.Outcome == LoadOrderLineOutcome.LibraryAdd
                        || (r.Outcome == LoadOrderLineOutcome.SiblingImport
                            && importedByRow.ContainsKey(r))))
                .Select(r => r.Outcome == LoadOrderLineOutcome.LibraryAdd
                    ? r.ContainerId!.Value
                    : importedByRow[r])
                .ToArray();
            foreach (var add in adds)
            {
                _profiles.AddMod(profileId, add, ModVersionPolicy.Latest);
            }

            // (c) Order: ONE write over the profile matches plus the
            // successfully added/imported containers in file order (skipped
            // and failed rows are omitted entirely).
            var order = Rows
                .Where(r => r.Outcome == LoadOrderLineOutcome.Reorder
                    || (r.Outcome == LoadOrderLineOutcome.LibraryAdd && !r.IsSkipped)
                    || (r.Outcome == LoadOrderLineOutcome.SiblingImport
                        && !r.IsSkipped && importedByRow.ContainsKey(r)))
                .Select(r => r.Outcome == LoadOrderLineOutcome.SiblingImport
                    ? importedByRow[r]
                    : r.ContainerId!.Value)
                .ToArray();
            if (order.Length > 0)
            {
                _profiles.SetModOrder(profileId, order);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Applying the load-order import for {Profile} failed.", profileId);
            ShowInlineFailure(ex.Message);
            return;
        }

        // (d) The download batch, in TWO phases so a fast completion can
        // never race the placement plan: (1) resolve every head file + the
        // profile name (awaits allowed, NO admissions), then (2) record the
        // plan FIRST and admit every resolved item synchronously, with no
        // awaits between the plan registration and the admissions. Under an
        // admit-as-you-resolve shape, a quick earlier download could complete
        // (and append its container) while the loop still awaited the next
        // resolve, before the plan existed, missing the convergence forever.
        var admissions = new List<(LoadOrderRowViewModel Row, int ModId, int FileId)>();
        var rateLimited = false;
        if (enqueueRows.Length > 0)
        {
            string profileName;
            try
            {
                profileName = _profiles.GetProfile(profileId).Name;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(
                    ex, "The load-order apply's target profile {Profile} vanished before the enqueue batch.", profileId);
                ShowInlineFailure(_localization["LoadOrder_ProfileGoneFailure"]);
                FinishApply();
                return;
            }

            foreach (var row in enqueueRows)
            {
                var modId = row.IdentifiedModId!.Value;
                try
                {
                    var (fileId, _) = await _acquisition.ResolveLatestNexusAsync(
                        NexusGameIdentity.DarktideDomain, modId);
                    admissions.Add((row, modId, fileId));
                }
                catch (NexusRateLimitException ex)
                {
                    // Stop-on-429: no further resolves. The admissions so far
                    // still enter phase (2) below: admitting them is
                    // in-memory (no Nexus call), so prior work keeps standing
                    // exactly as it did when each row was admitted as it
                    // resolved. The failure says the run can be re-applied.
                    rateLimited = true;
                    _logger.LogWarning(
                        ex, "The load-order enqueue batch hit a rate limit; {Remaining} line(s) remain.",
                        enqueueRows.Length - admissions.Count - 1);
                    break;
                }
                catch (Exception ex)
                {
                    // A single line's resolve failure has no row to host it
                    // on (no item was enqueued): record it on the line,
                    // continue the batch.
                    _logger.LogError(
                        ex, "Resolving the latest release of '{Name}' (mod {Mod}) failed.", row.Name, modId);
                    var detail = ex.Message;
                    row.LineFailure = _localization.Format("LoadOrder_LineEnqueueFailed", detail);
                }
            }

            AdmitDownloads(profileId, profileName, admissions, importedContainers);
        }

        if (rateLimited)
        {
            ShowInlineFailure(
                _localization["LoadOrder_EnqueueRateLimited"]
                + " " + _localization["LoadOrder_RerunnableHint"]);
            FinishApply();
            return;
        }

        _logger.LogInformation(
            "Applied a load-order import: order over {Ordered} container(s), {Imports} sibling import(s), {Enqueued} download enqueue(s).",
            Rows.Count(r => r.ContainerId is { } || admissions.Any(a => a.Row == r)),
            importedContainers.Count,
            admissions.Count);
        FinishApply();
    }

    /// <summary>
    /// Phase two of the download batch: records the placement plan BEFORE any
    /// admission (a completion racing the enqueue sequence finds the plan
    /// already registered), then admits each resolved item synchronously, with
    /// no awaits between the plan registration and the admissions. An
    /// admission that itself throws (a malformed request; a programming
    /// error) records a per-line failure and re-records the plan without that
    /// row, so only successfully admitted rows stay pending. Sets
    /// <see cref="_admittedDownloads"/> for the queued-downloads notice.
    /// </summary>
    private void AdmitDownloads(
        Guid profileId,
        string profileName,
        List<(LoadOrderRowViewModel Row, int ModId, int FileId)> admissions,
        List<(LoadOrderRowViewModel Row, Guid ContainerId)> imported)
    {
        _admittedDownloads = 0;
        if (admissions.Count == 0)
        {
            return;
        }

        var pending = new List<(LoadOrderRowViewModel Row, int ModId, int FileId)>(admissions);
        RecordPlacements(profileId, pending, imported);
        foreach (var admission in admissions)
        {
            try
            {
                _downloadQueue.Enqueue(new ModDownloadRequest(
                    NexusGameIdentity.DarktideDomain, admission.ModId, admission.FileId,
                    DownloadPurpose.ProfileAdd,
                    ContainerId: null, admission.Row.Name, profileId, profileName));
                _admittedDownloads++;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Enqueueing the download of '{Name}' (mod {Mod}) failed.", admission.Row.Name, admission.ModId);
                pending.Remove(admission);
                RecordPlacements(profileId, pending, imported);
                var detail = ex.Message;
                admission.Row.LineFailure = _localization.Format("LoadOrder_LineEnqueueFailed", detail);
            }
        }
    }

    /// <summary>
    /// Records the profile-scoped pending placement plan for the downloads
    /// about to be admitted (the apply's SetModOrder already fixed every
    /// known container's position; the plan carries those anchors + the
    /// pending mod ids so each completion converges the order). The slots are
    /// built from the apply's DATA (the import results), never the rows'
    /// display-side container assignments, which a deferred marshal seam may
    /// not have applied yet. A later import for the same profile supersedes
    /// the plan.
    /// </summary>
    private void RecordPlacements(
        Guid profileId,
        List<(LoadOrderRowViewModel Row, int ModId, int FileId)> downloads,
        List<(LoadOrderRowViewModel Row, Guid ContainerId)> imported)
    {
        var downloadRows = downloads.Select(d => d.Row).ToHashSet();
        var importedByRow = imported.ToDictionary(p => p.Row, p => p.ContainerId);
        var slots = new List<LoadOrderPlacementSlot>();
        foreach (var row in Rows)
        {
            if (row.Outcome == LoadOrderLineOutcome.Reorder
                || (row.Outcome == LoadOrderLineOutcome.LibraryAdd && !row.IsSkipped))
            {
                slots.Add(new LoadOrderPlacementSlot(row.ContainerId, 0));
            }
            else if (row.Outcome == LoadOrderLineOutcome.SiblingImport
                && !row.IsSkipped
                && importedByRow.TryGetValue(row, out var importedId))
            {
                slots.Add(new LoadOrderPlacementSlot(importedId, 0));
            }
            else if (row.Outcome == LoadOrderLineOutcome.Unresolved && downloadRows.Contains(row))
            {
                slots.Add(new LoadOrderPlacementSlot(null, row.IdentifiedModId!.Value));
            }
        }

        _placements.Set(profileId, slots);
    }

    /// <summary>
    /// The success tail (also the stop-on-429 + per-line-failure tail): marks
    /// the session pending, raises <see cref="OrderApplied"/> so the mod list
    /// reloads, and deactivates the workspace only when nothing on it still
    /// reports a failure (an apply failure or any row's per-line failure
    /// keeps the review open so the messages stay readable + a re-run can
    /// finish the lines).
    /// </summary>
    private void FinishApply()
    {
        _session.HasPendingChanges = true;
        OrderApplied?.Invoke(this, EventArgs.Empty);
        if (_resetPendingAfterApply)
        {
            // The profile switched mid-apply: the apply finished against its
            // captured profile, and the deferred reset deactivates the
            // workspace over the new profile regardless of any failure state.
            _resetPendingAfterApply = false;
            Reset();
            return;
        }

        if (ApplyFailure is null && Rows.All(r => r.LineFailure is null))
        {
            Reset();
        }
        else if (_admittedDownloads > 0)
        {
            // Admitted downloads render as rows in the mod list, which the
            // still-open workspace hides: name their existence so they are
            // not lost on the user (a one-line status, not a redesign).
            QueuedDownloadsNotice = _localization.Format(
                "LoadOrder_QueuedDownloadsNotice", _admittedDownloads);
            OnPropertyChanged(nameof(QueuedDownloadsNotice));
        }
    }

    /// <summary>
    /// The expected exception families for a sibling import (the import
    /// workflow's family): caught, recorded on the line, and skipped past.
    /// Any other exception is unexpected and aborts the apply.
    /// </summary>
    private static bool IsExpectedImportException(Exception ex) =>
        ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException
            or System.IO.InvalidDataException;

    /// <summary>
    /// Cancels the workflow: no writes, the workspace deactivates (the picked
    /// file is untouched; a re-run starts fresh). Refused mid-apply (Back's
    /// defense-in-depth shape): the in-flight apply owns its captured profile
    /// and its writes.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsApplying)
        {
            return;
        }

        Reset();
    }

    /// <summary>
    /// Returns from the review to the mode choice: the rows are discarded
    /// (rebuilt at the next choice) and the search queue stops.
    /// </summary>
    [RelayCommand]
    private void Back()
    {
        if (IsApplying || Stage != LoadOrderStage.Reviewing)
        {
            return;
        }

        _searchCancellation?.Cancel();
        ClearRows();
        Stage = LoadOrderStage.ChoosingMode;
        Mode = LoadOrderImportMode.Reorder;
        _premiumAtEntry = false;
        OnPropertyChanged(nameof(ChoiceSummaryText));
    }

    /// <summary>
    /// The inline failure detail of a refused apply, or null when the last
    /// attempt succeeded or none ran; the workspace stays open for retry or
    /// cancel.
    /// </summary>
    public string? ApplyFailure { get; private set; }

    /// <summary>
    /// The localized notice, shown only while a failure keeps the workspace
    /// open after downloads were admitted: those downloads render as rows in
    /// the mod list, which the open workspace hides, so their existence is
    /// named rather than lost. Null when nothing is queued.
    /// </summary>
    public string? QueuedDownloadsNotice { get; private set; }

    private void ShowInlineFailure(string detail)
    {
        ApplyFailure = detail;
        OnPropertyChanged(nameof(ApplyFailure));
    }

    /// <summary>
    /// Session-driven: the active profile changed. An open workspace resets
    /// (its reconciliation + apply target the profile it was started on). An
    /// in-flight apply finishes against its captured profile first (its
    /// imports + writes target it); the reset lands in
    /// <see cref="FinishApply"/>.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId) || !IsActive)
        {
            return;
        }

        if (IsApplying)
        {
            _logger.LogInformation(
                "Active profile changed during a load-order apply; deferring the reset until it completes.");
            _resetPendingAfterApply = true;
            return;
        }

        _logger.LogInformation("Active profile changed during a load-order import; resetting.");
        Reset();
    }

    /// <summary>
    /// The workspace's own localized property getters re-fired on a culture
    /// change; the per-row text refreshes through the rows' own Refresh.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(SearchStatusText),
        nameof(ChoiceSummaryText),
        nameof(RemoteUnavailableNoticeText),
    };

    /// <summary>Culture changed: re-fire each row's localized text.</summary>
    protected override void OnCultureChanged()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    private void ClearRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.Clear();
    }

    /// <summary>
    /// Resets the workspace to inactive: stops the search queue, clears the
    /// rows, the plan, the capture, and any apply failure, then reports the
    /// flip to the shared card gate (the toolbar lock + Add disable follow).
    /// </summary>
    private void Reset()
    {
        _searchCancellation?.Cancel();
        ClearRows();
        _capturedProfileId = null;
        _sourcePath = null;
        _planLines = Array.Empty<LoadOrderLine>();
        _premiumAtEntry = false;
        _resetPendingAfterApply = false;
        _admittedDownloads = 0;
        ApplyFailure = null;
        OnPropertyChanged(nameof(ApplyFailure));
        QueuedDownloadsNotice = null;
        OnPropertyChanged(nameof(QueuedDownloadsNotice));
        Stage = LoadOrderStage.Inactive;
        Mode = LoadOrderImportMode.Reorder;
        _cards.ReportActive(this, active: false);
    }
}
