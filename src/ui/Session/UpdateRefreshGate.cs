using Modificus.Curator.Integrations;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The refresh-gate policy for the mod list's manual "check now" affordance:
/// whether the refresh button is blocked, by which cause, and until when. Owns
/// the rate-limit tracking (fed by <see cref="UpdateCheckRunner"/>: every check
/// result flows through <see cref="ApplyResult"/>), the effective-reset
/// computation (the server-reported reset, or a client-side fallback when
/// Nexus stayed silent), the manual-throttle read (the runner's
/// <see cref="UpdateCheckRunner.NextManualRefreshAllowedAt"/>), and the shared
/// 1-second countdown timer lifecycle. Owned + exposed by the runner
/// (<see cref="UpdateCheckRunner.RefreshGate"/>); the list VM renders the
/// state, it does not compute it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two blocking causes, one timer.</b> The button is blocked while EITHER
/// an active rate limit (until its effective reset elapses) OR the manual
/// sliding-window throttle (until the runner's next-allowed instant) holds.
/// The countdown timer runs while either has an unelapsed deadline (so the
/// rate-limit pill clears the instant its reset passes, even mid-throttle) and
/// stops when neither does.</para>
/// <para>
/// <b>Pull-based state change.</b> <see cref="StateChanged"/> carries nothing;
/// readers re-pull the state. The event is marshaled to the UI thread through
/// the injected <see cref="Action{Action}"/> seam (a pass-through by default,
/// so tests run synchronously).</para>
/// <para>
/// <b>Timer seams.</b> The start/stop delegates mirror the runner's timer
/// pattern: production wires a lazily created 1-second <c>DispatcherTimer</c>
/// (the start delegate is idempotent); tests pass null and invoke the captured
/// tick directly.</para>
/// </remarks>
public sealed class UpdateRefreshGate
{
    /// <summary>
    /// The client-side cooldown applied when a rate-limited check did not carry
    /// a server-reported reset (e.g. an HTTP 429 with no <c>x-rl-*</c> headers).
    /// Measured from the result's <see cref="UpdateCheckResult.CheckedAt"/> so
    /// the refresh button re-enables on a reasonable schedule even when Nexus
    /// stays silent about when the window refills.
    /// </summary>
    public static readonly TimeSpan RateLimitFallbackCooldown = TimeSpan.FromMinutes(1);

    private readonly UpdateCheckRunner _runner;
    private readonly Action<Action> _invokeOnUi;
    private readonly Action<Action>? _startTimer;
    private readonly Action? _stopTimer;
    private readonly Func<DateTimeOffset> _getNow;

    private DateTimeOffset? _rateLimitCheckedAt;

