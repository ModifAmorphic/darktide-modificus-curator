using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.Settings;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the discovery escape-hatch modal
/// (<see cref="Views.DiscoveryEscapeHatchDialog"/>). Shown when a launch returns
/// <c>LaunchStatus.DiscoveryIncomplete</c>: a focused form that prompts for
/// <em>only</em> the missing fields (the ones <c>LaunchResult</c> listed), with
/// the same shared <see cref="DiscoveryField"/> descriptor the Settings
/// destination uses. There is <b>no auto-retry</b>: the user clicks Launch again
/// to retry (avoids a loop if the entered paths still do not work). Cancel
/// aborts.
/// </summary>
/// <remarks>
/// <para><b>Global mode + forced discover:</b> alongside the missing-field rows
/// the dialog carries the same global <see cref="OverrideAutomaticDiscovery"/>
/// checkbox + Discover button as Settings, with identical semantics. Both are
/// write-through: the checkbox persists the mode (turning it off runs an
/// ordinary <see cref="ISteamService.Discover"/> and refreshes the rows; turning
/// it on preserves values and enables editing), and the Discover button forces a
/// <see cref="ISteamService.Rediscover"/> and refreshes the rows. Cancel does
/// NOT roll these already-applied global actions back; it only abandons any
/// staged row edits that have not been submitted.</para>
/// <para><b>Row editability follows the mode:</b> automatic mode keeps the
/// missing-field rows read-only (Browse disabled) and shows the current invalid
/// strings from config; manual mode makes them editable (Browse enabled).</para>
/// <para><b>Submit depends on the mode, never infers it:</b> in manual mode it
/// writes each staged row value + <c>OverrideAutomaticDiscovery = true</c> in
/// one read-modify-save; in automatic mode it does not rewrite path values (the
/// toggle's own write-through already persisted the mode). Both then close.</para>
/// <para><b>Pre-fill:</b> each row is pre-filled with the current stored value
/// from config so a previously-set path that turned out wrong is shown for
/// correction rather than retyping.</para>
/// <para><b>Unknown fields are dropped:</b> if <c>LaunchResult</c> ever lists a
/// field name the catalog does not know (a future field), it is silently
/// omitted; the dialog always renders the fields it knows how to label +
/// browse.</para>
/// </remarks>
public partial class DiscoveryEscapeHatchViewModel : LocalizedViewModel
{
    private readonly IConfigLoader _configLoader;
    private readonly ISteamService _steam;
    private readonly IGamingModeState _gamingMode;

    /// <summary>True while rehydrating the rows so the toggle handler + row
    /// callbacks do not save. The values already match what is persisted.</summary>
    private bool _suppressApply;

    /// <param name="missingFields">The discovery field names the launch result
    /// reported missing (the values of <c>LaunchResult.MissingDiscoveryFields</c>,
    /// which match the <see cref="DiscoveryResult"/> property names).
    /// Empty yields no rows (the dialog should not be shown then anyway).</param>
    /// <param name="configLoader">The live config reader/writer. Submit + the
    /// toggle/Discover actions do read-modify-saves through this.</param>
    /// <param name="steamService">The Steam discovery service. Turning override
    /// off calls <see cref="ISteamService.Discover"/>; the Discover button calls
    /// <see cref="ISteamService.Rediscover"/>. The mode policy lives in the
    /// service; this VM only orchestrates user actions + display state.</param>
    /// <param name="localization">The localization service; handed to each row so
    /// its label resolves + refreshes on a culture change.</param>
    /// <param name="gamingMode">Whether the app runs inside a Steam Deck Gaming
    /// Mode session. Gates each row's Browse button (pickers are unusable
    /// there); the TextBoxes + Submit stay fully available so manual path entry
    /// keeps working.</param>
    public DiscoveryEscapeHatchViewModel(
        IReadOnlyList<string> missingFields,
        IConfigLoader configLoader,
        ISteamService steamService,
        LocalizationService localization,
        IGamingModeState gamingMode)
        : base(localization)
    {
        _configLoader = configLoader;
        _steam = steamService ?? throw new ArgumentNullException(nameof(steamService));
        _gamingMode = gamingMode ?? throw new ArgumentNullException(nameof(gamingMode));

        var discovery = _configLoader.Load().Discovery;

        // Resolve each missing field name to its catalog descriptor (drop
        // unknowns), then order by the catalog's canonical order (DiscoveryFields.All)
        // so the rows are top-to-bottom Steam, Darktide, compatdata, Proton
        // regardless of the order LaunchResult happened to list them in.
        Rows = new ObservableCollection<DiscoveryFieldRowViewModel>(
            missingFields
                .Select(DiscoveryFields.Find)
                .Where(f => f is not null)
                .Cast<DiscoveryField>()
                .OrderBy(f => CatalogIndex(f))
                .Select(field => new DiscoveryFieldRowViewModel(
                    field,
                    InitialValue(field, discovery),
                    _localization)));

        _suppressApply = true;
        try
        {
            OverrideAutomaticDiscovery = discovery.OverrideAutomaticDiscovery;
            foreach (var row in Rows)
            {
                row.IsEditable = discovery.OverrideAutomaticDiscovery;
                row.IsGamingMode = IsGamingMode;
            }
        }
        finally
        {
            _suppressApply = false;
        }

    }

