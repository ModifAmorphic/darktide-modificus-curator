namespace Modificus.Curator.RelayClient;

/// <summary>The outcome of <see cref="IGameDirModsHost.EnsureHosting"/>.</summary>
public enum GameDirHostingOutcome
{
    /// <summary>
    /// The game-dir <c>mods</c> slot is now a Curator-owned link serving the
    /// given staged tree (created fresh or re-pointed silently).
    /// </summary>
    Hosted,

    /// <summary>
    /// A foreign entry occupies the game-dir <c>mods</c> slot: a real
    /// directory, a real file, or a link Curator cannot prove ownership of.
    /// Nothing was mutated; the caller surfaces a consent prompt (and may then
    /// <see cref="IGameDirModsHost.TakeOver"/>).
    /// </summary>
    Conflict,
}

/// <summary>
/// One <see cref="IGameDirModsHost.EnsureHosting"/> ladder result: the outcome
/// plus, for <see cref="GameDirHostingOutcome.Conflict"/>, the detected path
/// (the game-dir <c>mods</c> entry itself).
/// </summary>
public sealed record GameDirHostingResult(
    GameDirHostingOutcome Outcome,
    string? ConflictPath = null);

/// <summary>
/// Hosts the staged mods tree inside the real game directory: the one service
/// that reads the game-dir <c>mods</c> ownership ladder and performs game-dir
/// mutations. Mods that resolve game-directory-relative paths require the mods
/// tree under <c>&lt;game&gt;/mods</c>, so the launch flow serves mods through
/// a single self-identifying link there instead of the staged path.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is decided by the staging marker inside a link's target plus the
/// profiles-root path prefix, never by reparse-ness alone (a user may have
/// made their own junction or symlink). A foreign entry is never deleted or
/// modified by this host; moving one aside happens only through
/// <see cref="TakeOver"/>, after user consent.</para>
/// <para>
/// Link creation goes through the Profiles staging-link primitive (an NTFS
/// junction on Windows, a symlink on Linux; both privilege-free), so the
/// game-dir link and the staged links use one platform mechanism.</para>
/// </remarks>
public interface IGameDirModsHost
{
    /// <summary>
    /// Ensures <c>&lt;gameDir&gt;/mods</c> is a Curator-owned link pointing at
    /// <c>&lt;stagedRoot&gt;/mods</c>: creates it when the slot is absent,
    /// silently re-points (delete + recreate of the link only; the staged tree
    /// is never touched through the link) when it is Curator's and aims
    /// elsewhere. Returns <see cref="GameDirHostingOutcome.Conflict"/> with
    /// the detected path when the slot holds anything Curator does not own; no
    /// mutation happens in that case.
    /// </summary>
    /// <exception cref="System.IO.IOException">The link could not be created,
    /// deleted, or read.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The caller lacks
    /// write access to <paramref name="gameDir"/>.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">A junction
    /// operation failed on Windows.</exception>
    GameDirHostingResult EnsureHosting(string gameDir, string stagedRoot);

    /// <summary>
    /// Performs the consented takeover of a foreign game-dir <c>mods</c>
    /// entry: renames it to a <c>mods_&lt;yyyyMMdd-HHmm&gt;</c> sibling (bumping a
    /// numeric suffix on collision), records a receipt in the persisted
    /// app-state, then best-effort writes a short <c>README.txt</c> inside the
    /// renamed entry (folder case only) explaining what happened and that
    /// nothing was deleted (a README failure is logged, never thrown; the
    /// receipt already records the move). Returns the renamed entry's full
    /// path, or <c>null</c> when nothing was renamed (the slot is absent or
    /// already Curator-owned).
    /// </summary>
    /// <exception cref="System.IO.IOException">The rename or receipt write
    /// failed.</exception>
    /// <exception cref="System.UnauthorizedAccessException">The caller lacks
    /// write access to <paramref name="gameDir"/>.</exception>
    string? TakeOver(string gameDir);

    /// <summary>
    /// Removes <c>&lt;gameDir&gt;/mods</c> when it is a Curator-owned link (the
    /// external-hosting opt-out). Best-effort: an absent slot, a foreign entry,
    /// or an IO failure leaves everything as it was (logged, never thrown) so
    /// the external-mode launch it serves cannot be blocked by cleanup.
    /// </summary>
    void RemoveOwnedLink(string gameDir);
}
