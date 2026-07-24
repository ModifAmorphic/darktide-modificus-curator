namespace Modificus.Curator.Config;

/// <summary>
/// Logging-related global configuration. Honored by the logging bootstrap
/// in <c>Modificus.Curator.General</c> when the Serilog logger is built.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// The token in <see cref="LogFile"/> that Curator replaces with the process
    /// start timestamp (e.g. <c>curator-{DateTime}.log</c> becomes
    /// <c>curator-20260723143000.log</c>). When present, each Curator start
    /// writes a new, uniquely named file pinned for the process lifetime; older
    /// files are pruned to <see cref="RetainedLogFileCount"/>. When absent, the
    /// configured file is reused and truncated on each start (the legacy
    /// behavior).
    /// </summary>
    public const string DateTimeToken = "{DateTime}";

    /// <summary>
    /// The minimum log level, as a Serilog level name
    /// (<c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c>, <c>Fatal</c>). Unknown values fall back to
    /// <c>Information</c>.
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>
    /// The file the structured log is written to. May contain
    /// <see cref="DateTimeToken"/>; when it does, Curator resolves the token to
    /// the process start timestamp once at startup and pins that path for the
    /// process lifetime (a new file per start, with older files pruned to
    /// <see cref="RetainedLogFileCount"/>). When the token is absent, the file
    /// is reused and truncated on each start. The resolved path is shared with
    /// Mod Relay: Curator passes it to the launcher as <c>--log-file</c>, so the
    /// two write the same per-process file.
    /// </summary>
    public string LogFile { get; set; } = AppPaths.DefaultLogFile;

    /// <summary>
    /// How many datetime-named log files to keep (including the current
    /// process's file). Default 5. Only applies when <see cref="LogFile"/>
    /// contains <see cref="DateTimeToken"/>. A value below 1 disables pruning
    /// (keeps all).
    /// </summary>
    public int RetainedLogFileCount { get; set; } = 5;
}
