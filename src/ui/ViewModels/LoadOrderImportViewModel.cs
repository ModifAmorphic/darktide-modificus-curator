using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// One search candidate proposed for an unresolved row: the mod's display
/// name + its Nexus mod id (what the accept action records on the row).
/// </summary>
/// <param name="ModId">The Nexus mod id.</param>
/// <param name="Name">The mod's display name.</param>
public sealed record NexusSearchCandidate(int ModId, string Name);

/// <summary>
/// One review-table row: a single parsed file line's reconciliation result.
/// Plain state (the outcome, the match, the include checkbox); the parent
/// <see cref="LoadOrderImportViewModel"/> owns every action. The localized
/// outcome label resolves through the injected
/// <see cref="LocalizationService"/> and re-fires via <see cref="Refresh"/>
/// (the parent's culture hook), the
/// <see cref="DiscoveryFieldRowViewModel"/> transient-row pattern.
/// </summary>
public partial class LoadOrderRowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Creates a row from a plan line. The include default follows the
    /// outcome: reorder lines default included, add lines default excluded
    /// (the operator's opt-in intent), unresolved lines start unchecked with
    /// their checkbox disabled.
    /// </summary>
    public LoadOrderRowViewModel(LoadOrderLine line, LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Name = line.Name;
        Outcome = line.Outcome;
        ContainerId = line.ContainerId;
        MatchText = line.DisplayName ?? "-";
        _isIncluded = Outcome == LoadOrderLineOutcome.Reorder;
    }

    /// <summary>The file's folder name (the parsed line, trimmed).</summary>
    public string Name { get; }

    /// <summary>The reconciliation outcome driving the label + checkbox rules.</summary>
    public LoadOrderLineOutcome Outcome { get; }

    /// <summary>
    /// The matched container, or null when unmatched. A sibling-import row
    /// starts null and receives the imported container's id at apply time
    /// (the parent records it so the final order write can include the new
    /// container).
    /// </summary>
    public Guid? ContainerId { get; internal set; }

    /// <summary>
    /// What Curator matched the line to (the mod's display name), or
    /// <c>"-"</c> when unresolved.
    /// </summary>
    public string MatchText { get; }

    /// <summary>Whether the line resolved to nothing.</summary>
    public bool IsUnresolved => Outcome == LoadOrderLineOutcome.Unresolved;

    /// <summary>
    /// Whether the include checkbox accepts input: unresolved lines are
    /// disabled until they are identified (the enqueue path needs an identity
    /// to act on) or resolve to a sibling folder on disk (the import path
    /// needs no identity).
    /// </summary>
    public bool IsIncludeEnabled => !IsUnresolved || IsIdentified;

    /// <summary>
    /// The Nexus search URL for an unresolved line (the folder name as the
    /// keyword), or null for a resolved line. Never opened implicitly; the
    /// parent's open-on-Nexus command launches it.
    /// </summary>
    public string? SearchUrl => IsUnresolved
        ? $"https://www.nexusmods.com/games/{NexusGameIdentity.DarktideDomain}/mods/?keyword={Uri.EscapeDataString(Name)}"
        : null;

    /// <summary>
    /// The include checkbox state. For reorder lines this is the review's
    /// participation signal (apply requires at least one included line);
    /// order application itself is not optional, so SetModOrder carries all
    /// matched containers regardless. For add lines it gates the AddMod.
    /// Identified rows keep the excluded default: identification is a
    /// correction of WHAT the line is, not a decision to include it (the
    /// user opts in per the established default).
    /// </summary>
    [ObservableProperty]
    private bool _isIncluded;

    // ---- identification (the resolver's row-facing surface) ------------------
    //
    // An unresolved row either carries search candidates (the resolver tier
    // filled them) or awaits manual identification. Identification records a
    // Nexus mod id on the row; the include checkbox stays as-is (excluded by
    // default) so applying never acts on content the user did not opt into.

    /// <summary>
    /// The search candidates for this row, best first (empty until the
    /// resolver tier ran + found something; an absent list never renders the
    /// workspace).
    /// </summary>
    public IReadOnlyList<NexusSearchCandidate> Candidates { get; private set; } =
        Array.Empty<NexusSearchCandidate>();

    /// <summary>Whether any search candidate arrived (drives the workspace's top slot).</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The best candidate (the inline top slot), when one exists.</summary>
    public NexusSearchCandidate? TopCandidate => Candidates.FirstOrDefault();

    /// <summary>
    /// The candidates beyond the top slot (the expandable alternates).
    /// </summary>
    public IReadOnlyList<NexusSearchCandidate> AlternateCandidates => Candidates.Skip(1).ToArray();

    /// <summary>Whether the alternates panel is expanded.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// The identified mod id, once the user accepts a candidate or enters one
    /// manually. Null while unidentified.
    /// </summary>
    public int? IdentifiedModId { get; private set; }

    /// <summary>Whether this row has been identified (candidate or manual).</summary>
    public bool IsIdentified => IdentifiedModId is not null;

    /// <summary>How the row was identified (candidate vs manual), for the label.</summary>
    public IdentificationKind IdentifiedBy { get; private set; }

    /// <summary>
    /// The display name of the identification (the accepted candidate's name,
    /// or the entered id), shown in the match cell once identified.
    /// </summary>
    public string? IdentifiedName { get; private set; }

    /// <summary>
    /// The manually entered mod id / URL text (two-way bound to the id cell).
    /// Parsed on save via the shared ImportSourceValidator rules; a bare id or
    /// a nexusmods.com URL both accepted.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualPending))]
    private string _manualId = string.Empty;

    /// <summary>
    /// The release tag typed for an identified SIBLING-IMPORT row (the version
    /// cell; empty by default): it tags the content imported from disk, per
    /// the association semantics. Rows with no local content (identified
    /// unresolved lines) carry no version - the Premium download resolves the
    /// real version, and non-premium must visit Nexus regardless - so the
    /// cell does not exist for them. Validated non-empty-when-Nexus per the
    /// import form rules.
    /// </summary>
    [ObservableProperty]
    private string _version = string.Empty;

    /// <summary>
    /// Whether the id + version cells accept input: identified rows only.
    /// Pre-identification, the row offers the search workspace instead.
    /// </summary>
    public bool AreIdCellsEnabled => !IsIdentified;

    /// <summary>
    /// Whether the version cell renders: an IDENTIFIED sibling-import row
    /// only. The cell tags the content imported from disk; every other row
    /// shape (unidentified siblings, identified lines with no local content)
    /// shows nothing in the version column - absent, not disabled - so no
    /// row contract advertises a version it cannot use.
    /// </summary>
    public bool IsVersionCellVisible =>
        Outcome == LoadOrderLineOutcome.SiblingImport && IsIdentified;

    /// <summary>Whether the row is still searching (a spinner affordance).</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>
    /// The sibling mod folder this line imports from (the txt's own
    /// directory carries the content), or null for every other line kind.
    /// Set once by the parent at activation; the apply's import path reads
    /// it.
    /// </summary>
    public string? SiblingPath { get; internal set; }

    /// <summary>
    /// The localized per-line failure of the apply's import or enqueue for
    /// this row (a failed import, or a download resolve that could not
    /// produce an item), or null. The apply continues past failed lines;
    /// the message states what did not land so a re-run can finish it.
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
    /// Whether the manual-identification Apply button shows: an unresolved,
    /// unidentified row whose manual text parses to a Nexus id (the check
    /// button commits the parse).
    /// </summary>
    public bool IsManualPending =>
        IsUnresolved
        && !IsIdentified
        && ImportSourceValidator.TryParseUrl(ImportSource.Nexus, ManualId, out var parsed)
        && parsed is NexusSource;

    /// <summary>
    /// Whether the candidate workspace renders: an unresolved, unidentified
    /// row with at least one candidate (the top slot + the expand
    /// affordance).
    /// </summary>
    public bool ShowCandidateWorkspace => IsUnresolved && !IsIdentified && HasCandidates;

    /// <summary>Whether any alternate candidate exists (the expand affordance's visibility).</summary>
    public bool HasAlternateCandidates => AlternateCandidates.Count > 0;

    /// <summary>
    /// Applies the resolver tier's candidates to the row (best first). The
    /// include checkbox is untouched: candidates are proposals, and the
    /// identified-default stays excluded until the user opts in.
    /// </summary>
    public void ApplyCandidates(IReadOnlyList<NexusSearchCandidate> candidates)
    {
        Candidates = candidates;
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(TopCandidate));
        OnPropertyChanged(nameof(AlternateCandidates));
        OnPropertyChanged(nameof(ShowCandidateWorkspace));
        OnPropertyChanged(nameof(HasAlternateCandidates));
    }

    /// <summary>
    /// Marks the row identified by an accepted candidate (records the id +
    /// the candidate's name; collapses the workspace).
    /// </summary>
    public void IdentifyFromCandidate(NexusSearchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IdentifiedModId = candidate.ModId;
        IdentifiedName = candidate.Name;
        IdentifiedBy = IdentificationKind.Candidate;
        OnIdentified();
    }

    /// <summary>
    /// Marks the row identified manually (records the parsed id; the entered
    /// text may have been a URL). The display name falls back to the id.
    /// </summary>
    public void IdentifyManually(int modId)
    {
        IdentifiedModId = modId;
        IdentifiedName = "#" + modId;
        IdentifiedBy = IdentificationKind.Manual;
        OnIdentified();
    }

    /// <summary>
    /// Re-fires every projection that reads the identification state: the
    /// fact itself, the cell activation family (AreIdCellsEnabled gates the
    /// manual-id cell), the include checkbox's enable (an unresolved row's
    /// checkbox ENABLES once identified: the enqueue path now has an identity
    /// to act on), the manual-pending parse gate, the workspace visibility,
    /// and the version cell's visibility (it renders for identified
    /// sibling-import rows only). Anything new that keys off IsIdentified
    /// joins this list.
    /// </summary>
    private void OnIdentified()
    {
        OnPropertyChanged(nameof(IsIdentified));
        OnPropertyChanged(nameof(AreIdCellsEnabled));
        OnPropertyChanged(nameof(IsIncludeEnabled));
        OnPropertyChanged(nameof(IsManualPending));
        OnPropertyChanged(nameof(IdentifiedName));
        OnPropertyChanged(nameof(ShowCandidateWorkspace));
        OnPropertyChanged(nameof(IsVersionCellVisible));
        IsExpanded = false;
    }

    /// <summary>
    /// How a row came to be identified: an accepted search candidate, or the
    /// user's manual id/URL entry.
    /// </summary>
    public enum IdentificationKind
    {
        /// <summary>Not identified.</summary>
        None,

        /// <summary>An accepted search candidate.</summary>
        Candidate,

        /// <summary>Manual id/URL entry through the reserved cells.</summary>
        Manual,
    }

    /// <summary>The localized outcome label. Re-resolves on a culture change.</summary>
    public string OutcomeText => Outcome switch
    {
        LoadOrderLineOutcome.Reorder => _localization["LoadOrder_OutcomeReorder"],
        LoadOrderLineOutcome.LibraryAdd => _localization["LoadOrder_OutcomeAdd"],
        LoadOrderLineOutcome.SiblingImport => _localization["LoadOrder_OutcomeImport"],
        _ => _localization["LoadOrder_OutcomeUnresolved"],
    };

    /// <summary>
    /// Re-fires the localized outcome label after a culture change (called by
    /// the parent; the row never subscribes to the application-lifetime
    /// localization service itself).
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(OutcomeText));
    }
}

