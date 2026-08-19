using System.ComponentModel;
using System.Text;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Default <see cref="IGameDirModsHost"/>. Implements the ownership ladder over
/// the game-dir <c>mods</c> slot with two proofs, either of which claims a link:
/// the staging marker inside the link's target, or a target path under the
/// profiles root (a dead link after a data move stays Curator's). Everything
/// else at the slot is foreign and never mutated outside
/// <see cref="TakeOver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton from the composition root (after
/// <c>AddProfiles</c> supplies the staging-link primitive and
/// <c>AddGeneral</c> the receipts role): it holds no per-call state, and the
/// profiles root is read live from <see cref="IProfileService.ProfilesRoot"/>
/// on each ladder run, so a runtime folder change via the Settings window
/// takes effect on the next launch. Link creation + deletion reuse the
/// Profiles staging-link primitive and its reparse-aware delete semantics
/// (remove the link, never follow it into the target).
/// </para>
/// </remarks>
public sealed class GameDirModsHost : IGameDirModsHost
{
    /// <summary>The hosted entry's name inside the game dir.</summary>
    internal const string ModsFolderName = "mods";

    /// <summary>The README filename written inside a renamed-aside folder.</summary>
    internal const string TakeOverReadmeFileName = "README.txt";

    private readonly StagingLinkCreator _createLink;
    private readonly IProfileService _profiles;
    private readonly IRenamedModsFoldersState _renamedFolders;
    private readonly ILogger<GameDirModsHost> _logger;

