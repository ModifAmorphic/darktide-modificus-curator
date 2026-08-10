using CommunityToolkit.Mvvm.ComponentModel;
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
    /// Position within the load order (lower loads first). Drives the display sort
    /// and the up/down move commands (the parent re-persists the order).
    /// </summary>
    public int Order { get; }

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
    private bool _isUpdating;

    /// <summary>
    /// Whether the Nexus account was verified Premium. Pushed down by the parent
    /// (read once at construction). Drives the update action's click behavior
    /// (Premium -> in-app install; regular/unknown -> open the Nexus files page)
    /// and the tooltip. The button itself shows for Nexus + Latest rows
    /// regardless of premium; only the click behavior + tooltip differ.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateActionTooltip))]
    private bool _isPremiumUser;

    /// <summary>
    /// Whether any row (or the automatic updater) is currently running an
    /// install. Pushed down by the parent so the per-row enabled state can reflect
    /// the global "one install at a time" coordination without a parent walk in
    /// the binding. Premium clicks are disabled while this is true (the
    /// coordinator would reject a second concurrent install); regular/unknown
    /// clicks (which open a files page, no install) stay enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionEnabled))]
    private bool _anyRowUpdating;

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
    /// Premium + update available -> "install directly"; regular/unknown + update
    /// available -> "open the Nexus files page"; no update -> "up to date".
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

            return IsPremiumUser
                ? _localization["ModRow_UpdateTooltipInstall"]
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
    /// All other rows edit freely.
    /// </summary>
    public bool IsPolicyEditable => !IsLinked;

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
        NexusSource n => $"https://www.nexusmods.com/warhammer40kdarktide/mods/{n.ModId}",
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
    public ModItemViewModel(
        LocalizationService localization,
        Guid containerId,
        string name,
        ModSource source,
        string actualVersion,
        bool enabled,
        int order,
        ModVersionPolicy policy,
        IReadOnlyList<ModVersion> versions,
        bool found)
    {
        _localization = localization;
        ContainerId = containerId;
        _name = name;
        Source = source;
        ActualVersion = actualVersion;
        _enabled = enabled;
        Order = order;
        Policy = policy;
        Found = found;

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