    /// <summary>
    /// The rows for the missing fields only (in catalog order, which is the
    /// order <see cref="DiscoveryFields.All"/> lists). Bound to an
    /// <c>ItemsControl</c>; each row's Browse button is wired by the view.
    /// </summary>
    public ObservableCollection<DiscoveryFieldRowViewModel> Rows { get; }

    /// <summary>
    /// Whether the app runs inside a Steam Deck Gaming Mode session. Gates each
    /// row's Browse button (via each row's pushed-down flag); manual entry +
    /// Submit are deliberately unaffected.
    /// </summary>
    public bool IsGamingMode => _gamingMode.IsGamingMode;

    /// <summary>
    /// The localized header (the friendly "couldn't discover everything"
    /// message). Re-resolves on a culture change.</summary>
    public string Header => _localization["EscapeHatch_Header"];

    /// <summary>
    /// The Gaming Mode guidance shown inline under the header while gaming
    /// (pickers disabled; manual entry still works), or <c>null</c> in normal
    /// mode. Re-resolves on a culture change.
    /// </summary>
    public string? PickerGatingHint => IsGamingMode
        ? _localization["GamingMode_PickerGuidance"]
        : null;

    /// <summary>The localized "click Launch to retry" hint. Re-resolves on a
    /// culture change.</summary>
    public string RetryHint => _localization["EscapeHatch_RetryHint"];

    /// <summary>
    /// The global discovery mode, identical to Settings. <c>false</c> = automatic
    /// (rows read-only, Discover retries full automatic discovery);
    /// <c>true</c> = manual (rows editable, Browse enabled). Write-through (see
    /// <see cref="OnOverrideAutomaticDiscoveryChanged"/>). Cancel does not roll a
    /// mode toggle back; it is already persisted.
    /// </summary>
    [ObservableProperty]
    private bool _overrideAutomaticDiscovery;

    /// <summary>
    /// Persisted on every user toggle (write-through). Turning override off runs
    /// <see cref="ISteamService.Discover"/> and refreshes the rows from the new
    /// snapshot; turning it on preserves values and enables editing. Suppressed
    /// during construction + row refresh.
    /// </summary>
    partial void OnOverrideAutomaticDiscoveryChanged(bool value)
    {
        if (_suppressApply)
        {
            return;
        }

        var config = _configLoader.Load();
        config.Discovery.OverrideAutomaticDiscovery = value;
        _configLoader.Save(config);

        if (!value)
        {
            _steam.Discover();
        }

        RefreshRowsFromConfig();
    }

    /// <summary>
    /// Forces one automatic discovery pass regardless of the current mode (calls
    /// <see cref="ISteamService.Rediscover"/>), preserves the mode, and refreshes
    /// the displayed rows from the resulting snapshot. Works in either mode.
    /// Synchronous; no spinner or async plumbing. Cancel does not roll a Discover
    /// back; it is already persisted.
    /// </summary>
    [RelayCommand]
    private void Discover()
    {
        _steam.Rediscover();
        RefreshRowsFromConfig();
    }