    public GameDirModsHost(
        StagingLinkCreator createLink,
        IProfileService profiles,
        IRenamedModsFoldersState renamedFolders,
        ILogger<GameDirModsHost> logger)
    {
        _createLink = createLink ?? throw new ArgumentNullException(nameof(createLink));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _renamedFolders = renamedFolders ?? throw new ArgumentNullException(nameof(renamedFolders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public GameDirHostingResult EnsureHosting(string gameDir, string stagedRoot)
    {
        ArgumentNullException.ThrowIfNull(gameDir);
        ArgumentNullException.ThrowIfNull(stagedRoot);

        var linkPath = Path.Combine(gameDir, ModsFolderName);
        var targetPath = Path.Combine(stagedRoot, ModsFolderName);

        if (!TryGetAttributes(linkPath, out var attrs))
        {
            // Absent slot: create the link, silently.
            CreateLink(linkPath, targetPath);
            _logger.LogInformation("Created the game-dir mods link {Link} -> {Target}.", linkPath, targetPath);
            return new GameDirHostingResult(GameDirHostingOutcome.Hosted);
        }

        if (IsCuratorOwnedLink(linkPath, attrs, out var resolvedTarget))
        {
            if (SamePath(resolvedTarget, targetPath))
            {
                return new GameDirHostingResult(GameDirHostingOutcome.Hosted);
            }

            // Re-point by replacing the link only. Directory.Delete on a
            // reparse point removes the point itself, never the target's
            // contents; the staged tree is only ever reached through links.
            DeleteLink(linkPath, attrs);
            CreateLink(linkPath, targetPath);
            _logger.LogInformation(
                "Re-pointed the game-dir mods link {Link}: {Old} -> {Target}.", linkPath, resolvedTarget, targetPath);
            return new GameDirHostingResult(GameDirHostingOutcome.Hosted);
        }

        _logger.LogWarning(
            "Foreign entry at the game-dir mods slot {Link}; refusing to touch it.", linkPath);
        return new GameDirHostingResult(GameDirHostingOutcome.Conflict, linkPath);
    }

    /// <inheritdoc />
    public void TakeOver(string gameDir)
    {
        ArgumentNullException.ThrowIfNull(gameDir);

        var modsPath = Path.Combine(gameDir, ModsFolderName);
        if (!TryGetAttributes(modsPath, out var attrs) || IsCuratorOwnedLink(modsPath, attrs, out _))
        {
            // Nothing to move aside (the slot is empty or already ours); the
            // caller's retry hosts through the ordinary ladder.
            return;
        }

        var renamedPath = RenameAside(modsPath, attrs);
        _logger.LogInformation(
            "Renamed the foreign game-dir mods entry {Original} -> {Renamed} after consent.", modsPath, renamedPath);

        // The receipt is the audit trail for a mutation that already happened:
        // record it before anything best-effort.
        var receipts = _renamedFolders.RenamedModsFolders?.ToList() ?? new List<RenamedModsFolder>();
        receipts.Add(new RenamedModsFolder(modsPath, renamedPath, DateTimeOffset.UtcNow));
        _renamedFolders.RenamedModsFolders = receipts;

        // The README is a convenience for the user browsing the renamed folder:
        // a write failure is logged, never surfaced. It goes inside a real
        // folder only (a file or a foreign link has no inside to explain
        // from).
        var isRealDirectory = (attrs & FileAttributes.Directory) != 0
            && (attrs & FileAttributes.ReparsePoint) == 0;
        if (isRealDirectory)
        {
            try
            {
                WriteTakeOverReadme(renamedPath, modsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _logger.LogWarning(
                    ex, "Failed to write the takeover README inside {Renamed} (best-effort).", renamedPath);
            }
        }
    }

    /// <inheritdoc />
    public void RemoveOwnedLink(string gameDir)
    {
        ArgumentNullException.ThrowIfNull(gameDir);

        try
        {
            var linkPath = Path.Combine(gameDir, ModsFolderName);
            if (!TryGetAttributes(linkPath, out var attrs) || !IsCuratorOwnedLink(linkPath, attrs, out var target))
            {
                return;
            }

            DeleteLink(linkPath, attrs);
            _logger.LogInformation(
                "Removed the Curator-owned game-dir mods link {Link} (was -> {Target}).", linkPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Best-effort by contract: the external-mode launch this serves
            // must not be blocked by cleanup. The link stays; the next
            // external launch retries.
            _logger.LogWarning(ex, "Failed to remove the Curator-owned game-dir mods link (best-effort).");
        }
    }

    // ---- ownership ----------------------------------------------------------

    /// <summary>
    /// Whether the entry at <paramref name="linkPath"/> is a link Curator owns:
    /// a reparse point whose resolved target carries the staging marker or
    /// lies under the profiles root. The target of an owned link is returned
    /// via <paramref name="target"/> (normalized, no trailing separators).
    /// </summary>
    private bool IsCuratorOwnedLink(string linkPath, FileAttributes attrs, out string target)
    {
        target = string.Empty;
        if ((attrs & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        // ResolveLinkTarget reads the stored target without following it, so a
        // dead link still resolves (its target is simply missing on disk).
        var resolved = new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: false);
        if (resolved is null)
        {
            return false;
        }

        target = Normalize(resolved.FullName);
        return File.Exists(Path.Combine(target, StagingOwnership.MarkerFileName))
            || IsUnderProfilesRoot(target);
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies under the live profiles root (the
    /// prefix proof: a dead link aimed into Curator's own space is Curator's
    /// even when the target is currently missing).
    /// </summary>
    private bool IsUnderProfilesRoot(string path)
    {
        var root = Normalize(Path.GetFullPath(_profiles.ProfilesRoot));
        if (root.Length == 0)
        {
            return false;
        }

        var candidate = Normalize(path);
        return candidate.Length > root.Length
            && candidate[root.Length] == Path.DirectorySeparatorChar
            && candidate.StartsWith(root, PathComparison);
    }

    // ---- link mutation ------------------------------------------------------

    private void CreateLink(string linkPath, string targetPath) => _createLink(linkPath, targetPath);

    /// <summary>
    /// Deletes a link as a link (the reparse point only, never its target's
    /// contents). Mirrors the Profiles staged-entry delete: the API must match
    /// the link's kind or Windows throws.
    /// </summary>
    private static void DeleteLink(string linkPath, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(linkPath);
        }
        else
        {
            // A dangling directory symlink surfaces without the Directory bit
            // on Unix (the bit follows the missing target); File.Delete
            // removes such a link as a link.
            File.Delete(linkPath);
        }
    }

    /// <summary>
    /// Renames the entry at <paramref name="modsPath"/> to a
    /// <c>mods_&lt;yyyyMMdd-HHmm&gt;</c> sibling, bumping <c>-1</c>,
    /// <c>-2</c>, ... while the candidate exists. Local time stamps the
    /// sibling so a user browsing their game dir recognizes when it happened.
    /// </summary>
    private static string RenameAside(string modsPath, FileAttributes attrs)
    {
        var dir = Path.GetDirectoryName(modsPath) ?? throw new IOException(
            $"The game-dir mods path '{modsPath}' has no parent directory.");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");

        var renamed = Path.Combine(dir, $"{ModsFolderName}_{stamp}");
        var bump = 1;
        while (EntryExists(renamed))
        {
            renamed = Path.Combine(dir, $"{ModsFolderName}_{stamp}-{bump++}");
        }

        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Move(modsPath, renamed);
        }
        else
        {
            File.Move(modsPath, renamed);
        }
        return renamed;
    }

    private static void WriteTakeOverReadme(string renamedDir, string originalPath)
    {
        var text =
            "Modificus Curator moved this folder aside to host its own mod list.\r\n" +
            $"It was previously the 'mods' folder at: {originalPath}\r\n" +
            "Nothing was deleted: every file is here, unchanged.\r\n" +
            "Curator now serves your active profile's mods through a link at the old\r\n" +
            "location. To go back to this setup, delete the 'mods' link, rename this\r\n" +
            "folder back to 'mods', and enable the external hosting preference in\r\n" +
            "Curator's Preferences.\r\n";
        File.WriteAllText(Path.Combine(renamedDir, TakeOverReadmeFileName), text, new UTF8Encoding(false));
    }

    // ---- filesystem probes --------------------------------------------------

    /// <summary>
    /// File-system comparison for ownership paths: case-insensitive on
    /// Windows, ordinal elsewhere (mirroring path equality in the mod
    /// repository).
    /// </summary>
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>Normalizes a path for comparison: absolute, no trailing
    /// separators.</summary>
    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Ordinal/cased path equality after normalization. Both sides are links'
    /// stored or derived targets, so no existence is assumed.
    /// </summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>
    /// Reads attributes without following to existence semantics that miss
    /// dangling links: <see cref="File.GetAttributes"/> reports a dangling
    /// Unix symlink (as a reparse point) while <see cref="File.Exists"/> /
    /// <see cref="Directory.Exists"/> alone would miss it.
    /// </summary>
    private static bool TryGetAttributes(string path, out FileAttributes attrs)
    {
        try
        {
            attrs = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attrs = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attrs = default;
            return false;
        }
    }

    /// <summary>Whether any entry (file, directory, or dangling link) exists
    /// at <paramref name="path"/>.</summary>
    private static bool EntryExists(string path) => TryGetAttributes(path, out _);
}
