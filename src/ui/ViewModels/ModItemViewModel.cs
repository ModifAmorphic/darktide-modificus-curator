using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One row in the active profile's mod list. Carries the mod's display state
/// (container id + name + source + version + enabled + order + policy) and the
/// per-row policy edit state (Latest / Pinned). The parent <see cref="ModListViewModel"/>
/// owns all service calls; this row carries state only and never talks to
/// <see cref="Profiles.IProfileService"/> directly.
/// </summary>
/// <remarks>
/// <para><b>Identity:</b> <see cref="ContainerId"/> is immutable (the join key
/// against <see cref="IModRepository"/>). <see cref="Name"/> is the resolved
/// display name (joined from the container on reload). <see cref="Enabled"/> is
/// two-way bound to the row's CheckBox; the parent applies the toggle through
/// <c>IProfileService.SetModEnabled</c>. <see cref="Order"/> drives the display
/// sort and the up/down moves (the parent re-persists via
/// <c>IProfileService.SetModOrder</c>).</para>
/// <para><b>Source / version badge:</b> <see cref="Source"/> +
/// <see cref="ActualVersion"/> are joined from the repository by the parent on
/// reload (the resolved version for the row's policy). <see cref="Found"/> flags
/// a mod whose container is absent (a stale profile reference); the badge then
/// reads a "not found" marker (staging warns at launch; resolution is out of
/// scope here). A <see cref="LinkedSource"/> row carries no version (the external
/// folder is the single implicit version); its badge reads "External" or, when
/// the folder is missing, "Folder unavailable" (driven by
/// <see cref="IsExternalBroken"/>, pushed down from
/// <c>IModRepository.IsExternalAvailable</c> at reload).</para>
/// <para><b>Policy editor:</b> <see cref="PolicyChoice"/> (0 = Latest,
/// 1 = Pinned) is two-way bound to the row's policy ComboBox; switching it routes
/// through the view to the parent's <c>SetModPolicy</c> command. The ComboBox is
/// disabled for linked rows (<see cref="IsPolicyEditable"/>) since a linked mod
/// carries no versions to switch between.
/// <see cref="AvailableVersions"/> + <see cref="SelectedVersion"/> drive a
/// constrained dropdown of the container's versions (the row can only pin to a
/// version that exists in the container): the dropdown shows the readable
/// <see cref="ModVersion.VersionString"/> and stores the <see cref="ModVersion.Folder"/>
/// id, which the parent wraps as <c>PinnedPolicy(selectedVersionId)</c>.</para>
/// <para><b>Localized text is live:</b> <see cref="SourceBadgeText"/> +
/// <see cref="PolicyDisplayText"/> resolve from <see cref="LocalizationService"/>
/// and re-fire on a culture change (the parent calls <see cref="Refresh"/> per
/// row).</para>
/// </remarks>
public partial class ModItemViewModel : ObservableObject
{
    /// <summary>
    /// Policy choice index for the row ComboBox: <c>0</c> = Latest,
    /// <c>1</c> = Pinned.
    /// </summary>
    public const int PolicyLatest = 0;

    /// <summary>Policy choice index for the row ComboBox: Pinned.</summary>
    public const int PolicyPinned = 1;

    private readonly LocalizationService _localization;

    /// <summary>
    /// The mod container's id (immutable); the join key against
    /// <see cref="IModRepository"/> + the value written through
    /// <c>IProfileService.SetModEnabled/SetModPolicy/SetModOrder/RemoveMod</c>.
    /// </summary>
    public Guid ContainerId { get; }

    /// <summary>
    /// The container's display name (joined from the repository by the parent);
    /// shown in the row + used in the remove-confirm message. Empty when the
    /// container is missing. Settable + observable so the parent can refresh it
    /// in place after a check that renamed the container (the name-sync result),
    /// without rebuilding the row.
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Where this mod came from (Untracked / Nexus / Linked), joined from the
    /// repository by the parent. <see cref="UntrackedSource"/> when the container
    /// is absent.
    /// </summary>
    public ModSource Source { get; }

    /// <summary>
    /// The resolved version tag of the container (joined from the repository for
    /// the row's policy), or <see cref="string.Empty"/> when unknown. Shown in
    /// the policy display text + the source badge; never order-compared.
    /// </summary>
    public string ActualVersion { get; }

