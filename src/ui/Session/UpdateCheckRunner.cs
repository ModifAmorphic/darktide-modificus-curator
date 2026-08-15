using System.ComponentModel;
using System.Linq;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The UI-layer glue between <see cref="IProfileSession"/> (the active-profile
/// authority) and <see cref="IUpdateCheckService"/> (the Integrations update
/// check). Owns the candidate pull: on every fire it reads the profile's
/// mod list through <see cref="IProfileService"/> + maps the entries to
/// <see cref="ModListCandidate"/> at the call site, so Integrations holds no
/// Profiles dependency and the runner is the one place a profile-validity
/// problem surfaces (a pull failure is logged + skipped, not a failed check).
/// Fires a background update check on three triggers: (1) startup, when
/// the session restores the persisted active id; (2) an active-profile switch;
/// and (3) a periodic timer (every <c>AutoUpdateCheckIntervalMinutes</c> when
/// <c>AutoUpdateCheckEnabled</c> is on). All three fire the Month-only
/// <see cref="IUpdateCheckService.CheckAsync"/> (cheap, one API call) +
/// discard the task. A fourth trigger, the manual "check now" affordance on
/// the mod list, routes through <see cref="CheckNowAsync"/> + fires the thorough
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the per-mod pass that
/// also catches mods outside the Month window); its <see cref="Task"/> is
/// awaitable so the list VM can drive an <c>IsCheckingNow</c> affordance while
/// it runs. This class never blocks on a check beyond the await the manual
/// trigger opts into, never surfaces its result, and never lets an unobserved
/// exception escape the thread-pool task.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interval gate covers every automatic trigger.</b> Startup,
/// active-profile switch, and the periodic timer all gate on
/// <see cref="IntervalElapsed"/>: a check fires only when the configured
/// interval (clamped to the compliance floor) has elapsed since the last fire.
/// This prevents a rapid open/close loop from burning an API call per launch
/// and prevents rapid profile switching from burning calls in a session. The
/// <c>AutoUpdateCheckEnabled</c> toggle is ADDITIONAL and gates ONLY the
/// periodic timer (startup + switch fire regardless of the toggle, subject to
/// the interval gate); the toggle is read live on each tick so a runtime change
/// in the Integrations dialog takes effect without a restart. The manual
/// <see cref="CheckNowAsync"/> bypasses the interval gate entirely (user
/// initiated); it still stamps the shared last-check so the periodic clock
/// backs off after it. The manual path also carries its own separate throttle
/// (a sliding window, see below) so a rapid-click loop on the refresh button
/// cannot burn the Nexus budget on its own.</para>
/// <para>
/// <b>The manual sliding-window throttle.</b> Independent of the interval gate,
/// the manual "check now" path tracks its own rolling 1-hour window of
/// successful refresh timestamps: the first <see cref="FreeManualRefreshLimit"/>
/// (10) manual refreshes in the window fire freely (no cooldown); once those are
/// spent the path throttles to one per <see cref="ManualRefreshThrottleInterval"/>
/// (2 minutes) until enough timestamps age out of the window for free mode to
/// resume. As timestamps age past 1 hour they drop out, the count falls under
/// the limit, and free mode resumes (automatic, via the prune step). A blocked
/// attempt consumes nothing: no API call, no timestamp stamp, no persistence
/// change. The window PERSISTS across restarts via
/// <see cref="IUpdateCheckScheduleState.ManualRefreshTimestamps"/> (seeded at
/// <see cref="Start"/>, written back on every successful fire), so closing and
/// reopening the app does not reset the free-refresh budget. Worst case for a
/// determined user: 10 free + 30 throttled = 40 manual calls/hour, plus ~12
/// automatic calls at the 5-minute floor = ~52/hour, about 10.4% of the 500/hour
/// Nexus budget.</para>
/// <para>
/// <b>Last-check persisted across restarts.</b> The shared last-check timestamp
/// is seeded from <see cref="IUpdateCheckScheduleState.LastUpdateCheckUtc"/> at
/// <see cref="Start"/> and stamped back to it on every fire (auto + manual). So
/// the interval gate survives a close/reopen: a check that fired moments ago in
/// a prior session suppresses this session's opening check. The write is
/// best-effort (the store swallows failures); a missing or old state file seeds
/// <c>null</c>, the runner treats that as <see cref="DateTimeOffset.MinValue"/>,
/// and the opening check fires normally.</para>
/// <para>
/// <b>Interval honored to the tick granularity.</b> The timer ticks at a fixed
/// fine interval (<see cref="TickInterval"/>, 1 minute) and fires a check when
/// <c>AutoUpdateCheckIntervalMinutes</c> has elapsed since the last check. This
/// decouples the timer period (fixed) from the user-configured interval (live),
/// so changing the interval in the dialog takes effect on the next tick rather
/// than requiring a restart. The last-check timestamp is shared across every
/// trigger, so a manual or profile-load check resets the periodic clock too
/// (no double-fire right after a switch).</para>
/// <para>
/// <b>Fire-and-forget by design, except the manual trigger's await.</b> The
/// periodic + profile-load checks run on a thread-pool task; this class does
/// not await them. The manual trigger's <see cref="Task"/> IS awaited by the
/// list VM's <c>CheckForUpdatesNow</c> command, but only so the command can
/// toggle <c>IsCheckingNow</c> off in its finally block; the check itself still
/// runs on a thread-pool task + never blocks the UI thread. Either way, the
/// mod-list view model reads <see cref="IUpdateCheckService.LastResult"/> +
/// subscribes to <see cref="IUpdateCheckService.CheckCompleted"/> to render
/// badges without awaiting.</para>
/// <para>
/// <b>Belt-and-suspenders exception handling.</b>
/// <see cref="IUpdateCheckService.CheckAsync"/> /
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> are documented to
/// swallow their own non-cancellation failures and surface them as an empty /
/// partial <see cref="UpdateCheckResult"/>. But a fire-and-forget
/// <see cref="Task"/> must never leak an unobserved exception, so the run
/// wrapper catches regardless. <see cref="OperationCanceledException"/> is
/// expected on shutdown (not an error); anything else is logged + swallowed.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <c>NxmModDownloadHandler</c>: the
/// glue observes a UI-layer singleton (<see cref="IProfileSession"/> lives in
/// UI) and drives an Integrations service, so it belongs on the consumer side
/// of that boundary. The composition root registers this as a singleton +
/// calls <see cref="Start"/> once after the provider is built (best-effort: a
/// wiring failure is logged + swallowed, never blocks startup).</para>
/// <para>
/// <b>Testability:</b> the periodic timer is injected as a
/// <paramref name="startTimer"/> delegate (mirrors <see cref="ProfileSession"/>),
/// and the clock is injected as <paramref name="getNow"/> so tests drive time
/// deterministically. Production wires both (a <c>DispatcherTimer</c> +
/// <see cref="DateTimeOffset.UtcNow"/>); tests pass null + a controllable clock
/// and invoke the captured tick directly.</para>
/// </remarks>
public sealed class UpdateCheckRunner
{
    /// <summary>
    /// The periodic timer's fixed tick granularity. The user-configured interval
    /// (<c>AutoUpdateCheckIntervalMinutes</c>) is honored to this granularity:
    /// the runner fires when that much time has elapsed since the last check,
    /// checked on each tick. Five minutes is the finest interval the user can
    /// configure (the compliance floor,
    /// <see cref="NexusConfig.MinAutoUpdateCheckIntervalMinutes"/>), so a 1-minute
    /// tick resolves it exactly without burning cycles on sub-minute polling.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IProfileSession _session;
    private readonly IProfileService _profiles;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IConfigLoader _configLoader;
    private readonly IUpdateCheckScheduleState _appState;
    private readonly IAutomaticUpdateService _autoUpdate;
    private readonly ILogger<UpdateCheckRunner> _logger;
    private readonly Action<Action>? _startTimer;
    private readonly Func<DateTimeOffset> _getNow;

    // The last time any check fired (startup, switch, periodic, or manual).
    // Seeded from the persisted IUpdateCheckScheduleState.LastUpdateCheckUtc at Start();
    // stamped in-memory + persisted on every fire (auto + manual). Written +
    // read on the UI thread only (the timer tick, session property changes, and
    // CheckNow all run on the UI thread; RunAsync assigns this before
    // dispatching the thread-pool task), so no synchronization.
    private DateTimeOffset _lastCheckAt = DateTimeOffset.MinValue;

    // ---- the manual sliding-window throttle (CheckNowAsync's own gate) ------

    /// <summary>
    /// The free manual-refresh budget per rolling hour. The first this many
    /// manual "check now" refreshes in <see cref="ManualRefreshWindow"/> fire
    /// immediately (no cooldown); once spent the path drops to one per
    /// <see cref="ManualRefreshThrottleInterval"/> until the window slides.
    /// </summary>
    private const int FreeManualRefreshLimit = 10;

    /// <summary>
    /// The sliding window over which the free manual-refresh budget is counted.
    /// Timestamps older than this are pruned, so the window slides and free mode
    /// resumes once enough entries age out.
    /// </summary>
    private static readonly TimeSpan ManualRefreshWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// The minimum interval between manual refreshes once the free budget is
    /// exhausted: one per this interval until the window slides enough for free
    /// mode to resume.
    /// </summary>
    private static readonly TimeSpan ManualRefreshThrottleInterval = TimeSpan.FromMinutes(2);

    // Successful manual-refresh timestamps within the rolling 1-hour window.
    // Enqueued in CheckNowAsync when a manual refresh is allowed; pruned from
    // the front on every access (NextManualRefreshAllowedAt / CanRefreshManually)
    // as they age past ManualRefreshWindow. UI-thread only: CheckNowAsync and
    // the property getter run on the UI thread (the button click routes through
    // the list VM's CheckForUpdatesNow, and the VM's countdown reads the
    // property), so no synchronization. PERSISTS across restarts via
    // IUpdateCheckScheduleState.ManualRefreshTimestamps: seeded from the store at Start()
    // (the first access prunes entries aged past the 1-hour window), and written
    // back on every successful manual fire. SEPARATE from the shared
    // _lastCheckAt / IUpdateCheckScheduleState.LastUpdateCheckUtc interval-gate timestamp:
    // a successful manual fire stamps BOTH (the queue + the shared timestamp via
    // RunAsync); a blocked attempt stamps NEITHER.
    private readonly Queue<DateTimeOffset> _manualRefreshTimes = new();

    /// <param name="session">The active-profile authority (fires on load + switch).</param>
    /// <param name="profiles">The profile service the candidate pull reads the
    /// profile's mod list through (mapped to
    /// <see cref="ModListCandidate"/> at the call site; a pull failure is
    /// logged + skipped, never surfaced as a failed check).</param>
    /// <param name="updateCheck">The Integrations update check entry point.</param>
    /// <param name="configLoader">Read live for the periodic toggle + the interval gate.</param>
    /// <param name="appState">Persists
    /// <see cref="IUpdateCheckScheduleState.LastUpdateCheckUtc"/> (the interval-gate
    /// timestamp) and <see cref="IUpdateCheckScheduleState.ManualRefreshTimestamps"/>
    /// (the manual throttle's sliding window) across restarts.</param>
    /// <param name="autoUpdate">The opt-in Premium automatic-update installer,
    /// chained after each check completes (the runner captures the exact result
    /// + awaits the install batch so a manual CheckNow keeps its spinner active
    /// through the installations).</param>
    /// <param name="logger">Structured logger for swallowed exceptions.</param>
    /// <param name="startTimer">Starts the periodic tick. Production wires this
    /// to a <c>DispatcherTimer</c> (UI thread); tests pass null and invoke the
    /// captured tick directly.</param>
    /// <param name="getNow">The clock, for testable interval math. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>; tests inject a controllable clock.</param>
    public UpdateCheckRunner(
        IProfileSession session,
        IProfileService profiles,
        IUpdateCheckService updateCheck,
        IConfigLoader configLoader,
        IUpdateCheckScheduleState appState,
        IAutomaticUpdateService autoUpdate,
        ILogger<UpdateCheckRunner> logger,
        Action<Action>? startTimer = null,
        Func<DateTimeOffset>? getNow = null,
        Action<Action>? invokeOnUi = null,
        Action<Action>? startCountdownTimer = null,
        Action? stopCountdownTimer = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _updateCheck = updateCheck ?? throw new ArgumentNullException(nameof(updateCheck));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _autoUpdate = autoUpdate ?? throw new ArgumentNullException(nameof(autoUpdate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startTimer = startTimer;
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
        RefreshGate = new UpdateRefreshGate(
            this, invokeOnUi, startCountdownTimer, stopCountdownTimer, _getNow);
    }

    /// <summary>
    /// The refresh-gate policy for the manual "check now" affordance: the
    /// rate-limit tracking (fed by every check result this runner captures),
    /// the manual-throttle read, the effective-reset computation, the shared
    /// 1-second countdown timer, and the IsRateLimitActive / IsManualThrottled
    /// / IsRefreshEnabled decisions. The list VM renders this state; it does
    /// not compute it.
    /// </summary>
    public UpdateRefreshGate RefreshGate { get; }

    /// <summary>
    /// Subscribes to the session's active-profile changes, starts the periodic
    /// tick, seeds the last-check timestamp and the manual throttle's sliding
    /// window from the persisted store, and fires an opening check if a profile
    /// was already restored at startup AND the configured interval has elapsed.
    /// Called once from the composition root after the provider is built
    /// (best-effort: failures are logged + swallowed by the caller, never
    /// blocking app startup).
    /// </summary>
    /// <remarks>
    /// <see cref="IProfileSession.ActiveProfileId"/> is restored in the
    /// session's constructor (before this runner starts), so there is no
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> event for the
    /// restore itself. The opening check is fired explicitly here when an id is
    /// already present AND the interval gate is open; subsequent switches flow
    /// through <see cref="OnActiveProfileChanged"/>.</remarks>
    public void Start()
    {
        // Seed the shared last-check from the persisted store FIRST, before the
        // timer starts, so the interval gate survives a close/reopen (a check
        // that fired moments ago in a prior session suppresses this session's
        // opening check) and any timer implementation, even one that ticks
        // synchronously on start, sees the persisted value rather than the field
        // initializer. A missing / old / corrupt state file reads null, which
        // floors to MinValue, so the first run after upgrade (or a brand-new
        // install) fires normally.
        _lastCheckAt = _appState.LastUpdateCheckUtc ?? DateTimeOffset.MinValue;

        // Seed the manual throttle's sliding window from the persisted store so
        // a close/reopen cannot reset the free-refresh budget. The first access
        // (NextManualRefreshAllowedAt / CanRefreshManually) prunes entries aged
        // past the 1-hour window, so a restart longer than an hour yields an
        // empty window automatically (free mode resumes). A missing / old /
        // corrupt state file reads null, which yields an empty queue (no
        // throttle history).
        if (_appState.ManualRefreshTimestamps is { } seed)
        {
            foreach (var ts in seed)
            {
                _manualRefreshTimes.Enqueue(ts);
            }
        }

        _session.PropertyChanged += OnActiveProfileChanged;
        _startTimer?.Invoke(OnTimerTick);

        // Startup-with-last-profile: the session restores the persisted active
        // id in its constructor, before this runner starts. Fire the opening
        // check explicitly (subject to the interval gate) so the
        // restored profile gets checked when enough time has elapsed.
        if (_session.ActiveProfileId is Guid id && IntervalElapsed())
        {
            FireAndForget(id);
        }
    }

    /// <summary>
    /// The manual "check now" trigger (the mod-list header refresh button). Fires
    /// an immediate <em>thorough</em> check for the active profile (the per-mod
    /// pass that also catches mods outside the Month window), awaitable so the
    /// caller (the list VM's <c>CheckForUpdatesNow</c> command) can drive an
    /// <c>IsCheckingNow</c> affordance while it runs. No-op (returns
    /// <see cref="Task.CompletedTask"/>) when no profile is active.
    /// <b>Bypasses the interval gate</b> (user-initiated), but carries its own
    /// separate throttle (the manual sliding window, see the class remarks) and
    /// still stamps the shared last-check + persists it so the periodic clock
    /// backs off after it and the next auto trigger respects the configured
    /// interval from this check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manual sliding-window throttle gates this path on top of (independent
    /// of) the interval gate: the first <see cref="FreeManualRefreshLimit"/> (10)
    /// manual refreshes in the rolling 1-hour window fire freely; once spent, the
    /// path throttles to one per <see cref="ManualRefreshThrottleInterval"/>
    /// (2 minutes). A throttled attempt is a silent no-op: it returns
    /// <see cref="Task.CompletedTask"/> WITHOUT firing, WITHOUT recording a
    /// timestamp, and WITHOUT stamping <c>_lastCheckAt</c> / persisting. The VM
    /// reads <see cref="NextManualRefreshAllowedAt"/> after the await to drive the
    /// countdown tooltip + the button's disabled state.</para>
    /// </remarks>
    /// <returns>A <see cref="Task"/> that completes when the thorough check
    /// finishes (success or failure), or <see cref="Task.CompletedTask"/> when
    /// throttled or no profile is active. Never faults: the inner try/catch
    /// swallows the check's exceptions (mirrors the fire-and-forget path's
    /// exception safety). The caller awaits it to drive an
    /// <c>IsCheckingNow</c> spinner; it carries no result (the result lands via
    /// <see cref="IUpdateCheckService.CheckCompleted"/>).</returns>
    public Task CheckNowAsync()
    {
        if (_session.ActiveProfileId is not Guid id)
        {
            return Task.CompletedTask;
        }

        var now = _getNow();
        if (!CanRefreshManually(now))
        {
            // Throttled: a blocked attempt consumes nothing (no API call, no
            // timestamp stamp, no persistence change). Re-evaluate the refresh
            // gate so the countdown timer engages + listeners re-read the
            // throttled state.
            RefreshGate.Reevaluate();
            return Task.CompletedTask;
        }

        // Allowed: record the timestamp into the sliding window, persist the
        // updated window, then proceed to the existing thorough path (which
        // stamps the shared _lastCheckAt + persists it). The two timestamps are
        // separate by design. The snapshot is taken at enqueue time (the queue
        // is already pruned, because CanRefreshManually pruned it first), so it
        // holds exactly the live window. Best-effort (the store swallows write
        // failures), same as the LastUpdateCheckUtc write.
        _manualRefreshTimes.Enqueue(now);
        _appState.ManualRefreshTimestamps = _manualRefreshTimes.ToArray();
        return RunAsync(id, thorough: true);
    }

    /// <summary>
    /// The absolute instant the next manual refresh becomes allowed, or
    /// <c>null</c> when the manual path is not throttled (under the free limit,
    /// or the 2-minute cooldown has elapsed). Computed via the injected
    /// <c>_getNow</c> clock so it is deterministic in tests. The single source of
    /// truth for both the refresh button's enable state and the countdown
    /// tooltip: the list VM reads this on every manual attempt (after the await)
    /// and on each 1-second countdown tick.
    /// </summary>
    /// <remarks>
    /// Prunes the window first (so aged timestamps drop out + the count reflects
    /// the current window), then applies the free/throttled decision. A
    /// non-null result is strictly in the future; the cooldown having elapsed
    /// returns null (allowed now).</remarks>
    public DateTimeOffset? NextManualRefreshAllowedAt
    {
        get
        {
            var now = _getNow();
            PruneManualRefreshTimes(now);
            if (_manualRefreshTimes.Count < FreeManualRefreshLimit)
            {
                return null;
            }
            var nextAllowed = _manualRefreshTimes.Last() + ManualRefreshThrottleInterval;
            return now >= nextAllowed ? null : nextAllowed;
        }
    }

    /// <summary>
    /// True when a manual refresh is allowed right now: under the free limit, or
    /// (once spent) the 2-minute cooldown since the most recent manual refresh
    /// has elapsed. Prunes the window first so the decision reflects the current
    /// count.
    /// </summary>
    private bool CanRefreshManually(DateTimeOffset now)
    {
        PruneManualRefreshTimes(now);
        if (_manualRefreshTimes.Count < FreeManualRefreshLimit)
        {
            return true;
        }
        return now >= _manualRefreshTimes.Last() + ManualRefreshThrottleInterval;
    }

    /// <summary>
    /// Dequeues manual-refresh timestamps older than the 1-hour window. The
    /// clock is monotonic so the queue stays chronological; this drops aged
    /// entries from the front so the window slides and free mode resumes once
    /// enough entries age out.
    /// </summary>
    private void PruneManualRefreshTimes(DateTimeOffset now)
    {
        var cutoff = now - ManualRefreshWindow;
        while (_manualRefreshTimes.Count > 0 && _manualRefreshTimes.Peek() < cutoff)
        {
            _manualRefreshTimes.Dequeue();
        }
    }

    /// <summary>
    /// The periodic tick. Reads the toggle live (the toggle gates ONLY the
    /// periodic timer; startup + switch are toggle-independent), gates on an
    /// active profile, and fires a check only when the configured interval has
    /// elapsed since the last check (the shared <see cref="IntervalElapsed"/>
    /// gate, which startup, switch, and the periodic timer all use). Skipped entirely (no
    /// check, no clock reset) when the toggle is off, no profile is active, or
    /// the interval has not elapsed.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread (production wires a <c>DispatcherTimer</c>); tests
    /// invoke the captured tick directly. The config read is cheap (a tiny JSON
    /// file re-read each tick, the established live-read pattern).</remarks>
    private void OnTimerTick()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        if (!nexus.AutoUpdateCheckEnabled)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid id)
        {
            return;
        }

        if (!IntervalElapsed(nexus))
        {
            return;
        }

        FireAndForget(id);
    }

    /// <summary>
    /// The <see cref="IProfileSession.PropertyChanged"/> handler. Fires a check
    /// only for an <see cref="IProfileSession.ActiveProfileId"/> change to a
    /// non-null id AND when the interval gate is open; ignores every
    /// other property (notably <see cref="IProfileSession.IsRunning"/>, which
    /// the polling timer drives every few seconds). Toggle-independent: the
    /// <c>AutoUpdateCheckEnabled</c> toggle gates only the periodic timer, so a
    /// switch fires regardless of the toggle when the interval has elapsed.
    /// </summary>
    private void OnActiveProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The polling timer raises IsRunning changes every few seconds; those
        // carry no profile-load signal. Only an ActiveProfileId change is a
        // load, and only when the new id is non-null (a null id means the
        // active profile was cleared by delete-of-active).
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId))
        {
            return;
        }

        if (_session.ActiveProfileId is Guid newId && IntervalElapsed())
        {
            FireAndForget(newId);
        }
    }

    /// <summary>
    /// True when the configured interval (clamped to the compliance floor
    /// <see cref="NexusConfig.MinAutoUpdateCheckIntervalMinutes"/>) has elapsed
    /// since the last check fired. Read live from config so a runtime change in
    /// the Integrations dialog takes effect without a restart. Shared across
    /// startup, switch, AND the periodic timer so the three automatic triggers
    /// honor one gate (the manual <see cref="CheckNowAsync"/> bypasses it).
    /// </summary>
    /// <param name="nexus">When the caller already read the live Nexus config
    /// (<see cref="OnTimerTick"/>), pass it to avoid a second read; otherwise
    /// the helper reads it itself.</param>
    private bool IntervalElapsed(NexusConfig? nexus = null)
    {
        nexus ??= _configLoader.Load().Integrations.Nexus;
        var intervalMinutes = Math.Max(
            NexusConfig.MinAutoUpdateCheckIntervalMinutes,
            nexus.AutoUpdateCheckIntervalMinutes);
        return _getNow() - _lastCheckAt >= TimeSpan.FromMinutes(intervalMinutes);
    }

    /// <summary>
    /// Fires <see cref="IUpdateCheckService.CheckAsync"/> (Month-only) on a
    /// thread-pool task and discards the returned <see cref="Task"/>. Used by
    /// the startup + switch + periodic triggers (the cheap path). Never blocks
    /// the caller; never leaks an unobserved exception. Also stamps the shared
    /// <c>_lastCheckAt</c> (+ persists it) so the interval gate
    /// backs off after this fire.
    /// </summary>
    private void FireAndForget(Guid profileId) => _ = RunAsync(profileId, thorough: false);

    /// <summary>
    /// Runs a check on a thread-pool task. <paramref name="thorough"/> selects
    /// between the Month-only <see cref="IUpdateCheckService.CheckAsync"/> (the
    /// cheap path, used by the periodic + profile-load triggers) and the
    /// per-mod <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the
    /// thorough path, used by the manual "check now" affordance). The returned
    /// <see cref="Task"/> completes when the check finishes; the fire-and-forget
    /// caller discards it, the manual trigger awaits it to drive its
    /// <c>IsCheckingNow</c> affordance. Never faults: the inner try/catch
    /// swallows the check's exceptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The candidate pull runs here.</b> The profile's mod list is read
    /// through <see cref="IProfileService"/> inside the thread-pool task (a
    /// disk read must not land on the UI thread) + mapped to candidates. The
    /// runner owns profile validity now: a pull failure (e.g. the profile was
    /// deleted between the trigger + the read) is logged + the whole run is
    /// skipped, matching the old fire-and-forget swallow, so
    /// <see cref="IUpdateCheckService.LastResult"/> is never mutated by a
    /// profile-validity problem.</para>
    /// <para>
    /// <b>Capture the exact result.</b> The check's return value is captured
    /// directly (not re-read from <see cref="IUpdateCheckService.LastResult"/>,
    /// which a concurrent check could have replaced between the call + the
    /// read). That captured result is what the automatic-update service gates on
    /// + installs from.</para>
    /// <para>
    /// <b>Chain the automatic-update service on the UI context.</b> The
    /// thread-pool task returns the result; the outer await (no
    /// <c>ConfigureAwait(false)</c>, the UI-layer convention) resumes on the
    /// captured UI context, and the service is invoked there. So the manual
    /// CheckNow keeps its spinner active through the installations (the VM
    /// awaits this task), and the automatic triggers' check + install form one
    /// ordered task (asynchronous + non-blocking to the UI, but sequential
    /// within the run). The service gates itself (outcome, updates, setting,
    /// profile, fresh Premium); a no-op service call is cheap.</para>
    /// <para>
    /// <b>Belt-and-suspenders exception handling.</b>
    /// <see cref="IUpdateCheckService.CheckAsync"/> /
    /// <see cref="IUpdateCheckService.CheckThoroughAsync"/> are documented to
    /// catch their own non-cancellation failures and return an empty / partial
    /// <see cref="UpdateCheckResult"/>. The outer try/catch here is
    /// belt-and-suspenders: a fire-and-forget <see cref="Task"/> whose only
    /// awaited operation throws must not surface that as an unobserved
    /// exception. <see cref="OperationCanceledException"/> is swallowed silently
    /// (it would fire on shutdown if a cancellation token were wired through).
    /// Any other exception is logged. The check is never retried here; the next
    /// trigger fires the next check.</para>
    /// </remarks>
    private async Task RunAsync(Guid profileId, bool thorough)
    {
        // Shared across every trigger: the periodic timer, startup, and
        // switch all compute the elapsed-since-last-check from this stamp. Set BEFORE dispatching so a concurrent tick or switch cannot
        // double-fire between this assignment and the thread-pool task's actual
        // start. Persisted best-effort so the gate survives a close/reopen.
        _lastCheckAt = _getNow();
        _appState.LastUpdateCheckUtc = _lastCheckAt;

        UpdateCheckResult? result = null;
        try
        {
            // The check (including the candidate pull: a disk read that belongs
            // on the thread-pool task) runs in the background. The INNER awaits
            // use ConfigureAwait(false) (explicit background-task code, the
            // narrow documented exception). The OUTER await does NOT, so the
            // continuation resumes on the captured UI context (the UI-layer
            // convention) before invoking the automatic-update service below.
            result = await Task.Run(async () =>
            {
                IReadOnlyList<ModListCandidate> candidates;
                try
                {
                    candidates = _profiles.GetModList(profileId).ToCandidates();
                }
                catch (Exception ex)
                {
                    // The profile could not be read (deleted between the
                    // trigger + this read, or a disk problem). Skip the whole
                    // run: no check call, no LastResult mutation. Matches the
                    // old fire-and-forget swallow for a deleted profile.
                    _logger.LogInformation(ex,
                        "Update check for profile {Profile} skipped: the candidate pull failed.",
                        profileId);
                    return (UpdateCheckResult?)null;
                }

                if (thorough)
                {
                    return await _updateCheck.CheckThoroughAsync(profileId, candidates).ConfigureAwait(false);
                }
                return await _updateCheck.CheckAsync(profileId, candidates).ConfigureAwait(false);
            });
        }
        catch (OperationCanceledException)
        {
            // Defensive only: no cancellation token is wired today (the
            // runner uses the default token), so this does not fire in
            // production. Swallowed silently regardless.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Update check for profile {Profile} threw unexpectedly.", profileId);
            return;
        }

        // The thread-pool task returned the result. The outer await resumes on
        // the captured UI context (no ConfigureAwait(false) on it, per the
        // UI-layer convention). Feed the refresh gate first (every check
        // result flows through it: the rate-limit tracking + the coupled
        // button/pill state update even while installs run), then invoke the
        // automatic-update service here, on the UI context, so its dialog +
        // event callbacks land on the UI thread + the manual CheckNow (which
        // awaits this task) keeps its spinner active through the installations.
        // The service gates itself; a no-op call is cheap, so no need to
        // pre-filter here.
        if (result is not null)
        {
            RefreshGate.ApplyResult(result);
            try
            {
                await _autoUpdate.RunAfterCheckAsync(result, profileId);
            }
            catch (OperationCanceledException)
            {
                // Cancellation from the automatic batch (shutdown). Swallowed.
            }
            catch (Exception ex)
            {
                // The service isolates per-mod failures + surfaces an aggregated
                // alert itself; this catch is belt-and-suspenders so a bug in the
                // service never leaks an unobserved exception through the runner.
                _logger.LogError(ex,
                    "Automatic update batch for profile {Profile} threw unexpectedly.", profileId);
            }
        }
    }
}
