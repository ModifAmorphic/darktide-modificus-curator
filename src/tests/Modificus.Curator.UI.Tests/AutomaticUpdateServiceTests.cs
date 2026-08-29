using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="AutomaticUpdateService"/> behaviors: gating, the enqueue batch
/// (one UpdateInstall item per flagged candidate, through the real
/// <see cref="ModUpdateEnqueuer"/> over the acquisition + queue fakes),
/// resolve-failure isolation, the stop/cancel-on-profile-switch policy, and
/// cancellation propagation. The queue-side install contracts (eligibility
/// revalidation, acknowledge-once, failure rows, cancel semantics, serial
/// non-overlap) are covered by the download-queue tests.
/// </summary>
public sealed class AutomaticUpdateServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds the service over fresh fakes + returns them so each test drives
    /// the gating + scheduling. The profile has one Nexus+Latest mod by
    /// default; tests adjust the fakes per case.
    /// </summary>
    private static (AutomaticUpdateService Service, FakeProfileSession Session, FakeProfileService Profiles, FakeModRepository Repo, FakeModAcquisitionService Acquisition, FakeModDownloadQueue Queue, FakeNexusAuthService Auth, FakeConfigLoader Config, FakeDialogService Dialogs)
        Build(bool premium = true, bool enabled = true)
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var acquisition = new FakeModAcquisitionService();
        var queue = new FakeModDownloadQueue();
        var auth = new FakeNexusAuthService
        {
            State = premium
                ? new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true)
                : new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AutomaticUpdatesEnabled = enabled;
        var dialogs = new FakeDialogService();

        var service = new AutomaticUpdateService(
            session,
            new ModUpdateEnqueuer(acquisition, queue, profiles),
            queue,
            auth,
            config,
            dialogs,
            Localization,
            NullLogger<AutomaticUpdateService>.Instance);
        return (service, session, profiles, repo, acquisition, queue, auth, config, dialogs);
    }

    private static UpdateCheckResult Success(params ModUpdateInfo[] updates) =>
        new(updates, DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success);

    private static UpdateCheckResult OutcomeResult(CheckOutcome outcome) =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false, Outcome: outcome);

    private static ModUpdateInfo Update(Guid containerId, int modId, string name, string version) =>
        new(containerId, modId, name, version, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RunAfterCheck_enqueues_one_update_install_per_flagged_candidate()
    {
        // Two flagged mods: both admitted as UpdateInstall items through the
        // enqueue front, in result order, each targeting the checked profile.
        var (service, session, profiles, repo, _, queue, _, _, _) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });

        await service.RunAfterCheckAsync(
            Success(Update(c1, 10, "Mod10", "1.0"), Update(c2, 11, "Mod11", "1.0")),
            session.ActiveProfileId!.Value);

        Assert.Equal(2, queue.Requests.Count);
        Assert.Collection(queue.Requests,
            r =>
            {
                Assert.Equal(DownloadPurpose.UpdateInstall, r.Purpose);
                Assert.Equal(c1, r.ContainerId);
                Assert.Equal(10, r.ModId);
                Assert.Equal("1.0", r.ExpectedVersion);
                Assert.Equal(session.ActiveProfileId, r.TargetProfileId);
            },
            r => Assert.Equal(c2, r.ContainerId));
        Assert.Equal(2, queue.Items.Count);
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_setting_disabled()
    {
        var (service, session, _, repo, acquisition, queue, auth, _, _) = Build(enabled: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(Update(nexusId, 8, "DMF", "1.0")), session.ActiveProfileId!.Value);

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        // Premium was NOT verified (the gate failed before the premium check).
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_non_authoritative_and_rate_limited_results()
    {
        foreach (var outcome in new[] { CheckOutcome.NoAuth, CheckOutcome.RateLimited, CheckOutcome.Failed, CheckOutcome.NoNexusMods })
        {
            var (service, session, _, _, acquisition, queue, auth, _, _) = Build();
            await service.RunAfterCheckAsync(OutcomeResult(outcome), session.ActiveProfileId!.Value);
            Assert.Empty(queue.Requests);
            Assert.Empty(acquisition.ResolveLatestCalls);
            Assert.Equal(0, auth.GetCurrentStateCallCount); // gated before the premium check
        }
    }

    [Fact]
    public async Task RunAfterCheck_skips_a_successful_result_with_no_updates()
    {
        var (service, session, _, _, acquisition, queue, auth, _, _) = Build();

        await service.RunAfterCheckAsync(Success(), session.ActiveProfileId!.Value);

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_verifies_premium_fresh_only_when_gated()
    {
        // A successful result with updates + enabled: premium is verified.
        var (service, session, _, repo, _, _, auth, _, _) = Build();
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(Update(nexusId, 8, "DMF", "1.0")), session.ActiveProfileId!.Value);

        Assert.Equal(1, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_fresh_premium_check_returns_non_premium()
    {
        var (service, session, _, repo, acquisition, queue, auth, _, _) = Build(premium: true);
        // Override the auth state to non-premium AFTER construction (the fresh
        // check at run time returns non-premium).
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "lapsed", IsPremium: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(Update(nexusId, 8, "DMF", "1.0")), session.ActiveProfileId!.Value);

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
    }

    [Fact]
    public async Task RunAfterCheck_excludes_version_unknown_rows_from_the_batch()
    {
        // Version-unknown rows (an empty CurrentVersion) are manual-click-only:
        // silently installing over content Curator cannot identify is a
        // footgun. Tier 2 never flags an empty installed version, but tier 1
        // (the account download record) can, so the exclusion lives in the
        // batch: the unknown entry is skipped while the ordinary flagged entry
        // still enqueues.
        var (service, session, profiles, repo, _, queue, _, _, _) = Build();
        var unknown = repo.Seed(new NexusSource { ModId = 10 }, "Unknown", string.Empty).Id;
        var known = repo.Seed(new NexusSource { ModId = 11 }, "Known", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = unknown, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = known, Order = 2, Policy = ModVersionPolicy.Latest });

        await service.RunAfterCheckAsync(
            Success(Update(unknown, 10, "Unknown", string.Empty), Update(known, 11, "Known", "1.0")),
            session.ActiveProfileId!.Value);

        var request = Assert.Single(queue.Requests);
        Assert.Equal(known, request.ContainerId);
        Assert.Equal("1.0", request.ExpectedVersion);
    }

    [Fact]
    public async Task RunAfterCheck_isolates_resolve_failures_and_aggregates_one_alert()
    {
        // Two flagged mods; the first resolve fails (a one-shot API failure),
        // the second succeeds. The failure has no row (nothing was enqueued),
        // so it surfaces in the single aggregated alert; the second mod still
        // enqueues.
        var (service, session, profiles, repo, acquisition, queue, _, _, dialogs) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });
        acquisition.ResolveThrowQueue.Enqueue(new InvalidOperationException("api down"));

        await service.RunAfterCheckAsync(
            Success(Update(c1, 10, "Mod10", "1.0"), Update(c2, 11, "Mod11", "1.0")),
            session.ActiveProfileId!.Value);

        // The failure did not abort the batch.
        var request = Assert.Single(queue.Requests);
        Assert.Equal(c2, request.ContainerId);
        // One aggregated failure alert naming the failed mod.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("Mod10", alert.Message);
    }

    [Fact]
    public async Task RunAfterCheck_a_download_failure_is_row_hosted_and_never_alerts()
    {
        // Download failures render inline on their rows (the queue's Failed
        // phase with dismiss + retry); the batch's alert covers resolve
        // failures only, so a batch whose downloads later fail stays silent.
        var (service, session, _, repo, _, queue, _, _, dialogs) = Build();
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(Update(nexusId, 8, "DMF", "1.0")), session.ActiveProfileId!.Value);

        // The admitted item later fails on the row.
        var item = queue.Items.Single();
        item.ErrorMessage = "network reset";
        item.Phase = DownloadPhase.Failed;
        queue.Publish(item);

        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task RunAfterCheck_stops_scheduling_after_a_prior_profile_switch()
    {
        var (service, session, profiles, repo, acquisition, queue, _, _, _) = Build();
        var c1 = repo.List().First().Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c2, Order = 1, Policy = ModVersionPolicy.Latest });

        // Switch the active profile BEFORE running. The entry gate sees the
        // mismatch: no resolves, no enqueues.
        session.ActiveProfileId = Guid.NewGuid();

        await service.RunAfterCheckAsync(
            Success(Update(c1, 8, "DMF", "1.0"), Update(c2, 11, "Mod11", "1.0")),
            profiles.ListProfiles()[0].Id); // the original profile id

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
    }

    [Fact]
    public async Task RunAfterCheck_profile_switch_mid_batch_cancels_queued_but_not_active_items()
    {
        // Three flagged mods. The first two enqueue (one simulates the worker
        // having started it: Downloading); the third resolve is held while the
        // user switches profiles. The watcher cancels the still-queued item
        // only (the active one completes under its own rules), the entry
        // admitted after the switch is cancelled by the post-enqueue
        // re-check, + the batch stops scheduling.
        var (service, session, profiles, repo, acquisition, queue, _, _, dialogs) = Build();
        var c1 = repo.List().First().Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        var c3 = repo.Seed(new NexusSource { ModId = 12 }, "Mod12", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c2, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c3, Order = 2, Policy = ModVersionPolicy.Latest });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        acquisition.ResolveGates.Enqueue(null); // candidate 1 resolves freely
        acquisition.ResolveGates.Enqueue(null); // candidate 2 resolves freely
        acquisition.ResolveGates.Enqueue(gate); // candidate 3 is held
        var originalProfile = session.ActiveProfileId!.Value;

        var run = service.RunAfterCheckAsync(
            Success(Update(c1, 8, "DMF", "1.0"), Update(c2, 11, "Mod11", "1.0"), Update(c3, 12, "Mod12", "1.0")),
            originalProfile);

        // The first two candidates enqueued (the third resolve is held).
        Assert.Equal(2, queue.Items.Count);
        var active = queue.Items[0];
        active.Phase = DownloadPhase.Downloading; // the worker started it

        session.ActiveProfileId = Guid.NewGuid();
        gate.SetResult();
        await run;

        // The queued second item was cancelled; the active one untouched; the
        // third (admitted after the switch) cancelled by the re-check.
        Assert.Contains(queue.Items[1], queue.CancelCalls);
        Assert.DoesNotContain(active, queue.CancelCalls);
        Assert.Equal(DownloadPhase.Downloading, active.Phase);
        Assert.Equal(3, queue.Requests.Count); // the third resolve completed + admitted
        Assert.Contains(queue.Items[2], queue.CancelCalls);
        // No failure alert: cancels are not failures.
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task RunAfterCheck_stops_the_batch_when_the_profile_is_deleted_mid_batch()
    {
        // The enqueue front's profile read throws KeyNotFoundException when
        // the profile is gone: the batch stops, nothing further enqueues, and
        // no failure alert is surfaced for it.
        var (service, session, profiles, _, acquisition, queue, _, _, dialogs) = Build();
        var originalProfile = session.ActiveProfileId!.Value;
        profiles.GetProfileThrows = new KeyNotFoundException("gone");

        await service.RunAfterCheckAsync(
            Success(Update(Guid.NewGuid(), 8, "DMF", "1.0")), originalProfile);

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task RunAfterCheck_cancellation_from_the_resolve_propagates()
    {
        // Cancellation is not a failure: it propagates out of the batch so the
        // runner sees it (the shutdown posture), and no aggregated alert fires.
        var (service, session, _, repo, acquisition, _, _, _, dialogs) = Build();
        var nexusId = repo.List().First().Id;
        acquisition.ThrowOnResolve = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RunAfterCheckAsync(
                Success(Update(nexusId, 8, "DMF", "1.0")), session.ActiveProfileId!.Value));

        Assert.Empty(dialogs.AlertCalls);
    }
}
