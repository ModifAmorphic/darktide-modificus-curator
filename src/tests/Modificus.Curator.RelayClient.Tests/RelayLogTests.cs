namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Tests for <see cref="RelayLog"/>: Relay's own per-day log path resolution
/// and the best-effort prune of old <c>relay-*.log</c> files. The relay log is
/// written by Relay's mod_loader (an external process via
/// <c>--log-file</c>), not by Serilog, so relay-client owns the path lifecycle.
/// </summary>
public sealed class RelayLogTests
{
    [Fact]
    public void ResolveRelayLogPath_uses_the_config_log_directory_and_a_day_stamp()
    {
        var configured = Path.Combine(Path.GetTempPath(), "cfg", "curator-.log");

        var resolved = RelayLog.ResolveRelayLogPath(configured, new DateTime(2026, 8, 1));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "cfg", "relay-20260801.log"), resolved);
    }

    [Fact]
    public void ResolveRelayLogPath_falls_back_to_a_relative_name_when_config_has_no_directory()
    {
        // A bare filename (no directory) resolves to a relative relay-<date>.log
        // rather than throwing; the prune is a no-op in that case (see below).
        var resolved = RelayLog.ResolveRelayLogPath("curator.log", new DateTime(2026, 8, 1));

        Assert.Equal("relay-20260801.log", resolved);
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

            // The "today" file need not exist; the prune derives only its directory.
            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-20260801.log"), 5);

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

            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-20260801.log"), 0);

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
    public void PruneOldRelayLogs_only_matches_relay_files_and_leaves_unrelated_files()
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

            RelayLog.PruneOldRelayLogs(Path.Combine(dir, "relay-20260801.log"), 5);

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
    public void PruneOldRelayLogs_is_best_effort_when_the_directory_is_missing()
    {
        // A missing directory is a no-op (never throws); a launch in a fresh
        // install with no log directory yet proceeds normally.
        var missing = Path.Combine(Path.GetTempPath(), "relay-prune-missing-" + Guid.NewGuid().ToString("N"));
        RelayLog.PruneOldRelayLogs(Path.Combine(missing, "relay-20260801.log"), 5);
        Assert.False(Directory.Exists(missing));
    }
}
