namespace Modificus.Curator.Profiles;

/// <summary>
/// A Modificus Curator profile -- a named, owned set of mods + load order. The
/// aggregate root persisted to <c>&lt;ProfilesBaseFolder&gt;/&lt;Id&gt;/profile.json</c>.
/// </summary>
/// <remarks>
/// Identity is <see cref="Id"/> (a <see cref="Guid"/>, stable across renames);
/// the on-disk directory is keyed by it. <see cref="Name"/> is a display label,
/// not unique and not used as a path.
/// </remarks>
public sealed class Profile
{
    /// <summary>
    /// The maximum length of <see cref="Description"/>, in characters, after
    /// trimming. Enforced at the service boundary (<see cref="IProfileService"/>).
    /// </summary>
    public const int DescriptionMaxLength = 120;

    /// <summary>Stable identity; also the on-disk directory name.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name. Editable via <see cref="IProfileService.UpdateProfile"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A short, single-line description shown in the profile banner + picker.
    /// Defaults to empty; trimmed at the service boundary. Mutable setter for
    /// STJ deserialization (like <see cref="Name"/>); changes go through
    /// <see cref="IProfileService"/>. Coerced from JSON <c>null</c> / missing
    /// property to empty on read (mirrors <see cref="LaunchSettings"/>'s
    /// normalization).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When the profile was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The profile's mods, in no particular storage order -- load order comes
    /// from each entry's <see cref="ModListEntry.Order"/>. Exposed as a
    /// <see cref="IReadOnlyList{T}"/> of immutable entries: neither the list
    /// nor its entries can be edited in place -- changes go through the
    /// <see cref="IProfileService"/> methods, which rebuild + persist.
    /// </summary>
    public IReadOnlyList<ModListEntry> Mods { get; set; } = Array.Empty<ModListEntry>();

    /// <summary>
    /// The profile's launch settings (environment variables + Darktide
    /// command-line arguments). Defaults to a non-null empty instance so a
    /// freshly-created profile serializes it. Mutable setter for STJ
    /// deserialization (like <see cref="Mods"/>); changes go through
    /// <see cref="IProfileService.UpdateProfile"/>, which validates +
    /// rebuilds + persists. Coerced from JSON <c>null</c> / missing property to
    /// an empty instance on read (mirrors <see cref="Mods"/>'s normalization).
    /// </summary>
    public LaunchSettings LaunchSettings { get; set; } = new();
}
