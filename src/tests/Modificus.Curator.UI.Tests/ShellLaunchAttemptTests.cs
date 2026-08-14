using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The shell-owned launch-attempt state (<see cref="ShellViewModel.IsLaunchAttemptInProgress"/>):
/// Launch disables for the whole attempt (the pre-launch render yield, the
/// synchronous launch call, failure-dialog handling, and the post-spawn
/// running-state handoff), so the process-detection gap after a successful
/// spawn can never double-launch. Deterministic seams drive the yield + the
/// handoff timeout (no live Avalonia dispatcher, no real 30-second wait).
/// </summary>
public sealed class ShellLaunchAttemptTests
{
    private static Modificus.Curator.Profiles.ProfileSummary Profile(string name) =>
        new(Guid.NewGuid(), name, "");

    /// <summary>
    /// Polls <paramref name="condition"/> on short task delays until true
    /// (bounded by the condition eventually holding); lets a test observe the
    /// attempt mid-flight after releasing the yield while the handoff is still
    /// parked.
    /// </summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(10);
        }
    }

    // ---- attempt state gates the button before the launch service runs -----

    [Fact]
    public async Task Attempt_state_disables_Launch_before_the_service_is_invoked()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService();
        var yieldTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch,
            yieldForLaunchRender: () => yieldTcs.Task,
            launchHandoffTimeout: () => handoffTcs.Task).Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);

        // Held at the pre-launch yield: the attempt is in progress, the
        // command is disabled, and the launch service has NOT run yet.
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));
        Assert.Empty(launch.LaunchCalls);

        yieldTcs.SetResult();
        await UntilAsync(() => launch.LaunchCalls.Count == 1);

        // Launched result: parked in the handoff (signal + timeout both
        // pending), so the command stays disabled.
        Assert.False(shell.LaunchCommand.CanExecute(null));

        handoffTcs.SetResult(); // timeout elapses with the game unobserved
        await executing;

        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public async Task False_running_state_never_re_enables_Launch_while_waiting()
    {
        // A false eager refresh + a false polling notification must not clear
        // the attempt state or re-enable the command during the handoff.
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var launch = new FakeLaunchService();
        var handoffTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch,
            launchHandoffTimeout: () => handoffTcs.Task).Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);

        // The eager post-spawn refresh observed a NOT-running game; the
        // attempt stays in progress while the handoff waits.
        await UntilAsync(() => launch.LaunchCalls.Count == 1);
        Assert.Equal(1, session.RefreshCalls);
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));

        // A false polling notification changes nothing.
        session.RaiseIsRunningPropertyChanged();
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));

        handoffTcs.SetResult();
        await executing;

        Assert.False(shell.IsLaunchAttemptInProgress);
    }

    [Fact]
    public async Task Later_IsRunning_true_completes_the_handoff_and_running_keeps_Launch_disabled()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService();
        var handoffTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch,
            launchHandoffTimeout: () => handoffTcs.Task).Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);
        await UntilAsync(() => launch.LaunchCalls.Count == 1);

        // The session's live signal observes Darktide: the handoff resolves,
        // the attempt state clears, and the ordinary IsGameRunning gate keeps
        // Launch disabled.
        session.IsRunning = true;
        await executing;

        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.IsGameRunning);
        Assert.False(shell.LaunchCommand.CanExecute(null));
        Assert.False(handoffTcs.Task.IsCompleted); // the timeout never fired
    }

    [Fact]
    public async Task Handoff_timeout_clears_attempt_state_and_re_enables_Launch()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var launch = new FakeLaunchService();
        var handoffTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch,
            launchHandoffTimeout: () => handoffTcs.Task).Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);
        await UntilAsync(() => launch.LaunchCalls.Count == 1);

        // The timeout elapses with Darktide still absent: retry is possible.
        handoffTcs.SetResult();
        await executing;

        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.IsGameRunning);
        Assert.True(shell.LaunchCommand.CanExecute(null));
    }

    // ---- failure + exception paths ------------------------------------------

    [Fact]
    public async Task Failure_result_keeps_attempt_state_through_the_dialog_then_clears_and_permits_retry()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.DiscoveryIncomplete, "missing", new[] { "ProtonBinaryPath" }),
        };
        var dialogs = new FakeDialogService();
        var dialogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogs.NextEscapeHatchGate = dialogGate.Task;
        var parts = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            dialogs: dialogs,
            launch: launch);
        var shell = parts.Shell;

        var executing = shell.LaunchCommand.ExecuteAsync(null);
        await UntilAsync(() => dialogs.EscapeHatchCalls.Count == 1);

        // The dialog is open: the attempt state is still set and Launch stays
        // disabled.
        Assert.True(shell.IsLaunchAttemptInProgress);
        Assert.False(shell.LaunchCommand.CanExecute(null));

        dialogGate.SetResult();
        await executing;

        Assert.Single(dialogs.EscapeHatchCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null));

        // Retry after the failure works: a second execution launches again.
        await shell.LaunchCommand.ExecuteAsync(null);
        Assert.Equal(2, launch.LaunchCalls.Count);
    }

    [Fact]
    public async Task Error_result_clears_attempt_state_and_permits_retry()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.Error, "boom", Array.Empty<string>()),
        };
        var dialogs = new FakeDialogService();
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            dialogs: dialogs,
            launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null));

        await shell.LaunchCommand.ExecuteAsync(null);
        Assert.Equal(2, launch.LaunchCalls.Count);
    }

    [Fact]
    public async Task Launch_service_exception_clears_attempt_state()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService { LaunchThrows = new InvalidOperationException("boom") };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch).Shell;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => shell.LaunchCommand.ExecuteAsync(null));

        Assert.Single(launch.LaunchCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
        Assert.True(shell.LaunchCommand.CanExecute(null));
    }

    // ---- direct / programmatic concurrency guard -----------------------------

    [Fact]
    public async Task Direct_concurrent_execution_is_rejected_and_launches_only_once()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService();
        var yieldTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: session,
            launch: launch,
            yieldForLaunchRender: () => yieldTcs.Task,
            launchHandoffTimeout: () => handoffTcs.Task).Shell;

        var first = shell.LaunchCommand.ExecuteAsync(null);

        // A direct/programmatic execution while the first attempt holds the
        // state (AsyncRelayCommand.ExecuteAsync does not consult CanExecute)
        // must be refused by the shell's method-level guard.
        var second = shell.LaunchCommand.ExecuteAsync(null);
        await second;

        yieldTcs.SetResult();
        await UntilAsync(() => launch.LaunchCalls.Count == 1);
        handoffTcs.SetResult();
        await first;

        Assert.Single(launch.LaunchCalls);
    }

    [Fact]
    public async Task Execution_with_no_active_profile_sets_no_attempt_state()
    {
        var launch = new FakeLaunchService();
        var shell = TestDoubles.BuildShell(launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Empty(launch.LaunchCalls);
        Assert.False(shell.IsLaunchAttemptInProgress);
    }
}
