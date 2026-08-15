using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The Settings updates section: the initial inline status reads
/// <see cref="IAppUpdateService.LastCheckResult"/>; the manual check toggles
/// <see cref="SettingsViewModel.IsCheckingAppUpdate"/> + updates the status
/// (up-to-date vs. available); the download + restart flow runs the download
/// under the spinner then applies; and a download failure surfaces an alert and
/// does NOT apply. All against the recording fakes in <see cref="TestDoubles"/>.
/// </summary>
/// <remarks>
/// <para>The VM's <c>OnAppUpdateStateChanged</c> handler marshals its refresh
/// through an injected <c>Action&lt;Action&gt;</c> seam; tests inject a
/// synchronous <c>action =&gt; action()</c>, so the refresh runs inline the
/// moment the event is raised (no Avalonia dispatcher to pump).</para>
/// </remarks>
public sealed class SettingsViewModelAppUpdateTests
{
    private static readonly LocalizationService Localization = new();

    private static SettingsViewModel Build(
        FakeAppUpdateService? appUpdate = null,
        FakeDialogService? dialogs = null,
        FakeConfigLoader? configLoader = null)
    {
        appUpdate ??= new FakeAppUpdateService();
        dialogs ??= new FakeDialogService();
        configLoader ??= new FakeConfigLoader();
        return new SettingsViewModel(
            configLoader,
            new FakeSteamService(),
            Localization,
            appUpdate,
            dialogs,
            new GamingModeState(false),
            invokeOnUi: static action => action(),
            NullLogger<SettingsViewModel>.Instance);
    }

    // ---- initial status reads LastCheckResult -----------------------------

    [Fact]
    public void Initial_status_is_blank_before_any_check()
    {
        var vm = Build();

        Assert.Null(vm.AppUpdateStatusMessage);
        Assert.False(vm.IsAppUpdateAvailable);
    }

    [Fact]
    public void Initial_status_shows_available_when_a_check_already_found_an_update()
    {
        // The startup check completed before Settings opened; the section shows
        // the available version immediately.
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);

        var vm = Build(appUpdate);

        Assert.True(vm.IsAppUpdateAvailable);
        Assert.Contains("2.0.0", vm.AppUpdateStatusMessage);
    }

    [Fact]
    public void Current_version_display_shows_resolved_version_or_unknown()
    {
        var supported = new FakeAppUpdateService { CurrentVersion = "1.2.3" };
        Assert.Equal("1.2.3", Build(supported).CurrentVersionDisplay);

        var unsupported = new FakeAppUpdateService
        {
            IsUpdateSupported = false,
            CurrentVersion = null,
        };
        Assert.Equal(Localization["Settings_VersionUnknown"], Build(unsupported).CurrentVersionDisplay);
    }

    // ---- manual check -----------------------------------------------------

    [Fact]
    public async Task CheckAppUpdate_toggles_IsChecking_and_reports_up_to_date_when_no_update()
    {
        var vm = Build();
        Assert.False(vm.IsCheckingAppUpdate);
        Assert.True(vm.CheckAppUpdateCommand.CanExecute(null)); // supported + not checking

        await vm.CheckAppUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.IsCheckingAppUpdate); // reset after completion
        Assert.Contains(Localization["Settings_UpToDate"], vm.AppUpdateStatusMessage);
        Assert.False(vm.IsAppUpdateAvailable);
    }

    [Fact]
    public async Task CheckAppUpdate_reports_available_when_an_update_is_found()
    {
        var appUpdate = new FakeAppUpdateService
        {
            NextCheckResult = new AppUpdateInfo("2.0.0", Notes: null),
        };
        var vm = Build(appUpdate);

        await vm.CheckAppUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsAppUpdateAvailable);
        Assert.Contains("2.0.0", vm.AppUpdateStatusMessage);
    }

    [Fact]
    public async Task CheckAppUpdate_failure_surfaces_a_check_failed_inline_status()
    {
        var appUpdate = new FakeAppUpdateService
        {
            ThrowOnCheck = new InvalidOperationException("network"),
        };
        var vm = Build(appUpdate);

        // The fake throws synchronously from inside the Task.Run; the command's
        // catch surfaces the localized "check failed" message.
        await vm.CheckAppUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.IsCheckingAppUpdate); // reset in finally
        Assert.Equal(Localization["AppUpdate_CheckFailedMessage"], vm.AppUpdateStatusMessage);
    }

    [Fact]
    public void CheckAppUpdate_command_is_disabled_when_self_update_is_unsupported()
    {
        var appUpdate = new FakeAppUpdateService { IsUpdateSupported = false };
        var vm = Build(appUpdate);

        Assert.False(vm.CheckAppUpdateCommand.CanExecute(null));
    }

    // ---- download and restart --------------------------------------------

    [Fact]
    public async Task DownloadAndRestart_runs_download_under_spinner_then_applies()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var dialogs = new FakeDialogService();
        var vm = Build(appUpdate, dialogs);
        Assert.True(vm.DownloadAndRestartAppUpdateCommand.CanExecute(null));

        await vm.DownloadAndRestartAppUpdateCommand.ExecuteAsync(null);

        Assert.Single(dialogs.ProgressCalls); // download ran under the spinner
        Assert.Equal(1, appUpdate.DownloadCallCount);
        Assert.Equal(1, appUpdate.ApplyCallCount);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task DownloadAndRestart_failure_surfaces_an_alert_and_does_not_apply()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        appUpdate.ThrowOnDownload = new InvalidOperationException("io error");
        var dialogs = new FakeDialogService();
        var vm = Build(appUpdate, dialogs);

        await vm.DownloadAndRestartAppUpdateCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Contains("io error", dialogs.AlertCalls[0].Message);
        Assert.Equal(0, appUpdate.ApplyCallCount); // did NOT apply
    }

    [Fact]
    public void DownloadAndRestart_command_is_gated_on_an_available_update()
    {
        var noUpdate = Build();
        Assert.False(noUpdate.DownloadAndRestartAppUpdateCommand.CanExecute(null));

        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var withUpdate = Build(appUpdate);
        Assert.True(withUpdate.DownloadAndRestartAppUpdateCommand.CanExecute(null));
    }

    // ---- startup-check toggle ---------------------------------------------

    [Fact]
    public void CheckOnStartup_is_pre_filled_from_config_on_construction()
    {
        var configLoader = new FakeConfigLoader();
        configLoader.Config.AppUpdates.CheckOnStartup = false;

        var vm = Build(configLoader: configLoader);

        Assert.False(vm.CheckOnStartup);

        // The pre-fill must not write back (the value already matches config).
        Assert.Equal(0, configLoader.SaveCalls);
    }

    [Fact]
    public void CheckOnStartup_defaults_true_from_a_default_config()
    {
        var vm = Build();

        Assert.True(vm.CheckOnStartup);
    }

    [Fact]
    public void Flipping_CheckOnStartup_persists_to_config()
    {
        var configLoader = new FakeConfigLoader();
        var vm = Build(configLoader: configLoader);
        Assert.True(vm.CheckOnStartup); // default

        vm.CheckOnStartup = false;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.False(configLoader.LastSaved!.AppUpdates.CheckOnStartup);

        // The FakeConfigLoader promotes the save to its live Config, so a
        // subsequent Load reflects the written value.
        Assert.False(configLoader.Load().AppUpdates.CheckOnStartup);

        vm.CheckOnStartup = true;

        Assert.Equal(2, configLoader.SaveCalls);
        Assert.True(configLoader.LastSaved!.AppUpdates.CheckOnStartup);
    }
}
