using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Exercises <see cref="ModDownloadQueue"/> (the serial download coordinator)
/// against in-memory fakes: enqueue thread-safety + the marshal seam, dedupe
/// join/pulse, FIFO serial non-overlap (nxm + update installs share the one
/// worker), the repository hit path (zero network, policy from the matched
/// version), the miss path (acquisition with progress, name swap, policy from
/// IsHeadFile), the ProfileAdd completion matrix (fresh AddMod / existing
/// SetModPolicy / reload-only-when-active / profile-deleted inline failure /
/// acknowledge failure non-fatal), the UpdateInstall completion matrix
/// (eligibility revalidation per rule, acknowledge, the applied event,
/// failure/cancel without side effects, background-profile completion), both
/// cancel phases, sign-out at dequeue, retry, and dismiss.
/// </summary>
/// <remarks>
/// Determinism comes from the scripted acquisition fake (per-call hold/throw/
/// progress steps over task gates) + the inline marshal seams. Waits are
/// SpinUntil polls against observable conditions, never sleeps.
/// </remarks>
public sealed class ModDownloadQueueTests
{
    private static readonly LocalizationService Localization = new();

    // ---- enqueue: thread-safety + the marshal seam -------------------------

    [Fact]
    public async Task Enqueue_from_a_background_thread_publishes_items_on_the_seam_thread()
    {
        // The IPC handler calls Enqueue from its background task. The item
        // collection + events must still be published through the injected
        // marshal seam only (observed on the seam's thread, never the
        // caller's).
        var marshal = new DeferredMarshal();
        var harness = new QueueHarness(marshal);
        var profile = harness.AddProfile();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Acquisition.Steps.Enqueue(new ScriptedStep { Hold = gate });
        var collectionChangeThreads = new ConcurrentQueue<int>();
        harness.Queue.Items.CollectionChanged += (_, _) =>
            collectionChangeThreads.Enqueue(Environment.CurrentManagedThreadId);

        var item = await Task.Run(() => harness.Queue.Enqueue(AddRequest(profile.Id)));

        // Nothing ran yet: the add is queued on the seam, not executed.
        Assert.Empty(harness.Queue.Items);
        marshal.DrainUntil(() => harness.Queue.Items.Count == 1);
        gate.SetResult();
        marshal.DrainUntil(() => item.Phase == DownloadPhase.Completed);
        Assert.Empty(harness.Queue.Items);

        Assert.True(collectionChangeThreads.Count >= 2); // add + removal
        Assert.All(collectionChangeThreads, id =>
            Assert.Equal(Environment.CurrentManagedThreadId, id));
        Assert.All(marshal.ExecutedOnThreadIds, id =>
            Assert.Equal(Environment.CurrentManagedThreadId, id));
    }

