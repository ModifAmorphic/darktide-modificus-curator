using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="LoadOrderDownloadPlacements"/>: the profile-scoped pending
/// placement plans that converge an imported load order as its enqueued
/// downloads complete (the completion signal is the queue's existing
/// ItemChanged), including the supersede/failure/cancel/deleted-profile
/// matrix and the profile-membership filtering.
/// </summary>
public sealed class LoadOrderDownloadPlacementsTests
{
    private static (LoadOrderDownloadPlacements Placements, FakeModDownloadQueue Queue, FakeProfileService Profiles)
        Build(FakeProfileService? profiles = null)
    {
        profiles ??= TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var queue = new FakeModDownloadQueue();
        var placements = new LoadOrderDownloadPlacements(
            queue, profiles, NullLogger<LoadOrderDownloadPlacements>.Instance);
        return (placements, queue, profiles);
    }

    private static Guid ProfileId(FakeProfileService profiles) => profiles.ListProfiles().First().Id;

    /// <summary>An admitted ProfileAdd item for the mod, as the queue would host it.</summary>
    private static DownloadItem Item(Guid profileId, int modId) =>
        new(new ModDownloadRequest(
            "warhammer40kdarktide", modId, 900 + modId,
            DownloadPurpose.ProfileAdd, ContainerId: null, "Mod " + modId,
            profileId, "Alpha"));

    private static void Complete(DownloadItem item, Guid containerId)
    {
        item.ContainerId = containerId;
        item.Phase = DownloadPhase.Completed;
    }

