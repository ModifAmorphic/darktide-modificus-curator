namespace Modificus.Curator.Profiles;

/// <summary>
/// The identity <see cref="IModOrderResolver"/>: returns the container ids in
/// their current <see cref="ModListEntry.Order"/> (a no-op). The real
/// dependency-driven auto-sort resolver is a separate concern; this keeps the
/// UI's auto-sort toggle wired + shippable now and preserves the seam so the
/// resolver can drop in without a UI change.
/// </summary>
/// <remarks>
/// Pure + stateless. Stable on equal <see cref="ModListEntry.Order"/> values
/// (<see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>
/// is a stable sort, so equal orders keep the input's relative order, which is
/// usually storage order from <see cref="IProfileService.GetModList"/>).</remarks>
public sealed class IdentityModOrderResolver : IModOrderResolver
{
    /// <inheritdoc />
    public IReadOnlyList<Guid> ResolveOrder(IReadOnlyList<ModListEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        // Identity: current order, unchanged. Select container ids ordered by
        // current Order; OrderBy is stable on ties.
        return mods
            .OrderBy(m => m.Order)
            .Select(m => m.ContainerId)
            .ToArray();
    }
}