    [Fact]
    public void Hit_path_item_observes_admission_events_before_the_terminal_removal()
    {
        // Pins the add-before-release ordering on the fast path: the item is
        // admitted, posted to the collection, and announced through
        // ItemChanged BEFORE the worker is released, so a hit-path item (no
        // held acquisition, completes the moment the worker sees it) still
        // shows add-then-terminal-removal in posted order, with the resolve
        // announcement between them (the hit path resolves from the
        // repository before completing). The deferred seam
        // runs nothing until drained, so the posted order is fully
        // serialized and observable; the thread-affinity test above holds
        // the acquisition, which never exercises this race.
        var marshal = new DeferredMarshal();
        var harness = new QueueHarness(marshal);
        var profile = harness.AddProfile();
        var (container, _, _) = harness.SeedKnownMod();
        var events = new ConcurrentQueue<string>();
        harness.Queue.Items.CollectionChanged += (_, e) =>
            events.Enqueue(e.Action == NotifyCollectionChangedAction.Add ? "add" : "remove");
        harness.Queue.ItemChanged += _ => events.Enqueue("changed");

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownHeadFileId, containerId: container.Id));

        marshal.DrainUntil(() =>
            events.Count(e => e == "changed") == 3 && harness.Queue.Items.Count == 0);

        Assert.Equal(new[] { "add", "changed", "changed", "remove", "changed" }, events);
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        // The hit path really ran: no acquisition, no network.
        Assert.Empty(harness.Acquisition.Calls);
    }

    // ---- dedupe ------------------------------------------------------------

    [Fact]
    public void Enqueue_same_key_while_live_joins_pulses_and_never_doubles()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var gate = HoldNext(harness);
        var first = harness.Queue.Enqueue(AddRequest(profile.Id));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        var second = harness.Queue.Enqueue(AddRequest(profile.Id));

        Assert.Same(first, second);
        Assert.Equal(1, first.Pulse);
        Assert.Single(harness.Queue.Items);

        gate.SetResult();
        Assert.True(WaitUntil(() => first.IsTerminal));
        Assert.Single(harness.Acquisition.Calls);
    }

    [Fact]
    public void Enqueue_same_key_case_insensitive_domain_joins()
    {
        // The gate accepts any casing of the Darktide domain; the dedupe key
        // must not treat differently-cased domains as distinct files.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        var first = harness.Queue.Enqueue(AddRequest(profile.Id));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        var second = harness.Queue.Enqueue(
            AddRequest(profile.Id, gameDomain: "WARHAMMER40KDARKTIDE"));

        Assert.Same(first, second);
        _gateLast.SetResult();
        Assert.True(WaitUntil(() => first.IsTerminal));
        Assert.Single(harness.Acquisition.Calls);
    }

    [Fact]
    public void Enqueue_different_file_of_same_mod_queues_separately()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        var first = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 100));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        var second = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 200));

        Assert.NotSame(first, second);
        Assert.Equal(2, harness.Queue.Items.Count);
        _gateLast.SetResult();
        Assert.True(WaitUntil(() => second.IsTerminal));
        Assert.Equal(2, harness.Acquisition.Calls.Count);
    }

    // ---- worker: FIFO, one at a time ----------------------------------------

    [Fact]
    public void Worker_processes_in_fifo_order_and_never_overlaps()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        harness.Acquisition.Steps.Enqueue(new ScriptedStep());

        var first = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 100));
        var second = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 200));

        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));
        // The first item downloads; the second has not started.
        Assert.Equal(DownloadPhase.Downloading, first.Phase);
        Assert.Equal(DownloadPhase.Queued, second.Phase);
        Assert.Single(harness.Acquisition.Calls);

        _gateLast.SetResult();
        Assert.True(WaitUntil(() => harness.Acquisition.Calls.Count == 2));
        Assert.True(WaitUntil(() => second.IsTerminal));

        // FIFO order (admission order) + one acquisition in flight at a time.
        Assert.Equal(new[] { 100, 200 }, harness.Acquisition.Calls.Select(c => c.FileId));
        Assert.Equal(1, harness.Acquisition.MaxInFlight);
        Assert.Empty(harness.Queue.Items);
    }

    // ---- hit path: exact file-id match, zero network ------------------------

    [Fact]
    public void Hit_path_head_version_completes_with_no_network_and_latest_policy()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, folderOld, folderHead) = harness.SeedKnownMod();

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownHeadFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.Empty(harness.Acquisition.Calls);
        Assert.Empty(harness.Queue.Items);

        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(profile.Id, add.Id);
        Assert.Equal(container.Id, add.ContainerId);
        Assert.IsType<LatestPolicy>(add.Policy);
        // The fixture is what it claims (the head is not the old version).
        Assert.NotEqual(folderOld, folderHead);
        // The active target reloads.
        Assert.Equal(1, harness.Refresh.Reloads);
    }

    [Fact]
    public void Hit_path_non_head_version_pins_to_that_versions_folder()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, folderOld, _) = harness.SeedKnownMod();

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownOldFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Empty(harness.Acquisition.Calls);

        var add = Assert.Single(harness.Profiles.AddModCalls);
        var pinned = Assert.IsType<PinnedPolicy>(add.Policy);
        Assert.Equal(folderOld, pinned.VersionId);
    }

    [Fact]
    public void Hit_path_resolves_container_version_and_stored_name()
    {
        // A peek that missed at enqueue (the fallback row name) is healed at
        // dequeue: the hit supplies the container id, the version tag, and the
        // stored name.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, _, _) = harness.SeedKnownMod();

        var item = harness.Queue.Enqueue(AddRequest(
            profile.Id, fileId: KnownHeadFileId, containerId: null, name: null));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(container.Id, item.ContainerId);
        Assert.Equal(KnownHeadVersion, item.Version);
        Assert.Equal(KnownModName, item.DisplayName);
    }

    // ---- miss path: acquisition with progress -------------------------------

    [Fact]
    public void Miss_path_reports_bytes_moves_to_importing_and_swaps_the_name()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Acquisition.Steps.Enqueue(new ScriptedStep
        {
            Progress = new() { (5, 10), (10, 10) },
            Hold = gate,
        });

        var item = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 300, name: null));

        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1
            && item.Phase == DownloadPhase.Importing));
        Assert.Equal(10, item.ReceivedBytes);
        Assert.Equal(10, item.TotalBytes);
        // The fallback name stays until the acquisition resolves it.
        Assert.Equal(Localization.Format("Nxm_ModNameFallback", KnownModId), item.DisplayName);

        gate.SetResult();
        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(harness.Acquisition.NextModName, item.DisplayName);
        Assert.Equal(harness.Acquisition.NextVersionString, item.Version);
        Assert.NotNull(item.ContainerId);
    }

    [Fact]
    public void Miss_path_head_file_registers_with_latest_policy()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        harness.Acquisition.NextIsHead = true;

        var item = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 300, name: null));

        Assert.True(WaitUntil(() => item.IsTerminal));
        var call = Assert.Single(harness.Acquisition.Calls);
        Assert.Equal(300, call.FileId);
        Assert.Equal("KEY", call.NxmKey);
        Assert.Equal(123L, call.NxmExpires);

        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(call.ContainerId, add.ContainerId);
        Assert.IsType<LatestPolicy>(add.Policy);
    }

    [Fact]
    public void Miss_path_non_head_file_pins_to_the_result_version()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        harness.Acquisition.NextIsHead = false;

        var item = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 300, name: null));

        Assert.True(WaitUntil(() => item.IsTerminal));
        var add = Assert.Single(harness.Profiles.AddModCalls);
        var pinned = Assert.IsType<PinnedPolicy>(add.Policy);
        // The pin targets the acquired version's opaque folder id.
        var container = harness.Repo.Get(add.ContainerId);
        var acquired = container!.Versions.Single(
            v => v.VersionString == harness.Acquisition.NextVersionString);
        Assert.Equal(acquired.Folder, pinned.VersionId);
    }

    // ---- ProfileAdd completion matrix ---------------------------------------

    [Fact]
    public void Completion_existing_container_rewrites_policy_via_setmodpolicy()
    {
        // The pinned-to-old-then-click-head case: the container is already in
        // the profile (pinned); the click must win, so the policy is rewritten
        // through SetModPolicy (AddMod would no-op and keep the old pin).
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, folderOld, _) = harness.SeedKnownMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = new PinnedPolicy(folderOld),
        });

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownHeadFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Empty(harness.Profiles.AddModCalls);
        var set = Assert.Single(harness.Profiles.SetModPolicyCalls);
        Assert.Equal(profile.Id, set.Id);
        Assert.Equal(container.Id, set.ContainerId);
        Assert.IsType<LatestPolicy>(set.Policy);
    }

    [Fact]
    public void Completion_reloads_only_when_the_target_is_still_active()
    {
        var harness = new QueueHarness();
        var active = harness.AddProfile("Active");
        var background = harness.AddProfile("Background");
        harness.Session.ActiveProfileId = active.Id;

        var item = harness.Queue.Enqueue(AddRequest(background.Id, fileId: KnownOldFileId));

        Assert.True(WaitUntil(() => item.IsTerminal));
        // Registered into the background profile; nothing reloaded for it.
        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(background.Id, add.Id);
        Assert.Equal(0, harness.Refresh.Reloads);
    }

    [Fact]
    public void Completion_target_profile_deleted_mid_flight_fails_inline()
    {
        // The hit path isolates the failure to the completion step (no network
        // at all): the mod is local, but the profile it was enqueued for is
        // gone.
        var harness = new QueueHarness();
        var (container, _, _) = harness.SeedKnownMod();
        var gone = Guid.NewGuid();

        var item = harness.Queue.Enqueue(
            AddRequest(gone, fileId: KnownOldFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.Phase == DownloadPhase.Failed));
        Assert.Equal(Localization["ModDownloadQueue_ProfileDeletedMessage"], item.ErrorMessage);
        // The failed row stays for dismiss/retry; nothing was registered.
        Assert.Contains(item, harness.Queue.Items);
        Assert.Empty(harness.Profiles.AddModCalls);
        Assert.Empty(harness.Acquisition.Calls);
    }

    [Fact]
    public void Completion_mod_removed_mid_flight_fails_with_the_removed_message()
    {
        // The removed-mod race: the membership read says in-profile, then
        // SetModPolicy throws KeyNotFoundException (its contract covers BOTH
        // an unknown profile and a container missing from the list). The
        // profile was verified one call earlier, so the row must say the mod
        // was removed, not that the profile is gone.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, folderOld, _) = harness.SeedKnownMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = new PinnedPolicy(folderOld),
        });
        harness.Profiles.SetModPolicyThrows =
            new KeyNotFoundException("No container in profile");

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownHeadFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.Phase == DownloadPhase.Failed));
        Assert.Equal(Localization["ModDownloadQueue_ModRemovedMessage"], item.ErrorMessage);
        Assert.Contains(item, harness.Queue.Items);
        // The removed race failed the membership rewrite only: no fresh
        // registration, no acknowledge, no reload.
        Assert.Empty(harness.Profiles.AddModCalls);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, harness.Refresh.Reloads);
        Assert.Empty(harness.Acquisition.Calls);
    }

    [Fact]
    public void Completion_acknowledge_failure_is_logged_not_fatal()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var (container, _, _) = harness.SeedKnownMod();
        harness.UpdateState.AcknowledgeThrows = new InvalidOperationException("state write failed");

        var item = harness.Queue.Enqueue(
            AddRequest(profile.Id, fileId: KnownHeadFileId, containerId: container.Id));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.Empty(harness.Queue.Items);
        Assert.Single(harness.Profiles.AddModCalls);
        Assert.Single(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(1, harness.Refresh.Reloads);
    }

    // ---- cancel -------------------------------------------------------------

    [Fact]
    public void Cancel_queued_item_removes_it_without_worker_involvement()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        var active = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 100));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));
        var queued = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 200));

        harness.Queue.Cancel(queued);

        Assert.Equal(DownloadPhase.Canceled, queued.Phase);
        Assert.DoesNotContain(queued, harness.Queue.Items);
        Assert.Contains(active, harness.Queue.Items);

        _gateLast.SetResult();
        Assert.True(WaitUntil(() => active.IsTerminal));
        // The cancelled item never reached the acquisition, and only the
        // active item completed.
        var call = Assert.Single(harness.Acquisition.Calls);
        Assert.Equal(100, call.FileId);
        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(active.ContainerId, add.ContainerId);
    }

    [Fact]
    public void Cancel_active_item_cancels_the_acquisition_with_no_side_effects()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        var item = harness.Queue.Enqueue(AddRequest(profile.Id));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        harness.Queue.Cancel(item);
        Assert.True(WaitUntil(() => item.IsTerminal));

        Assert.Equal(DownloadPhase.Canceled, item.Phase);
        Assert.Empty(harness.Queue.Items);
        Assert.Null(item.ErrorMessage);
        // No completion side effects: no registration, no acknowledge, no reload.
        Assert.Empty(harness.Profiles.AddModCalls);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, harness.Refresh.Reloads);
    }

    [Fact]
    public void Cancel_active_item_surfacing_as_ioexception_lands_canceled()
    {
        // Token-authoritative cancel: an interrupted native read surfaces as
        // IOException rather than OCE once the token fires; the item must
        // still land Canceled with no error row.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        harness.Acquisition.Steps.Enqueue(new ScriptedStep
        {
            ThrowOnCancel = new IOException("The read operation failed"),
        });
        var item = harness.Queue.Enqueue(AddRequest(profile.Id));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        harness.Queue.Cancel(item);
        Assert.True(WaitUntil(() => item.IsTerminal));

        Assert.Equal(DownloadPhase.Canceled, item.Phase);
        Assert.Null(item.ErrorMessage);
        Assert.Empty(harness.Queue.Items);
        Assert.Empty(harness.Profiles.AddModCalls);
    }

    [Fact]
    public void Cancel_terminal_item_is_a_noop()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var item = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: KnownHeadFileId));
        Assert.True(WaitUntil(() => item.IsTerminal));

        harness.Queue.Cancel(item);

        Assert.Equal(DownloadPhase.Completed, item.Phase);
    }

    // ---- dequeue-time auth gate ----------------------------------------------

    [Fact]
    public void Signout_between_enqueue_and_dequeue_fails_the_item_inline()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        var first = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 100));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));
        var second = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 200));

        // The user signs out while the first download runs.
        harness.Loader.Config = new CuratorConfig
        {
            Integrations = { Nexus = new NexusConfig() },
        };

        _gateLast.SetResult();
        Assert.True(WaitUntil(() => second.Phase == DownloadPhase.Failed));

        Assert.Equal(Localization["ModDownloadQueue_SignedOutMessage"], second.ErrorMessage);
        Assert.Contains(second, harness.Queue.Items);
        var call = Assert.Single(harness.Acquisition.Calls);
        Assert.Equal(100, call.FileId);
        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(first.ContainerId, add.ContainerId);
    }

    // ---- retry + dismiss ------------------------------------------------------

    [Fact]
    public void Retry_failed_item_enqueues_a_fresh_item_with_the_same_request()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        harness.Acquisition.Steps.Enqueue(
            new ScriptedStep { Throw = new InvalidOperationException("network down") });
        harness.Acquisition.Steps.Enqueue(new ScriptedStep());

        var failed = harness.Queue.Enqueue(AddRequest(profile.Id));
        Assert.True(WaitUntil(() => failed.Phase == DownloadPhase.Failed));
        Assert.Contains(failed, harness.Queue.Items);

        var retried = harness.Queue.Retry(failed);

        Assert.NotSame(failed, retried);
        Assert.DoesNotContain(failed, harness.Queue.Items);
        Assert.True(WaitUntil(() => retried.IsTerminal));
        Assert.Equal(2, harness.Acquisition.Calls.Count);
        var add = Assert.Single(harness.Profiles.AddModCalls);
        Assert.Equal(retried.ContainerId, add.ContainerId);
    }

    [Fact]
    public void Dismiss_removes_a_failed_row_and_ignores_anything_else()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        HoldNext(harness);
        harness.Acquisition.Steps.Enqueue(
            new ScriptedStep { Throw = new InvalidOperationException("boom") });

        var active = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 100));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));
        var queued = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 200));

        // Dismiss on a queued (non-failed) row is a no-op.
        harness.Queue.Dismiss(queued);
        Assert.Contains(queued, harness.Queue.Items);

        _gateLast.SetResult();
        Assert.True(WaitUntil(() => queued.Phase == DownloadPhase.Failed));

        harness.Queue.Dismiss(queued);
        Assert.DoesNotContain(queued, harness.Queue.Items);
        // The completed first item is already gone; dismissing it is a no-op.
        harness.Queue.Dismiss(active);
        Assert.Equal(DownloadPhase.Completed, active.Phase);
    }

    // ---- UpdateInstall ---------------------------------------------------------

    [Fact]
    public void UpdateInstall_acquires_acknowledges_once_and_raises_the_applied_event()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(new ModDownloadRequest(
            "warhammer40kdarktide", KnownModId, 300, DownloadPurpose.UpdateInstall,
            container.Id, KnownModName, profile.Id, profile.Name,
            ExpectedVersion: KnownOldVersion));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.True(WaitUntil(() => applied == 1));
        // The acknowledge cleared the flag for the updated container.
        var acknowledge = Assert.Single(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(profile.Id, acknowledge.ProfileId);
        Assert.Equal(container.Id, acknowledge.ContainerId);
        // No profile write: the mod is already a Latest member.
        Assert.Empty(harness.Profiles.AddModCalls);
        Assert.Empty(harness.Profiles.SetModPolicyCalls);
        // The applied event is the reload signal (no direct reload here).
        Assert.Equal(0, harness.Refresh.Reloads);
        Assert.Single(harness.Acquisition.Calls);
    }

    [Fact]
    public void UpdateInstall_ineligible_is_a_silent_noop()
    {
        // The installed version moved on since the flag was recorded (the
        // "version changed" rule): nothing installs, acknowledges, or raises;
        // the row resolves without a failure.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(new ModDownloadRequest(
            "warhammer40kdarktide", KnownModId, 300, DownloadPurpose.UpdateInstall,
            container.Id, KnownModName, profile.Id, profile.Name,
            ExpectedVersion: "0.9"));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.Empty(harness.Queue.Items);
        Assert.Empty(harness.Acquisition.Calls);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Enqueue_update_install_requires_container_and_expected_version()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();

        // Null expected version is a programming error (the eligibility rule's
        // input is missing); an EMPTY one is the legitimate unknown-resolution
        // install and is accepted.
        Assert.Throws<ArgumentException>(() => harness.Queue.Enqueue(new ModDownloadRequest(
            "warhammer40kdarktide", KnownModId, 300, DownloadPurpose.UpdateInstall,
            null, "Mod", profile.Id, profile.Name, ExpectedVersion: "1.0")));
        Assert.Throws<ArgumentException>(() => harness.Queue.Enqueue(new ModDownloadRequest(
            "warhammer40kdarktide", KnownModId, 300, DownloadPurpose.UpdateInstall,
            Guid.NewGuid(), "Mod", profile.Id, profile.Name)));
        Assert.NotNull(harness.Queue.Enqueue(new ModDownloadRequest(
            "warhammer40kdarktide", KnownModId, 300, DownloadPurpose.UpdateInstall,
            Guid.NewGuid(), "Mod", profile.Id, profile.Name,
            ExpectedVersion: string.Empty)));
    }

    [Fact]
    public void UpdateInstall_with_an_empty_expected_version_installs_over_an_unknown_container()
    {
        // The unknown-resolution click: a version-unknown container (its
        // latest version carries an empty tag) enqueues an UpdateInstall with
        // an empty expected version. The dequeue-time revalidation must treat
        // empty-vs-empty as a MATCH (never dropped as "version changed"), so
        // the item acquires, acknowledges, and raises like any update.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUnknownVersionMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(profile, container, string.Empty));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.True(WaitUntil(() => applied == 1));
        var acknowledge = Assert.Single(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(container.Id, acknowledge.ContainerId);
        Assert.Single(harness.Acquisition.Calls);
        // The acquired version really landed + the unknown state self-clears:
        // the fresh import is now the container's latest under the
        // arrival rule.
        var updated = harness.Repo.Get(container.Id)!;
        var latest = updated.Versions.Single(v => v.IsLatest);
        Assert.Equal("2.0", latest.VersionString);
    }

    [Fact]
    public void UpdateInstall_removed_candidate_is_a_silent_noop()
    {
        // The mod left the profile between the flag + the dequeue (the
        // "removed" rule): nothing installs, acknowledges, or raises; the row
        // resolves without a failure.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(profile, container, KnownOldVersion));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.Empty(harness.Queue.Items);
        Assert.Empty(harness.Acquisition.Calls);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void UpdateInstall_repinned_candidate_is_a_silent_noop()
    {
        // The user re-pinned the mod between the flag + the dequeue (the
        // "re-pinned" rule): same silent no-op as a removed candidate.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        var pinnedFolder = container.Versions.Single(v => v.VersionString == KnownOldVersion).Folder;
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = new PinnedPolicy(pinnedFolder),
        });
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(profile, container, KnownOldVersion));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        Assert.Empty(harness.Acquisition.Calls);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void UpdateInstall_acquisition_failure_fails_the_row_without_acknowledging()
    {
        // A download failure is row-hosted (the Failed phase with dismiss +
        // retry); it never acknowledges + never raises the applied event.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        harness.Acquisition.Steps.Enqueue(
            new ScriptedStep { Throw = new InvalidOperationException("network down") });
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(profile, container, KnownOldVersion));

        Assert.True(WaitUntil(() => item.Phase == DownloadPhase.Failed));
        Assert.Equal("network down", item.ErrorMessage);
        Assert.Contains(item, harness.Queue.Items); // the row stays for retry
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void UpdateInstall_cancel_active_propagates_to_the_acquisition_with_no_side_effects()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var container = harness.SeedUpdateTargetMod();
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        HoldNext(harness);
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(profile, container, KnownOldVersion));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));

        harness.Queue.Cancel(item);
        Assert.True(WaitUntil(() => item.IsTerminal));

        Assert.Equal(DownloadPhase.Canceled, item.Phase);
        Assert.Empty(harness.Queue.Items);
        Assert.Null(item.ErrorMessage);
        Assert.Empty(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(0, applied);
        Assert.Equal(0, harness.Refresh.Reloads);
    }

    [Fact]
    public void UpdateInstall_completion_for_a_background_profile_still_acknowledges_and_raises()
    {
        // An in-flight item completing after a profile switch (or enqueued for
        // any non-active target) observes ITS captured profile: the
        // acknowledge lands on the target's persisted entry + the applied
        // event fires (the list VM reloads whatever profile is showing).
        var harness = new QueueHarness();
        var active = harness.AddProfile("Active");
        var background = harness.AddProfile("Background");
        var container = harness.SeedUpdateTargetMod();
        harness.Profiles.WithMods(background.Id, new ModListEntry
        {
            ContainerId = container.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        harness.Session.ActiveProfileId = active.Id;
        var applied = 0;
        harness.Queue.UpdatesApplied += (_, _) => applied++;

        var item = harness.Queue.Enqueue(UpdateRequest(background, container, KnownOldVersion));

        Assert.True(WaitUntil(() => item.IsTerminal));
        Assert.Equal(DownloadPhase.Completed, item.Phase);
        var acknowledge = Assert.Single(harness.UpdateState.AcknowledgeCalls);
        Assert.Equal(background.Id, acknowledge.ProfileId);
        Assert.True(WaitUntil(() => applied == 1));
        // The applied event (not a direct reload) is the signal; the target is
        // not the active profile, so the queue itself reloaded nothing.
        Assert.Equal(0, harness.Refresh.Reloads);
    }

    [Fact]
    public void Mixed_nxm_and_update_clicks_share_the_serial_worker_without_overlap()
    {
        // One engine: an nxm ProfileAdd download + a premium UpdateInstall
        // (the automatic batch's admission shape) never hold two acquisitions
        // at once; the queue's single worker is the only gate. The update
        // targets a DIFFERENT mod than the nxm click, so neither item can
        // make the other's dequeue-time eligibility stale.
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var updateMod = harness.Repo.CreateContainer(new NexusSource { ModId = 20 }, "Update Target");
        harness.Repo.AddVersion(
            updateMod.Id, KnownOldVersion, _ => { },
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), KnownOldFileId);
        harness.Profiles.WithMods(profile.Id, new ModListEntry
        {
            ContainerId = updateMod.Id,
            Enabled = true,
            Policy = ModVersionPolicy.Latest,
        });
        HoldNext(harness);
        harness.Acquisition.Steps.Enqueue(new ScriptedStep());

        var nxm = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: 999));
        Assert.True(WaitUntil(() => harness.Acquisition.InFlight == 1));
        var update = harness.Queue.Enqueue(UpdateRequest(profile, updateMod, KnownOldVersion, modId: 20));
        Assert.Equal(DownloadPhase.Queued, update.Phase);

        _gateLast.SetResult();
        Assert.True(WaitUntil(() => update.IsTerminal));

        Assert.Equal(1, harness.Acquisition.MaxInFlight);
        Assert.Equal(new[] { 999, 300 }, harness.Acquisition.Calls.Select(c => c.FileId));
    }

    // ---- events ---------------------------------------------------------------

    [Fact]
    public void ItemChanged_fires_on_admission_resolve_and_the_terminal_transition()
    {
        var harness = new QueueHarness();
        var profile = harness.AddProfile();
        var raised = 0;
        harness.Queue.ItemChanged += _ => raised++;

        var item = harness.Queue.Enqueue(AddRequest(profile.Id, fileId: KnownHeadFileId));

        Assert.Equal(1, raised);
        // Wait for BOTH signals: IsTerminal flips inside the marshaled phase
        // write while the terminal OnItemChanged is a separate marshaled post,
        // so a poll can observe terminal while raised is still 2.
        Assert.True(WaitUntil(() => raised == 3 && item.IsTerminal));
    }

    // ---- fixture + helpers ------------------------------------------------------

    private const int KnownModId = 8;
    private const int KnownOldFileId = 100;
    private const int KnownHeadFileId = 200;
    private const string KnownOldVersion = "1.0";
    private const string KnownHeadVersion = "2.0";
    private const string KnownModName = "Known Mod";

    /// <summary>
    /// The gate of the most recent HoldNext step, so tests release the held
    /// acquisition without threading the TCS around. Instance state: xUnit
    /// creates a fresh test-class instance per test.
    /// </summary>
    private TaskCompletionSource _gateLast = null!;

    private TaskCompletionSource HoldNext(QueueHarness harness)
    {
        _gateLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Acquisition.Steps.Enqueue(new ScriptedStep { Hold = _gateLast });
        return _gateLast;
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(30));

    /// <summary>The marshal seams the harness accepts (inline or deferred).</summary>
    private interface IMarshalSeam
    {
        void Invoke(Action action);
    }

    private static ModDownloadRequest AddRequest(
        Guid profileId,
        int fileId = KnownOldFileId,
        Guid? containerId = null,
        string? name = "Some Mod",
        string gameDomain = "warhammer40kdarktide") =>
        new(
            gameDomain, KnownModId, fileId, DownloadPurpose.ProfileAdd,
            containerId,
            name ?? Localization.Format("Nxm_ModNameFallback", KnownModId),
            profileId, "Target",
            NxmKey: "KEY", NxmExpires: 123L);

    /// <summary>
    /// A premium update-install request: the update front's admission shape
    /// (file 300 never exists in the seeded repo, so the item exercises the
    /// acquisition path).
    /// </summary>
    private static ModDownloadRequest UpdateRequest(
        ProfileSummary profile, ModContainer container, string expectedVersion, int modId = KnownModId) =>
        new(
            "warhammer40kdarktide", modId, 300, DownloadPurpose.UpdateInstall,
            container.Id, container.Name, profile.Id, profile.Name,
            ExpectedVersion: expectedVersion);

    /// <summary>
    /// The queue's in-memory dependencies: the scripted acquisition (with repo
    /// mirroring so an acquired version really lands), the shared profile /
    /// repository / update-state fakes, and the inline lock-serializing marshal
    /// (or the deferred marshal for the thread-affinity test).
    /// </summary>
    private sealed class QueueHarness
    {
        public QueueHarness(IMarshalSeam? deferredMarshal = null)
        {
            Marshal = deferredMarshal ?? new LockingMarshal();
            Loader.Config = new CuratorConfig
            {
                Integrations =
                {
                    Nexus = new NexusConfig { AuthMethod = NexusAuthMethod.OAuth },
                },
            };
            Acquisition = new ScriptedAcquisitionService(Repo);
            Queue = new ModDownloadQueue(
                Acquisition, Repo, Profiles, Session, UpdateState, Loader,
                () => Refresh,
                Localization, Marshal.Invoke, NullLogger<ModDownloadQueue>.Instance);
        }

        public ScriptedAcquisitionService Acquisition { get; }
        public FakeModRepository Repo { get; } = new();
        public FakeProfileService Profiles { get; } = new();
        public FakeProfileSession Session { get; } = new();
        public FakeUpdateStateStore UpdateState { get; } = new();
        public FakeConfigLoader Loader { get; } = new();
        public RefreshRecorder Refresh { get; } = new();
        public IMarshalSeam Marshal { get; }
        public ModDownloadQueue Queue { get; }

        public ProfileSummary AddProfile(string name = "Target")
        {
            var summary = Profiles.WithProfile(name);
            Session.ActiveProfileId ??= summary.Id;
            return summary;
        }

        /// <summary>
        /// Seeds a known Nexus mod with two MAIN versions: an older "1.0"
        /// (file 100) and the head "2.0" (file 200, IsLatest under the
        /// arrival rule). Returns the container + both version
        /// folder ids.
        /// </summary>
        public (ModContainer Container, string FolderOld, string FolderHead) SeedKnownMod()
        {
            var container = Repo.CreateContainer(
                new NexusSource { ModId = KnownModId }, KnownModName);
            var old = Repo.AddVersion(
                container.Id, KnownOldVersion, _ => { },
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), KnownOldFileId);
            var head = Repo.AddVersion(
                container.Id, KnownHeadVersion, _ => { },
                new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), KnownHeadFileId);
            return (
                head,
                old.Versions.Single(v => v.VersionString == KnownOldVersion).Folder,
                head.Versions.Single(v => v.VersionString == KnownHeadVersion).Folder);
        }

        /// <summary>
        /// Seeds the update-install target: a Nexus mod whose only version is
        /// "1.0" (file 100, the resolved Latest). An update flag recorded
        /// against "1.0" is still eligible; the scripted acquisition adds
        /// "2.0" over it.
        /// </summary>
        public ModContainer SeedUpdateTargetMod()
        {
            var container = Repo.CreateContainer(
                new NexusSource { ModId = KnownModId }, KnownModName);
            Repo.AddVersion(
                container.Id, KnownOldVersion, _ => { },
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), KnownOldFileId);
            return Repo.Get(container.Id)!;
        }

        /// <summary>
        /// Seeds a version-unknown target: a Nexus mod whose only version
        /// carries an EMPTY tag (an association recorded without a version
        /// stamp). The scripted acquisition adds "2.0" over it.
        /// </summary>
        public ModContainer SeedUnknownVersionMod()
        {
            var container = Repo.CreateContainer(
                new NexusSource { ModId = KnownModId }, KnownModName);
            Repo.AddVersion(container.Id, string.Empty, _ => { });
            return Repo.Get(container.Id)!;
        }
    }

    /// <summary>One scripted acquisition call: progress to pump, a hold gate,
    /// and throws (immediate, or cancel-wrapped).</summary>
    private sealed class ScriptedStep
    {
        public List<(long Received, long? Total)>? Progress { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public Exception? Throw { get; set; }

        /// <summary>
        /// When set, the call waits for the caller's cancel token and then
        /// throws this exception: simulates a cancel surfacing as a wrapped
        /// abort (an interrupted native read) rather than OCE.
        /// </summary>
        public Exception? ThrowOnCancel { get; set; }
    }

    /// <summary>
    /// A configurable <see cref="IModAcquisitionService"/> for the queue tests.
    /// Each call consumes one scripted step, falling back to an immediate
    /// success. Mirrors the real import: the acquired version really lands in
    /// the wired repository under the requested file id, so name swaps +
    /// subsequent hit checks observe real state. Tracks in-flight concurrency
    /// so tests assert the one-at-a-time contract.
    /// </summary>
    private sealed class ScriptedAcquisitionService : IModAcquisitionService
    {
        private readonly FakeModRepository _repo;
        private int _inFlight;

        public ScriptedAcquisitionService(FakeModRepository repo) => _repo = repo;

        public ConcurrentQueue<ScriptedStep> Steps { get; } = new();
        public string NextModName { get; set; } = "Resolved Mod";
        public string NextVersionString { get; set; } = "2.0";
        public bool NextIsHead { get; set; } = true;

        public List<(string GameDomain, int ModId, int FileId, string? NxmKey, long? NxmExpires, Guid ContainerId)> Calls
        { get; } = new();

        public int InFlight => Volatile.Read(ref _inFlight);
        public int MaxInFlight { get; private set; }

        public async Task<NexusAcquisitionResult> AcquireFromNexusAsync(
            string gameDomain, int modId, int fileId,
            string? nxmKey = null, long? nxmExpires = null,
            IProgress<(long Received, long? Total)>? progress = null,
            CancellationToken ct = default)
        {
            Calls.Add((gameDomain, modId, fileId, nxmKey, nxmExpires, Guid.Empty));
            var entered = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, entered);
            try
            {
                var step = Steps.TryDequeue(out var scripted) ? scripted : new ScriptedStep();
                if (step.Progress is not null)
                {
                    foreach (var report in step.Progress)
                    {
                        progress?.Report(report);
                    }
                }
                if (step.Hold is { } hold)
                {
                    await hold.Task.WaitAsync(ct);
                }
                if (step.ThrowOnCancel is { } wrapped)
                {
                    var cancelObserved = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => cancelObserved.TrySetResult()))
                    {
                        await cancelObserved.Task;
                    }
                    throw wrapped;
                }
                if (step.Throw is not null)
                {
                    throw step.Throw;
                }

                // Mirror the import: upsert the version onto the container
                // under the requested file id.
                var container = _repo.FindBySource(new NexusSource { ModId = modId })
                    ?? _repo.CreateContainer(new NexusSource { ModId = modId }, NextModName);
                var updated = _repo.AddVersion(
                    container.Id, NextVersionString, _ => { },
                    DateTimeOffset.UtcNow, fileId);
                var version = updated.Versions.Single(v => v.VersionString == NextVersionString);
                Calls[^1] = (gameDomain, modId, fileId, nxmKey, nxmExpires, container.Id);
                return new NexusAcquisitionResult(container.Id, version.Folder, NextVersionString, NextIsHead);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<NexusAcquisitionResult> AcquireLatestNexusAsync(
            string gameDomain, int modId,
            IProgress<(long Received, long? Total)>? progress = null,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<(int FileId, string Version)> ResolveLatestNexusAsync(
            string gameDomain, int modId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// An inline marshal seam: runs each action immediately on the calling
    /// thread, serialized under a lock (the single worker + the test thread
    /// both mutate the item collection through it). Recording thread ids keeps
    /// the seam observable.
    /// </summary>
    private sealed class LockingMarshal : IMarshalSeam
    {
        private readonly object _gate = new();

        public void Invoke(Action action)
        {
            lock (_gate)
            {
                action();
            }
        }
    }

    /// <summary>
    /// A deferring marshal seam: queues every action for the test thread's
    /// <see cref="DrainUntil"/> to execute, proving the queue publishes
    /// nothing on the calling thread.
    /// </summary>
    private sealed class DeferredMarshal : IMarshalSeam
    {
        private readonly ConcurrentQueue<Action> _actions = new();

        public ConcurrentQueue<int> ExecutedOnThreadIds { get; } = new();

        public void Invoke(Action action) => _actions.Enqueue(action);

        public void DrainUntil(Func<bool> condition)
        {
            if (!SpinWait.SpinUntil(() =>
                    {
                        while (_actions.TryDequeue(out var action))
                        {
                            ExecutedOnThreadIds.Enqueue(Environment.CurrentManagedThreadId);
                            action();
                        }
                        return condition();
                    },
                    TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("DrainUntil condition not reached.");
            }
        }
    }
}