    /// <summary>
    /// Whether the mod is active (two-way bound to the row's CheckBox). The parent
    /// applies a user toggle through <c>IProfileService.SetModEnabled</c>.
    /// </summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>
    /// The active download morphing this row in place: the row-facing
    /// projection of the queue item whose container is this row's
    /// <see cref="ContainerId"/> while that item targets it AND the row is
    /// realized in <see cref="ModListViewModel.VisibleMods"/>. Assigned
    /// exclusively by the parent's hosting projection (never the row, never
    /// the coordinator); null when the row is an ordinary mod row. While
    /// set, the summary/metadata area and the action strip swap to the
    /// download content, the policy editor and the update-action cell
    /// suppress, and the structural controls (grip, lock, move, remove,
    /// enabled) stay functional: position and membership are profile
    /// metadata staged at launch, so reordering or toggling mid-download is
    /// harmless.
    /// </summary>
    /// <remarks>
    /// The wrapper, not the row, holds the download state (phase, bytes,
    /// failure); the row exposes only the morph decision members
    /// (<see cref="IsDownloadMorphed"/>, <see cref="ShowUpdateSpinner"/>, the
    /// widened <see cref="IsPolicyEditable"/>) the templates bind against.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadMorphed))]
    [NotifyPropertyChangedFor(nameof(IsPolicyEditable))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateSpinner))]
    private DownloadRowViewModel? _activeDownload;

    /// <summary>
    /// Whether an active download is morphing this row in place (the
    /// content-swap gate for the summary area, the update-action cell, and
    /// the action strip's Cancel affordance).
    /// </summary>
    public bool IsDownloadMorphed => ActiveDownload is not null;

    /// <summary>
    /// Whether the per-row update spinner in the badge area should render:
    /// an install in flight AND not morphed (a download morph suppresses the
    /// update affordances entirely; the download's own progress owns the
    /// row's progress surface).
    /// </summary>
    public bool ShowUpdateSpinner => IsUpdating && !IsDownloadMorphed;

    /// <summary>
    /// Position within the load order (lower loads first). Drives the display sort
    /// and the up/down move commands (the parent re-persists the order).
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// Whether this row's load-order position is locked against reordering. A
    /// locked row keeps its exact zero-based position; the move up / down buttons
    /// and the reorder grip are disabled for it, and it is skipped as a drag
    /// destination. The drag grip stays visually present but non-intercepting for
    /// a locked row, so its area falls through to touch scrolling. Pushed down by
    /// the parent from <see cref="ModListEntry.OrderLocked"/> on reload; toggled
    /// through the parent's <c>ToggleOrderLockCommand</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGripEnabled))]
    [NotifyPropertyChangedFor(nameof(OrderLockTooltip))]
    [NotifyPropertyChangedFor(nameof(OrderLockAutomationName))]
    private bool _orderLocked;

    /// <summary>
    /// Whether the move-up button is enabled for this row: an unlocked row with at
    /// least one unlocked row above it. Computed by the parent on reload (a locked
    /// row is always <c>false</c>). Pushed down so the view binds directly without
    /// a parent walk.
    /// </summary>
    [ObservableProperty]
    private bool _canMoveUp;

    /// <summary>
    /// Whether the move-down button is enabled for this row: an unlocked row with
    /// at least one unlocked row below it. Computed by the parent on reload (a
    /// locked row is always <c>false</c>). Pushed down so the view binds directly
    /// without a parent walk.
    /// </summary>
    [ObservableProperty]
    private bool _canMoveDown;

    /// <summary>
    /// Whether the drag-reorder grip is enabled for this row: an unlocked row can
    /// initiate a reorder drag; a locked row's grip is disabled and falls through
    /// to touch scrolling. The grip stays visually present in both states.
    /// </summary>
    public bool IsGripEnabled => !OrderLocked;

    /// <summary>
    /// Whether the accent insertion marker should render just above this row's top
    /// edge. Set by the view's pointer gesture on exactly one row (or none) while
    /// dragging; the marker line itself is non-hit-testable.
    /// </summary>
    [ObservableProperty]
    private bool _showReorderMarkerBefore;

    /// <summary>
    /// Whether the accent insertion marker should render just below this row's
    /// bottom edge. Set by the view's pointer gesture on exactly one row (or
    /// none) while dragging; the marker line itself is non-hit-testable.
    /// </summary>
    [ObservableProperty]
    private bool _showReorderMarkerAfter;

    /// <summary>
    /// The localized tooltip / automation text for the order-lock toggle button,
    /// describing the action the click will perform (lock vs. unlock).
    /// </summary>
    public string OrderLockTooltip => OrderLocked
        ? _localization["ModRow_UnlockOrderTooltip"]
        : _localization["ModRow_LockOrderTooltip"];

    /// <summary>
    /// The localized automation name for the order-lock toggle button, describing
    /// the row's current state (locked vs. unlocked) for assistive tech.
    /// </summary>
    public string OrderLockAutomationName => OrderLocked
        ? _localization["ModRow_OrderLocked"]
        : _localization["ModRow_OrderUnlocked"];

    /// <summary>
    /// The mod's current effective version policy. Set by the parent on reload;
    /// drives <see cref="PolicyChoice"/> + <see cref="PolicyDisplayText"/>.
    /// </summary>
    public ModVersionPolicy Policy { get; private set; }

    /// <summary>
    /// Whether the repository had a container for this entry at reload. <c>false</c>
    /// marks a stale profile reference (the badge reads "not found"; staging warns
    /// at launch).
    /// </summary>
    public bool Found { get; }

    /// <summary>
    /// Whether the update check flagged this container as having a newer release
    /// on Nexus than the imported version. Set by the parent
    /// <see cref="ModListViewModel"/> from the profile-scoped known-update state
    /// (persisted across restarts) on reload + on every
    /// <c>CheckCompleted</c>. Drives the stable update-action button's enabled
    /// state + the accent-blue download arrow. Always <c>false</c> for Pinned /
    /// Untracked rows (the update check skips them).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateActionTooltip))]
    private bool _updateAvailable;

    /// <summary>
    /// Whether the row is currently running an update install (the parent's
    /// <c>UpdateCommand</c> set it + the global coordinator's busy flag). While
    /// true, the row shows an indeterminate progress affordance in the
    /// source-badge area (immediately left of the badge), and the update-action
    /// button is disabled via <see cref="UpdateActionEnabled"/>. The button
    /// itself stays visible in its fixed cell. Cleared when the install
    /// completes (success or failure).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateSpinner))]
    private bool _isUpdating;

    /// <summary>
    /// The shared row context (premium / install-busy / gaming) this row reads
    /// its global halves from. Passed once at construction by the parent; the
    /// parent's single context subscription fans change notifications into the
    /// live rows (via <see cref="OnRowContextChanged"/>), so a row never
    /// subscribes to the application-lifetime context itself.
    /// </summary>
    public ModRowContext RowContext { get; }

    /// <summary>
    /// Whether the Nexus account was verified Premium: forwarded from
    /// <see cref="RowContext"/> (its one-shot construction read). Drives the
    /// update action's click behavior (Premium -> in-app install;
    /// regular/unknown -> open the Nexus files page) and the tooltip. The
    /// button itself shows for Nexus + Latest rows regardless of premium; only
    /// the click behavior + tooltip differ.
    /// </summary>
    public bool IsPremiumUser => RowContext.IsPremiumUser;

    /// <summary>
    /// Whether any row (or the automatic updater) is currently running an
    /// install: forwarded from <see cref="RowContext"/> (the installer's
    /// coordinator-gated busy flag), so the per-row enabled state reflects the
    /// global "one install at a time" coordination without a parent walk in
    /// the binding. Premium clicks are disabled while this is true (the
    /// coordinator would reject a second concurrent install); regular/unknown
    /// clicks (which open a files page, no install) stay enabled.
    /// </summary>
    public bool AnyRowUpdating => RowContext.AnyRowUpdating;

    /// <summary>
    /// Whether a linked row's external folder is missing at the last reload.
    /// Pushed down by the parent from <c>IModRepository.IsExternalAvailable</c>
    /// (read once per reload; availability is recomputed on rescan per the linked
    /// contract). Always <c>false</c> for non-linked rows (managed containers have
    /// no external content). Drives the badge two-state: available shows a
    /// clickable "External" pill; broken shows non-clickable "Folder unavailable"
    /// text in a warning foreground.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinkedAvailable))]
    [NotifyPropertyChangedFor(nameof(IsLinkedBroken))]
    [NotifyPropertyChangedFor(nameof(SourceBadgeText))]
    private bool _isExternalBroken;

    /// <summary>
    /// Whether the app runs inside a Steam Deck Gaming Mode session: forwarded
    /// from <see cref="RowContext"/> (constant for the process lifetime).
    /// Disables the linked row's "External" badge (its click opens the OS file
    /// manager, which depends on a desktop shell); the non-interactive "Folder
    /// unavailable" text is unaffected either way. Also swaps the
    /// update-action tooltip's open-files variant for the Desktop Mode guidance
    /// (regular/unknown users only; Premium installs stay in-app).
    /// </summary>
    public bool IsGamingMode => RowContext.IsGamingMode;

    /// <summary>
    /// A row-affecting global flipped on the shared row context (the parent's
    /// single subscription fans it here; already on the UI thread). Re-fires
    /// the row's forwarding property + the derived members that read it, so
    /// bindings + tooltips re-resolve exactly as the former per-flag pushdown
    /// made them.
    /// </summary>
    /// <param name="propertyName">The context property that flipped (one of
    /// <see cref="ModRowContext"/>'s observable names).</param>
    internal void OnRowContextChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(ModRowContext.IsPremiumUser):
                OnPropertyChanged(nameof(IsPremiumUser));
                OnPropertyChanged(nameof(UpdateActionEnabled));
                OnPropertyChanged(nameof(UpdateActionTooltip));
                break;

            case nameof(ModRowContext.AnyRowUpdating):
                OnPropertyChanged(nameof(AnyRowUpdating));
                OnPropertyChanged(nameof(UpdateActionEnabled));
                break;

            case nameof(ModRowContext.IsGamingMode):
                OnPropertyChanged(nameof(IsGamingMode));
                OnPropertyChanged(nameof(LinkedBadgeTooltip));
                OnPropertyChanged(nameof(UpdateActionTooltip));
                break;
        }
    }

    /// <summary>
    /// The linked badge's tooltip: the localized Gaming Mode guidance while
    /// gaming (shown on the disabled badge), or the ordinary open-folder tooltip
    /// in normal mode (preserving the badge's pre-existing affordance hint).
    /// Re-resolves on a culture change (via <see cref="Refresh"/>).
    /// </summary>
    public string LinkedBadgeTooltip => IsGamingMode
        ? _localization["GamingMode_FileManagerGuidance"]
        : _localization["ModRow_LinkedOpenTooltip"];

    /// <summary>
    /// Optional source-agnostic display metadata (summary, thumbnail URL,
    /// adult-content flag) joined from the container at construction and updated
    /// by the detailed-rows coordinator when backfill enriches it. <c>null</c>
    /// means no metadata has been fetched. Drives the summary, thumbnail, and
    /// content-safety derived members.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(SummaryTooltip))]
    [NotifyPropertyChangedFor(nameof(IsAdultContent))]
    [NotifyPropertyChangedFor(nameof(ThumbnailUrl))]
    [NotifyPropertyChangedFor(nameof(CanLoadThumbnail))]
    private ModDisplayMetadata? _displayMetadata;

    /// <summary>
    /// The decoded thumbnail image for this row, or <c>null</c> when none has
    /// been loaded (or the row was switched to Compact / the metadata changed).
    /// Set by the detailed-rows coordinator; the row never performs I/O.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private IImage? _thumbnail;

    /// <summary>
    /// Whether this row is displayed in Detailed mode. Pushed down by the
    /// detailed-rows coordinator. Drives the thumbnail-loading eligibility check
    /// (<see cref="CanLoadThumbnail"/>) and the shared action strip's Enabled
    /// label (<see cref="EnabledLabel"/>). The row itself performs no density
    /// work; the coordinator owns all loading/clearing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadThumbnail))]
    [NotifyPropertyChangedFor(nameof(EnabledLabel))]
    private bool _isDetailed;

    /// <summary>
    /// The action strip's Enabled checkbox label: the localized "Enabled"
    /// string in Detailed mode (where the label is part of the checkbox's
    /// click hit target + automation name), or <c>null</c> in Compact (the
    /// contentless checkbox the single-line row has always shown). Density is
    /// row state, so one shared checkbox definition serves both roots; the
    /// label re-resolves on a culture change (via <see cref="Refresh"/>).
    /// </summary>
    public string? EnabledLabel => IsDetailed ? _localization["ModRow_Enabled"] : null;

    /// <summary>
    /// The ComboBox selection for the policy editor (0 = Latest, 1 = Pinned),
    /// two-way bound. Initialized from <see cref="Policy"/> on construction; a user
    /// change routes through the view to the parent's <c>SetModPolicy</c> command.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    private int _policyChoice;

    /// <summary>
    /// The versions the row's pin dropdown can choose between, joined from the
    /// container by the parent on reload. Each entry pairs the readable
    /// <see cref="VersionOption.VersionString"/> (shown in the dropdown) with the
    /// opaque <see cref="VersionOption.VersionId"/> (the value written through
    /// as <c>PinnedPolicy(versionId)</c>). Empty when the container is missing or
    /// has no versions; the dropdown then has nothing to offer (a no-version
    /// container cannot be pinned).
    /// </summary>
    public IReadOnlyList<VersionOption> AvailableVersions { get; }

    /// <summary>
    /// The dropdown's current selection (two-way bound). Pre-selected on
    /// construction: when the policy is Pinned, the entry matching the pin's
    /// versionId; when the policy is Latest, the resolved (<c>IsLatest</c>)
    /// version so a switch to Pinned offers the actual version rather than a
    /// blank. <c>null</c> when <see cref="AvailableVersions"/> is empty. A user
    /// selection routes through the view to the parent's <c>SetPolicyPinned</c>
    /// command with the selected versionId.
    /// </summary>
    [ObservableProperty]
    private VersionOption? _selectedVersion;

    /// <summary>
    /// Whether the row's policy is Pinned (derived from <see cref="PolicyChoice"/>),
    /// driving the inline version dropdown's visibility.
    /// </summary>
    public bool IsPinned => PolicyChoice == PolicyPinned;

    /// <summary>
    /// Whether the stable update-action button should show for this row: the row
    /// is Nexus-sourced AND on the <see cref="LatestPolicy"/>. Pinned Nexus and
    /// Untracked rows do not show the button (their reserved update-action cell
    /// stays fixed-width but empty). The button stays visible while a row is
    /// updating (it is disabled via <see cref="UpdateActionEnabled"/>, which
    /// includes <c>!IsUpdating</c>); the indeterminate progress affordance shows
    /// in the source-badge area, not in the action cell.
    /// </summary>
    public bool CanShowUpdateAction => IsNexusLatest;

    /// <summary>
    /// Whether the stable update-action button is enabled. A Premium user's
    /// button is enabled only when an update is available and no other install is
    /// running globally (one install at a time). A regular/unknown user's button
    /// is enabled whenever an update is available (the click opens the Nexus
    /// files page, which needs no install coordination). No update -> disabled.
    /// </summary>
    public bool UpdateActionEnabled =>
        UpdateAvailable && !IsUpdating && (!IsPremiumUser || !AnyRowUpdating);

    /// <summary>
    /// The localized tooltip for the stable update-action button, distinguished by
    /// the row's state so the affordance is discoverable without clicking:
    /// Premium + update available -> "install directly" (works inside Gaming
    /// Mode too, so the gaming flag does not change it); regular/unknown +
    /// update available -> "open the Nexus files page", or the Desktop Mode
    /// guidance while inside a Gaming Mode session (the click shows the same
    /// guidance instead of opening the browser); no update -> "up to date".
    /// Unsupported rows (Pinned / Untracked) never show the button, so no tooltip
    /// applies there.
    /// </summary>
    public string UpdateActionTooltip
    {
        get
        {
            if (!UpdateAvailable)
            {
                return _localization["ModRow_UpdateTooltipNoUpdate"];
            }

            if (IsPremiumUser)
            {
                return _localization["ModRow_UpdateTooltipInstall"];
            }

            return IsGamingMode
                ? _localization["GamingMode_BrowserGuidance"]
                : _localization["ModRow_UpdateTooltipOpenFiles"];
        }
    }

    /// <summary>
    /// The source badge text (localized): "Local" / "Nexus #{id}" (with the
    /// resolved version appended for a Nexus + Latest row that has one, e.g.
    /// "Nexus #{id} · {version}") / "External" / "Folder unavailable", or a
    /// "not found" marker when <see cref="Found"/> is <c>false</c>. Pinned rows
    /// keep their version in the pin dropdown, so the badge stays plain
    /// "Nexus #{id}". Linked rows resolve to "External" when the external
    /// folder is available and "Folder unavailable" when it is missing; the
    /// XAML swaps the clickable pill for non-clickable warning text on the same
    /// flag (see <see cref="IsLinkedAvailable"/> / <see cref="IsLinkedBroken"/>).
    /// </summary>
    public string SourceBadgeText
    {
        get
        {
            if (!Found)
            {
                return _localization["ModRow_NotFound"];
            }

            return Source switch
            {
                NexusSource n when Policy is LatestPolicy && !string.IsNullOrEmpty(ActualVersion)
                    => _localization.Format("ModRow_SourceNexusWithVersion", n.ModId, ActualVersion),
                NexusSource n => _localization.Format("ModRow_SourceNexus", n.ModId),
                LinkedSource => IsExternalBroken
                    ? _localization["ModRow_LinkedFolderBroken"]
                    : _localization["ModRow_SourceLinked"],
                _ => _localization["ModRow_SourceUntracked"],
            };
        }
    }

    /// <summary>
    /// The policy display text (localized): "Latest", or "Pinned {version}" with
    /// the resolved version's readable tag (falls back to the bare "Pinned" label
    /// when the version is empty, e.g. an orphan pin that no longer resolves).
    /// The version shown is the current effective resolution
    /// (<see cref="ActualVersion"/>, joined from the repository for the row's
    /// policy), not the in-flight <see cref="SelectedVersion"/> edit.
    /// </summary>
    public string PolicyDisplayText
    {
        get
        {
            if (Policy is PinnedPolicy)
            {
                return string.IsNullOrEmpty(ActualVersion)
                    ? _localization["ModRow_PolicyPinned"]
                    : _localization.Format("ModRow_PolicyPinnedDisplay", ActualVersion);
            }

            return _localization["ModRow_PolicyLatest"];
        }
    }

    // ---- display metadata derived members (detailed-row support) ----------

    /// <summary>
    /// The trimmed summary text for display, or a localized generic fallback
    /// ("Details unavailable") when the metadata is absent or the summary is
    /// empty. Re-resolves on a culture change (the fallback is localized).
    /// </summary>
    public string SummaryText
    {
        get
        {
            var summary = DisplayMetadata?.Summary?.Trim();
            return string.IsNullOrEmpty(summary)
                ? _localization["ModRow_DetailsUnavailable"]
                : summary;
        }
    }

    /// <summary>
    /// The full (untrimmed) summary for the tooltip / accessibility name, or
    /// <c>null</c> when there is no summary (the fallback text is already shown
    /// in <see cref="SummaryText"/>; the tooltip does not repeat it).
    /// </summary>
    public string? SummaryTooltip =>
        string.IsNullOrWhiteSpace(DisplayMetadata?.Summary)
            ? null
            : DisplayMetadata!.Summary;

    /// <summary>
    /// Whether the metadata flags this mod as adult content. The coordinator uses
    /// this to skip thumbnail loading; the row shows the ordinary placeholder.
    /// </summary>
    public bool IsAdultContent => DisplayMetadata?.IsAdultContent ?? false;

    /// <summary>
    /// The thumbnail URL the coordinator reads to decide whether to load an
    /// image. <c>null</c> when there is no metadata or no URL. The coordinator
    /// matches this against the URL it requested before assigning a result
    /// (stale-result protection).
    /// </summary>
    public string? ThumbnailUrl => DisplayMetadata?.ThumbnailUrl;

    /// <summary>
    /// Whether a decoded thumbnail is currently bound to this row.
    /// </summary>
    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>
    /// Whether the coordinator should load a thumbnail for this row: Detailed
    /// mode + Nexus source + non-null metadata + not adult + non-empty
    /// ThumbnailUrl. The coordinator checks this before calling
    /// <c>IModThumbnailService</c>.
    /// </summary>
    public bool CanLoadThumbnail =>
        IsDetailed &&
        Source is NexusSource &&
        DisplayMetadata is not null &&
        !DisplayMetadata.IsAdultContent &&
        !string.IsNullOrEmpty(DisplayMetadata.ThumbnailUrl);

    /// <summary>
    /// Applies newly backfilled (or initially joined) display metadata to the
    /// row. Clears any existing thumbnail when the new metadata is adult, has no
    /// thumbnail URL, or carries a different URL than the one the old thumbnail
    /// was loaded from. Performs no I/O and calls no service.
    /// </summary>
    public void ApplyDisplayMetadata(ModDisplayMetadata? metadata)
    {
        var oldUrl = DisplayMetadata?.ThumbnailUrl;
        DisplayMetadata = metadata;
        var newUrl = metadata?.ThumbnailUrl;

        if (metadata?.IsAdultContent is true || string.IsNullOrEmpty(newUrl) || newUrl != oldUrl)
        {
            Thumbnail = null;
        }
    }

    /// <summary>
    /// Whether the row's source is a <see cref="LinkedSource"/> (an external
    /// folder added without copying). Constant for a row's lifetime (the source
    /// is joined at construction and never changes). Drives the badge two-state
    /// and the policy-editor gating.
    /// </summary>
    public bool IsLinked => Source is LinkedSource;

    /// <summary>
    /// Whether the row is a linked mod whose external folder is present. The
    /// clickable "External" badge shows in this state (click opens the folder in
    /// the OS file manager via the parent's open-folder command).
    /// </summary>
    public bool IsLinkedAvailable => IsLinked && !IsExternalBroken;

    /// <summary>
    /// Whether the row is a linked mod whose external folder is missing. The
    /// non-clickable "Folder unavailable" warning text replaces the badge in this
    /// state.
    /// </summary>
    public bool IsLinkedBroken => IsLinked && IsExternalBroken;

    /// <summary>
    /// Whether the standard source-badge hyperlink (the Nexus / Untracked badge
    /// with <c>NavigateUri</c>) should show. Suppressed for linked rows, which
    /// use the dedicated linked-available or linked-broken badge element instead.
    /// </summary>
    public bool IsBadgeHyperlink => !IsLinked;

    /// <summary>
    /// Whether the policy ComboBox is editable for this row. Linked rows hold a
    /// single implicit version (the external folder) with no version management,
    /// so the policy editor is disabled for them (the Latest label shows, inert).
    /// A download morph also disables it: the morphing download is about to
    /// write the policy itself (head file Latest, non-head pinned), so a manual
    /// edit mid-download would race the completion. All other rows edit freely.
    /// </summary>
    public bool IsPolicyEditable => !IsLinked && !IsDownloadMorphed;

    /// <summary>
    /// The external folder path for a linked row (the <c>LinkedSource.ExternalPath</c>),
    /// or <c>null</c> for non-linked rows. The parent's open-folder command reads
    /// this to launch the OS file manager at the folder.
    /// </summary>
    public string? ExternalFolderPath => (Source as LinkedSource)?.ExternalPath;

    /// <summary>
    /// The Nexus mod id when the row's source is <see cref="NexusSource"/>, else
    /// <c>null</c>. The parent's update command reads this to call
    /// <c>IModAcquisitionService.AcquireLatestNexusAsync</c> (which takes the mod
    /// id, not the file id). Null for Untracked / not-found rows.
    /// </summary>
    public int? NexusModId => Source is NexusSource n ? n.ModId : null;

    /// <summary>
    /// Whether the row is both Nexus-sourced AND on the <see cref="LatestPolicy"/>
    /// (the conjunction the update check requires). The stable update-action
    /// button's visibility binds to <see cref="CanShowUpdateAction"/> (which adds
    /// <c>!IsUpdating</c>); Pinned / Untracked rows are always <c>false</c>, so
    /// the button never shows for them (their reserved cell stays fixed-width but
    /// empty).
    /// </summary>
    public bool IsNexusLatest => Source is NexusSource && Policy is LatestPolicy;

    /// <summary>
    /// The mod's remote page URL for the source-badge link (the badge is a
    /// hyperlink). Nexus -> the mod page; Untracked / not-found -> <c>null</c>
    /// (the link is a no-op + the badge reads as plain metadata). The URL is not
    /// localized, so it does not re-resolve on a culture change;
    /// <see cref="Refresh"/> re-fires it only for binding consistency if the
    /// source ever changes (it does not today, but the hook keeps the contract
    /// uniform with the other derived members).
    /// </summary>
    public string? SourceUrl => Source switch
    {
        NexusSource n => $"https://www.nexusmods.com/{NexusGameIdentity.DarktideDomain}/mods/{n.ModId}",
        _ => null,
    };

    /// <summary>
    /// The mod's Nexus <c>files</c> tab URL. The regular/unknown update action
    /// opens this in the user's browser (the per-file download page where a
    /// non-Premium user can mint the nxm token). Nexus -> the mod page with
    /// <c>?tab=files</c>; Untracked / not-found -> <c>null</c> (those rows never
    /// show the update action anyway). Reuses <see cref="SourceUrl"/> for the
    /// base, so any future change to the page URL shape lands in one place.
    /// </summary>
    public string? UpdatePageUrl => Source is NexusSource
        ? SourceUrl + "?tab=files"
        : null;

    /// <summary>
    /// Creates a row. The parent (<see cref="ModListViewModel"/>) builds rows on
    /// reload, joining source + version + the version list from the repository.
    /// </summary>
    /// <param name="localization">The localization service, used for the derived
    /// badge + policy text (re-resolves on a culture change).</param>
    /// <param name="rowContext">The shared row context (premium / install-busy /
    /// gaming) the row's global halves read; passed once per row.</param>
    /// <param name="containerId">The mod container's id (immutable join key).</param>
    /// <param name="name">The container's display name.</param>
    /// <param name="source">The joined source provenance.</param>
    /// <param name="actualVersion">The joined resolved version tag (readable),
    /// for the policy display text.</param>
    /// <param name="enabled">Whether the mod is active.</param>
    /// <param name="order">The load-order position.</param>
    /// <param name="policy">The current effective version policy.</param>
    /// <param name="versions">The container's versions (joined from the
    /// repository); drives the pin dropdown. Empty when the container is missing
    /// or version-less.</param>
    /// <param name="found">Whether the repository had a container for this entry.</param>
    /// <param name="orderLocked">Whether this entry's position is locked against
    /// reordering (joined from <see cref="ModListEntry.OrderLocked"/>).</param>
    /// <param name="displayMetadata">Optional display metadata (summary,
    /// thumbnail URL, adult flag) joined from the container. <c>null</c> when
    /// no metadata has been fetched. Defaults to <c>null</c> for existing call
    /// sites that have not yet been updated.</param>
    public ModItemViewModel(
        LocalizationService localization,
        ModRowContext rowContext,
        Guid containerId,
        string name,
        ModSource source,
        string actualVersion,
        bool enabled,
        int order,
        ModVersionPolicy policy,
        IReadOnlyList<ModVersion> versions,
        bool found,
        bool orderLocked = false,
        ModDisplayMetadata? displayMetadata = null)
    {
        _localization = localization;
        RowContext = rowContext ?? throw new ArgumentNullException(nameof(rowContext));
        ContainerId = containerId;
        _name = name;
        Source = source;
        ActualVersion = actualVersion;
        _enabled = enabled;
        Order = order;
        Policy = policy;
        Found = found;
        _orderLocked = orderLocked;
        _displayMetadata = displayMetadata;

        // Build the dropdown source from the container's versions: each entry
        // pairs the readable tag (shown) with the opaque folder id (stored).
        AvailableVersions = versions
            .Select(v => new VersionOption(v.VersionString, v.Folder))
            .ToArray();

        // Seed the policy editor from the effective policy. Pinned selects the
        // Pinned choice + the dropdown entry matching the pin's versionId; Latest
        // selects Latest + pre-selects the resolved (IsLatest) version so a switch
        // to Pinned offers the actual version rather than a blank.
        if (policy is PinnedPolicy pinned)
        {
            _policyChoice = PolicyPinned;
            _selectedVersion = AvailableVersions.FirstOrDefault(o => o.VersionId == pinned.VersionId);
        }
        else
        {
            _policyChoice = PolicyLatest;
            var resolved = versions.FirstOrDefault(v => v.IsLatest);
            _selectedVersion = resolved is null
                ? AvailableVersions.FirstOrDefault()
                : AvailableVersions.FirstOrDefault(o => o.VersionId == resolved.Folder);
        }
    }

    /// <summary>
    /// Re-fires the property-changed events for the localized derived strings so
    /// their bindings re-resolve after a UI culture switch. Called by the parent
    /// when the LocalizationService raises its culture-changed event. The
    /// non-localized derived members (<see cref="SourceUrl"/>,
    /// <see cref="UpdatePageUrl"/>, <see cref="NexusModId"/>,
    /// <see cref="IsNexusLatest"/>) do not change with the culture, but
    /// re-firing <see cref="SourceUrl"/> + <see cref="UpdatePageUrl"/> keeps the
    /// refresh contract uniform across derived members.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(PolicyDisplayText));
        OnPropertyChanged(nameof(SourceUrl));
        OnPropertyChanged(nameof(UpdatePageUrl));
        OnPropertyChanged(nameof(UpdateActionTooltip));
        // The summary fallback is localized; re-fire so the binding re-resolves
        // after a culture switch.
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryTooltip));
        // The lock tooltip + automation name are localized by state; re-fire.
        OnPropertyChanged(nameof(OrderLockTooltip));
        OnPropertyChanged(nameof(OrderLockAutomationName));
        // The Gaming Mode badge tooltip is localized by state; re-fire.
        OnPropertyChanged(nameof(LinkedBadgeTooltip));
        // The action strip's Enabled label is localized + density-dependent.
        OnPropertyChanged(nameof(EnabledLabel));
    }
}

/// <summary>
/// One entry in a row's pin dropdown: pairs the readable version tag (shown in
/// the dropdown) with the opaque version id (the <see cref="ModVersion.Folder"/>
/// value written through as <c>PinnedPolicy(versionId)</c>). A value-equal
/// record so Avalonia's ComboBox selection matches by (tag, id), not by
/// reference.
/// </summary>
/// <param name="VersionString">The readable release tag (e.g. <c>"1.2"</c>),
/// shown in the dropdown. Display only.</param>
/// <param name="VersionId">The version's opaque folder id (a
/// <see cref="ModVersion.Folder"/>); the value stored on selection + wrapped as
/// <c>PinnedPolicy(versionId)</c>.</param>
public sealed record VersionOption(string VersionString, string VersionId);
