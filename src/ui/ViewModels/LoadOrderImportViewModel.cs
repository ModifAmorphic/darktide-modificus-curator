using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>The matched container, or null when unresolved.</summary>
    public Guid? ContainerId { get; }

    /// <summary>
    /// What Curator matched the line to (the mod's display name), or
    /// <c>"-"</c> when unresolved.
    /// </summary>
    public string MatchText { get; }

    /// <summary>Whether the line resolved to nothing.</summary>
    public bool IsUnresolved => Outcome == LoadOrderLineOutcome.Unresolved;

    /// <summary>
    /// Whether the include checkbox accepts input: unresolved lines are
    /// disabled (they cannot be included until the resolver tiers can
    /// identify them).
    /// </summary>
    public bool IsIncludeEnabled => !IsUnresolved;

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
    /// </summary>
    [ObservableProperty]
    private bool _isIncluded;

    /// <summary>The localized outcome label. Re-resolves on a culture change.</summary>
    public string OutcomeText => Outcome switch
    {
        LoadOrderLineOutcome.Reorder => _localization["LoadOrder_OutcomeReorder"],
        LoadOrderLineOutcome.LibraryAdd => _localization["LoadOrder_OutcomeAdd"],
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
/// <para><b>Offline-complete for the local tier:</b> the table renders the
/// repo-tier outcomes only (profile / library / unresolved). The resolver
/// tiers (download record, search, user identification) extend the
/// unresolved rows in later work; the id/version cells are reserved in the
/// table layout for them.</para>
/// </remarks>
public partial class LoadOrderImportViewModel : LocalizedViewModel
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly ILoadOrderReconciler _reconciler;
    private readonly ModCardsGate _cards;
    private readonly IExternalLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LoadOrderImportViewModel> _logger;

    private Guid? _capturedProfileId;
    private string? _sourcePath;

    /// <summary>
    /// Creates the card VM, inactive, and subscribes to the session (reset on
    /// active-profile change) and localization (refresh the row labels on a
    /// culture change).
    /// </summary>
    public LoadOrderImportViewModel(
        IProfileService profiles,
        IProfileSession session,
        ILoadOrderReconciler reconciler,
        ModCardsGate cards,
        IExternalLauncher launcher,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<LoadOrderImportViewModel> logger)
        : base(localization)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    /// <summary>
    /// A row's include checkbox flipped: re-fire <see cref="CanApply"/> so
    /// the Apply button's enabled state follows (the button binds CanApply;
    /// the rows are transient, so they push up instead of the card polling).
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoadOrderRowViewModel.IsIncluded))
        {
            OnPropertyChanged(nameof(CanApply));
        }
    }

    /// <summary>The review table's rows, one per parsed file line, in file order.</summary>
    public ObservableCollection<LoadOrderRowViewModel> Rows { get; } = new();

    /// <summary>Whether the card is showing (a review session is open).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyNotice))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
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
        foreach (var line in plan.Lines)
        {
            var row = new LoadOrderRowViewModel(line, _localization);
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        IsActive = true;
        _cards.ReportActive(this, active: true);
        OnPropertyChanged(nameof(SourcePath));
        _logger.LogInformation(
            "Started a load-order review of {Path}: {Lines} line(s), {Adds} add candidate(s), {Unmatched} unmatched.",
            path, plan.Lines.Count, plan.LibraryAdds.Count, plan.UnmatchedNames.Count);
    }

    /// <summary>
    /// Applies the reviewed table: ONE <see cref="IProfileService.SetModOrder"/>
    /// carrying every matched container in file order (included or not; order
    /// application is not optional, and SetModOrder's own lock projection
    /// keeps locked entries at their exact slots), then an
    /// <see cref="IProfileService.AddMod"/> (Latest policy) for each INCLUDED
    /// library-add line in file-position order, marks the session pending,
    /// deactivates the card, and raises <see cref="OrderApplied"/> so the mod
    /// list reloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rung-2 apply sequencing (deliberate simplification):</b> the order
    /// write happens BEFORE the adds, and AddMod appends, so included adds
    /// land after the listed block rather than at their file positions
    /// (SetModOrder ignores ids that are not profile members yet). The apply
    /// refinement that lands with the resolver tiers revisits this
    /// deliberately so imported/associated mods join at their file
    /// positions.</para>
    /// <para>A profile or repository failure mid-apply surfaces the localized
    /// inline failure with the card still open for retry or cancel; the
    /// writes before the failure stand (re-runs are idempotent).</para>
    /// </remarks>
    [RelayCommand]
    private void Apply()
    {
        if (!CanApply || _capturedProfileId is not Guid profileId)
        {
            return;
        }

        var order = Rows
            .Where(r => r.ContainerId is { })
            .Select(r => r.ContainerId!.Value)
            .ToArray();
        var includedAdds = Rows
            .Where(r => r.Outcome == LoadOrderLineOutcome.LibraryAdd && r.IsIncluded)
            .ToArray();

        try
        {
            _profiles.SetModOrder(profileId, order);
            foreach (var add in includedAdds)
            {
                _profiles.AddMod(profileId, add.ContainerId!.Value, ModVersionPolicy.Latest);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Applying the load-order review for {Profile} failed.", profileId);
            ShowInlineFailure(ex.Message);
            return;
        }

        _session.HasPendingChanges = true;
        _logger.LogInformation(
            "Applied a load-order review: {Ordered} container(s) ordered, {Adds} library add(s) included.",
            order.Length, includedAdds.Length);
        Reset();
        OrderApplied?.Invoke(this, EventArgs.Empty);
    }

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
    /// The inline failure detail of a refused apply, or null. Empty when the
    /// last attempt succeeded or none ran; the card stays open for retry or
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
        if (e.PropertyName == nameof(IProfileSession.ActiveProfileId) && IsActive)
        {
            _logger.LogInformation("Active profile changed during a load-order review; resetting.");
            Reset();
        }
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
