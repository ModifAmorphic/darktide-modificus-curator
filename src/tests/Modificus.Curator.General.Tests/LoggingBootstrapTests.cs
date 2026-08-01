using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Proves the structured-logging pipeline is wired and config-honoring: the
/// Serilog logger day-rolls the configured file at the configured level.
/// </summary>
/// <remarks>
/// Day-naming, midnight rolling, and pruning are Serilog's own behavior
/// (<c>RollingInterval.Day</c> + <c>retainedFileCountLimit</c>), so these tests
/// cover the bootstrap wiring (console + file sinks, the level, the day-rolled
/// filename, append-within-a-day) rather than re-testing the library internals.
/// </remarks>
public sealed class LoggingBootstrapTests
{
    // Serilog's RollingInterval.Day stamps the file before the extension, so a
    // configured "curator.log" is written as "curator<yyyyMMdd>.log" today (a
    // stem like "curator-" would read "curator-<yyyyMMdd>.log").
    private static string LogDir(string configured) => Path.GetDirectoryName(configured)!;

    [Fact]
    public void LoggerFactory_writes_to_a_day_rolled_file_and_honors_level()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log-" + Guid.NewGuid());
        var configured = Path.Combine(dir, "sub", "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = configured };

        try
        {
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                var logger = factory.CreateLogger("test");
                logger.LogInformation("structured hello {Token}", "world");
                logger.LogDebug("this is below the level and should be dropped");
            }

            // Exactly one day-rolled file in the configured directory, stamped
            // curator<8 digits>.log (the date Serilog inserted before .log).
            var written = Assert.Single(Directory.GetFiles(LogDir(configured)));
            Assert.Matches(@"^curator\d{8}\.log$", Path.GetFileName(written));

            var contents = File.ReadAllText(written);
            Assert.Contains("structured hello", contents);
            Assert.Contains("world", contents);
            Assert.DoesNotContain("below the level", contents);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Repeated_starts_within_the_same_day_append_to_the_day_file()
    {
        // Serilog's day-rolling File sink appends within the current period, so a
        // second start the same day keeps the first start's lines (the previous
        // per-process truncate-on-start behavior is gone). The day file is
        // shared across Curator starts within a day.
        var dir = Path.Combine(Path.GetTempPath(), "curator-rot-" + Guid.NewGuid());
        var configured = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = configured };

        try
        {
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                factory.CreateLogger("run1").LogInformation("first run content");
            }
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                factory.CreateLogger("run2").LogInformation("second run content");
            }

            // One day file holds both runs (append, not truncate).
            var written = Assert.Single(Directory.GetFiles(dir));
            var finalContents = File.ReadAllText(written);
            Assert.Contains("first run content", finalContents);
            Assert.Contains("second run content", finalContents);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Unknown_level_falls_back_to_information()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log2-" + Guid.NewGuid());
        var configured = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "NotARealLevel", LogFile = configured };

        try
        {
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                factory.CreateLogger("test").LogInformation("wrote something");
            }

            // Information-level fallback still produces a day-rolled file.
            Assert.NotEmpty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
