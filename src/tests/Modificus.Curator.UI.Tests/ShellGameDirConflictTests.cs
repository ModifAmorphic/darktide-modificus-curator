using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The shell's game-dir conflict consent flow: the
/// <see cref="LaunchStatus.GameDirConflict"/> branch shows the two-choice
/// modal, Rename performs the takeover + shows the rename notice + retries
/// the launch once, Cancel aborts, a second conflict in the same chain
/// surfaces the ordinary error alert (never a loop), and the launch-attempt
/// state stays held through the modal + notice + retry exactly like the
/// failure dialogs.
/// </summary>
public sealed class ShellGameDirConflictTests
{
    private static Modificus.Curator.Profiles.ProfileSummary Profile(string name) =>
        new(Guid.NewGuid(), name, "");

    private static LaunchResult Conflict(string gameDir) => new(
        LaunchStatus.GameDirConflict,
        Path.Combine(gameDir, "mods"),
        Array.Empty<string>(),
        GameDirPath: gameDir);

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(10);
        }
    }

    private const string GameDir = @"C:\games\DARKTIDE";

    [Fact]
    public async Task Rename_performs_the_takeover_notifies_and_retries_the_launch_once()
    {
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            ResultQueue = new[]
            {
                Conflict(GameDir),
                new LaunchResult(LaunchStatus.Launched, null, Array.Empty<string>()),
            },
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        // The consent modal ran once.
        Assert.Single(dialogs.GameDirConflictCalls);
        // The takeover ran for the result's game dir, and the launch retried.
        Assert.Equal(GameDir, Assert.Single(parts.GameDirHost.TakeOverCalls));
        Assert.Equal(2, launch.LaunchCalls.Count);
        // The rename notice carries the path the takeover returned; no error
        // alerts on the success chain.
        var notice = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(parts.GameDirHost.TakeOverResult!, notice.Message);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Rename_notice_is_shown_before_the_launch_retry()
    {
        // The notice must land before the retry so the information survives a
        // later launch failure. Hold the alert open and prove the retry has
        // not run (and the attempt state is still held) until it closes.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            ResultQueue = new[]
            {
                Conflict(GameDir),
                new LaunchResult(LaunchStatus.Launched, null, Array.Empty<string>()),
            },
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var alertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.NextAlertGate = alertGate.Task;
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);
        await UntilAsync(() => dialogs.AlertCalls.Count == 1);

        // The notice is open: the takeover ran, the retry has NOT, and the
        // attempt state is still held.
        Assert.Single(parts.GameDirHost.TakeOverCalls);
        Assert.Single(launch.LaunchCalls);
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));

        alertGate.SetResult();
        await executing;

        Assert.Equal(2, launch.LaunchCalls.Count);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Rename_returning_null_skips_the_notice_but_still_retries()
    {
        // A null return means nothing was renamed (the slot was absent or
        // already Curator's at takeover time): there is nothing to notice,
        // and the retry still runs through the ordinary ladder.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            ResultQueue = new[]
            {
                Conflict(GameDir),
                new LaunchResult(LaunchStatus.Launched, null, Array.Empty<string>()),
            },
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch,
            gameDirHost: new FakeGameDirModsHost { TakeOverResult = null });
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(parts.GameDirHost.TakeOverCalls);
        Assert.Empty(dialogs.AlertCalls);
        Assert.Equal(2, launch.LaunchCalls.Count);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Cancel_aborts_without_takeover_notice_or_retry()
    {
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Cancel };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(launch.LaunchCalls); // no retry
        Assert.Empty(parts.GameDirHost.TakeOverCalls);
        Assert.Empty(dialogs.AlertCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null)); // retry is possible
    }

    [Fact]
    public async Task Cancel_does_not_touch_the_external_hosting_preference()
    {
        // The preference is owned by the Preferences destination alone; the
        // conflict flow never writes it.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Cancel };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);

        await parts.Shell.LaunchCommand.ExecuteAsync(null);

        Assert.False(parts.Config.Config.Preferences.ExternalModHosting);
        Assert.Equal(0, parts.Config.SaveCalls);
    }

    [Fact]
    public async Task Second_conflict_in_the_same_chain_surfaces_the_error_alert_not_a_loop()
    {
        // The retry is one-shot per consent: a conflict that survives the
        // consented rename surfaces the standard error alert (one prompt, one
        // retry, no loop).
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.GameDirConflictCalls); // prompted once
        Assert.Equal(2, launch.LaunchCalls.Count);   // original + single retry
        // The rename notice + the recurrence error, in that order.
        Assert.Equal(2, dialogs.AlertCalls.Count);
        Assert.Contains(parts.GameDirHost.TakeOverResult!, dialogs.AlertCalls[0].Message);
        Assert.Contains(Path.Combine(GameDir, "mods"), dialogs.AlertCalls[1].Message);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Takeover_failure_surfaces_an_alert_without_a_retry()
    {
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var host = new FakeGameDirModsHost { TakeOverThrows = new IOException("disk full") };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch,
            gameDirHost: host);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(launch.LaunchCalls); // no retry after a failed takeover
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("disk full", alert.Message);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Attempt_state_stays_set_while_the_consent_modal_is_open()
    {
        // The overlay state machine holds through the conflict modal exactly
        // like the failure dialogs: the attempt (and the disabled Launch
        // button) persists until the consent + notice + retry chain finishes.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            ResultQueue = new[]
            {
                Conflict(GameDir),
                new LaunchResult(LaunchStatus.Launched, null, Array.Empty<string>()),
            },
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var modalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.NextGameDirConflictGate = modalGate.Task;
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);
        await UntilAsync(() => dialogs.GameDirConflictCalls.Count == 1);

        // The modal is open: the attempt is still in progress and Launch stays
        // disabled; the takeover has not run yet.
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));
        Assert.Empty(parts.GameDirHost.TakeOverCalls);

        modalGate.SetResult();
        await executing;

        Assert.Single(parts.GameDirHost.TakeOverCalls);
        Assert.Equal(2, launch.LaunchCalls.Count);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Conflict_result_without_a_game_dir_degrades_to_the_error_alert()
    {
        // The contract populates GameDirPath for the conflict status; a result
        // that violates it must not crash the consent path.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.GameDirConflict, @"C:\game\mods", Array.Empty<string>(), GameDirPath: null),
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Rename };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.GameDirConflictCalls);
        Assert.Empty(parts.GameDirHost.TakeOverCalls); // never invoked with null
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }
}