    [Fact]
    public void A_completed_download_converges_the_order_over_the_resolved_anchors()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 11),
            new LoadOrderPlacementSlot(null, 22),
        });

        // The queue's completion registered the container first (mirroring
        // the real queue's AddMod-then-signal order).
        var first = Item(profileId, 11);
        var firstContainer = Guid.NewGuid();
        profiles.AddMod(profileId, firstContainer, ModVersionPolicy.Latest); // appended at the end
        var applied = new List<Guid>();
        placements.PlacementApplied += (_, id) => applied.Add(id);
        queue.Add(first);
        Complete(first, firstContainer);
        queue.Publish(first);

        // ONE order write: the anchor + the landed container in file order
        // (the append was corrected to the file position).
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal([anchor, firstContainer], order);
        Assert.Equal([profileId], applied);
        Assert.True(placements.HasPending(profileId)); // mod 22 still pending
    }

    [Fact]
    public void The_plan_drops_after_the_last_pending_completion()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[] { new LoadOrderPlacementSlot(null, 11) });
        var item = Item(profileId, 11);
        var container = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = container, Order = 0, Policy = ModVersionPolicy.Latest });
        queue.Add(item);
        Complete(item, container);
        queue.Publish(item);

        Assert.False(placements.HasPending(profileId));
    }

    [Fact]
    public void Multiple_completions_converge_to_the_full_file_order()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 11),
            new LoadOrderPlacementSlot(null, 22),
        });
        var first = queue.Add(Item(profileId, 11));
        var second = queue.Add(Item(profileId, 22));

        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        profiles.AddMod(profileId, c1, ModVersionPolicy.Latest);
        profiles.AddMod(profileId, c2, ModVersionPolicy.Latest);
        // BOTH land out of order (c2 before c1): each rewrite uses the plan's
        // file order, so the final order converges regardless of landing
        // order.
        Complete(second, c2);
        queue.Publish(second);
        Complete(first, c1);
        queue.Publish(first);

        Assert.Equal(
            [anchor, c1, c2],
            profiles.GetModList(profileId).Select(e => e.ContainerId).ToArray());
        Assert.False(placements.HasPending(profileId));
    }

    [Fact]
    public void A_failed_download_preserves_its_slot_for_a_later_retry()
    {
        // Failure is not authoritative: the queue's Retry admits a fresh item
        // for the same request, and that item's completion must still
        // converge. Dropping the slot on failure would strand the retried
        // download at the end of the profile forever.
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 11),
        });

        var failed = queue.Add(Item(profileId, 11));
        failed.Phase = DownloadPhase.Failed;
        queue.Publish(failed);

        // Nothing written for the failure; the intent survives.
        Assert.Empty(profiles.SetModOrderCalls);
        Assert.True(placements.HasPending(profileId));

        // The retry (a FRESH item for the same request, as the queue's Retry
        // admits) completes: the preserved slot resolves + converges.
        var retry = queue.Add(Item(profileId, 11));
        var container = Guid.NewGuid();
        profiles.AddMod(profileId, container, ModVersionPolicy.Latest); // appended
        Complete(retry, container);
        queue.Publish(retry);

        Assert.Equal([anchor, container], Assert.Single(profiles.SetModOrderCalls));
        Assert.False(placements.HasPending(profileId));
    }

    [Fact]
    public void A_cancelled_download_still_drops_its_slot()
    {
        // Cancellation IS authoritative: a cancelled slot never resolves, so
        // it drops out of the converging order (the survivors stand).
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 11),
        });

        var canceled = queue.Add(Item(profileId, 11));
        canceled.Phase = DownloadPhase.Canceled;
        queue.Publish(canceled);

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(placements.HasPending(profileId)); // the plan drained
    }

    [Fact]
    public void A_transient_order_write_failure_retains_the_plan_for_a_later_retry()
    {
        // An IO/access failure writing the order is not "the profile is
        // gone": the plan stays so a later completion (a sibling landing or a
        // retry) rewrites the order.
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(null, 11),
            new LoadOrderPlacementSlot(null, 22),
        });

        // The first completion's order write fails transiently.
        var first = queue.Add(Item(profileId, 11));
        var c1 = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = c1, Order = 0, Policy = ModVersionPolicy.Latest });
        profiles.SetModOrderThrows = new IOException("disk hiccup");
        Complete(first, c1);
        queue.Publish(first);
        Assert.Single(profiles.SetModOrderCalls); // attempted, failed
        Assert.True(placements.HasPending(profileId)); // retained

        // The sibling's completion retries the write over BOTH resolved
        // slots and succeeds.
        profiles.SetModOrderThrows = null;
        var second = queue.Add(Item(profileId, 22));
        var c2 = Guid.NewGuid();
        profiles.AddMod(profileId, c2, ModVersionPolicy.Latest);
        Complete(second, c2);
        queue.Publish(second);

        Assert.Equal([c1, c2], profiles.SetModOrderCalls[1]);
        Assert.False(placements.HasPending(profileId));
    }

    [Fact]
    public void A_later_import_supersedes_the_stale_plan()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[] { new LoadOrderPlacementSlot(null, 11) });

        // A second import for the same profile replaces the intent.
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 22),
        });

        // The superseded mod's completion does nothing.
        var stale = queue.Add(Item(profileId, 11));
        var staleContainer = Guid.NewGuid();
        profiles.AddMod(profileId, staleContainer, ModVersionPolicy.Latest);
        Complete(stale, staleContainer);
        queue.Publish(stale);
        Assert.Empty(profiles.SetModOrderCalls);

        // The current plan's mod converges.
        var fresh = queue.Add(Item(profileId, 22));
        var freshContainer = Guid.NewGuid();
        profiles.AddMod(profileId, freshContainer, ModVersionPolicy.Latest);
        Complete(fresh, freshContainer);
        queue.Publish(fresh);
        Assert.Equal([anchor, freshContainer], Assert.Single(profiles.SetModOrderCalls));
    }

    [Fact]
    public void A_deleted_profile_drops_the_plan_without_throwing()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[] { new LoadOrderPlacementSlot(null, 11) });
        profiles.DeleteProfile(profileId);

        var item = queue.Add(Item(profileId, 11));
        Complete(item, Guid.NewGuid());
        queue.Publish(item);

        Assert.False(placements.HasPending(profileId));
        Assert.Empty(profiles.SetModOrderCalls);
    }

    [Fact]
    public void Unknown_profiles_mods_and_purposes_are_ignored()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[] { new LoadOrderPlacementSlot(null, 11) });

        // A different profile's item, an unplanned mod, an UpdateInstall, and
        // a non-terminal phase: none may trigger an order write.
        var otherProfile = profiles.WithProfile("Beta").Id;
        var other = queue.Add(Item(otherProfile, 11));
        Complete(other, Guid.NewGuid());
        queue.Publish(other);

        var unplanned = queue.Add(Item(profileId, 99));
        Complete(unplanned, Guid.NewGuid());
        queue.Publish(unplanned);

        var update = new DownloadItem(new ModDownloadRequest(
            "warhammer40kdarktide", 11, 900, DownloadPurpose.UpdateInstall,
            Guid.NewGuid(), "Mod 11", profileId, "Alpha", ExpectedVersion: "1.0"));
        queue.Add(update);
        Complete(update, Guid.NewGuid());
        queue.Publish(update);

        var inFlight = queue.Add(Item(profileId, 11));
        inFlight.ContainerId = Guid.NewGuid();
        inFlight.Phase = DownloadPhase.Downloading; // not terminal
        queue.Publish(inFlight);

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.True(placements.HasPending(profileId));
    }

    [Fact]
    public void A_slot_for_a_container_the_user_removed_is_filtered_from_the_write()
    {
        var (placements, queue, profiles) = Build();
        var profileId = ProfileId(profiles);
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        placements.Set(profileId, new[]
        {
            new LoadOrderPlacementSlot(anchor, 0),
            new LoadOrderPlacementSlot(null, 11),
        });

        // The anchor was removed from the profile while the download ran:
        // only the landed container is placed (SetModOrder semantics keep the
        // rest in relative order).
        profiles.RemoveMod(profileId, anchor);
        var item = queue.Add(Item(profileId, 11));
        var container = Guid.NewGuid();
        profiles.AddMod(profileId, container, ModVersionPolicy.Latest);
        Complete(item, container);
        queue.Publish(item);

        Assert.Equal([container], Assert.Single(profiles.SetModOrderCalls));
    }

    [Fact]
    public void A_plan_with_no_pending_downloads_is_not_recorded()
    {
        var (placements, _, profiles) = Build();
        var profileId = ProfileId(profiles);
        placements.Set(profileId, new[] { new LoadOrderPlacementSlot(Guid.NewGuid(), 0) });
        Assert.False(placements.HasPending(profileId));
    }
}
