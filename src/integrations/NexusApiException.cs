namespace Modificus.Curator.Integrations;

/// <summary>
/// Thrown when the Nexus v1 API returns a non-success response. Carries the HTTP
/// status + the API's <c>message</c> field when available.
/// </summary>
/// <remarks>
/// Unsealed so <see cref="NexusRateLimitException"/> can specialize it; callers
/// can catch the base type to handle every Nexus API failure uniformly.
/// </remarks>
public class NexusApiException : Exception
{
    /// <summary>The HTTP status code returned by the API.</summary>
    public int StatusCode { get; }

    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The API error message (its <c>message</c> field when available).</param>
    public NexusApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Thrown when the Nexus API refuses a request because the rate limit is
/// exhausted (HTTP 429, or the <c>x-rl-daily-remaining</c> /
/// <c>x-rl-hourly-remaining</c> headers reporting zero). Carries the parsed
/// <see cref="NexusRateLimits"/> so callers can advise the user when to retry.
/// <see cref="NexusApiException.StatusCode"/> reflects the actual response status.
/// </summary>
public sealed class NexusRateLimitException : NexusApiException
{
    /// <summary>
    /// The rate-limit counters as reported on the failing response (used to
    /// surface "retry in N minutes" or the hourly/daily remaining).
    /// </summary>
    public NexusRateLimits? Limits { get; }

    public NexusRateLimitException(int statusCode, NexusRateLimits? limits)
        : base(statusCode, BuildMessage(statusCode, limits))
    {
        Limits = limits;
    }

    private static string BuildMessage(int statusCode, NexusRateLimits? limits)
    {
        var suffix = limits switch
        {
            { DailyReset: { } daily } => $"; daily window resets at {daily:O}.",
            { HourlyReset: { } hourly } => $"; hourly window resets at {hourly:O}.",
            _ => ".",
        };
        return "Nexus API rate limit exhausted (HTTP " + statusCode + ")" + suffix;
    }
}

/// <summary>
/// Thrown when the user has not selected a Nexus auth method
/// (<c>NexusAuthMethod.None</c>) or the selected method has no usable
/// credentials. Callers should gate API calls on the auth state from the
/// Integrations dialog; this is the defensive failure when they do not.
/// </summary>
public sealed class NexusNotAuthenticatedException : Exception
{
    public NexusNotAuthenticatedException()
        : base("No Nexus auth method is configured. Sign in via the Integrations dialog.")
    {
    }
}
