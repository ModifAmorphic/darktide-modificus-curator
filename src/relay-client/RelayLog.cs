namespace Modificus.Curator.RelayClient;

/// <summary>
/// Resolves and prunes Mod Relay's own log file. Relay's mod_loader writes the
/// <c>--log-file</c> it receives directly (it is an external process, not
/// Serilog), so Curator owns that file's lifecycle here rather than in the
/// logging bootstrap: a per-day <c>relay-&lt;yyyyMMdd&gt;.log</c> alongside
/// Curator's Serilog day-rolled log (shared directory, stem-first so the two
/// file sets group by type), pruned to the shared retained count at each launch.
/// </summary>
internal static class RelayLog
{
    private const string FileNamePrefix = "relay-";
    private const string FileNameSuffix = ".log";

    /// <summary>
    /// Resolves today's Relay log file under the same directory as Curator's
    /// configured log file (<paramref name="configFile"/>). The name is
    /// <c>relay-&lt;<paramref name="instant"/> as yyyyMMdd&gt;.log</c>: the
    /// fixed-width stamp means lexicographic order matches chronological order,
    /// which <see cref="PruneOldRelayLogs"/> relies on to pick the newest.
    /// </summary>
    internal static string ResolveRelayLogPath(string configFile, DateTime instant)
    {
        var dir = Path.GetDirectoryName(configFile);
        var name = FileNamePrefix + instant.ToString("yyyyMMdd") + FileNameSuffix;
        return Path.Combine(dir ?? string.Empty, name);
    }

    /// <summary>
    /// Best-effort prune of old Relay logs in the directory of
    /// <paramref name="relayLogFile"/>: keeps the newest
    /// <paramref name="retainedCount"/> files matching <c>relay-*.log</c> and
    /// deletes the rest. The fixed-width <c>yyyyMMdd</c> stamp means ordering by
    /// file name descending equals chronological order. A count below 1 keeps
    /// everything. Any directory-read or delete failure is swallowed so pruning
    /// never breaks a launch. Unrelated files in the directory (including
    /// Curator's <c>curator-*.log</c>) are left untouched.
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

        string[] files;
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            files = Directory.GetFiles(dir, FileNamePrefix + "*" + FileNameSuffix);
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
