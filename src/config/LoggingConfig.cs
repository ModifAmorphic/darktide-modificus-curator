namespace Modificus.Curator.Config;

/// <summary>
/// Logging-related global configuration. Honored by the logging bootstrap
/// in <c>Modificus.Curator.General</c> when the Serilog logger is built.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// The minimum log level, as a Serilog level name
    /// (<c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c>, <c>Fatal</c>). Unknown values fall back to
    /// <c>Information</c>.
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>
    /// The file Serilog writes the structured log to. Serilog day-rolls it
    /// (<c>RollingInterval.Day</c>): the date is inserted before the extension,
    /// so a stem of <c>curator-</c> yields <c>curator-&lt;yyyyMMdd&gt;.log</c>,
    /// one file per day, appended to across starts within the same day and rolled
    /// at local midnight. The file's directory is also the home of Relay's own
    /// <c>relay-&lt;yyyyMMdd&gt;.log</c> (managed by relay-client at launch).
    /// </summary>
    public string LogFile { get; set; } = AppPaths.DefaultLogFile;

    /// <summary>
    /// How many day-rolled log files to retain. Default 5. Feeds Serilog's
    /// <c>retainedFileCountLimit</c> for Curator's own log (pruning the oldest
    /// <c>curator-*.log</c> files) AND the relay-client prune for Relay's log
    /// (keeping the newest <c>relay-*.log</c> files): the single user-facing
    /// retention knob for both. A value below 1 keeps everything (Serilog's own
    /// convention for the null/unlimited case; the relay prune mirrors that).
    /// </summary>
    public int RetainedLogFileCount { get; set; } = 5;
}
