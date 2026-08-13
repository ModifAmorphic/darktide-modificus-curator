using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// A single mod entry within a profile's mod list: the source of truth that
/// <see cref="IProfileService.PrepareModRoot"/> projects into the staged mod
/// root's <c>mods/</c> host folder (staging links + <c>mods.lst</c>). References a
/// mod by its <see cref="ContainerId"/> (a profile never stores mod files of its
/// own).
/// </summary>
/// <remarks>
/// <para>Immutable: all properties are init-only. Mutations go through the
/// <see cref="IProfileService"/> methods, which rebuild the changed entry
/// (via <c>with</c> expressions) and persist, so a consumer can't silently edit
/// an entry returned from <see cref="IProfileService.GetModList"/> and have it
/// look persisted when it isn't.</para>
/// <para>
/// <b>Identity is <see cref="ContainerId"/>.</b> The display name, source badge,
/// and version are resolved at stage/render time through the
/// <see cref="IModRepository"/>. A profile never carries the mod's name or
/// version directly, so a container rename or an <c>isLatest</c> flip is
/// reflected without a profile-entry change.</para>
/// <para>
/// <b>Fresh-start tolerance:</b> an old <c>profile.json</c> that carries mod
/// entries with a <c>Name</c> field instead of <see cref="ContainerId"/>
/// deserializes those entries with <see cref="ContainerId"/> =
/// <see cref="Guid.Empty"/>; the profile service drops them on read + logs
/// (Curator does not migrate that shape: a fresh start, not a shim).</para>
/// </remarks>
public sealed record ModListEntry
{
    /// <summary>
    /// The referenced mod container's id. The join key against
    /// <see cref="IModRepository"/>; the single source of truth for the mod's
    /// identity across the profile.
    /// </summary>
    public Guid ContainerId { get; init; }

    /// <summary>
    /// Whether the mod is active. Disabled mods are omitted from
    /// <c>mods.lst</c> (enable-by-omission, per the loader contract).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Position within the load order; lower loads first. <see cref="int"/>
    /// rather than the list index so partial reordering is stable.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether this entry's load-order position is locked. A locked entry keeps
    /// its current zero-based list index across
    /// <see cref="IProfileService.SetModOrder"/> calls; reorder requests are
    /// projected onto the unlocked slots only, so no reorder can displace it.
    /// Defaults to <c>false</c>; an existing <c>profile.json</c> written before
    /// this field existed loads every entry unlocked. Independent of
    /// <see cref="Enabled"/> (a disabled or linked row can still be
    /// order-locked). Toggled via
    /// <see cref="IProfileService.SetModOrderLocked"/>.
    /// </summary>
    public bool OrderLocked { get; init; }

    /// <summary>
    /// This profile mod's version policy: <see cref="LatestPolicy"/> resolves to
    /// the container's <see cref="ModVersion.IsLatest"/> version at stage time;
    /// <see cref="PinnedPolicy"/> resolves to the version whose
    /// <see cref="ModVersion.Folder"/> matches the pin's
    /// <see cref="PinnedPolicy.VersionId"/>. Defaults to
    /// <see cref="ModVersionPolicy.Latest"/>.
    /// </summary>
    public ModVersionPolicy Policy { get; init; } = ModVersionPolicy.Latest;
}
