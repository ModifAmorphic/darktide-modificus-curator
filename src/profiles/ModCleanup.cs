using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Startup cleanup for the mod repository. Collects every
/// <c>(containerId, versionFolder)</c> referenced by any profile (resolving each
/// entry's policy against its container), then asks the repository to drop
/// unreferenced versions + empty containers. Keeps the on-disk tree in sync
/// with what the profiles actually use; safe to run on every startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the spec's startup prune: <c>PruneUnreferenced</c> runs once
/// after composition. The resolution here mirrors
/// <c>ProfileService.PrepareModRoot</c> via <see cref="ModContainer.ResolveVersion"/>,
/// so a version folder survives the prune when at least one profile would
/// stage it, and every referenced container's CURRENT latest folder survives
/// unconditionally (a pinned entry must not let the prune delete the
/// container's newest version; see <see cref="PruneUnreferenced"/>). Disabled
/// mods still count as referenced (the profile entry is the reference;
/// enable/disable is a stage-time decision, not a delete signal).</para>
/// <para>
/// <see cref="LinkedSource"/> containers have no versions, so they are
/// referenced by containerId alone: a linked profile entry adds
/// <c>(containerId, <c>string.Empty</c>)</c> to the referenced set. The empty
/// version folder is a sentinel that never matches a real opaque version id, so
/// it cannot affect version dropping; its only role is to mark the containerId
/// as referenced so the prune keeps the linked container while any profile uses
/// it. An unreferenced linked container is pruned like any empty container.</para>
/// <para>
/// I/O failures (a missing directory, an unreadable profile) propagate as
/// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>: the
/// composition root decides whether to swallow them (startup should not abort
/// on a cleanup failure; the repository is still usable, and the next startup
/// retries).</para>
/// </remarks>
public static class ModCleanup
{
    /// <summary>
    /// Runs the startup prune: collects referenced version folders across every
    /// profile, then calls <see cref="IModRepository.PruneUnreferenced"/>. Safe
    /// to call once after composition; idempotent.
    /// </summary>
    /// <exception cref="IOException">On a filesystem failure while reading
    /// profiles.</exception>
    /// <exception cref="UnauthorizedAccessException">On an access-denied failure
    /// while reading profiles.</exception>
    public static void PruneUnreferenced(IProfileService profiles, IModRepository repo)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(repo);

        var referenced = new HashSet<(Guid, string)>();

        foreach (var summary in profiles.ListProfiles())
        {
            IReadOnlyList<ModListEntry> mods;
            try
            {
                mods = profiles.GetModList(summary.Id);
            }
            catch (KeyNotFoundException)
            {
                // The profile disappeared between list + read (unlikely, but
                // possible if a cleanup ran concurrently). Skip it.
                continue;
            }

            foreach (var entry in mods)
            {
                var container = repo.Get(entry.ContainerId);
                if (container is null)
                {
                    continue; // dangling reference; nothing to keep.
                }

                // Linked containers have no versions to resolve; reference them
                // by containerId so the prune keeps them while a profile uses
                // them. The empty-string version folder is a sentinel: real
                // version folders are non-empty opaque ids (Guid "N" format),
                // so it cannot collide, and the container's Versions is empty so
                // the prune's version-drop loop never matches it anyway.
                if (container.Source is LinkedSource)
                {
                    referenced.Add((entry.ContainerId, string.Empty));
                    continue;
                }

                var version = container.ResolveVersion(entry.Policy);
                if (version is not null)
                {
                    referenced.Add((entry.ContainerId, version.Folder));
                }

                // The container's CURRENT latest always survives the prune,
                // regardless of the entry's own policy: a LatestPolicy
                // resolves to it today, and a pinned entry must not let the
                // prune delete the container's newest version on restart (a
                // pin is a user choice about what stages, not a delete signal
                // for the rest of the container). This also keeps the latest
                // folder alive across a mixed manual/download container whose
                // latest flag is stale on disk (the flag self-heals on the
                // next add/remove; the folder must still exist then).
                if (container.ResolveVersion(ModVersionPolicy.Latest) is { } latestVersion)
                {
                    referenced.Add((entry.ContainerId, latestVersion.Folder));
                }
            }
        }

        repo.PruneUnreferenced(referenced);
    }
}
