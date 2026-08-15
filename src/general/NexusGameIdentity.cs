namespace Modificus.Curator.General;

/// <summary>
/// The Nexus Mods identity of the game Curator manages. Curator is Darktide-only
/// by design, so these are fixed facts of the managed game, not configuration.
/// </summary>
/// <remarks>
/// One home for the values so every surface agrees: the API path segment, the
/// URL slug checks, the v2 GraphQL UID computation
/// (<c>uid = game_id * 2^32 + mod_id</c>), and the Darktide mod-page URLs.
/// </remarks>
public static class NexusGameIdentity
{
    /// <summary>
    /// The Nexus game domain: the URL slug under <c>nexusmods.com</c> and the
    /// <c>{domain}</c> segment of the v1 API paths.
    /// </summary>
    public const string DarktideDomain = "warhammer40kdarktide";

    /// <summary>
    /// The Nexus game id: identifies Darktide in the v2 GraphQL endpoints (the
    /// high bits of the per-mod UID).
    /// </summary>
    public const int DarktideGameId = 4943;
}
