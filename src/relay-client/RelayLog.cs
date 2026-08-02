namespace Modificus.Curator.RelayClient;

/// <summary>
/// Resolves and prunes Mod Relay's own log file. Relay's mod_loader writes the
/// <c>--log-file</c> it receives directly (it is an external process, not
/// Serilog), so Curator owns that file's lifecycle here rather than in the
/// logging bootstrap: a per-day file derived from the configured
/// <c>Logging.RelayLogFile</c> stem (relay-client inserts the day stamp
/// manually; Serilog does it for Curator's own log), pruned to the shared
/// retained count at each launch.
/// </summary>
internal static class RelayLog
{
    /// <summary>
    /// Resolves today's Relay log file from the configured
    /// <paramref name="relayLogFile"/> stem. The path is split into directory +
    /// stem (filename without extension) + extension, then reassembled as
    /// <c>stem + &lt;<paramref name="instant"/> as yyyyMMdd&gt; + extension</c>:
    /// the date lands before the extension, mirroring Serilog's
    /// <c>RollingInterval.Day</c> behavior, so a stem of <c>relay-</c> yields
    /// <c>relay-&lt;yyyyMMdd&gt;.log</c>. The fixed-width stamp means
    /// lexicographic order matches chronological order, which
    /// <see cref="PruneOldRelayLogs"/> relies on to pick the newest.
    /// </summary>
    internal static string ResolveRelayLogPath(string relayLogFile, DateTime instant)
    {
        var dir = Path.GetDirectoryName(relayLogFile);
        var stem = Path.GetFileNameWithoutExtension(relayLogFile);
        var ext = Path.GetExtension(relayLogFile);
        return Path.Combine(dir ?? string.Empty, stem + instant.ToString("yyyyMMdd") + ext);
    }

    /// <summary>
    /// Best-effort prune of old Relay logs in the directory of the configured
    /// <paramref name="relayLogFile"/> stem: keeps the newest
    /// <paramref name="retainedCount"/> files matching the glob derived from that
    /// stem + extension (so a <c>relay-.log</c> stem matches
    /// <c>relay-*.log</c>) and deletes the rest. <paramref name="relayLogFile"/>
    /// is the configured <c>RelayLogFile</c> (the undated stem), not the dated
    /// path from <see cref="ResolveRelayLogPath"/>, so the glob matches every
    /// day's file rather than only today's. The fixed-width <c>yyyyMMdd</c>
    /// stamp means ordering by file name descending equals chronological order.
    /// A count below 1 keeps everything. Any directory-read or delete failure is
    /// swallowed so pruning never breaks a launch. Unrelated files in the
    /// directory (including Curator's <c>curator-*.log</c>) are left untouched.
    /// </summary>
    internal static void PruneOldRelayLogs(string relayLogFile, int retainedCount)
    {
        if (retainedCount < 1)
        {
            return;
        }

        var dir = Path.GetDirectoryName(relayLogFile);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(relayLogFile);
        var ext = Path.GetExtension(relayLogFile);

        string[] files;
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            files = Directory.GetFiles(dir, stem + "*" + ext);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Fixed-width yyyyMMdd => lexicographic order == chronological.
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
                // Best-effort: a single delete failure must not block a launch.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
