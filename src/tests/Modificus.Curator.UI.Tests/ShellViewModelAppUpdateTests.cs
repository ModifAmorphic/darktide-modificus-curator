using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The shell's app self-update notice: the dismissible status-strip pill appears
/// only when self-update is supported + a check found an update + the user has
/// not dismissed it this session; the notice-click flow (confirm -> download ->
/// apply); the <see cref="IAppUpdateService.UpdateStateChanged"/> event marshals
/// safely to the UI thread; and leaving Settings re-reads the startup-check
/// toggle so the notice visibility tracks the current config.
/// </summary>
/// <remarks>
/// <para>The VM's <c>OnAppUpdateStateChanged</c> handler marshals its refresh
/// through an injected <c>Action&lt;Action&gt;</c> seam; <see cref="TestDoubles.BuildShell"/>
/// injects a synchronous <c>action =&gt; action()</c>, so the refresh runs inline
/// the moment the event is raised. No Avalonia dispatcher is touched, so the
/// suite runs in parallel with the rest of the assembly.</para>
/// </remarks>
public sealed class ShellViewModelAppUpdateTests
{
    private static readonly LocalizationService Localization = new();

    // ---- ShowAppUpdateNotice gating ---------------------------------------

    [Fact]
    public void Notice_is_hidden_when_self_update_is_unsupported()
    {
        var appUpdate = new FakeAppUpdateService { IsUpdateSupported = false };
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);

        var shell = TestDoubles.BuildShell(appUpdate: appUpdate).Shell;

        Assert.False(shell.ShowAppUpdateNotice);
    }

    [Fact]
    public void Notice_is_hidden_when_no_check_has_found_an_update()
    {
        var shell = TestDoubles.BuildShell().Shell;

        Assert.False(shell.ShowAppUpdateNotice);
    }

    [Fact]
    public void Notice_is_shown_when_supported_and_an_update_is_available()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);

        var shell = TestDoubles.BuildShell(appUpdate: appUpdate).Shell;

        Assert.True(shell.ShowAppUpdateNotice);
        Assert.Contains("2.0.0", shell.AppUpdateNoticeText);
    }

    [Fact]
    public void Dismiss_hides_the_notice_for_the_session_only()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate).Shell;
        Assert.True(shell.ShowAppUpdateNotice);

        shell.DismissAppUpdateCommand.Execute(null);

        Assert.True(shell.IsAppUpdateDismissed);
        Assert.False(shell.ShowAppUpdateNotice);
    }

    // ---- leaving Settings re-reads the startup-check toggle ---------------

    [Fact]
    public async Task Leaving_Settings_re_reads_the_toggle_so_turning_it_off_hides_a_showing_notice()
    {
        // The notice shows under an enabled toggle; after the user turns it off
        // in Settings + leaves the destination, the shell re-reads the config on
        // leave and the notice is dismissed immediately (no restart, no dismiss
        // click needed). Settings is the sole place the toggle changes, so the
        // on-leave refresh is sufficient + no config-change subscription is
        // required.
        var config = new FakeConfigLoader();
        config.Config.AppUpdates.CheckOnStartup = true;
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var parts = TestDoubles.BuildShell(appUpdate: appUpdate, config: config);
        var shell = parts.Shell;
        Assert.True(shell.ShowAppUpdateNotice);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        Assert.True(shell.ShowAppUpdateNotice); // entering Settings does not re-read the toggle

        config.Config.AppUpdates.CheckOnStartup = false;
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods); // leave Settings

        Assert.False(shell.ShowAppUpdateNotice);
    }

    [Fact]
    public async Task Leaving_Settings_re_reads_the_toggle_so_turning_it_on_shows_the_notice()
    {
        // Symmetric: a notice hidden by an off toggle re-enables the moment the
        // toggle is turned back on + Settings is left.
        var config = new FakeConfigLoader();
        config.Config.AppUpdates.CheckOnStartup = false;
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var parts = TestDoubles.BuildShell(appUpdate: appUpdate, config: config);
        var shell = parts.Shell;
        Assert.False(shell.ShowAppUpdateNotice);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        config.Config.AppUpdates.CheckOnStartup = true;
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.True(shell.ShowAppUpdateNotice);
    }

    [Fact]
    public async Task Entering_Settings_does_not_re_read_the_toggle()
    {
        // Only leaving Settings re-reads the toggle; entering Settings rehydrates
        // the page's discovery rows + CheckOnStartup but the notice visibility is
        // driven by the leave point. Toggling between enter + leave proves the
        // asymmetry: the notice does not flip until the leave.
        var config = new FakeConfigLoader();
        config.Config.AppUpdates.CheckOnStartup = true;
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var parts = TestDoubles.BuildShell(appUpdate: appUpdate, config: config);
        var shell = parts.Shell;
        Assert.True(shell.ShowAppUpdateNotice);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        config.Config.AppUpdates.CheckOnStartup = false;
        // Still showing immediately after entering (the leave has not run).
        Assert.True(shell.ShowAppUpdateNotice);
    }

    // ---- notice-click flow ------------------------------------------------

    [Fact]
    public async Task Notice_click_with_confirm_cancel_dismisses_for_the_session_and_does_not_download()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate, dialogs: dialogs).Shell;

        await shell.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.True(shell.IsAppUpdateDismissed);
        Assert.False(shell.ShowAppUpdateNotice);
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(0, appUpdate.DownloadCallCount);
        Assert.Equal(0, appUpdate.ApplyCallCount);
    }

    [Fact]
    public async Task Notice_click_with_confirm_ok_downloads_under_spinner_then_applies()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate, dialogs: dialogs).Shell;

        await shell.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Contains("2.0.0", dialogs.LastConfirmMessage);
        Assert.Single(dialogs.ProgressCalls);
        Assert.Equal(1, appUpdate.DownloadCallCount);
        Assert.Equal(1, appUpdate.ApplyCallCount);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task Notice_click_download_failure_surfaces_an_alert_and_does_not_apply()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        appUpdate.ThrowOnDownload = new InvalidOperationException("checksum mismatch");
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate, dialogs: dialogs).Shell;

        await shell.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["AppUpdate_DownloadFailedTitle"], dialogs.AlertCalls[0].Title);
        Assert.Contains("checksum mismatch", dialogs.AlertCalls[0].Message);
        Assert.Equal(0, appUpdate.ApplyCallCount);
    }

    [Fact]
    public async Task Notice_click_with_no_result_is_a_no_op()
    {
        var appUpdate = new FakeAppUpdateService(); // LastCheckResult = null
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate, dialogs: dialogs).Shell;

        await shell.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(0, appUpdate.DownloadCallCount);
    }

    // ---- UpdateStateChanged wiring ----------------------------------------

    [Fact]
    public void UpdateStateChanged_refreshes_the_notice_so_a_newly_found_update_shows()
    {
        // The VM's handler marshals its refresh through the injected seam; the
        // test seam runs inline, so raising the event resolves the notice at
        // once. Verifies the wiring (event -> handler -> refresh) without the
        // production dispatcher.
        var appUpdate = new FakeAppUpdateService();
        var shell = TestDoubles.BuildShell(appUpdate: appUpdate).Shell;
        Assert.False(shell.ShowAppUpdateNotice);

        appUpdate.LastCheckResult = new AppUpdateInfo("3.0.0", Notes: null);
        appUpdate.RaiseUpdateStateChanged();

        Assert.True(shell.ShowAppUpdateNotice);
        Assert.Contains("3.0.0", shell.AppUpdateNoticeText);
    }
}
