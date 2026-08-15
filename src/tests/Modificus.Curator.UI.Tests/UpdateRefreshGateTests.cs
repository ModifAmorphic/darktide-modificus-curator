using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="UpdateRefreshGate"/> unit tests: the rate-limit tracking fed by
/// <see cref="UpdateRefreshGate.ApplyResult"/>, the effective-reset computation
/// (server reset governs; the 1-minute fallback when silent), the coupled
/// IsRateLimitActive / IsManualThrottled / IsRefreshEnabled decisions, the
/// shared countdown-timer lifecycle, and the marshaled StateChanged event. The
/// VM-level rendering (tooltip priority, localized strings) is covered by the
/// mod-list VM tests.
/// </summary>
public sealed class UpdateRefreshGateTests
{
    private static UpdateCheckRunner BuildRunner(
        Func<DateTimeOffset> getNow,
        out UpdateRefreshGate gate,
        Action<Action>? startTimer = null,
        Action? stopTimer = null,
        Action<Action>? invokeOnUi = null)
    {
        var session = new FakeProfileSession { ActiveProfileId = Guid.NewGuid() };
        var runner = new UpdateCheckRunner(
            session,
            new FakeProfileService(),
            new FakeUpdateCheckService(),
            new FakeConfigLoader(),
            new FakeAppStateStore(),
            new FakeAutomaticUpdateService(),
            NullLogger<UpdateCheckRunner>.Instance,
            getNow: getNow,
            invokeOnUi: invokeOnUi,
            startCountdownTimer: startTimer,
            stopCountdownTimer: stopTimer);
        gate = runner.RefreshGate;
        return runner;
    }

    private static UpdateCheckResult Result(
        bool rateLimited, DateTimeOffset checkedAt, DateTimeOffset? resetsAt = null) =>
        new(Array.Empty<ModUpdateInfo>(), checkedAt, rateLimited, false,
            Outcome: rateLimited ? CheckOutcome.RateLimited : CheckOutcome.Success,
            RateLimitResetsAt: resetsAt);

    // ---- rate-limit tracking ------------------------------------------------

    [Fact]
    public void Server_reported_reset_governs_the_active_window()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = null as UpdateRefreshGate;
        BuildRunner(() => now, out gate);

        gate.ApplyResult(Result(true, now, now.AddMinutes(5)));

        Assert.True(gate!.IsRateLimited);
        Assert.True(gate.IsRateLimitActive);
        Assert.False(gate.IsRefreshEnabled);

        // One second before the reset: still active.
        now = now.AddMinutes(5).AddSeconds(-1);
        gate.Reevaluate();
        Assert.True(gate.IsRateLimitActive);

        // Past the reset: clears.
        now = now.AddSeconds(2);
        gate.Reevaluate();
        Assert.False(gate.IsRateLimitActive);
        Assert.True(gate.IsRefreshEnabled);
    }

    [Fact]
    public void Null_reset_falls_back_to_one_minute_from_checked_at()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = null as UpdateRefreshGate;
        BuildRunner(() => now, out gate);
        var checkedAt = now;

        gate.ApplyResult(Result(true, checkedAt, resetsAt: null));

        Assert.True(gate!.IsRateLimitActive);
        Assert.Null(gate.RateLimitResetsAt);

        // Halfway through the fallback: still active.
        now = checkedAt.AddSeconds(30);
        gate.Reevaluate();
        Assert.True(gate.IsRateLimitActive);

        // Past the 1-minute fallback: clears.
        now = checkedAt.AddMinutes(1).AddSeconds(1);
        gate.Reevaluate();
        Assert.False(gate.IsRateLimitActive);
    }

    [Fact]
    public void Non_rate_limited_result_clears_the_tracking_immediately()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = null as UpdateRefreshGate;
        BuildRunner(() => now, out gate);

        gate.ApplyResult(Result(true, now, now.AddMinutes(30)));
        Assert.True(gate.IsRateLimitActive);

        gate.ApplyResult(Result(false, now));

        Assert.False(gate.IsRateLimited);
        Assert.Null(gate.RateLimitResetsAt);
        Assert.False(gate.IsRateLimitActive);
        Assert.True(gate.IsRefreshEnabled);
    }

    [Fact]
    public void Null_result_changes_nothing()
    {
        // A swallowed check failure (result null) never touches the tracking.
        var now = DateTimeOffset.UtcNow;
        var gate = null as UpdateRefreshGate;
        BuildRunner(() => now, out gate);
        gate.ApplyResult(Result(true, now, now.AddMinutes(5)));

        gate.ApplyResult(null);

        Assert.True(gate!.IsRateLimited);
        Assert.True(gate.IsRateLimitActive);
    }

    // ---- timer lifecycle + StateChanged -------------------------------------

    [Fact]
    public void The_shared_timer_runs_while_either_cause_is_active_and_stops_after()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var stops = 0;
        var gate = null as UpdateRefreshGate;
        BuildRunner(
            () => now,
            out gate,
            startTimer: _ => starts++,
            stopTimer: () => stops++);

        // A rate limit engages the timer (the gate never evaluated before, so
        // the stop seam is untouched).
        gate!.ApplyResult(Result(true, now, now.AddMinutes(5)));
        Assert.Equal(1, starts);
        Assert.Equal(0, stops);

        // Still active: each tick re-starts (production's seam is idempotent).
        gate.Reevaluate();
        Assert.Equal(2, starts);

        // Past the reset: the tick stops the timer.
        now = now.AddMinutes(6);
        gate.Reevaluate();
        Assert.Equal(2, starts);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void StateChanged_is_raised_through_the_injected_marshal_seam()
    {
        var marshaled = 0;
        var gate = null as UpdateRefreshGate;
        BuildRunner(
            static () => DateTimeOffset.UtcNow,
            out gate,
            invokeOnUi: action => { marshaled++; action(); });
        var raised = 0;
        gate!.StateChanged += () => raised++;

        gate.ApplyResult(Result(true, DateTimeOffset.UtcNow));

        Assert.Equal(1, raised);
        Assert.Equal(1, marshaled);
    }

    // ---- manual throttle coupling -------------------------------------------

    [Fact]
    public async Task Manual_throttle_blocks_the_gate_until_the_cooldown_elapses()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = null as UpdateRefreshGate;
        var runner = BuildRunner(() => now, out gate);

        // Spend the free budget: ten manual fires inside the window.
        for (var i = 0; i < 10; i++)
        {
            now = now.AddSeconds(i);
            await runner.CheckNowAsync();
        }

        Assert.True(gate!.IsManualThrottled);
        Assert.NotNull(gate.ManualThrottleClearsAt);
        Assert.False(gate.IsRefreshEnabled);

        // Advance past the 2-minute cooldown + re-evaluate (the tick).
        now = now.AddMinutes(3);
        gate.Reevaluate();

        Assert.False(gate.IsManualThrottled);
        Assert.True(gate.IsRefreshEnabled);
    }
}
