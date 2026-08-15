using System.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="UpdateCheckRunner"/> unit tests: the trigger points (startup with
/// a restored active id, an active-profile switch, the periodic timer, and the
/// manual <see cref="UpdateCheckRunner.CheckNow"/>), the non-triggers (null id,
/// non-<see cref="IProfileSession.ActiveProfileId"/> property changes, the
/// toggle off, no active profile), the interval math, and the fire-and-forget
/// exception safety (a throwing <see cref="IUpdateCheckService.CheckAsync"/> is
/// swallowed; the runner survives and keeps firing on the next switch).
/// </summary>
/// <remarks>
/// <para>
/// The runner dispatches each check on a thread-pool task (fire-and-forget), so
/// the tests coordinate timing against a recording fake service. Each assertion
/// is gated by <see cref="WaitAsync"/>, which polls the recorded call count on a
/// short delay until the expected call lands or a 2s timeout elapses (then
/// asserts the condition held), so a missing fire surfaces as a deterministic
/// test failure rather than a flaky stale-pass.</para>
/// <para>
/// The fake session is the shared <see cref="FakeProfileSession"/> from
/// <c>TestDoubles.cs</c>: its <see cref="FakeProfileSession.ActiveProfileId"/>
/// setter raises <see cref="INotifyPropertyChanged.PropertyChanged"/> via
/// <c>ObservableObject</c>'s <c>SetProperty</c>, mirroring how the real
/// <see cref="ProfileSession"/> raises it (the
/// <c>[ObservableProperty]</c> source generator).</para>
/// <para>
/// The periodic timer is injected as a capturing <c>startTimer</c> delegate so
/// the test invokes the captured tick directly (no real timer). The clock is
/// injected as a controllable <c>getNow</c> so the interval math is deterministic
/// without real-time delays.</para>
/// </remarks>
public sealed class UpdateCheckRunnerTests
{
    // ---- startup trigger ---------------------------------------------------

    [Fact]
    public async Task Start_with_active_profile_fires_check_once()
    {
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        runner.Start();

        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(id, Assert.Single(service.Calls));
    }

    [Fact]
    public async Task Start_with_no_active_profile_does_not_fire()
    {
        // No id restored: Start() must not fire. The only fire path in Start is
        // the explicit "id already present" check, which short-circuits on
        // null. A short delay confirms no async fire lands.
        var session = new FakeProfileSession(); // ActiveProfileId stays null
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        runner.Start();

        await Task.Delay(50);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task Start_is_suppressed_when_last_check_is_within_the_interval()
    {
        // The core "no call-per-launch" guarantee: a persisted
        // LastUpdateCheckUtc within the configured interval suppresses the
        // opening startup check. Then advancing the clock past the interval
        // lets the periodic tick fire. This is what survives a rapid
        // open/close loop without burning an API call per launch.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        appState.LastUpdateCheckUtc = now; // a check "just fired" in a prior session
        var (runner, tick) = BuildWithCapturedTick(
            session, service, getNow: () => now, appState: appState);

        runner.Start();
        await Task.Delay(50);
        Assert.Empty(service.Calls); // interval NOT elapsed -> no startup fire

        // Tick while still within the interval -> still no fire.
        tick();
        await Task.Delay(50);
        Assert.Empty(service.Calls);

        // Advance past the default 10-minute interval -> the next tick fires.
        now = now.AddMinutes(11);
        tick();
        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(id, Assert.Single(service.Calls));
    }

    // ---- profile-switch trigger --------------------------------------------

    [Fact]
    public async Task Profile_switch_fires_check_for_the_new_id()
    {
        var newId = Guid.NewGuid();
        var session = new FakeProfileSession(); // null at start
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await Task.Delay(50); // confirm zero startup calls (none expected)

        session.ActiveProfileId = newId; // raises PropertyChanged

        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(newId, Assert.Single(service.Calls));
    }

    [Fact]
    public async Task Switch_to_null_does_not_fire_a_second_check()
    {
        // Delete-of-active clears the selection (null). That is not a load:
        // the runner must not fire for a null id. Only the opening startup
        // call should be recorded.
        var firstId = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = firstId };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.ActiveProfileId = null; // cleared, not switched

        await Task.Delay(50);
        Assert.Single(service.Calls); // still just the startup call
    }

    [Fact]
    public async Task Switch_is_suppressed_within_interval_and_fires_once_it_elapses()
    {
        // Mirrors Start_is_suppressed_when_last_check_is_within_the_interval but
        // via OnActiveProfileChanged: a recent persisted check suppresses the
        // first switch; once the interval elapses, a second switch fires. This
        // prevents rapid profile switching from burning calls in a session.
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var session = new FakeProfileSession(); // null at start
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        appState.LastUpdateCheckUtc = now; // a check "just fired"
        var runner = Build(session, service, getNow: () => now, appState: appState);
        runner.Start();
        await Task.Delay(50);
        Assert.Empty(service.Calls); // no id at startup -> no startup fire

        session.ActiveProfileId = firstId; // switch while within interval
        await Task.Delay(50);
        Assert.Empty(service.Calls); // interval NOT elapsed -> switch suppressed

        // Advance past the default 10-minute interval -> the next switch fires.
        now = now.AddMinutes(11);
        session.ActiveProfileId = secondId;
        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(secondId, Assert.Single(service.Calls));
    }

    // ---- non-trigger -------------------------------------------------------

    [Fact]
    public async Task IsRunning_change_does_not_fire_a_check()
    {
        // The polling timer drives IsRunning changes every few seconds. Those
        // are not profile loads; the runner must ignore them. Start fires the
        // one startup check, then the IsRunning change adds nothing.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.IsRunning = true; // polling-timer driven, not a profile load

        await Task.Delay(50);
        Assert.Single(service.Calls); // still just the startup call
    }

    // ---- periodic timer ---------------------------------------------------

    [Fact]
    public async Task Timer_tick_fires_check_when_enabled_and_profile_active()
    {
        // The periodic tick fires a check when: the toggle is on, a profile is
        // active, and the configured interval has elapsed since the last check.
        // Start() fires the opening check (stamps _lastCheckAt = t0); the
        // first tick at t0 does not re-fire (no time elapsed); advancing the
        // clock past the interval makes the next tick fire.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, getNow: () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the startup fire

        // Tick immediately: no interval elapsed since startup -> no new fire.
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls);

        // Advance past the default 10-minute interval -> the next tick fires.
        now = now.AddMinutes(11);
        tick();
        await WaitAsync(() => service.CallCount == 2);
    }

    [Fact]
    public async Task Timer_tick_does_not_fire_when_toggle_is_off()
    {
        // The toggle gates ONLY the periodic timer. With it off, ticks (even
        // past the interval) fire nothing. The startup check still ran.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckEnabled = false;
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, configLoader, () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // startup (always)

        now = now.AddMinutes(20); // well past the interval
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls); // only the startup fire
    }

    [Fact]
    public async Task Timer_tick_does_not_fire_when_no_profile_is_active()
    {
        // No active profile -> nothing to check. The periodic tick is a no-op.
        // (Start fires nothing here either, since the session has no id.)
        var session = new FakeProfileSession(); // null
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, getNow: () => now);

        runner.Start();
        await Task.Delay(50);
        Assert.Empty(service.Calls);

        now = now.AddMinutes(20);
        tick();
        await Task.Delay(50);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task Timer_tick_honors_a_runtime_interval_change_live()
    {
        // The interval is read live on each tick. With the default 10-minute
        // interval, advance 5 minutes (no fire under 10), then lower the config
        // to 5 minutes (the compliance floor) so the 5 elapsed minutes now meet
        // it, and tick -> fires. This is what makes a dialog change take effect
        // without a restart. (Both 10 and 5 are at/above the 5-minute floor, so
        // neither is silently raised by the tick-time clamp.)
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var configLoader = new FakeConfigLoader(); // default 10-minute interval
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, configLoader, () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        now = now.AddMinutes(5);
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls); // 5 < 10, no fire

        // Lower the interval to 5 minutes (the floor); the 5 elapsed minutes now meet it.
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes = 5;
        tick();
        await WaitAsync(() => service.CallCount == 2);
    }

    [Fact]
    public async Task Timer_tick_clamps_a_sub_floor_interval_up_to_the_floor()
    {
        // The tick-time Math.Max clamp is the compliance defense-in-depth: a
        // sub-floor value that reached the runner (a hand-edited config, or a
        // save path that skipped its own clamp) can never drive an API call
        // faster than NexusConfig.MinAutoUpdateCheckIntervalMinutes. Configure a
        // 1-minute interval (below that floor) and confirm the clamp raises it:
        // no fire at 1 minute, fire at the clamped floor.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes = 1;
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, configLoader, () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the startup fire (stamps _lastCheckAt)

        // 1 minute elapsed: below the clamped floor, so the configured 1-minute
        // interval must NOT fire here.
        now = now.AddMinutes(1);
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls);

        // Advance to 5 minutes total since the last check (the clamped floor):
        // the next tick fires.
        now = now.AddMinutes(4);
        tick();
        await WaitAsync(() => service.CallCount == 2);
    }

    // ---- manual check now ------------------------------------------------

    [Fact]
    public async Task CheckNow_triggers_a_thorough_check_for_active_profile()
    {
        // The manual trigger (header refresh button) fires a THOROUGH check
        // right away (the per-mod pass), regardless of the toggle. Construct
        // with the profile already active so Start fires the opening Month-only
        // check; then CheckNow fires a thorough one for the SAME profile.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the opening startup fire (Month-only)

        await runner.CheckNowAsync(); // the manual trigger (thorough)

        await WaitAsync(() => service.ThoroughCallCount == 1);
        // The Month-only path got the startup id; the thorough path got the
        // manual id (same profile).
        Assert.Equal(new[] { id }, service.Calls);
        Assert.Equal(new[] { id }, service.ThoroughCalls);
    }

    // ---- automatic-update chaining (after each check) ---------------------

    [Fact]
    public async Task Run_chains_the_automatic_update_service_after_each_check_with_the_captured_result()
    {
        // The runner captures the exact result from the check invocation + hands
        // it to the automatic-update service. A periodic fire lands one
        // RunAfterCheckAsync call with the profile id.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var autoUpdate = new FakeAutomaticUpdateService();
        var runner = Build(session, service, autoUpdate: autoUpdate);
        runner.Start();

        await WaitAsync(() => autoUpdate.Calls.Count == 1);

        var (result, profileId) = Assert.Single(autoUpdate.Calls);
        Assert.Equal(id, profileId);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CheckNow_awaits_the_automatic_update_batch()
    {
        // The manual trigger awaits the runner's task, which now includes the
        // automatic-update batch. So CheckNowAsync does not complete until the
        // batch lands (the manual spinner stays active through installations).
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var autoUpdate = new FakeAutomaticUpdateService();
        var runner = Build(session, service, autoUpdate: autoUpdate);

        await runner.CheckNowAsync();

        // The batch ran (synchronously in the fake) before CheckNowAsync returned.
        Assert.Single(autoUpdate.Calls);
    }

    [Fact]
    public async Task CheckNow_is_a_noop_when_no_profile_is_active()
    {
        var session = new FakeProfileSession(); // null
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        await runner.CheckNowAsync();

        await Task.Delay(50);
        Assert.Empty(service.Calls);
        Assert.Empty(service.ThoroughCalls);
    }

    [Fact]
    public async Task CheckNow_swallowing_a_throwing_thorough_check_does_not_fault_the_awaited_task()
    {
        // The manual trigger awaits the runner's Task; a throwing
        // CheckThoroughAsync must be swallowed by the runner's belt-and-suspenders
        // catch so the awaited Task does not fault (which would surface as an
        // unobserved exception on the list VM's command).
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var runner = Build(session, service);

        await runner.CheckNowAsync(); // must not throw

        Assert.Equal(new[] { id }, service.ThoroughCalls);
    }

    [Fact]
    public async Task CheckNow_bypasses_the_interval_gate()
    {
        // The manual trigger is user-initiated, so it bypasses the interval
        // gate even when the interval has NOT elapsed. Seed the store
        // with a recent check (so the gate is closed), Start() must skip its
        // opening fire, but CheckNowAsync still fires the thorough check.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        appState.LastUpdateCheckUtc = now; // gate closed
        var runner = Build(session, service, getNow: () => now, appState: appState);

        runner.Start();
        await Task.Delay(50);
        Assert.Empty(service.Calls); // startup suppressed by the gate

        await runner.CheckNowAsync(); // manual bypass -> fires thorough

        await WaitAsync(() => service.ThoroughCallCount == 1);
        Assert.Equal(new[] { id }, service.ThoroughCalls);
    }

    // ---- manual sliding-window throttle ------------------------------------

    // The manual "check now" path carries its own throttle (independent of the
    // interval gate): 10 free refreshes per rolling hour, then 1 per 2 minutes
    // until the window slides. These tests drive it deterministically via the
    // injected getNow clock (no real-time delays). The clock is captured by the
    // lambda so reassigning the local advances what the runner sees.

    [Fact]
    public async Task ManualRefresh_first_ten_fire_freely_and_throttle_stays_null_under_the_limit()
    {
        // The free budget is 10 per rolling hour. The first 10 manual refreshes
        // all fire (CheckThoroughAsync called 10x); NextManualRefreshAllowedAt
        // is null while the count is under 10 (the button stays enabled).
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now);

        // Fire 9: each fires freely; the throttle stays null (count < 10).
        for (var i = 0; i < 9; i++)
        {
            Assert.Null(runner.NextManualRefreshAllowedAt);
            now = now.AddSeconds(1);
            await runner.CheckNowAsync();
        }
        // After 9 in the window, still free (the 10th will be the last free one).
        Assert.Null(runner.NextManualRefreshAllowedAt);

        // The 10th fires too (count is 9 before it, < 10 -> allowed).
        now = now.AddSeconds(1);
        await runner.CheckNowAsync();

        await WaitAsync(() => service.ThoroughCallCount == 10);
        Assert.Equal(10, service.ThoroughCallCount);
    }

    [Fact]
    public async Task ManualRefresh_eleventh_within_two_minutes_of_tenth_is_blocked()
    {
        // After the free budget is spent (10 in the window), an 11th within 2
        // minutes of the most recent (the 10th) is BLOCKED: it does not fire,
        // and NextManualRefreshAllowedAt is the absolute unlock instant
        // (10thTimestamp + 2 minutes).
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now);

        // Spend the free budget (10 fires), advancing 1s each so the 10th
        // timestamp is known.
        var tenthTimestamp = FireManualBudget(runner, ref now, 10);
        await WaitAsync(() => service.ThoroughCallCount == 10);

        // 11th, 30 seconds past the 10th (within the 2-minute cooldown): blocked.
        now = tenthTimestamp.AddSeconds(30);
        await runner.CheckNowAsync();
        await Task.Delay(50);
        Assert.Equal(10, service.ThoroughCallCount); // no 11th fire

        // The unlock instant is the 10th + 2 minutes (deterministic via the
        // injected clock).
        Assert.Equal(tenthTimestamp.AddMinutes(2), runner.NextManualRefreshAllowedAt);
    }

    [Fact]
    public async Task ManualRefresh_blocked_attempt_does_not_stamp_or_persist_the_last_check()
    {
        // A blocked attempt consumes nothing: no API call, no timestamp stamp,
        // no persistence change. The shared last-check (the interval-gate
        // timestamp) must stay at the value the 10th fire wrote.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now, appState: appState);

        var tenthTimestamp = FireManualBudget(runner, ref now, 10);
        await WaitAsync(() => service.ThoroughCallCount == 10);
        // The 10th fire stamped + persisted the shared last-check.
        Assert.Equal(tenthTimestamp, appState.LastUpdateCheckUtc);

        // 11th within the cooldown: blocked.
        now = tenthTimestamp.AddSeconds(30);
        await runner.CheckNowAsync();

        // The persisted timestamp is UNCHANGED (the blocked attempt stamped
        // nothing). This is the "stamps NEITHER" half of the contract: the
        // sliding-window queue and the shared last-check are separate, and a
        // blocked attempt touches neither.
        Assert.Equal(tenthTimestamp, appState.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task ManualRefresh_after_two_minutes_past_the_tenth_fires_and_reschedules()
    {
        // Once 2 minutes elapse since the 10th, the next manual refresh is
        // allowed again: it fires (the 11th), records its own timestamp, and
        // NextManualRefreshAllowedAt reflects the new most-recent + 2 minutes.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now);

        var tenthTimestamp = FireManualBudget(runner, ref now, 10);
        await WaitAsync(() => service.ThoroughCallCount == 10);

        // Advance past the 2-minute cooldown from the 10th.
        var eleventhTimestamp = tenthTimestamp.AddMinutes(2).AddSeconds(5);
        now = eleventhTimestamp;
        await runner.CheckNowAsync();

        await WaitAsync(() => service.ThoroughCallCount == 11);
        Assert.Equal(11, service.ThoroughCallCount);

        // The 11th recorded its timestamp, so the throttle reschedules from it
        // (count is still >= 10: the window has not slid yet).
        Assert.Equal(eleventhTimestamp.AddMinutes(2), runner.NextManualRefreshAllowedAt);
    }

    [Fact]
    public async Task ManualRefresh_window_resets_after_one_hour_and_free_mode_resumes()
    {
        // Timestamps age out of the rolling 1-hour window; as the count drops
        // under 10, free mode resumes. After advancing past 1 hour from the
        // FIRST timestamp, the first is pruned (count 10 -> 9), the throttle
        // clears, and a refresh fires freely.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now);

        var firstTimestamp = now.AddSeconds(1); // the first fire's timestamp
        FireManualBudget(runner, ref now, 10);
        await WaitAsync(() => service.ThoroughCallCount == 10);
        Assert.NotNull(runner.NextManualRefreshAllowedAt); // throttled (count 10)

        // Advance past 1 hour from the first timestamp so it ages out of the
        // window (count drops to 9, under the limit).
        now = firstTimestamp + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);
        Assert.Null(runner.NextManualRefreshAllowedAt); // free mode resumed

        // A refresh fires freely now (the 11th, no longer blocked).
        await runner.CheckNowAsync();
        await WaitAsync(() => service.ThoroughCallCount == 11);
        Assert.Equal(11, service.ThoroughCallCount);
    }

    [Fact]
    public async Task ManualRefresh_throttle_is_independent_of_the_interval_gate()
    {
        // The sliding window gates ONLY the manual path. A throttled manual
        // attempt (the 11th) is blocked, but the automatic triggers (startup,
        // switch, periodic) still fire governed only by the interval gate. This
        // is the "do NOT gate automatic triggers" contract.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, getNow: () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the startup auto fire (Month-only)

        // Spend the manual free budget (10 fires) -> manual path now throttled.
        FireManualBudget(runner, ref now, 10);
        await WaitAsync(() => service.ThoroughCallCount == 10);

        // 11th manual within the cooldown: blocked (thorough count stays 10).
        now = now.AddSeconds(30);
        await runner.CheckNowAsync();
        Assert.Equal(10, service.ThoroughCallCount);

        // A periodic tick past the interval still fires the auto (Month-only)
        // path, unaffected by the manual throttle. The auto + manual paths are
        // independent: the manual sliding window never gates the auto triggers.
        now = now.AddMinutes(11); // past the default 10-minute interval
        tick();
        await WaitAsync(() => service.CallCount == 2); // 2nd auto call landed
    }

    [Fact]
    public async Task ManualRefresh_window_persists_across_a_simulated_restart()
    {
        // The manual throttle's sliding window PERSISTS across restarts via
        // IUpdateCheckScheduleState.ManualRefreshTimestamps, so a user who spends their 10
        // free refreshes, closes the app, and reopens within the 1-hour window
        // is STILL throttled (the 11th is blocked until the 2-minute cadence
        // allows it). This test simulates a restart by building a SECOND runner
        // reusing the SAME FakeAppStateStore (the persisted state the first
        // runner wrote).
        var id = Guid.NewGuid();
        var service1 = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;

        // Runner #1 (the first app session): spend the free budget (10 fires).
        var session1 = new FakeProfileSession { ActiveProfileId = id };
        var runner1 = Build(session1, service1, getNow: () => now, appState: appState);
        var tenthTimestamp = FireManualBudget(runner1, ref now, 10);
        await WaitAsync(() => service1.ThoroughCallCount == 10);

        // The persist-on-fire path wrote the window to the shared store: 10
        // entries, matching the 10 successful fires.
        Assert.NotNull(appState.ManualRefreshTimestamps);
        Assert.Equal(10, appState.ManualRefreshTimestamps!.Count);
        Assert.True(appState.ManualRefreshSetCount >= 10);

        // Simulate a restart: a NEW runner instance over the SAME persisted
        // state, with the clock advanced only slightly (well within the
        // 2-minute cooldown since the 10th fire, and well within the 1-hour
        // window).
        now = tenthTimestamp.AddSeconds(30);
        var service2 = new FakeUpdateCheckService();
        var session2 = new FakeProfileSession { ActiveProfileId = id };
        var runner2 = Build(session2, service2, getNow: () => now, appState: appState);
        runner2.Start();

        // The throttle state survived the "restart": the second runner is
        // throttled (NextManualRefreshAllowedAt is non-null, pointing at the
        // 10th timestamp + 2 minutes).
        Assert.NotNull(runner2.NextManualRefreshAllowedAt);
        Assert.Equal(tenthTimestamp.AddMinutes(2), runner2.NextManualRefreshAllowedAt);

        // The 11th manual refresh on the second runner is BLOCKED (does not
        // fire): the free budget did not reset on reopen.
        await runner2.CheckNowAsync();
        await Task.Delay(50);
        Assert.Equal(0, service2.ThoroughCallCount); // blocked -> no fire
    }

    [Fact]
    public async Task ManualRefresh_window_ages_to_empty_after_a_long_restart()
    {
        // A restart longer than the 1-hour window yields an empty window (aged
        // entries prune on the first access after the seed), restoring free
        // mode. The persisted entries do not gate forever: the prune step on
        // the first access drops anything older than ManualRefreshWindow.
        var id = Guid.NewGuid();
        var service1 = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;

        var session1 = new FakeProfileSession { ActiveProfileId = id };
        var runner1 = Build(session1, service1, getNow: () => now, appState: appState);
        FireManualBudget(runner1, ref now, 10); // spend the budget
        await WaitAsync(() => service1.ThoroughCallCount == 10);
        Assert.NotNull(appState.ManualRefreshTimestamps);

        // Simulate a restart 2 hours later: every persisted timestamp has aged
        // out of the 1-hour window.
        now = now.AddHours(2);
        var service2 = new FakeUpdateCheckService();
        var session2 = new FakeProfileSession { ActiveProfileId = id };
        var runner2 = Build(session2, service2, getNow: () => now, appState: appState);
        runner2.Start();

        // Free mode resumed: not throttled, and a manual refresh fires.
        Assert.Null(runner2.NextManualRefreshAllowedAt);
        await runner2.CheckNowAsync();
        await WaitAsync(() => service2.ThoroughCallCount == 1);
    }

    /// <summary>
    /// Fires <paramref name="count"/> manual refreshes, advancing the captured
    /// clock 1 second before each so the timestamps are distinct but all within
    /// the 1-hour window (and within 2 minutes of each other for the cooldown
    /// tests). Returns the last (most recent) timestamp enqueued.
    /// </summary>
    private static DateTimeOffset FireManualBudget(
        UpdateCheckRunner runner, ref DateTimeOffset now, int count)
    {
        DateTimeOffset last = now;
        for (var i = 0; i < count; i++)
        {
            now = now.AddSeconds(1);
            last = now;
            // CheckNowAsync is synchronous up to the thread-pool dispatch; the
            // recording fake lands the call inside Task.Run. The caller awaits
            // the count via WaitAsync after this helper returns.
            _ = runner.CheckNowAsync();
        }
        return last;
    }

    // ---- exception safety --------------------------------------------------

    [Fact]
    public async Task CheckAsync_throwing_is_swallowed_and_runner_survives()
    {
        // The fire-and-forget Task.Run must catch a throwing CheckAsync (the
        // service is documented to self-catch, but the runner wraps it as
        // belt-and-suspenders). The throw must not escape the test thread,
        // and the runner must keep firing on the next switch (handler not dead).
        // Note: the interval gate also covers the switch, so the second fire
        // needs the clock advanced past the interval since the startup fire, so
        // an injected controllable clock is used.
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = firstId };
        var service = new FakeUpdateCheckService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now);

        // Start() and the switch both run on the test thread and must return
        // normally (the throw happens inside the thread-pool task, swallowed).
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        // Advance past the default 10-minute interval so the switch's interval
        // gate opens; without this the gate would suppress the second
        // fire (no call-per-launch on rapid triggers).
        now = now.AddMinutes(11);
        session.ActiveProfileId = secondId; // would throw again; must not escape

        await WaitAsync(() => service.CallCount == 2);

        // Both calls were recorded (recording happens before the throw, and the
        // handler survived the first failure to fire the second).
        Assert.Equal(new[] { firstId, secondId }, service.Calls);
    }

    // ---- last-check persistence -------------------------------------------

    [Fact]
    public async Task RunAsync_persists_last_check_timestamp_on_startup_fire()
    {
        // Every fire (auto + manual) stamps _lastCheckAt AND persists it via
        // IUpdateCheckScheduleState.LastUpdateCheckUtc. Use an injected clock for an exact
        // assertion: the persisted value equals the getNow value at fire time.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now, appState: appState);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        // The stamp is written synchronously in RunAsync BEFORE Task.Run, so it
        // is set immediately after Start() returns the fire.
        Assert.Equal(now, appState.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task RunAsync_persists_last_check_timestamp_on_manual_fire()
    {
        // The manual path also persists the shared last-check so the periodic
        // clock backs off after it and the next auto trigger respects the
        // interval from this manual fire.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore();
        var now = DateTimeOffset.UtcNow;
        var runner = Build(session, service, getNow: () => now, appState: appState);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // startup stamps "now"

        // Advance the clock + manual fire: the persisted timestamp updates to
        // the new getNow value (the manual path persists too).
        now = now.AddMinutes(1);
        await runner.CheckNowAsync();

        Assert.Equal(now, appState.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task Start_seeds_last_check_from_persisted_store_on_first_run()
    {
        // First-run / first-run-after-upgrade: the store reads null (no file or
        // an old file without the field). The runner seeds _lastCheckAt to
        // DateTimeOffset.MinValue, so the interval is treated as long elapsed
        // and the opening startup check fires normally.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var appState = new FakeAppStateStore(); // LastUpdateCheckUtc stays null
        var runner = Build(session, service, appState: appState);

        runner.Start();

        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(id, Assert.Single(service.Calls));
    }

    // ---- the candidate pull (the runner owns profile validity) -------------

    [Fact]
    public async Task Check_maps_the_profile_entries_to_candidates_at_the_call_site()
    {
        // The runner reads the profile's mod list through IProfileService +
        // maps each entry to a ModListCandidate (container id + current
        // policy, policy identity preserved) so Integrations receives the
        // check set without holding a Profiles reference.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = new FakeProfileService();
        var latestContainer = Guid.NewGuid();
        var pinnedContainer = Guid.NewGuid();
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = latestContainer, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = pinnedContainer, Order = 1, Policy = new PinnedPolicy("v") });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var service = new FakeUpdateCheckService();
        var autoUpdate = new FakeAutomaticUpdateService();
        var runner = new UpdateCheckRunner(
            session, profiles, service, new FakeConfigLoader(), new FakeAppStateStore(),
            autoUpdate, NullLogger<UpdateCheckRunner>.Instance);

        await runner.CheckNowAsync();

        Assert.Equal(1, service.ThoroughCallCount);
        var candidates = Assert.Single(service.CandidateCalls);
        Assert.Equal(
            profiles.GetModList(a.Id).Select(e => (e.ContainerId, e.Policy)),
            candidates.Select(c => (c.ContainerId, c.Policy)));
    }

    [Fact]
    public async Task Unreadable_profile_pull_skips_the_check_without_touching_LastResult()
    {
        // A deleted/unreadable profile surfaces at the runner's candidate pull
        // now (Integrations no longer pulls the list itself). The pull failure
        // is logged + the run skipped: no check call, no LastResult mutation,
        // no automatic-update chaining - matching the old fire-and-forget
        // swallow for a deleted profile.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var profiles = new FakeProfileService
        {
            GetModListThrows = new KeyNotFoundException($"No profile {id}"),
        };
        var service = new FakeUpdateCheckService
        {
            // A staged result proves the skip path never mutated it.
            LastResult = null,
        };
        var autoUpdate = new FakeAutomaticUpdateService();
        var runner = new UpdateCheckRunner(
            session, profiles, service, new FakeConfigLoader(), new FakeAppStateStore(),
            autoUpdate, NullLogger<UpdateCheckRunner>.Instance);

        await runner.CheckNowAsync();

        Assert.Equal(0, service.CallCount);
        Assert.Equal(0, service.ThoroughCallCount);
        Assert.Null(service.LastResult);
        Assert.Empty(autoUpdate.Calls);
    }

    [Fact]
    public async Task Empty_profile_pull_still_calls_the_check_with_an_empty_candidate_set()
    {
        // A VALID profile whose mod list is empty is not a failure: the check
        // runs with an empty candidate batch (the service maps that to its
        // NoNexusMods short-circuit), so persisted state reconciles normally.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = new UpdateCheckRunner(
            session, new FakeProfileService(), service, new FakeConfigLoader(),
            new FakeAppStateStore(), new FakeAutomaticUpdateService(),
            NullLogger<UpdateCheckRunner>.Instance);

        await runner.CheckNowAsync();

        Assert.Equal(1, service.ThoroughCallCount);
        Assert.Empty(Assert.Single(service.CandidateCalls));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Builds a runner with the shared fakes + a real <see cref="FakeConfigLoader"/>
    /// + no periodic timer (the default). For startup / switch / manual tests
    /// that do not drive the periodic tick.
    /// </summary>
    private static UpdateCheckRunner Build(
        FakeProfileSession session,
        FakeUpdateCheckService service,
        FakeConfigLoader? configLoader = null,
        Func<DateTimeOffset>? getNow = null,
        FakeAppStateStore? appState = null,
        FakeAutomaticUpdateService? autoUpdate = null)
    {
        configLoader ??= new FakeConfigLoader();
        appState ??= new FakeAppStateStore();
        autoUpdate ??= new FakeAutomaticUpdateService();
        return new UpdateCheckRunner(
            session,
            new FakeProfileService(),
            service,
            configLoader,
            appState,
            autoUpdate,
            NullLogger<UpdateCheckRunner>.Instance,
            startTimer: null,
            getNow: getNow);
    }

    /// <summary>
    /// Builds a runner whose periodic-timer start delegate captures the tick
    /// callback, so the test invokes it directly. Returns the runner + the
    /// captured tick action.
    /// </summary>
    private static (UpdateCheckRunner runner, Action tick) BuildWithCapturedTick(
        FakeProfileSession session,
        FakeUpdateCheckService service,
        FakeConfigLoader? configLoader = null,
        Func<DateTimeOffset>? getNow = null,
        FakeAppStateStore? appState = null,
        FakeAutomaticUpdateService? autoUpdate = null)
    {
        configLoader ??= new FakeConfigLoader();
        appState ??= new FakeAppStateStore();
        autoUpdate ??= new FakeAutomaticUpdateService();
        Action? tick = null;
        var runner = new UpdateCheckRunner(
            session,
            new FakeProfileService(),
            service,
            configLoader,
            appState,
            autoUpdate,
            NullLogger<UpdateCheckRunner>.Instance,
            startTimer: t => tick = t,
            getNow: getNow);
        return (runner, () => tick!.Invoke());
    }

    /// <summary>
    /// Polls <paramref name="condition"/> on a short delay until it returns
    /// <c>true</c> or a 2s timeout elapses. The runner fires its check on a
    /// thread-pool task, so tests must wait for that task to actually invoke
    /// the fake service before asserting. Asserts the condition held at the
    /// end so a timeout surfaces as a deterministic failure rather than a
    /// stale-pass.
    /// </summary>
    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!condition())
            {
                await Task.Delay(10, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout elapsed; fall through to the assertion below.
        }

        Assert.True(condition(), "Timed out waiting for the runner to fire the update check.");
    }
}
