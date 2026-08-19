using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The shell's game-dir conflict consent flow: the
/// <see cref="LaunchStatus.GameDirConflict"/> branch shows the three-choice
/// modal, Proceed performs the takeover + retries the launch once, Keep my
/// current setup persists the external-hosting preference + retries once,
/// Cancel aborts, a second conflict in the same chain surfaces the ordinary
/// error alert (never a loop), and the launch-attempt state stays held
/// through the modal + retry exactly like the failure dialogs.
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
    public async Task Proceed_performs_the_takeover_and_retries_the_launch_once()
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
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Proceed };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        // The consent modal ran once with the detected path in its message.
        var message = Assert.Single(dialogs.GameDirConflictCalls);
        Assert.Contains(Path.Combine(GameDir, "mods"), message);
        // The takeover ran for the result's game dir, and the launch retried.
        Assert.Equal(GameDir, Assert.Single(parts.GameDirHost.TakeOverCalls));
        Assert.Equal(2, launch.LaunchCalls.Count);
        // The retry launched: no failure alerts.
        Assert.Empty(dialogs.AlertCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Keep_setup_persists_the_external_preference_and_retries_once()
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
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.KeepCurrentSetup };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch,
            config: new FakeConfigLoader());
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        // No takeover: the game dir is untouched.
        Assert.Empty(parts.GameDirHost.TakeOverCalls);
        // The external-hosting preference is persisted (the retry reads it live).
        Assert.True(parts.Config.Config.Preferences.ExternalModHosting);
        Assert.True(parts.Config.LastSaved!.Preferences.ExternalModHosting);
        Assert.Equal(2, launch.LaunchCalls.Count);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task Cancel_aborts_without_takeover_preference_or_retry()
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
        Assert.False(parts.Config.Config.Preferences.ExternalModHosting);
        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null)); // retry is possible
    }

    [Fact]
    public async Task Second_conflict_in_the_same_chain_surfaces_the_error_alert_not_a_loop()
    {
        // The retry is one-shot per consent: a conflict that survives the
        // consented action surfaces the standard error alert (one prompt, one
        // retry, no loop).
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Proceed };
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.GameDirConflictCalls); // prompted once
        Assert.Equal(2, launch.LaunchCalls.Count);   // original + single retry
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(Path.Combine(GameDir, "mods"), alert.Message);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Takeover_failure_surfaces_an_alert_without_a_retry()
    {
        var a = Profile("Alpha");
        var launch = new FakeLaunchService { NextResult = Conflict(GameDir) };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Proceed };
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
        // button) persists until the consent + retry chain finishes.
        var a = Profile("Alpha");
        var launch = new FakeLaunchService
        {
            ResultQueue = new[]
            {
                Conflict(GameDir),
                new LaunchResult(LaunchStatus.Launched, null, Array.Empty<string>()),
            },
        };
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Proceed };
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
        var dialogs = new FakeDialogService { GameDirConflictResult = GameDirConflictChoice.Proceed };
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
