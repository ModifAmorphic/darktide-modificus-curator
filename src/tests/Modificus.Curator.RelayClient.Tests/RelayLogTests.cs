namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Tests for <see cref="RelayLog"/>: Relay's own per-day log path resolution
/// and the best-effort prune of old log files matching the configured stem. The
/// relay log is written by Relay's mod_loader (an external process via
/// <c>--log-file</c>), not by Serilog, so relay-client owns the path lifecycle.
/// </summary>
public sealed class RelayLogTests
{
    [Fact]
    public void ResolveRelayLogPath_derives_directory_and_day_stamp_from_the_configured_stem()
    {
        var configured = Path.Combine(Path.GetTempPath(), "cfg", "relay-.log");

        var resolved = RelayLog.ResolveRelayLogPath(configured, new DateTime(2026, 8, 1));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "cfg", "relay-20260801.log"), resolved);
    }

    [Fact]
    public void ResolveRelayLogPath_inserts_the_date_before_the_extension()
    {
        // The date lands before the extension (mirroring Serilog's
        // RollingInterval.Day), so a stem of relay- yields relay-<yyyyMMdd>.log
        // rather than relay-<yyyyMMdd> (extension stripped).
        var resolved = RelayLog.ResolveRelayLogPath(
            Path.Combine(Path.GetTempPath(), "x", "relay-.log"),
            new DateTime(2026, 8, 1));

        Assert.Equal("relay-20260801.log", Path.GetFileName(resolved));
    }

    [Fact]
    public void ResolveRelayLogPath_falls_back_to_a_relative_name_when_config_has_no_directory()
    {
        // A bare filename (no directory) resolves to a relative <stem><date><ext>
        // rather than throwing; the prune is a no-op in that case (see below).
        var resolved = RelayLog.ResolveRelayLogPath("relay-.log", new DateTime(2026, 8, 1));

        Assert.Equal("relay-20260801.log", resolved);
    }

    [Fact]
    public void ResolveRelayLogPath_is_generic_not_hardcoded_to_a_relay_prefix_or_extension()
    {
        // A configured stem with a different prefix + extension proves the derive
        // is generic: any stem + extension resolves, not just relay-.log.
        var configured = Path.Combine(Path.GetTempPath(), "x", "game-.txt");

        var resolved = RelayLog.ResolveRelayLogPath(configured, new DateTime(2026, 8, 1));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "game-20260801.txt"), resolved);
    }

    [Fact]
    public void PruneOldRelayLogs_keeps_the_newest_n_and_deletes_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "relay-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Seed seven dated relay files (days 1-7); the prune target is today.
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"relay-2026070{day}.log"), "");
            }

            // The "today" file need not exist; the prune derives the glob from the
            // configured stem passed in.
            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-.log"), 5);

            // Newest five (days 3-7) survive; oldest two (days 1-2) are pruned.
            for (var day = 1; day <= 7; day++)
            {
                var path = Path.Combine(dir, $"relay-2026070{day}.log");
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
    public void PruneOldRelayLogs_with_retention_below_one_keeps_all()
    {
        var dir = Path.Combine(Path.GetTempPath(), "relay-prune-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"relay-2026070{day}.log"), "");
            }

            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-.log"), 0);

            for (var day = 1; day <= 7; day++)
            {
                Assert.True(File.Exists(Path.Combine(dir, $"relay-2026070{day}.log")));
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
    public void PruneOldRelayLogs_only_matches_the_configured_stem_and_leaves_unrelated_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "relay-prune-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var day = 1; day <= 7; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"relay-2026070{day}.log"), "");
            }
            // Curator's own day-rolled log + an unrelated file must survive.
            File.WriteAllText(Path.Combine(dir, "curator-20260701.log"), "");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "");

            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-.log"), 5);

            // Only the oldest two relay-*.log files are pruned.
            Assert.False(File.Exists(Path.Combine(dir, "relay-20260701.log")));
            Assert.False(File.Exists(Path.Combine(dir, "relay-20260702.log")));
            // Unrelated files survive (Curator's log is untouched by Relay's prune).
            Assert.True(File.Exists(Path.Combine(dir, "curator-20260701.log")));
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

    [Fact]
    public void PruneOldRelayLogs_is_generic_not_hardcoded_to_a_relay_stem()
    {
        // A different stem + extension derives a different glob, proving the
        // prune is driven by the configured stem, not a hardcoded relay- constant.
        var dir = Path.Combine(Path.GetTempPath(), "relay-prune-generic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var day = 1; day <= 3; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"game-2026070{day}.txt"), "");
            }
            // A relay-*.log file must survive (different stem + extension).
            File.WriteAllText(Path.Combine(dir, "relay-20260701.log"), "");

            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "game-.txt"), 1);

            // Newest 1 game-*.txt survives (day 3); oldest two (days 1-2) pruned.
            Assert.False(File.Exists(Path.Combine(dir, "game-20260701.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "game-20260702.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "game-20260703.txt")));
            // The unrelated relay-*.log file is untouched.
            Assert.True(File.Exists(Path.Combine(dir, "relay-20260701.log")));
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
    public void PruneOldRelayLogs_is_best_effort_when_the_directory_is_missing()
    {
        // A missing directory is a no-op (never throws); a launch in a fresh
        // install with no log directory yet proceeds normally.
        var missing = Path.Combine(Path.GetTempPath(), "relay-prune-missing-" + Guid.NewGuid().ToString("N"));
        RelayLog.PruneOldRelayLogs(Path.Combine(missing, "relay-.log"), 5);
        Assert.False(Directory.Exists(missing));
    }
}
