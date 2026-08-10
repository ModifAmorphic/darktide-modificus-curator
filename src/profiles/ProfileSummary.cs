namespace Modificus.Curator.Profiles;

/// <summary>
/// Lightweight projection of a <see cref="Profile"/> for listing -- just enough
/// to render a profile picker without loading every profile's full mod list.
/// </summary>
/// <param name="Id">The profile's stable identity.</param>
/// <param name="Name">The display name (trimmed at the service boundary).</param>
/// <param name="Description">The short description (trimmed; empty when none).</param>
public sealed record ProfileSummary(Guid Id, string Name, string Description);
