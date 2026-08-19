namespace Modificus.Curator.Profiles;

/// <summary>The enabled mod-manager mod on a profile, as derived from profile
/// state: the manager-occupied container + the staged manager file path.</summary>
/// <remarks>
/// One derivation (see <see cref="IProfileService.GetActiveModManager"/>)
/// shared by the launch path (Relay's <c>--mod-manager</c>) and the mod-list
/// banner, so the two can never disagree.
/// </remarks>
public sealed record ActiveModManager(Guid ContainerId, string ManagerPath);