    /// <param name="runner">The owning runner (the manual-throttle read +
    /// the result feed).</param>
    /// <param name="invokeOnUi">The UI-thread marshal seam for
    /// <see cref="StateChanged"/>. Defaults to a pass-through (tests).</param>
    /// <param name="startTimer">Starts the 1-second countdown tick. Production
    /// wires a lazily created <c>DispatcherTimer</c>; tests pass null and
    /// invoke the captured tick directly.</param>
    /// <param name="stopTimer">Stops the countdown timer.</param>
    /// <param name="getNow">The clock backing the rate-limit-active decision.
    /// Defaults to <see cref="DateTimeOffset.UtcNow"/>; the runner shares its
    /// own injected clock so tests drive both deterministically.</param>
    public UpdateRefreshGate(
        UpdateCheckRunner runner,
        Action<Action>? invokeOnUi = null,
        Action<Action>? startTimer = null,
        Action? stopTimer = null,
        Func<DateTimeOffset>? getNow = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _invokeOnUi = invokeOnUi ?? (static action => action());
        _startTimer = startTimer;
        _stopTimer = stopTimer;
        _getNow = getNow ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Raised (marshaled to the UI thread) whenever a re-evaluation may have
    /// changed the gate's state: after every applied check result, after a
    /// blocked manual attempt, and on each countdown tick while a cause is
    /// active. Carries nothing; readers pull the state.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Whether the last applied result was rate-limited. Drives the header
    /// rate-limit notice (the "check incomplete" indicator). Stays <c>true</c>
    /// until a later non-rate-limited result lands; the coupled
    /// <see cref="IsRateLimitActive"/> flag (and the pill's visibility) is what
    /// flips back when the reset elapses.
    /// </summary>
    public bool IsRateLimited { get; private set; }

    /// <summary>
    /// The server-reported reset of the exhausted window from the last
    /// rate-limited result (UTC), or <c>null</c> when the server gave none.
    /// Paired with the result's checked-at stamp (see
    /// <see cref="EffectiveRateLimitReset"/>) so the active flag can fall back
    /// to <see cref="RateLimitFallbackCooldown"/> when this is null.
    /// </summary>
    public DateTimeOffset? RateLimitResetsAt { get; private set; }

    /// <summary>
    /// Whether the refresh button is currently blocked by an active rate limit:
    /// <c>true</c> when the last result was rate-limited AND the effective
    /// reset (the server-reported one, or checked-at + the fallback cooldown
    /// when the server was silent) has not yet elapsed. The pill shows exactly
    /// while this holds, and both clear together when the reset passes.
    /// </summary>
    public bool IsRateLimitActive { get; private set; }

    /// <summary>
    /// Whether the manual sliding-window throttle is currently blocking the
    /// refresh button (the runner's free 10/hour budget is spent + the
    /// 2-minute cooldown has not elapsed).
    /// </summary>
    public bool IsManualThrottled { get; private set; }

    /// <summary>
    /// The absolute instant the manual throttle clears (the runner's
    /// <see cref="UpdateCheckRunner.NextManualRefreshAllowedAt"/>), or
    /// <c>null</c> when not throttled. Rendering input for the countdown
    /// tooltip.
    /// </summary>
    public DateTimeOffset? ManualThrottleClearsAt => _runner.NextManualRefreshAllowedAt;

    /// <summary>
    /// Whether the refresh button is enabled for gate reasons: NOT while an
    /// active rate limit holds and NOT while the manual throttle blocks. (An
    /// in-flight check is the caller's own affordance state, composed on top:
    /// the VM's IsRefreshEnabled also excludes IsCheckingNow.)
    /// </summary>
    public bool IsRefreshEnabled => !IsRateLimitActive && !IsManualThrottled;

    /// <summary>
    /// Feeds a check result into the rate-limit tracking (the runner calls
    /// this for every result it captures) + immediately re-evaluates. A null
    /// result (a swallowed failure) changes nothing.
    /// </summary>
    public void ApplyResult(UpdateCheckResult? result)
    {
        if (result is null)
        {
            return;
        }

        IsRateLimited = result.RateLimited;
        RateLimitResetsAt = result.RateLimitResetsAt;
        _rateLimitCheckedAt = result.CheckedAt;
        Reevaluate();
    }

    /// <summary>
    /// Recomputes the two blocking causes against the clock + the runner's
    /// throttle, manages the shared countdown timer (run while either cause
    /// has an unelapsed deadline, stop when neither does), and raises
    /// <see cref="StateChanged"/> (marshaled to the UI thread). Called after
    /// every applied result, after a blocked manual attempt, and on each
    /// countdown tick.
    /// </summary>
    public void Reevaluate()
    {
        // (1) Rate-limit active: the last result was rate-limited AND the
        //     effective reset (server-reported, or CheckedAt + the fallback
        //     cooldown when the server was silent) has not elapsed.
        var effectiveReset = EffectiveRateLimitReset;
        IsRateLimitActive = IsRateLimited
            && effectiveReset is { } reset
            && _getNow() < reset;

        // (2) Manual sliding-window throttle: the runner reports the next
        //     allowed manual fire, or null once the cooldown has elapsed.
        IsManualThrottled = ManualThrottleClearsAt is not null;

        // (3) Shared timer: run while either cause has an unelapsed deadline,
        //     stop when neither does.
        if (IsRateLimitActive || IsManualThrottled)
        {
            _startTimer?.Invoke(Reevaluate);
        }
        else
        {
            _stopTimer?.Invoke();
        }

        _invokeOnUi(() => StateChanged?.Invoke());
    }

    /// <summary>
    /// The moment the active rate limit clears: the server-reported
    /// <see cref="RateLimitResetsAt"/>, or (when the server was silent) the
    /// rate-limited result's checked-at stamp plus
    /// <see cref="RateLimitFallbackCooldown"/>. <c>null</c> when no
    /// rate-limited result has landed. A pure function of the tracked state,
    /// so the active flag stays a pure function of the clock.
    /// </summary>
    private DateTimeOffset? EffectiveRateLimitReset =>
        RateLimitResetsAt ?? (_rateLimitCheckedAt is { } checkedAt
            ? checkedAt + RateLimitFallbackCooldown
            : null);
}
