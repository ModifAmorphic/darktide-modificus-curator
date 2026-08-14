using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Settings destination content (hosted by
/// <see cref="Views.SettingsView"/>). Three sections:
/// <list type="bullet">
/// <item><description><b>Discovery:</b> the persisted Steam/Darktide ( +
/// compatdata + Proton on Linux) paths plus the global
/// <see cref="OverrideAutomaticDiscovery"/> mode. Platform-gated so Windows
/// renders only the Steam install + Darktide binary rows (the compatdata +
/// Proton rows are Linux-only). In automatic mode (off, the default) the rows
/// are read-only and show the latest discovered snapshot; in manual mode (on)
/// the rows are editable and Browse is enabled. Flipping the toggle is
/// write-through: turning it on persists <c>true</c> and enables editing; turning
/// it off persists <c>false</c>, runs <see cref="ISteamService.Discover"/>
/// (automatic), and refreshes every row. The <see cref="DiscoverCommand"/>
/// forces a <see cref="ISteamService.Rediscover"/> in either mode. A manual-mode
/// row edit writes through immediately via a read-modify-save (Preferences
/// pattern); row writes are ignored while in automatic mode.</description></item>
/// <item><description><b>Storage:</b> two buttons, each opening the OS file
/// manager at a Curator-owned path. <see cref="OpenDataFolderCommand"/>
/// opens the Curator data root (<c>AppPaths.AppDataDir</c>, a static path)
/// + <see cref="OpenProfilesFolderCommand"/> reads <c>ProfilesBaseFolder</c>
/// live from config. Nothing in this section is editable.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Never holds a cached <see cref="CuratorConfig"/>:</b> each field change
/// calls <see cref="IConfigLoader.Load"/> + <see cref="IConfigLoader.Save"/> (a
/// read-modify-save), so concurrent edits by other surfaces (the escape-hatch)
/// are never clobbered. The config file is tiny; the round-trip is cheap.</para>
/// <para><b>The browse buttons:</b> opening a storage-provider picker is a view
/// concern (it needs the live TopLevel), so the view code-behind opens the
/// picker and sets the row's <c>Value</c> (discovery), which triggers the VM's
/// write-through. The picker honors the row's current value as its
/// <c>SuggestedStartLocation</c> so it opens where the user already is. No file
/// paths cross the VM boundary.</para>
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigLoader _configLoader;
    private readonly ISteamService _steam;
    private readonly LocalizationService _localization;
    private readonly IAppUpdateService _appUpdate;
    private readonly IDialogService _dialogs;
    private readonly Action<Action> _invokeOnUi;
    private readonly Func<string, bool> _launchExternalPath;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// True while <see cref="RefreshFromConfig"/> or
    /// <see cref="RefreshDiscoveryRows"/> rehydrates the bound controls from a
    /// live config snapshot, so the row change callbacks + the toggle change
    /// handler do not save. The values already match what is persisted, so
    /// re-writing would be a noisy no-op.
    /// </summary>
    private bool _suppressApply;

    /// <summary>
    /// True after the user has run at least one manual check from this Settings
    /// session (or a background check landed while Settings was open). Before
    /// that, <see cref="AppUpdateStatusMessage"/> is null (no status shown); once
    /// a check has run, a null <see cref="IAppUpdateService.LastCheckResult"/>
    /// resolves to the "up to date" message rather than blank.
    /// </summary>
    /// <remarks>Volatile: <see cref="OnAppUpdateStateChanged"/> fires on a
    /// threadpool thread (the service publishes from its background check) and
    /// sets this before marshaling to the UI thread, while
    /// <see cref="RefreshAppUpdateStatus"/> (and the manual-check paths) read it
    /// on the UI thread. <c>volatile</c> guarantees the UI-thread read observes
    /// the background write without a stale-cache reordering. The UI-thread
    /// writes (the manual <see cref="CheckAppUpdate"/> paths) are
    /// single-threaded by construction.</remarks>
    private volatile bool _hasCheckedAppUpdate;

    /// <summary>
    /// Creates the Settings VM, pre-fills the discovery rows (platform-gated:
    /// Steam install + Darktide binary on Windows; all four on Linux), and
    /// wires each discovery row's change callback to the write-through path.
    /// </summary>
    /// <param name="configLoader">The live config reader/writer. Each field change
    /// does a read-modify-save through this.</param>
    /// <param name="steam">The Steam discovery service. Turning override off calls
    /// <see cref="ISteamService.Discover"/> (automatic); the
    /// <see cref="DiscoverCommand"/> calls <see cref="ISteamService.Rediscover"/>.
    /// The mode policy itself lives in the service; this VM only orchestrates
    /// user actions + display state.</param>
    /// <param name="localization">The localization service; handed to each
    /// discovery row so its label resolves + refreshes on a culture change.</param>
    /// <param name="appUpdate">The app self-update service; backs the Updates
    /// section (current version, manual check, download + restart).</param>
    /// <param name="dialogs">The dialog service; the download + restart flow runs
    /// the download under its modal spinner and surfaces failures as an alert.
    /// Also used by the open-folder actions' failure alert.</param>
    /// <param name="invokeOnUi">Marshals the off-thread
    /// <see cref="IAppUpdateService.UpdateStateChanged"/> handler's refresh onto
    /// the UI thread. Production wires <c>Dispatcher.UIThread.Post</c>; tests
    /// inject a synchronous <c>action =&gt; action()</c>.</param>
    /// <param name="logger">Logger for the open-folder flow.</param>
    /// <param name="launchExternalPath">The OS file-manager launcher seam used by
    /// the open-folder actions. Production passes null (falls back to the
    /// static default that shells out via <c>Process.Start</c>); tests inject a
    /// controllable delegate.</param>
    public SettingsViewModel(
        IConfigLoader configLoader,
        ISteamService steam,
        LocalizationService localization,
        IAppUpdateService appUpdate,
        IDialogService dialogs,
        Action<Action> invokeOnUi,
        ILogger<SettingsViewModel> logger,
        Func<string, bool>? launchExternalPath = null)
    {
        _configLoader = configLoader;
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _localization = localization;
        _appUpdate = appUpdate;
        _dialogs = dialogs;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger;
        _launchExternalPath = launchExternalPath ?? LaunchExternalPathDefault;

        // Build the platform-gated discovery rows once (each carries its
        // write-through callback + localization subscription). Initial values
        // + editability are populated by RefreshFromConfig below, so
        // construction + later rehydrations share one restoration path.
        //
        // Platform-gated: on Windows the compatdata + Proton fields are
        // Linux-only, so surfacing them would be silently ineffective rows.
        // Only the Steam install + Darktide binary rows render on Windows. The
        // escape-hatch is already correct (it renders only the names in
        // LaunchResult.MissingDiscoveryFields, which on Windows never includes
        // the Linux-only ones).
        DiscoveryRows = new ObservableCollection<DiscoveryFieldRowViewModel>(
            DiscoveryFields.All
                .Where(field => OperatingSystem.IsLinux() || !IsLinuxOnlyField(field))
                .Select(field => new DiscoveryFieldRowViewModel(
                    field,
                    initialValue: string.Empty,
                    _localization,
                    onValueChanged: WriteThroughDiscovery)));

        // Subscribe for the localized section headers + labels; the row VMs
        // each subscribe on their own.
        _localization.PropertyChanged += OnCultureChanged;

        // Subscribe to the app self-update state so a check that lands while
        // Settings is open refreshes the inline status. RefreshFromConfig then
        // reflects any result the startup check already published so the section
        // shows the current state immediately.
        _appUpdate.UpdateStateChanged += OnAppUpdateStateChanged;

        // Populate the bound values from the live config + the app-update
        // display state. Runs under the write-suppression guard so the row
        // callbacks + toggle handler do not save.
        RefreshFromConfig();
    }

    /// <summary>
    /// Rehydrates the discovery mode + row values/editability + the
    /// startup-update toggle from a live config snapshot, then refreshes the
    /// app-update display/command state. Idempotent and safe to call repeatedly
    /// (the constructor calls it once for the initial fill; the host calls it
    /// when entering Settings so changes made through the discovery escape-hatch
    /// or the launch flow are visible on a later visit). Executes under
    /// <see cref="_suppressApply"/> so the row change callbacks + the toggle
    /// change handler do not save. Does not replace <see cref="DiscoveryRows"/>
    /// or re-subscribe: existing row object instances are preserved (their Value
    /// + IsEditable setters fire the change callbacks, which the suppress guard
    /// no-ops).
    /// </summary>
    public void RefreshFromConfig()
    {
        var config = _configLoader.Load();

        _suppressApply = true;
        try
        {
            OverrideAutomaticDiscovery = config.Discovery.OverrideAutomaticDiscovery;
            foreach (var row in DiscoveryRows)
            {
                row.Value = InitialValue(row.Field, config.Discovery);
                row.IsEditable = config.Discovery.OverrideAutomaticDiscovery;
            }
            CheckOnStartup = config.AppUpdates.CheckOnStartup;
        }
        finally
        {
            _suppressApply = false;
        }

        RefreshAppUpdateStatus();
    }

    /// <summary>
    /// Re-reads the discovery snapshot from config + pushes the current values
    /// + editability onto each existing row. Used after a toggle or forced
    /// discover changes the persisted snapshot, so the rows reflect it without
    /// rebuilding the collection. Runs under <see cref="_suppressApply"/> so the
    /// row Value setters do not save.
    /// </summary>
    private void RefreshDiscoveryRows()
    {
        var discovery = _configLoader.Load().Discovery;

        _suppressApply = true;
        try
        {
            foreach (var row in DiscoveryRows)
            {
                row.Value = InitialValue(row.Field, discovery);
                row.IsEditable = discovery.OverrideAutomaticDiscovery;
            }
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>
    /// The discovery-field rows (Steam install, Darktide binary, compatdata,
    /// Proton binary), platform-gated: Windows renders only the first two (the
    /// compatdata + Proton rows are Linux-only). Bound to an
    /// <c>ItemsControl</c> in the view; each row owns its TextBox value + Browse
    /// button (the browse kind drives which picker opens) + its
    /// <c>IsEditable</c> (which drives read-only + Browse enabled).
    /// </summary>
    public ObservableCollection<DiscoveryFieldRowViewModel> DiscoveryRows { get; }

    /// <summary>
    /// The global discovery mode. <c>false</c> (the default) is automatic:
    /// <see cref="ISteamService.Discover"/> runs the platform discoverer and
    /// owns the persisted snapshot, so the rows stay read-only. <c>true</c> is
    /// manual: the rows are editable and the discoverer is not invoked on
    /// launch. Flipping the toggle is write-through (see
    /// <see cref="OnOverrideAutomaticDiscoveryChanged"/>): turning it off runs a
    /// fresh automatic discovery and refreshes the rows; turning it on merely
    /// enables editing without discovering.
    /// </summary>
    [ObservableProperty]
    private bool _overrideAutomaticDiscovery;

    /// <summary>
    /// Persisted on every user toggle (write-through, read-modify-save of
    /// <c>Discovery.OverrideAutomaticDiscovery</c>). Suppressed during
    /// <see cref="RefreshFromConfig"/> + <see cref="RefreshDiscoveryRows"/> so
    /// rehydrating the field does not trigger a redundant write-back. Turning
    /// the override off additionally runs <see cref="ISteamService.Discover"/>
    /// (automatic) and refreshes the rows from the resulting snapshot; turning
    /// it on preserves the current snapshot and only enables editing (no
    /// discover). The mode policy itself lives in the service; this handler only
    /// persists the bool + orchestrates the refresh.
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
            // Turning override off: run a fresh automatic discovery so the rows
            // immediately reflect the discoverer's snapshot rather than the
            // stale manual strings. The service persists the snapshot.
            _steam.Discover();
        }

        // Re-read the persisted snapshot (unchanged on the on path; just
        // refreshed by an automatic pass on the off path) + push it onto the
        // rows, flipping editability to match the new mode.
        RefreshDiscoveryRows();
    }

    /// <summary>
    /// Forces one automatic discovery pass regardless of the current mode (calls
    /// <see cref="ISteamService.Rediscover"/>), then refreshes every row from
    /// the resulting snapshot. Works in either mode + leaves the mode unchanged.
    /// A partial result clears unresolved fields in the UI to match the
    /// persisted config. Synchronous (the discovery API is synchronous); no
    /// spinner or async plumbing is needed.
    /// </summary>
    [RelayCommand]
    private void Discover()
    {
        _steam.Rediscover();
        RefreshDiscoveryRows();
    }

    /// <summary>
    /// The localized header for the discovery section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string DiscoverySectionHeader => _localization["Settings_DiscoverySection"];

    /// <summary>
    /// The localized header for the storage section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string StorageSectionHeader => _localization["Settings_StorageSection"];

    // ---- Updates section ---------------------------------------------------

    /// <summary>
    /// The localized header for the Updates section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string UpdatesSectionHeader => _localization["Settings_UpdatesSection"];

    /// <summary>
    /// The localized label for the current-version row. Re-resolves on a culture
    /// change.
    /// </summary>
    public string CurrentVersionLabel => _localization["Settings_CurrentVersionLabel"];

    /// <summary>
    /// Whether app self-update is meaningful for this build (a packaged Windows
    /// install). The section always renders; the controls are disabled when this
    /// is false (Linux, a dev run) so those users still see the version.
    /// </summary>
    public bool IsAppUpdateSupported => _appUpdate.IsUpdateSupported;

    /// <summary>
    /// The installed Curator version, or a localized "unknown" when it cannot be
    /// resolved (a non-packaged build). Re-resolves on a culture change.
    /// </summary>
    public string CurrentVersionDisplay =>
        _appUpdate.CurrentVersion ?? _localization["Settings_VersionUnknown"];

    /// <summary>
    /// Whether Curator checks for a new version of itself on startup. Pre-filled
    /// from <c>CuratorConfig.AppUpdates.CheckOnStartup</c> on construction;
    /// persisted on each user change via a read-modify-save. Gates ONLY the
    /// automatic startup check (<c>AppUpdateCheckRunner</c>); the manual "Check
    /// for Updates" button always works regardless.
    /// </summary>
    [ObservableProperty]
    private bool _checkOnStartup;

    /// <summary>
    /// Persisted when the user flips <see cref="CheckOnStartup"/>. Read-modify-
    /// saves <c>CuratorConfig.AppUpdates.CheckOnStartup</c> (no caching, mirrors
    /// <c>IntegrationsViewModel.SaveAutoUpdateSettings</c>). Suppressed during
    /// <see cref="RefreshFromConfig"/> so rehydrating the field does not trigger
    /// a redundant write-back.
    /// </summary>
    partial void OnCheckOnStartupChanged(bool value)
    {
        if (_suppressApply)
        {
            return;
        }

        var config = _configLoader.Load();
        config.AppUpdates.CheckOnStartup = value;
        _configLoader.Save(config);
    }

    /// <summary>
    /// True while a manual update check is in flight (drives the inline spinner +
    /// disables the Check button).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckAppUpdateCommand))]
    private bool _isCheckingAppUpdate;

    /// <summary>
    /// Inline status under the Check button: null before any check; a localized
    /// "up to date" once a check finds nothing; or a formatted "Curator X is
    /// available" once a check finds an update. The visibility of this row in the
    /// view is gated on non-empty (the up-to-date + available messages).
    /// </summary>
    [ObservableProperty]
    private string? _appUpdateStatusMessage;

    /// <summary>
    /// Whether an update is available (a non-null
    /// <see cref="IAppUpdateService.LastCheckResult"/>). Gates the Download and
    /// Restart button's visibility + the download command's CanExecute.
    /// </summary>
    public bool IsAppUpdateAvailable => _appUpdate.LastCheckResult is not null;

    /// <summary>
    /// Re-fires the localized derived strings (section headers) on a culture
    /// change. The per-row labels refresh themselves (each row subscribes on
    /// its own).
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        OnPropertyChanged(nameof(DiscoverySectionHeader));
        OnPropertyChanged(nameof(StorageSectionHeader));
        OnPropertyChanged(nameof(UpdatesSectionHeader));
        OnPropertyChanged(nameof(CurrentVersionLabel));
        OnPropertyChanged(nameof(CurrentVersionDisplay));
    }

    /// <summary>
    /// Opens the OS file manager (Windows Explorer, xdg-open on Linux, etc.) at
    /// the Curator data root (<c>AppPaths.AppDataDir</c>, which contains
    /// <c>mods/</c>, <c>profiles/</c>, <c>logs/</c>, <c>config.json</c>, etc.)
    /// via the injectable path-launcher seam. Delegates to
    /// <see cref="OpenFolderAsync"/> for the no-op + alert handling.
    /// </summary>
    [RelayCommand]
    private async Task OpenDataFolder() =>
        await OpenFolderAsync(AppPaths.AppDataDir);

    /// <summary>
    /// Opens the OS file manager at the current profiles root
    /// (<c>ProfilesBaseFolder</c>, read live from config) via the injectable
    /// path-launcher seam. Delegates to <see cref="OpenFolderAsync"/> for the
    /// no-op + alert handling.
    /// </summary>
    [RelayCommand]
    private async Task OpenProfilesFolder() =>
        await OpenFolderAsync(_configLoader.Load().ProfilesBaseFolder);

    /// <summary>
    /// Shared body of the two open-folder commands: no-op when
    /// <paramref name="path"/> is empty/whitespace or the directory does not
    /// exist on disk; on a launch failure (the seam returns false or throws),
    /// surfaces a localized alert that includes the path so the user can open
    /// it manually; the exception is logged and never propagates.
    /// </summary>
    private async Task OpenFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!_launchExternalPath(path))
            {
                _logger.LogWarning("Opening the folder failed: {Path}", path);
                await ShowOpenFolderFailedAlertAsync(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launching the folder threw: {Path}", path);
            await ShowOpenFolderFailedAlertAsync(path);
        }
    }

    /// <summary>
    /// Shows the localized open-folder-failure alert (the launcher seam
    /// returned false or threw). Includes the path so the user can open it
    /// manually.
    /// </summary>
    private async Task ShowOpenFolderFailedAlertAsync(string path)
    {
        await _dialogs.ShowAlertAsync(
            _localization["Settings_OpenFolderFailedTitle"],
            _localization.Format("Settings_OpenFolderFailedMessage", path));
    }

    /// <summary>
    /// The default path-launcher: opens the OS file manager at
    /// <paramref name="path"/> via <c>Process.Start(UseShellExecute=true)</c>.
    /// Same narrow exception filter + return contract as
    /// <c>ModListViewModel.LaunchExternalPathDefault</c> (duplicated here so
    /// this VM stays self-contained without reaching into the mod-list VM; the
    /// extraction is a flag-and-review item if a third caller appears). Tests
    /// inject a controllable seam.
    /// </summary>
    private static bool LaunchExternalPathDefault(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            using (Process.Start(psi))
            {
            }
            return true;
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or PlatformNotSupportedException
                or FileNotFoundException)
        {
            return false;
        }
    }

    // ---- Updates section: app self-update ---------------------------------

    /// <summary>
    /// The app self-update service published new state (a check landed while
    /// Settings was open). The event fires on a threadpool thread, so the
    /// property changes are marshaled to the UI thread via the
    /// <see cref="_invokeOnUi"/> seam before touching
    /// <see cref="ObservableObject"/> bindings (mirrors the shell's handler).
    /// </summary>
    private void OnAppUpdateStateChanged(object? sender, EventArgs e)
    {
        // Set on the threadpool thread (before the marshal); volatile so the
        // UI-thread RefreshAppUpdateStatus observes the write.
        _hasCheckedAppUpdate = true;
        _invokeOnUi(RefreshAppUpdateStatus);
    }

    /// <summary>
    /// Re-derives <see cref="AppUpdateStatusMessage"/> from
    /// <see cref="IAppUpdateService.LastCheckResult"/> + re-fires
    /// <see cref="IsAppUpdateAvailable"/> + the download command's CanExecute.
    /// Before any check has run, the status is blank (no message shown); after a
    /// check, a null result resolves to "up to date" and a non-null result to
    /// the available-version message.
    /// </summary>
    private void RefreshAppUpdateStatus()
    {
        var info = _appUpdate.LastCheckResult;
        if (info is not null)
        {
            AppUpdateStatusMessage = _localization.Format("Settings_UpdateAvailable", info.TargetVersion);
        }
        else if (_hasCheckedAppUpdate)
        {
            AppUpdateStatusMessage = _localization["Settings_UpToDate"];
        }
        else
        {
            AppUpdateStatusMessage = null;
        }

        OnPropertyChanged(nameof(IsAppUpdateAvailable));
        DownloadAndRestartAppUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Manual "Check for Updates": runs the availability check off the UI thread
    /// and refreshes the inline status from the result. A failure surfaces a
    /// localized "check failed" inline status (the check itself is best-effort,
    /// so a throw here is a wiring problem; defensive). Toggles
    /// <see cref="IsCheckingAppUpdate"/> around the check for the spinner.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckAppUpdate))]
    private async Task CheckAppUpdate()
    {
        IsCheckingAppUpdate = true;
        try
        {
            // The check is I/O; offload it to a thread-pool task so the UI thread
            // stays free. Bare await inside Task.Run is fine (no
            // SynchronizationContext).
            await Task.Run(() => _appUpdate.CheckForUpdatesAsync());
            _hasCheckedAppUpdate = true;
            RefreshAppUpdateStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual app update check failed.");
            _hasCheckedAppUpdate = true;
            AppUpdateStatusMessage = _localization["AppUpdate_CheckFailedMessage"];
            OnPropertyChanged(nameof(IsAppUpdateAvailable));
            DownloadAndRestartAppUpdateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    /// <summary>Only one check at a time, and only when self-update is supported.</summary>
    private bool CanCheckAppUpdate() => !IsCheckingAppUpdate && IsAppUpdateSupported;

    /// <summary>
    /// Download and Restart: runs the download under a modal spinner, then
    /// applies the update on restart. Download failures surface an alert and do
    /// NOT proceed to apply. Mirrors the shell's download flow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAndRestartAppUpdate))]
    private async Task DownloadAndRestartAppUpdate()
    {
        try
        {
            // The download is I/O; offload it to a thread-pool task inside the
            // spinner's work delegate. The ProgressDialog is indeterminate (the
            // final design), so no percentage is surfaced.
            await _dialogs.ShowProgressAsync(
                _localization["AppUpdate_DownloadingTitle"],
                _localization["AppUpdate_DownloadingMessage"],
                () => Task.Run(async () =>
                {
                    // Bare await inside Task.Run (no SynchronizationContext); the
                    // VM-file convention forbids ConfigureAwait(false) entirely.
                    await _appUpdate.DownloadUpdatesAsync();
                    return true;
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App update download failed (Settings).");
            await _dialogs.ShowAlertAsync(
                _localization["AppUpdate_DownloadFailedTitle"],
                _localization["AppUpdate_DownloadFailedMessage"] + " " + ex.Message);
            return;
        }

        // Success: terminates this process + relaunches under the new version.
        _appUpdate.ApplyUpdatesAndRestart();
    }

    /// <summary>The download is only reachable when an update is available.</summary>
    private bool CanDownloadAndRestartAppUpdate() => IsAppUpdateAvailable;

    /// <summary>
    /// The write-through for a discovery field change: read-modify-save the
    /// matching property in <see cref="DiscoveryConfig"/>. An empty / whitespace
    /// value writes <c>null</c> (clears the stored path). Only honored in manual
    /// mode (override on); in automatic mode the discoverer owns the snapshot,
    /// so row writes are ignored even if a callback fires programmatically.
    /// Suppressed during <see cref="RefreshFromConfig"/> +
    /// <see cref="RefreshDiscoveryRows"/> (the fills).
    /// </summary>
    private void WriteThroughDiscovery(DiscoveryFieldRowViewModel row)
    {
        if (_suppressApply || !OverrideAutomaticDiscovery)
        {
            return;
        }

        var value = row.Value;
        var written = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var config = _configLoader.Load();
        SetPath(config.Discovery, row.Field.FieldName, written);
        _configLoader.Save(config);
    }

    /// <summary>
    /// Maps a discovery field's canonical name to its setter on
    /// <see cref="DiscoveryConfig"/>. The name matches the property names of
    /// <see cref="DiscoveryResult"/> (the same names that flow through
    /// <c>LaunchResult.MissingDiscoveryFields</c>). Kept duplicated with the
    /// escape-hatch VM rather than extracted into a shared helper: it is a small
    /// identity switch, the field-name catalog is already the shared source of
    /// truth, and an abstraction here would couple two independent VMs for no
    /// behavioral gain.
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
    /// is null). Used to fill each row at construction + on refresh.
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

    /// <summary>
    /// Whether a discovery field is Linux-only (compatdata + Proton). Used to
    /// platform-gate the Settings rows so Windows does not surface
    /// silently-ineffective rows. The catalog is the single source of truth for
    /// field identity; this helper is the only place that knows which of those
    /// fields are Linux-scoped.
    /// </summary>
    private static bool IsLinuxOnlyField(DiscoveryField field) =>
        field.FieldName is "CompatdataPath" or "ProtonBinaryPath";
}
