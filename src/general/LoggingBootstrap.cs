using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Modificus.Curator.Config;

namespace Modificus.Curator.General;

/// <summary>
/// Builds the structured-logging pipeline from <see cref="CuratorConfig.Logging"/>.
/// Uses Serilog (console + file sinks) bridged into
/// <c>Microsoft.Extensions.Logging</c>, honoring the configured level and file.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="LoggingConfig.LogFile"/> contains the
/// <see cref="LoggingConfig.DateTimeToken"/>, each Curator start resolves the
/// token to a fixed-width local timestamp (<c>yyyyMMddHHmmss</c>) and pins that
/// path for the process lifetime: the process writes a new, uniquely named file
/// for its whole run, and the log directory is pruned to
/// <see cref="LoggingConfig.RetainedLogFileCount"/> newest files (including the
/// current). When the token is absent, the configured file is reused and
/// truncated on each start (the legacy behavior).</para>
/// <para>
/// The resolved path is exposed on <see cref="CurrentLogFile"/> and shared with
/// Mod Relay: the Relay launcher receives it as <c>--log-file</c>, so Curator
/// and Relay write the same per-process file.</para>
/// </remarks>
public static class LoggingBootstrap
{
    /// <summary>
    /// The log file resolved for this process (the configured template with the
    /// <see cref="LoggingConfig.DateTimeToken"/> substituted), set once when
    /// <see cref="CreateLoggerFactory"/> runs. Consumed by the Relay launcher
    /// (<c>--log-file</c>) and the startup banner so both report the file this
    /// session actually writes. <c>null</c> before the bootstrap runs.
    /// </summary>
    public static string? CurrentLogFile { get; internal set; }

    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> wired to a Serilog logger that
    /// writes to the console and to the resolved log file, filtered to
    /// <see cref="LoggingConfig.Level"/>.
    /// </summary>
    /// <remarks>
    /// Disposing the returned factory disposes the underlying Serilog logger
    /// (flushing the file sink). The Serilog logger is also assigned to
    /// <see cref="Log.Logger"/> for any static/global logging.
    /// </remarks>
    public static ILoggerFactory CreateLoggerFactory(CuratorConfig config)
    {
        var level = ParseLevel(config.Logging.Level);

        // Resolve the process-pinned log path once: substitute the token with a
        // fixed-width local timestamp when present, else reuse the template
        // verbatim (the legacy truncate-on-start path).
        var template = config.Logging.LogFile;
        var hasToken = template.Contains(LoggingConfig.DateTimeToken);
        var resolved = ResolveLogFile(template, DateTime.Now);
        CurrentLogFile = resolved;

        // Ensure the log directory exists; the file sink does not create
        // missing parent directories reliably across versions.
        var logDir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        if (hasToken && config.Logging.RetainedLogFileCount >= 1)
        {
            // Per-process rotation. Ensure this session's file exists first so it
            // is counted among the newest, then prune to the retained count. The
            // resolved file's timestamp is the newest, so pruning never deletes it.
            if (!File.Exists(resolved))
            {
                File.Create(resolved).Dispose();
            }
            PruneOldLogs(template, config.Logging.RetainedLogFileCount);
        }
        else
        {
            // Legacy: truncate on each start (no rotation). File.Delete is a
            // no-op when the file is absent; the sink then starts a fresh file.
            File.Delete(resolved);
        }

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(resolved)
            .CreateLogger();

        Log.Logger = serilogLogger;

        var factory = new LoggerFactory();
        factory.AddSerilog(serilogLogger, dispose: true);
        return factory;
    }

    /// <summary>
    /// Resolves a log-file template by substituting
    /// <see cref="LoggingConfig.DateTimeToken"/> with <paramref name="instant"/>
    /// formatted as a fixed-width, filesystem-safe local timestamp
    /// (<c>yyyyMMddHHmmss</c>, no time separators). A template without the token
    /// is returned unchanged. Pure (no IO); unit-tested directly.
    /// </summary>
    internal static string ResolveLogFile(string template, DateTime instant) =>
        template.Contains(LoggingConfig.DateTimeToken)
            ? template.Replace(LoggingConfig.DateTimeToken, instant.ToString("yyyyMMddHHmmss"))
            : template;

    /// <summary>
    /// Prunes the log directory derived from <paramref name="logFileTemplate"/>
    /// to the <paramref name="retainedCount"/> newest files matching the
    /// template's pattern. The glob is the template's file name with
    /// <see cref="LoggingConfig.DateTimeToken"/> replaced by <c>*</c> (a template
    /// without the token matches only itself, so pruning is a no-op). Files are
    /// ordered by name descending (the fixed-width timestamp makes lexicographic
    /// order match chronological order); all but the newest
    /// <paramref name="retainedCount"/> are deleted. Best-effort: any delete or
    /// directory-read failure is swallowed so pruning never crashes startup. A
    /// <paramref name="retainedCount"/> below 1 keeps everything.
    /// </summary>
    internal static void PruneOldLogs(string logFileTemplate, int retainedCount)
    {
        if (retainedCount < 1)
        {
            return;
        }

        var dir = Path.GetDirectoryName(logFileTemplate);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        var glob = Path.GetFileName(logFileTemplate);
        if (string.IsNullOrEmpty(glob))
        {
            return;
        }
        glob = glob.Replace(LoggingConfig.DateTimeToken, "*");

        string[] files;
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            files = Directory.GetFiles(dir, glob);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Fixed-width yyyyMMddHHmmss => lexicographic order == chronological.
        foreach (var stale in files
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Skip(retainedCount))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Best-effort: a single delete failure must not crash startup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;
}