    /// <summary>
    /// Re-reads the discovery snapshot from config + pushes the current values +
    /// editability onto each existing row. Runs under <see cref="_suppressApply"/>
    /// so the row Value setters do not fire a spurious save.
    /// </summary>
    private void RefreshRowsFromConfig()
    {
        var discovery = _configLoader.Load().Discovery;

        _suppressApply = true;
        try
        {
            foreach (var row in Rows)
            {
                row.Value = InitialValue(row.Field, discovery);
                row.IsEditable = discovery.OverrideAutomaticDiscovery;
                row.IsGamingMode = IsGamingMode;
            }
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>
    /// The outcome of the dialog: <c>true</c> when the user submitted, <c>false</c>
    /// when they cancelled. Read by the dialog service after <c>ShowDialog</c>
    /// returns. The toggle + Discover actions are write-through and persist
    /// regardless of this value; only staged row edits are gated on Submit.
    /// </summary>
    public bool Result { get; private set; }

    /// <summary>
    /// Detaches the VM's culture subscription + each row's, so the short-lived
    /// dialog VM is collectable after its window closes. Called by the dialog on
    /// close.
    /// </summary>
    public void Detach()
    {
        DetachLocalization();
        foreach (var row in Rows)
        {
            row.Detach();
        }
    }

    /// <summary>
    /// The dialog's localized property names, re-fired by the shared
    /// culture-refresh base on a culture change. The per-row labels refresh
    /// themselves.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(Header),
        nameof(RetryHint),
        nameof(PickerGatingHint),
    };

    /// <summary>
    /// Submit: in manual mode, one read-modify-save writing every row's staged
    /// value into the matching <see cref="DiscoveryConfig"/> property plus
    /// <c>OverrideAutomaticDiscovery = true</c>, then marks <see cref="Result"/>
    /// true. In automatic mode it does NOT rewrite path values (the toggle's
    /// write-through already persisted the mode); it just marks Result true.
    /// Submit never infers or flips the mode from the staged values. The dialog
    /// closes on a true result. No auto-retry: the user clicks Launch again.
    /// </summary>
    [RelayCommand]
    private void Submit()
    {
        if (OverrideAutomaticDiscovery)
        {
            var config = _configLoader.Load();
            foreach (var row in Rows)
            {
                var value = row.Value;
                var written = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                SetPath(config.Discovery, row.Field.FieldName, written);
            }
            config.Discovery.OverrideAutomaticDiscovery = true;
            _configLoader.Save(config);
        }

        Result = true;
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// Cancel: marks <see cref="Result"/> false so the dialog closes. No staged
    /// row edits are persisted. The mode toggle + Discover are write-through
    /// actions and stay applied (they were already persisted when the user
    /// pressed them); Cancel does not create a transaction across those service
    /// calls.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// The catalog position of a field (the index in
    /// <see cref="DiscoveryFields.All"/>). Used to order the rows in the
    /// canonical top-to-bottom render order regardless of the input order. A
    /// field not in the catalog (defensive: never happens after the
    /// <see cref="DiscoveryFields.Find"/> filter) sorts last.
    /// </summary>
    private static int CatalogIndex(DiscoveryField field)
    {
        for (var i = 0; i < DiscoveryFields.All.Count; i++)
        {
            if (ReferenceEquals(DiscoveryFields.All[i], field)
                || string.Equals(DiscoveryFields.All[i].FieldName, field.FieldName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Maps a discovery field's canonical name to its setter on
    /// <see cref="DiscoveryConfig"/>. Mirrors the Settings VM's helper; kept
    /// duplicated to keep the two VMs decoupled (the field-name catalog is
    /// already the shared source of truth; an abstraction coupling the
    /// escape-hatch to Settings would be a worse trade than this small switch).
    /// </summary>
    private static void SetPath(DiscoveryConfig discovery, string fieldName, string? value)
    {
        switch (fieldName)
        {
            case "SteamInstallPath":
                discovery.SteamInstallPath = value;
                return;
            case "DarktideGameBinaryPath":
                discovery.DarktideGameBinaryPath = value;
                return;
            case "CompatdataPath":
                discovery.CompatdataPath = value;
                return;
            case "ProtonBinaryPath":
                discovery.ProtonBinaryPath = value;
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Reads the current stored value for a field from config (or empty when it
    /// is null). Mirrors the Settings VM's helper.
    /// </summary>
    private static string InitialValue(DiscoveryField field, DiscoveryConfig discovery) =>
        field.FieldName switch
        {
            "SteamInstallPath" => discovery.SteamInstallPath ?? string.Empty,
            "DarktideGameBinaryPath" => discovery.DarktideGameBinaryPath ?? string.Empty,
            "CompatdataPath" => discovery.CompatdataPath ?? string.Empty,
            "ProtonBinaryPath" => discovery.ProtonBinaryPath ?? string.Empty,
            _ => string.Empty,
        };
}
