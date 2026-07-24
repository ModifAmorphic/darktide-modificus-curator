using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Proves the structured-logging pipeline is wired and config-honoring: the
/// logger writes to the configured file at or above the configured level.
/// </summary>
public sealed class LoggingBootstrapTests
{
    [Fact]
    public void LoggerFactory_writes_to_configured_file_and_honors_level()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "sub", "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = logFile };

        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            var logger = factory.CreateLogger("test");
            logger.LogInformation("structured hello {Token}", "world");
            logger.LogDebug("this is below the level and should be dropped");
        }

        Assert.True(File.Exists(logFile));
        var contents = File.ReadAllText(logFile);
        Assert.Contains("structured hello", contents);
        Assert.Contains("world", contents);
        Assert.DoesNotContain("below the level", contents);
    }

    [Fact]
    public void Log_file_is_truncated_on_each_startup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-trunc-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = logFile };

        // First startup: write a line and flush.
        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("run1").LogInformation("first run content");
        }
        Assert.Contains("first run content", File.ReadAllText(logFile));

        // Second startup. We don't read the file inside this block: on Windows
        // the Serilog writer holds the file open and File.ReadAllText throws a
        // sharing violation. (If a future test must read a live log, open it via
        // a FileStream with FileShare.ReadWrite.) Truncation is proven by the
        // post-disposal assertion below instead.
        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("run2").LogInformation("second run content");
        }

        // Truncation proof: the second run's content is present, the first run's
        // is not (append behavior would have kept both).
        var finalContents = File.ReadAllText(logFile);
        Assert.Contains("second run content", finalContents);
        Assert.DoesNotContain("first run content", finalContents);
    }

    [Fact]
    public void Unknown_level_falls_back_to_information()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log2-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "NotARealLevel", LogFile = logFile };

        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("test").LogInformation("wrote something");
        }

        Assert.True(File.Exists(logFile));
    }

    [Fact]
    public void ResolveLogFile_substitutes_the_token_with_a_sortable_timestamp()
    {
        // Fixed-width, sortable, filesystem-safe (no time separators): the token
        // becomes yyyyMMddHHmmss.
        Assert.Equal(
            "curator-20260723143000.log",
            LoggingBootstrap.ResolveLogFile("curator-{DateTime}.log", new DateTime(2026, 7, 23, 14, 30, 0)));

        // A template without the token is returned unchanged.
        Assert.Equal(
            "curator.log",
            LoggingBootstrap.ResolveLogFile("curator.log", new DateTime(2026, 7, 23, 14, 30, 0)));
    }

    [Fact]
    public void CreateLoggerFactory_with_token_creates_a_new_timestamped_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-rot-" + Guid.NewGuid());
        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig
        {
            Level = "Information",
            LogFile = Path.Combine(dir, "curator-{DateTime}.log"),
            RetainedLogFileCount = 5,
        };

        try
        {
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                factory.CreateLogger("test").LogInformation("rotated run");
            }

            // A single timestamped file matching the glob exists.
            var matches = Directory.GetFiles(dir, "curator-*.log");
            Assert.Single(matches);

            // CurrentLogFile points at that file and no longer carries the token.
            Assert.NotNull(LoggingBootstrap.CurrentLogFile);
            Assert.DoesNotContain(LoggingConfig.DateTimeToken, LoggingBootstrap.CurrentLogFile!);
            Assert.EndsWith(Path.GetFileName(matches[0]), LoggingBootstrap.CurrentLogFile);
        }
        finally
        {
            // Reset the process-global static so it does not leak between tests.
            LoggingBootstrap.CurrentLogFile = null;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateLoggerFactory_with_token_prunes_to_the_retained_count()
    {
        // End-to-end rotation: seed six older files, start with retention 5, and
        // confirm exactly five remain (the four newest seeds plus this session's
        // new file). Proves the resolved file is created before pruning so it
        // counts toward the budget.
        var dir = Path.Combine(Path.GetTempPath(), "curator-rot-prune-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        for (var day = 1; day <= 6; day++)
        {
            File.WriteAllText(Path.Combine(dir, $"curator-2026070{day}000000.log"), "old");
        }

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig
        {
            Level = "Information",
            LogFile = Path.Combine(dir, "curator-{DateTime}.log"),
            RetainedLogFileCount = 5,
        };

        try
        {
            using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
            {
                factory.CreateLogger("test").LogInformation("new run");
            }

            var remaining = Directory.GetFiles(dir, "curator-*.log");
            Assert.Equal(5, remaining.Length);

            // The oldest two seeds are gone; the newest four seeds survive.
            Assert.False(File.Exists(Path.Combine(dir, "curator-20260701000000.log")));
            Assert.False(File.Exists(Path.Combine(dir, "curator-20260702000000.log")));
            Assert.True(File.Exists(Path.Combine(dir, "curator-20260706000000.log")));
        }
        finally
        {
            LoggingBootstrap.CurrentLogFile = null;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void PruneOldLogs_keeps_the_newest_n_and_deletes_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-prune-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // Seed seven dated files; the template carries the token.
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"curator-2026070{day}000000.log"), "");
            }

            LoggingBootstrap.PruneOldLogs(Path.Combine(dir, "curator-{DateTime}.log"), 5);

            // Newest five (days 3-7) survive; oldest two (days 1-2) are pruned.
            for (var day = 1; day <= 7; day++)
            {
                var path = Path.Combine(dir, $"curator-2026070{day}000000.log");
                if (day <= 2)
                {
                    Assert.False(File.Exists(path), $"expected day {day} to be pruned");
                }
                else
                {
                    Assert.True(File.Exists(path), $"expected day {day} to be kept");
                }
            }
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
    public void PruneOldLogs_with_retention_below_one_keeps_all()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-prune-keep-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"curator-2026070{day}000000.log"), "");
            }

            LoggingBootstrap.PruneOldLogs(Path.Combine(dir, "curator-{DateTime}.log"), 0);

            for (var day = 1; day <= 7; day++)
            {
                Assert.True(File.Exists(Path.Combine(dir, $"curator-2026070{day}000000.log")));
            }
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
    public void PruneOldLogs_only_matches_the_pattern_and_leaves_unrelated_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-prune-scope-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"curator-2026070{day}000000.log"), "");
            }
            // Unrelated files in the same directory must survive the prune.
            File.WriteAllText(Path.Combine(dir, "relay.log"), "");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "");

            LoggingBootstrap.PruneOldLogs(Path.Combine(dir, "curator-{DateTime}.log"), 5);

            // Only the oldest two curator-*.log files are pruned.
            Assert.False(File.Exists(Path.Combine(dir, "curator-20260701000000.log")));
            Assert.False(File.Exists(Path.Combine(dir, "curator-20260702000000.log")));
            // Unrelated files survive.
            Assert.True(File.Exists(Path.Combine(dir, "relay.log")));
            Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
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
