namespace Modificus.Curator.Integrations;

/// <summary>
/// Resolves the soonest server-reported reset of an actually-exhausted
/// rate-limit window from a <see cref="NexusRateLimits"/> snapshot. Internal:
/// shared by the update-check service + the metadata-backfill service so the
/// two cannot drift on what "the reset is" means.
/// </summary>
/// <remarks>
/// A window counts as exhausted only when its remaining budget is zero AND it
/// carries a reset strictly in the future; the earliest such reset wins. Returns
/// <c>null</c> when the limits are null, when no window is exhausted, or when
/// every exhausted window's reset is absent or already past (so a caller's
/// fallback cooldown, not a stale instant, governs re-enabling). The all-zero
/// <see cref="NexusRateLimits.Unknown"/> (a 429 with no <c>x-rl-*</c> headers)
/// has no remaining budgets reported as exhausted with a reset, so it yields
/// <c>null</c> here.
/// </remarks>
internal static class NexusRateLimitReset
{
    /// <summary>
    /// Computes the earliest valid reset of an exhausted rate-limit window, or
    /// <c>null</c> when none applies.
    /// </summary>
    /// <param name="limits">The parsed rate-limit counters from the response
    /// headers, or <c>null</c> when absent.</param>
    /// <param name="now">The current UTC instant, used to reject resets that are
    /// already in the past.</param>
    public static DateTimeOffset? ComputeEarliest(NexusRateLimits? limits, DateTimeOffset now)
    {
        if (limits is null)
        {
            return null;
        }

        DateTimeOffset? earliest = null;
        if (limits.DailyRemaining <= 0 && limits.DailyReset is { } daily && daily > now)
        {
            earliest = daily;
        }
        if (limits.HourlyRemaining <= 0 && limits.HourlyReset is { } hourly && hourly > now)
        {
            earliest = earliest is { } current && current <= hourly ? current : hourly;
        }
        return earliest;
    }
}
