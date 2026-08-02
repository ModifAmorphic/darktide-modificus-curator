using Microsoft.Extensions.Logging;
using Serilog;
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
/// The file sink day-rolls: <see cref="LoggingConfig.LogFile"/> is the stem Serilog
/// writes, and <c>RollingInterval.Day</c> inserts the date before the extension
/// (<c>curator-.log</c> becomes <c>curator-&lt;yyyyMMdd&gt;.log</c>). One file per
/// day, appended across Curator starts within the same day, rolled at local
/// midnight, and pruned to <see cref="LoggingConfig.RetainedLogFileCount"/> newest
/// files. Serilog owns the day-naming, the midnight rolling, and the pruning.</para>
/// <para>
/// Relay keeps its own separate log (<c>relay-&lt;yyyyMMdd&gt;.log</c>) in the same
/// directory; relay-client resolves and prunes that file at launch.</para>
/// </remarks>
public static class LoggingBootstrap
{
    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> wired to a Serilog logger that
    /// writes to the console and to the day-rolled log file, filtered to
    /// <see cref="LoggingConfig.Level"/>, and pruned to
    /// <see cref="LoggingConfig.RetainedLogFileCount"/> retained files.
    /// </summary>
    /// <remarks>
    /// Disposing the returned factory disposes the underlying Serilog logger
    /// (flushing the file sink). The Serilog logger is also assigned to
    /// <see cref="Log.Logger"/> for any static/global logging.
    /// </remarks>
    public static ILoggerFactory CreateLoggerFactory(CuratorConfig config)
    {
        var level = ParseLevel(config.Logging.Level);

        // Ensure the log directory exists; the file sink does not create
        // missing parent directories reliably across versions.
        var logDir = Path.GetDirectoryName(config.Logging.LogFile);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                config.Logging.LogFile,
                rollingInterval: RollingInterval.Day,
                // Serilog.Sinks.File rejects retainedFileCountLimit < 1 (it
                // throws ArgumentException; unlimited requires null, not a
                // sub-1 count). Map < 1 to null so a "keep everything" config
                // value does not crash startup. Mirrors the relay-client prune,
                // which treats < 1 as unlimited.
                retainedFileCountLimit: config.Logging.RetainedLogFileCount < 1
                    ? null
                    : (int?)config.Logging.RetainedLogFileCount)
            .CreateLogger();

        Log.Logger = serilogLogger;

        var factory = new LoggerFactory();
        factory.AddSerilog(serilogLogger, dispose: true);
        return factory;
    }

    private static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;
}