/// <summary>
/// The load-order import card's VM: the table-centric review + apply flow for
/// a picked <c>mod_load_order.txt</c>. An application-lifetime singleton
/// child VM (the <see cref="ImportWorkflowViewModel"/> pattern: registered
/// before <see cref="ModListViewModel"/>, inactive until started). Owns the
/// file read + parse, the reconciliation call, the review table rows, and the
/// apply (one <c>SetModOrder</c> + the included <c>AddMod</c>s); the parent
/// reloads the mod list on the raised <see cref="OrderApplied"/> event.
/// </summary>
/// <remarks>
/// <para><b>Review-as-the-confirm:</b> the table IS the review; Apply
/// confirms nothing further. The checkboxes gate only the library ADDS
/// (reorder lines default included, add lines default excluded, unresolved
/// lines disabled); the order application carries every matched container
/// regardless, and unmatched names are fully visible with an open-on-Nexus
/// link, never silently dropped.</para>
/// <para><b>Coexistence:</b> the card refuses to start while any other hosted
/// card is active and reports its own activity through the shared
/// <see cref="ModCardsGate"/> (which the import workflow also reports to, so
/// the exclusion is symmetric without either VM referencing the other; the
/// gate also drives the toolbar lock + Add disable).</para>
/// <para><b>Resolution tiers:</b> the repo tier (profile / library) resolves
/// what Curator already holds; the sibling tier upgrades unresolved lines
/// whose folders sit beside the picked txt (the migration path); the search
/// tier proposes anonymous Nexus candidates for the remaining unresolved
/// rows (the identification workspace: accepted candidates + manual id/URL
/// entry). Identified rows feed the apply's enqueue batch; identified
/// sibling rows carry their Nexus identity into the import.</para>
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
    private readonly ModCardsGate _cards;
    private readonly IExternalLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LoadOrderImportViewModel> _logger;

    private Guid? _capturedProfileId;
    private string? _sourcePath;

    // ---- the search queue ----------------------------------------------------
    //
    // One search at a time over the unresolved rows, in table order, human
    // paced (the Cloudflare posture): no parallel fan-out, no retries. The
    // token stops the queue on cancel/reset; arrivals marshal to the UI
    // thread through the injected seam.

    private CancellationTokenSource? _searchCancellation;

    /// <summary>
    /// Set when the session's active profile changed while an apply was
    /// running: the reset is deferred so the in-flight apply completes
    /// against its captured profile (its imports + writes target it), and
    /// <see cref="FinishApply"/> performs the reset afterward. Resetting
    /// mid-apply would empty Rows under the running phases, silently
    /// dropping the AddMods, the order write, and the enqueues while the
    /// repo imports had already landed.
    /// </summary>
    private bool _resetPendingAfterApply;

    /// <summary>
    /// Creates the card VM, inactive, and subscribes to the session (reset on
    /// active-profile change) and localization (refresh the row labels on a
    /// culture change).
    /// </summary>
    /// <param name="nexus">The Nexus client: the anonymous search used to
    /// propose candidates for unresolved rows (one at a time, human-paced).
    /// A failed search leaves the row unresolved with the manual path
    /// available; failures are logged, never alerted.</param>
    /// <param name="invokeOnUi">The marshal-to-UI-thread seam: search
    /// continuations resume on a background context (the client is awaited
    /// directly), so every row mutation funnels through here (the download
    /// queue's seam pattern).</param>
    public LoadOrderImportViewModel(
        IProfileService profiles,
        IProfileSession session,
        ILoadOrderReconciler reconciler,
        INexusClient nexus,
        IModImportService imports,
        INexusAuthService auth,
        IModAcquisitionService acquisition,
        IModDownloadQueue downloadQueue,
        ModCardsGate cards,
        IExternalLauncher launcher,
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
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
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

    /// <summary>
    /// A row's include checkbox flipped: re-fire the Apply button's enabled
    /// projections so the button follows (the button binds
    /// <see cref="CanApplyNow"/>; the rows are transient, so they push up
    /// instead of the card polling).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoadOrderRowViewModel.IsIncluded))
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanApplyNow));
        }
    }

    /// <summary>The review table's rows, one per parsed file line, in file order.</summary>
    public ObservableCollection<LoadOrderRowViewModel> Rows { get; } = new();

    /// <summary>Whether the card is showing (a review session is open).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyNotice))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    private bool _isActive;

    /// <summary>The picked file's full path (tooltip + automation name; the header ellipsizes it).</summary>
    public string SourcePath => _sourcePath ?? string.Empty;

    /// <summary>Whether the table holds any row (false only for an empty/comment-only file).</summary>
    public bool HasRows => IsActive && Rows.Count > 0;

    /// <summary>
    /// Whether the localized empty-file notice shows: an active session over
    /// a file that parsed to nothing (only comments or blank lines). Apply
    /// refuses rather than performing a no-op write.
    /// </summary>
    public bool ShowEmptyNotice => IsActive && Rows.Count == 0;

    /// <summary>
    /// Whether Apply may run: an active session with at least one included
    /// line (a reorder include or a toggled-on add). An all-unmatched or
    /// entirely-excluded table stays disabled.
    /// </summary>
    public bool CanApply => IsActive && Rows.Any(r => r.IsIncluded);

    /// <summary>
    /// The Apply button's enabled projection: CanApply AND not mid-apply
    /// (the buttons disable while the apply runs; the card stays active,
    /// holding the card gate + the toolbar lock through it).
    /// </summary>
    public bool CanApplyNow => CanApply && !IsApplying;

    /// <summary>The Cancel button's enabled projection: not mid-apply.</summary>
    public bool CanCancelNow => !IsApplying;

    /// <summary>
    /// Raised after a successful apply (UI thread). The mod list reloads from
    /// it: the order changed and included library mods joined the profile.
    /// </summary>
    public event EventHandler? OrderApplied;

    /// <summary>
    /// Starts a review session from a picked <c>mod_load_order.txt</c>:
    /// reads + parses the file, reconciles it against the active profile and
    /// repository, and activates the card with the table rows. Refuses
    /// (no-op, logged) while this or any other hosted card is active, with
    /// no active profile, or for a null/blank path; a read or reconciliation
    /// failure surfaces the localized alert and leaves the card inactive.
    /// </summary>
    [RelayCommand]
    private async Task StartImport(string? path)
    {
        if (IsActive)
        {
            _logger.LogWarning("Load-order start rejected: the card is already active.");
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

        _capturedProfileId = profileId;
        _sourcePath = path;
        Rows.Clear();
        var siblings = ScanSiblingModFolders(path);
        foreach (var line in plan.Lines)
        {
            var effective = line;
            var siblingPath = (string?)null;
            // A sibling folder with this name upgrades the unmatched line to
            // an import line: the txt's own directory carries the content
            // (the migration case). Resolved lines are never upgraded (the
            // profile/library match wins; the folder is not scanned for
            // them).
            if (effective.Outcome == LoadOrderLineOutcome.Unresolved
                && siblings.TryGetValue(effective.Name, out siblingPath))
            {
                effective = new LoadOrderLine(
                    effective.Name,
                    LoadOrderLineOutcome.SiblingImport,
                    ContainerId: null,
                    MatchedBaseName: null,
                    DisplayName: Path.GetFileName(siblingPath.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }

            var row = new LoadOrderRowViewModel(effective, _localization)
            {
                SiblingPath = siblingPath,
            };
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        IsActive = true;
        _cards.ReportActive(this, active: true);
        OnPropertyChanged(nameof(SourcePath));
        _logger.LogInformation(
            "Started a load-order review of {Path}: {Lines} line(s), {Adds} add candidate(s), {Unmatched} unmatched.",
            path, plan.Lines.Count, plan.LibraryAdds.Count, plan.UnmatchedNames.Count);

        // The resolver tier: the remaining unresolved rows get search
        // candidates (one at a time, table order). Fire-and-forget: the
        // queue owns its failures (logged, never alerted) + stops when the
        // card closes.
        _ = RunSearchQueueAsync();
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

    /// <summary>
    /// Whether the apply is running: the Apply + Cancel buttons disable for
    /// the duration (the card stays active, so both hosted cards' start
    /// guards + the toolbar lock hold through the apply). Double-apply is a
    /// no-op.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNow))]
    [NotifyPropertyChangedFor(nameof(CanCancelNow))]
    private bool _isApplying;

    /// <summary>
    /// The serial search queue: one unresolved row at a time, in table order,
    /// firing the anonymous Nexus search with the folder name normalized into
    /// search terms (lowercase, underscores/hyphens to spaces, whitespace
    /// collapsed) and applying the top candidates to the row. Human-paced:
    /// serial awaits with the <see cref="SearchQueueDelay"/> pause between
    /// queries (the Cloudflare posture); no retries on failure (a failed
    /// search leaves the row unresolved with the manual path available). Row
    /// mutations marshal to the UI thread through the injected seam; the
    /// queue observes the card's cancellation so closing the card stops
    /// further searches while arrived candidates stay on their rows.
    /// </summary>
    private async Task RunSearchQueueAsync()
    {
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        var first = true;
        foreach (var row in Rows.Where(r => r.IsUnresolved).ToArray())
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Human-paced: pause between queries (not before the first: the
            // user just picked the file). Cancelled cards exit the delay
            // immediately.
            if (!first)
            {
                try
                {
                    await Task.Delay(SearchQueueDelay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            first = false;
            _invokeOnUi(() => row.IsSearching = true);
            try
            {
                // Awaited on the calling context: the client's I/O runs
                // without capturing this loop, and the continuation below
                // hops to the UI thread explicitly (no ConfigureAwait(false);
                // the UI-layer convention applies to the mutations, the seam
                // carries them).
                var response = await _nexus.SearchModsAsync(
                    NexusGameIdentity.DarktideDomain,
                    NormalizeSearchTerms(row.Name),
                    MaxCandidatesPerRow,
                    token);

                _invokeOnUi(() =>
                {
                    row.IsSearching = false;
                    row.ApplyCandidates(response.Data
                        .Take(MaxCandidatesPerRow)
                        .Select(r => new NexusSearchCandidate(r.ModId, r.Name))
                        .ToArray());
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed search is a proposal gap, not an error state: log,
                // leave the row unresolved (the manual path + the
                // open-on-Nexus link remain), keep draining the queue.
                _logger.LogInformation(
                    ex, "The search for load-order line '{Name}' failed; leaving it unresolved.", row.Name);
                _invokeOnUi(() => row.IsSearching = false);
            }
        }
    }

    /// <summary>
    /// Normalizes a mod folder name into search terms: lowercase, underscores
    /// + hyphens to spaces, whitespace collapsed to single spaces, trimmed.
    /// (The stemmed index matches space-separated stemmed words.)
    /// </summary>
    internal static string NormalizeSearchTerms(string folderName)
    {
        var spaced = folderName.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The per-row candidate cap (the inline top slot + the alternates).</summary>
    private const int MaxCandidatesPerRow = 5;

    /// <summary>
    /// Accepts a search candidate for a row: records the identification
    /// (the row's id + version cells activate) without touching the include
    /// checkbox (identification is not consent; the identified default stays
    /// excluded).
    /// </summary>
    [RelayCommand]
    private void AcceptCandidate(LoadOrderRowViewModel? row)
    {
        if (row?.TopCandidate is { } candidate)
        {
            row.IdentifyFromCandidate(candidate);
        }
    }

    /// <summary>
    /// Accepts one of the alternates (the expand panel's rows).
    /// </summary>
    [RelayCommand]
    private void AcceptAlternate((LoadOrderRowViewModel Row, NexusSearchCandidate Candidate)? arg)
    {
        if (arg is { } pair)
        {
            pair.Row.IdentifyFromCandidate(pair.Candidate);
        }
    }

    /// <summary>
    /// Applies the manual identification: parses the row's manual-id text (a
    /// bare mod id or a nexusmods.com URL, the shared
    /// <see cref="ImportSourceValidator"/> rules) and marks the row
    /// identified-manual. No-op on an unparsable entry (the cell's own
    /// validation shows why).
    /// </summary>
    [RelayCommand]
    private void ApplyManualId(LoadOrderRowViewModel? row)
    {
        if (row is null || row.IsIdentified)
        {
            return;
        }

        if (ImportSourceValidator.TryParseUrl(ImportSource.Nexus, row.ManualId, out var parsed)
            && parsed is NexusSource nexus)
        {
            row.IdentifyManually(nexus.ModId);
        }
    }

    /// <summary>
    /// Applies the reviewed table. Sequencing (membership before order: the
    /// order write cannot place ids that are not profile members yet):
    /// <list type="number">
    /// <item><b>Imports</b>: each INCLUDED sibling-import line imports its
    /// folder through <see cref="IModImportService.Import"/> (source =
    /// <see cref="NexusSource"/> of the identified id when identified, else
    /// <see cref="UntrackedSource"/>; version = the row's typed version when
    /// identified + non-empty, else empty, the version-unknown path). A
    /// per-line import failure is recorded on the line and the apply
    /// continues.</item>
    /// <item><b>Adds</b>: an <see cref="IProfileService.AddMod"/> (Latest
    /// policy) for every included add line (library + imported
    /// containers).</item>
    /// <item><b>Order</b>: ONE <see cref="IProfileService.SetModOrder"/>
    /// carrying every matched + newly-created container in file order, so
    /// every add lands at its file position. The checkboxes gate only adds;
    /// order application is not optional, and SetModOrder's own lock
    /// projection keeps locked entries at their exact slots.</item>
    /// <item><b>Enqueues</b>: for each INCLUDED identified not-in-Curator
    /// line, a Premium account (verified fresh through
    /// <see cref="INexusAuthService.GetCurrentStateAsync"/>) gets a download
    /// enqueued onto the shared queue (the DMF-prompt pattern: the head file
    /// is resolved first so the queue's dedupe key is real; purpose
    /// ProfileAdd onto the active profile; the download rows own progress +
    /// completion + the reload). Non-premium accounts perform no network
    /// action for these lines (the rows carry the open-on-Nexus link). These
    /// rows carry no version cell: the download resolves the real
    /// version.</item>
    /// </list>
    /// On full success the session is marked pending, the card deactivates,
    /// and <see cref="OrderApplied"/> reloads the mod list.
    /// </summary>
    /// <remarks>
    /// <para><b>Failure semantics.</b> A profile or repository failure
    /// mid-apply surfaces the localized inline failure with the card still
    /// open; the writes before the failure stand and re-runs are idempotent
    /// (imports dedupe, AddMod no-ops on existing membership, SetModOrder
    /// rewrites the same order, the queue dedupes live downloads). A
    /// per-line import or enqueue-resolve failure is recorded on its line
    /// and the rest continue. A <see cref="NexusRateLimitException"/> in the
    /// enqueue batch aborts the remaining enqueues: everything before it
    /// stands (local work + landed enqueues), the localized failure states
    /// the run can be re-applied, and the list still reloads so the landed
    /// work shows.</para>
    /// <para><b>Association note.</b> A sibling import whose row is
    /// identified IS the association (Import with
    /// <see cref="NexusSource"/>); there is no separate identity rewrite at
    /// apply. Lines matching an existing untracked container stay plain
    /// adds: the user can edit import details afterward if they want Nexus
    /// identity.</para>
    /// <para><b>Threads.</b> The import loop runs on a worker
    /// (<see cref="Task.Run"/>; filesystem-heavy) with every row mutation
    /// marshaled to the UI thread through the injected seam; the enqueue
    /// batch awaits the resolve + enqueue calls directly. No
    /// <c>ConfigureAwait(false)</c>.</para>
    /// </remarks>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply || _capturedProfileId is not Guid profileId || IsApplying)
        {
            return;
        }

        IsApplying = true;
        ApplyFailure = null;
        OnPropertyChanged(nameof(ApplyFailure));
        try
        {
            await ApplyCoreAsync(profileId);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>The apply body, staged so <see cref="ApplyAsync"/> owns the
    /// IsApplying guard in a finally.</summary>
    private async Task ApplyCoreAsync(Guid profileId)
    {
        // (a) Imports: the included sibling lines, on the worker; per-line
        // failures are recorded on their rows and the rest continue. The
        // import results are returned as DATA (the imported container ids
        // ride the task's return value); the row-side assignments below are
        // display-only, so nothing downstream depends on when they land
        // relative to the continuation.
        var importedContainers = new List<(LoadOrderRowViewModel Row, Guid ContainerId)>();
        var includedSiblingRows = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.SiblingImport && r.IsIncluded)
            .ToArray();
        if (includedSiblingRows.Length > 0)
        {
            var results = await Task.Run(() =>
            {
                var imported = new List<(LoadOrderRowViewModel Row, Guid ContainerId)>();
                foreach (var row in includedSiblingRows)
                {
                    try
                    {
                        ModSource source = row.IdentifiedModId is { } modId
                            ? new NexusSource { ModId = modId }
                            : new UntrackedSource();
                        // The version tags the content imported from disk:
                        // only an identified sibling row carries one (the
                        // version cell exists solely for that case); every
                        // other shape imports with the empty version-unknown
                        // tag.
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
            // (b) Adds: every included add line. Library adds read their
            // matched id; imported siblings read the id phase (a) returned
            // (either channel, never a cross-thread ordering guess).
            var importedByRow = importedContainers.ToDictionary(p => p.Row, p => p.ContainerId);
            var includedAdds = Rows
                .Where(r => r.IsIncluded
                    && (r.Outcome == LoadOrderLineOutcome.LibraryAdd
                        || (r.Outcome == LoadOrderLineOutcome.SiblingImport
                            && importedByRow.ContainsKey(r))))
                .Select(r => r.Outcome == LoadOrderLineOutcome.LibraryAdd
                    ? r.ContainerId!.Value
                    : importedByRow[r])
                .ToArray();
            foreach (var add in includedAdds)
            {
                _profiles.AddMod(profileId, add, ModVersionPolicy.Latest);
            }

            // (c) Order: ONE write over every matched + newly-created
            // container in file order (non-members, like a not-included
            // library add, are ignored by SetModOrder and keep their
            // relative order after the listed block). Same two channels as
            // the adds, resolved row-by-row in file order: matched ids from
            // the rows, imported ids from the returned results.
            var order = Rows
                .Select(r => r.Outcome == LoadOrderLineOutcome.SiblingImport
                    && importedByRow.TryGetValue(r, out var imported)
                        ? imported
                        : r.ContainerId)
                .Where(id => id is { })
                .Select(id => id!.Value)
                .ToArray();
            if (order.Length > 0)
            {
                _profiles.SetModOrder(profileId, order);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Applying the load-order review for {Profile} failed.", profileId);
            ShowInlineFailure(ex.Message);
            return;
        }

        // (d) Enqueues: the included identified not-in-Curator lines. Premium
        // is verified fresh (the construction-time row-context read is
        // one-shot; the account may have signed in or out since).
        var enqueueRows = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.Unresolved
                && r.IsIdentified && r.IsIncluded)
            .ToArray();
        var enqueued = 0;
        if (enqueueRows.Length > 0)
        {
            NexusAuthState? state = null;
            try
            {
                state = await _auth.GetCurrentStateAsync();
            }
            catch (Exception ex)
            {
                // The premium read failed: do not enqueue on an unverified
                // account (the fresh-verify posture; the rows keep their
                // open-on-Nexus path).
                _logger.LogWarning(ex, "The load-order premium verification failed; skipping the enqueue batch.");
            }

            if (state?.IsPremium == true)
            {
                string profileName;
                try
                {
                    // Inside the guarded region: a profile deleted mid-apply
                    // surfaces as the card-level failure rather than an
                    // unhandled KeyNotFoundException (the profile name is
                    // display-only on the request, but the read still throws
                    // for an unknown id).
                    profileName = _profiles.GetProfile(profileId).Name;
                }
                catch (KeyNotFoundException ex)
                {
                    _logger.LogWarning(
                        ex, "The load-order apply's target profile {Profile} vanished before the enqueue batch.", profileId);
                    ShowInlineFailure(ex.Message);
                    FinishApply(enqueued);
                    return;
                }

                foreach (var row in enqueueRows)
                {
                    var modId = row.IdentifiedModId!.Value;
                    try
                    {
                        // The DMF-prompt enqueue shape: resolve the head file
                        // so the queue's dedupe key is real, then admit a
                        // ProfileAdd item with no container (the download
                        // owns the import + the profile add at completion;
                        // these rows carry no version cell, and the download
                        // resolves the real version).
                        var (fileId, _) = await _acquisition.ResolveLatestNexusAsync(
                            NexusGameIdentity.DarktideDomain, modId);
                        _downloadQueue.Enqueue(new ModDownloadRequest(
                            NexusGameIdentity.DarktideDomain, modId, fileId,
                            DownloadPurpose.ProfileAdd,
                            ContainerId: null, row.Name, profileId, profileName));
                        enqueued++;
                    }
                    catch (NexusRateLimitException ex)
                    {
                        // Stop-on-429: the remaining enqueues abort. Prior
                        // work stands (the local apply above + every landed
                        // enqueue); the failure says the run can be
                        // re-applied, and the reload below surfaces it.
                        _logger.LogWarning(
                            ex, "The load-order enqueue batch hit a rate limit; {Remaining} line(s) remain.",
                            enqueueRows.Length - enqueued);
                        ShowInlineFailure(
                            _localization["LoadOrder_EnqueueRateLimited"]
                            + " " + _localization["LoadOrder_RerunnableHint"]);
                        FinishApply(enqueued);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A single line's resolve failure has no row to host
                        // it on (no item was enqueued): record it on the
                        // line, continue the batch.
                        _logger.LogError(
                            ex, "Resolving the latest release of '{Name}' (mod {Mod}) failed.", row.Name, modId);
                        var detail = ex.Message;
                        row.LineFailure = _localization.Format("LoadOrder_LineEnqueueFailed", detail);
                    }
                }
            }
            else
            {
                _logger.LogInformation(
                    "The load-order enqueue batch skipped: the account is not verified Premium ({State}).",
                    state is null ? "unverified" : "non-premium");
            }
        }

        _logger.LogInformation(
            "Applied a load-order review: order over {Ordered} container(s), {Imports} sibling import(s), {Enqueued} download enqueue(s).",
            Rows.Count(r => r.ContainerId is { }),
            includedSiblingRows.Length,
            enqueued);
        FinishApply(enqueued);
    }

    /// <summary>
    /// The success tail (also the stop-on-429 + per-line-failure tail): marks
    /// the session pending, raises <see cref="OrderApplied"/> so the mod list
    /// reloads (the local writes + any landed enqueues show), and deactivates
    /// the card only when nothing on it still reports a failure (a card-level
    /// apply failure or any row's per-line failure keeps the review open so
    /// the messages stay readable + a re-run can finish the lines).
    /// </summary>
    private void FinishApply(int enqueued)
    {
        _session.HasPendingChanges = true;
        OrderApplied?.Invoke(this, EventArgs.Empty);
        if (_resetPendingAfterApply)
        {
            // The profile switched mid-apply: the apply finished against its
            // captured profile (this reload reads whatever profile is now
            // active), and the deferred reset deactivates the card over the
            // new profile regardless of any failure state.
            _resetPendingAfterApply = false;
            Reset();
            return;
        }

        if (ApplyFailure is null && Rows.All(r => r.LineFailure is null))
        {
            Reset();
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
    /// Cancels the review: no writes, the card deactivates (the picked file
    /// is untouched; a re-run starts fresh).
    /// </summary>
    [RelayCommand]
    private void Cancel() => Reset();

    /// <summary>
    /// Opens an unresolved row's Nexus search (the folder name as the
    /// keyword) in the user's browser via the injectable launcher, surfacing
    /// the localized fallback alert on a launch failure rather than
    /// swallowing it.
    /// </summary>
    [RelayCommand]
    private async Task OpenOnNexus(LoadOrderRowViewModel? row)
    {
        if (row?.SearchUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            if (!_launcher.OpenUri(uri))
            {
                _logger.LogWarning("Opening the Nexus search for '{Name}' failed.", row.Name);
                await LaunchAlerts.ShowAsync(
                    _dialogs, _localization,
                    "LoadOrder_SearchFailedTitle",
                    "LoadOrder_SearchFailedMessage",
                    url);
            }
        }
        catch (Exception ex)
        {
            // The launcher's exception filter is narrow; a real wiring bug
            // surfaces here as a fallback alert rather than being swallowed.
            _logger.LogError(ex, "Launching the Nexus search for '{Name}' threw.", row.Name);
            await LaunchAlerts.ShowAsync(
                _dialogs, _localization,
                "LoadOrder_SearchFailedTitle",
                "LoadOrder_SearchFailedMessage",
                url);
        }
    }

    /// <summary>
    /// The inline failure detail of a refused apply, or null when the last
    /// attempt succeeded or none ran; the card stays open for retry or
    /// cancel.
    /// </summary>
    public string? ApplyFailure { get; private set; }

    private void ShowInlineFailure(string detail)
    {
        ApplyFailure = detail;
        OnPropertyChanged(nameof(ApplyFailure));
    }

    /// <summary>
    /// Session-driven: the active profile changed. An open review resets
    /// (its reconciliation + apply target the profile it was started on).
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId) || !IsActive)
        {
            return;
        }

        if (IsApplying)
        {
            // Defer: the in-flight apply owns the captured profile until it
            // finishes (its writes target it); the reset lands in
            // FinishApply.
            _logger.LogInformation(
                "Active profile changed during a load-order apply; deferring the reset until it completes.");
            _resetPendingAfterApply = true;
            return;
        }

        _logger.LogInformation("Active profile changed during a load-order review; resetting.");
        Reset();
    }

    /// <summary>
    /// The card VM itself carries no localized property getters (the title,
    /// buttons, empty notice, and table headers resolve in the view); the
    /// per-row outcome labels refresh through the rows' own Refresh.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = Array.Empty<string>();

    /// <summary>Culture changed: re-fire each row's localized outcome label.</summary>
    protected override void OnCultureChanged()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// Resets the card to inactive: clears the rows, the capture, the source
    /// path, and any apply failure, then reports the flip to the shared
    /// card gate (the toolbar lock + Add disable follow).
    /// </summary>
    private void Reset()
    {
        _searchCancellation?.Cancel();
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.Clear();
        _capturedProfileId = null;
        _sourcePath = null;
        ApplyFailure = null;
        OnPropertyChanged(nameof(ApplyFailure));
        IsActive = false;
        _cards.ReportActive(this, active: false);
    }
}
