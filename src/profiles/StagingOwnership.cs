namespace Modificus.Curator.Profiles;

/// <summary>
/// The staging ownership marker: the file Curator writes into the staged
/// <c>mods/</c> root on every staging pass, identifying the projected profile.
/// The marker (not reparse-ness) is what proves a game-dir hosting link is
/// Curator's: its presence inside a link's target means Curator projected that
/// tree. <see cref="IProfileService.PrepareModRoot"/> is the writer; the
/// relay-client game-dir host reads only the file's presence.
/// </summary>
public static class StagingOwnership
{
    /// <summary>
    /// The marker's filename inside the staged <c>mods/</c> root. The leading
    /// dot keeps it out of ordinary directory listings next to the staging
    /// links.
    /// </summary>
    public const string MarkerFileName = ".curator.json";
}
