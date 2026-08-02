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
    /// at local midnight. Relay has its own <see cref="RelayLogFile"/> stem
    /// (which defaults to the same directory).
    /// </summary>
    public string LogFile { get; set; } = AppPaths.DefaultLogFile;

    /// <summary>
    /// The stem for Mod Relay's own day-stamped log file, parallel to
    /// <see cref="LogFile"/>. relay-client inserts the day stamp
    /// (<c>yyyyMMdd</c>) before the extension at each launch (Relay's file is
    /// written by mod_loader, not Serilog, so the date is inserted manually rather
    /// than via rolling). Defaults to <c>relay-.log</c> next to Curator's log;
    /// resolves to <c>relay-&lt;yyyyMMdd&gt;.log</c>. Pruned to
    /// <see cref="RetainedLogFileCount"/> at launch.
    /// </summary>
    public string RelayLogFile { get; set; } = AppPaths.DefaultRelayLogFile;

    /// <summary>
    /// How many day-rolled log files to retain. Default 5. Feeds Serilog's
    /// <c>retainedFileCountLimit</c> for Curator's own log (pruning the oldest
    /// <c>curator-*.log</c> files) AND the relay-client prune for Relay's log
    /// (keeping the newest <c>relay-*.log</c> files): the single user-facing
    /// retention knob for both. A value below 1 keeps everything: the bootstrap
    /// maps it to Serilog's null/unlimited retention, and the relay prune
    /// treats it as keep-all.
    /// </summary>
    public int RetainedLogFileCount { get; set; } = 5;
}
