namespace Modificus.Curator.General;

/// <summary>
/// One persisted receipt for a foreign game-dir <c>mods</c> entry Curator renamed
/// aside with user consent before hosting its own link there. A plain
/// serializable DTO (no domain behavior) so <see cref="IRenamedModsFoldersState"/>
/// can persist it in <c>app-state.json</c>; the relay-client game-dir host owns
/// when a rename happens. The receipts are an audit trail: nothing reads them
/// back to drive behavior, but they record exactly what was moved and when so a
/// user can find their previous setup after the fact.
/// </summary>
/// <param name="OriginalPath">The full path of the entry before the rename
/// (always <c>&lt;game-dir&gt;/mods</c> for the takeover flow, but recorded as a
/// full path so the receipt is self-describing).</param>
/// <param name="RenamedPath">The full path the entry was renamed to (a
/// <c>mods_&lt;timestamp&gt;</c> sibling in the same directory).</param>
/// <param name="RenamedAtUtc">When the rename happened (UTC).</param>
public sealed record RenamedModsFolder(
    string OriginalPath,
    string RenamedPath,
    DateTimeOffset RenamedAtUtc);
