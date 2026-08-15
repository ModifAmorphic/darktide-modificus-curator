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
/// <see cref="AutomaticUpdateService"/> behaviors: gating, per-mod
/// revalidation, isolation, concurrency, and profile-switch stop. The service
/// is the opt-in Premium automatic installer chained from
/// <see cref="UpdateCheckRunner"/> after each check.
/// </summary>
public sealed class AutomaticUpdateServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds the service over fresh fakes + returns them so each test drives
    /// the gating + revalidation. The profile has one Nexus+Latest mod by
    /// default; tests adjust the fakes per case.
    /// </summary>
    private static (AutomaticUpdateService Service, FakeProfileSession Session, FakeProfileService Profiles, FakeModRepository Repo, FakeModAcquisitionService Acquisition, FakeNexusAuthService Auth, FakeConfigLoader Config, FakeUpdateStateStore State, UpdateCoordinator Coordinator, FakeDialogService Dialogs)
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
        var auth = new FakeNexusAuthService
        {
            State = premium
                ? new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true)
                : new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AutomaticUpdatesEnabled = enabled;
        var state = new FakeUpdateStateStore(repo);
        var coordinator = new UpdateCoordinator();
        var dialogs = new FakeDialogService();

        var service = new AutomaticUpdateService(
            session, profiles, repo, acquisition, auth, config, state, coordinator,
            dialogs, Localization, NullLogger<AutomaticUpdateService>.Instance);
        return (service, session, profiles, repo, acquisition, auth, config, state, coordinator, dialogs);
    }

    private static UpdateCheckResult Success(params ModUpdateInfo[] updates) =>
        new(updates, DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success);

    private static UpdateCheckResult OutcomeResult(CheckOutcome outcome) =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false, Outcome: outcome);

    [Fact]
    public async Task RunAfterCheck_installs_sequentially_when_enabled_premium_with_updates()
    {
        // Two flagged mods: both installed, one at a time, each acknowledged.
        var (service, session, profiles, repo, acquisition, _, _, state, coordinator, _) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Both mods acquired (sequentially).
        Assert.Equal(2, acquisition.LatestNexusCalls.Count);
        // Both acknowledged.
        Assert.Contains(state.AcknowledgeCalls, c => c.ContainerId == c1);
        Assert.Contains(state.AcknowledgeCalls, c => c.ContainerId == c2);
        // The coordinator is not stuck busy.
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_setting_disabled()
    {
        var (service, session, _, repo, acquisition, auth, _, _, _, _) = Build(enabled: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Empty(acquisition.LatestNexusCalls);
        // Premium was NOT verified (the gate failed before the premium check).
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_non_authoritative_and_rate_limited_results()
    {
        foreach (var outcome in new[] { CheckOutcome.NoAuth, CheckOutcome.RateLimited, CheckOutcome.Failed, CheckOutcome.NoNexusMods })
        {
            var (service, session, _, _, acquisition, auth, _, _, _, _) = Build();
            await service.RunAfterCheckAsync(OutcomeResult(outcome), session.ActiveProfileId!.Value);
            Assert.Empty(acquisition.LatestNexusCalls);
            Assert.Equal(0, auth.GetCurrentStateCallCount); // gated before the premium check
        }
    }

    [Fact]
    public async Task RunAfterCheck_skips_a_successful_result_with_no_updates()
    {
        var (service, session, _, _, acquisition, auth, _, _, _, _) = Build();

        await service.RunAfterCheckAsync(Success(), session.ActiveProfileId!.Value);

        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_verifies_premium_fresh_only_when_gated()
    {
        // A successful result with updates + enabled: premium is verified.
        var (service, session, _, repo, _, auth, _, _, _, _) = Build();
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Equal(1, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_fresh_premium_check_returns_non_premium()
    {
        var (service, session, _, repo, acquisition, auth, _, _, _, _) = Build(premium: true);
        // Override the auth state to non-premium AFTER construction (the fresh
        // check at run time returns non-premium).
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "lapsed", IsPremium: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task RunAfterCheck_isolates_per_mod_failures_and_aggregates_an_alert()
    {
        var (service, session, profiles, repo, acquisition, _, _, _, _, dialogs) = Build();
        // Two flagged mods; the first acquisition fails, the second succeeds.
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });
        acquisition.ThrowNext = new InvalidOperationException("boom on first");

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Both acquisitions were attempted (the first failure did not abort the
        // second). NOTE: FakeModAcquisitionService.ThrowNext throws once; the
        // second call succeeds.
        Assert.Equal(2, acquisition.LatestNexusCalls.Count);
        // One aggregated failure alert surfaced.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("Mod10", alert.Message);
    }

    [Fact]
    public async Task RunAfterCheck_stops_scheduling_after_profile_switch()
    {
        var (service, session, profiles, repo, acquisition, _, _, _, _, _) = Build();
        var c1 = repo.List().First().Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c2, Order = 1, Policy = ModVersionPolicy.Latest });

        // Switch the active profile BEFORE running. The batch sees the mismatch
        // + stops scheduling (no installs).
        session.ActiveProfileId = Guid.NewGuid();

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 8, "DMF", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            profiles.ListProfiles()[0].Id); // the original profile id

        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task RunAfterCheck_prevents_concurrency_with_manual_via_shared_coordinator()
    {
        // Acquire the shared coordinator (simulating a manual install in flight);
        // the automatic batch's per-mod AcquireAsync awaits its turn. This proves
        // the coordinator is the single mutual-exclusion point across both paths.
        var (service, session, _, repo, _, _, _, _, coordinator, _) = Build();
        var nexusId = repo.List().First().Id;
        Assert.True(coordinator.TryAcquire(out var manualScope));
        Assert.True(coordinator.IsBusy);

        // The batch runs concurrently with the held manual scope; its per-mod
        // acquire awaits. Use a cancellation token so the test does not hang
        // when the acquire blocks forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RunAfterCheckAsync(
                Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
                session.ActiveProfileId!.Value, cts.Token));

        manualScope?.Dispose();
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task RunAfterCheck_raises_UpdatesApplied_when_an_install_succeeded()
    {
        var (service, session, _, repo, _, _, _, _, _, _) = Build();
        var nexusId = repo.List().First().Id;
        var raised = 0;
        service.UpdatesApplied += (_, _) => raised++;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Equal(1, raised);
    }

    // ---- per-mod progress events -------------------------------------------

    [Fact]
    public async Task RunAfterCheck_emits_start_then_stop_progress_for_a_successful_install()
    {
        var (service, session, _, repo, _, _, _, _, _, _) = Build();
        var nexusId = repo.List().First().Id;
        var progress = new List<ModUpdateProgressEventArgs>();
        service.ModUpdateProgress += (_, e) => progress.Add(e);

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Exactly one start (active=true) then one stop (active=false) for the
        // installed container, in that order.
        Assert.Equal(2, progress.Count);
        Assert.Equal(nexusId, progress[0].ContainerId);
        Assert.True(progress[0].IsActive);
        Assert.Equal(nexusId, progress[1].ContainerId);
        Assert.False(progress[1].IsActive);
    }

    [Fact]
    public async Task RunAfterCheck_emits_start_then_stop_progress_even_on_failure()
    {
        // A per-mod failure must NOT leave progress active: the finally block
        // emits active=false regardless of the outcome.
        var (service, session, profiles, repo, acquisition, _, _, _, _, _) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest });
        acquisition.ThrowNext = new InvalidOperationException("boom");
        var progress = new List<ModUpdateProgressEventArgs>();
        service.ModUpdateProgress += (_, e) => progress.Add(e);

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Start then stop for the failed container (the finally fired).
        Assert.Equal(2, progress.Count);
        Assert.True(progress[0].IsActive);
        Assert.False(progress[1].IsActive);
        Assert.Equal(c1, progress[0].ContainerId);
    }

    [Fact]
    public async Task RunAfterCheck_progress_moves_row_by_row_across_a_sequential_batch()
    {
        // Two flagged mods: the spinner moves from the first to the second
        // (start1, stop1, start2, stop2), never overlapping.
        var (service, session, profiles, repo, _, _, _, _, _, _) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });
        var progress = new List<ModUpdateProgressEventArgs>();
        service.ModUpdateProgress += (_, e) => progress.Add(e);

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // start1, stop1, start2, stop2.
        Assert.Equal(4, progress.Count);
        Assert.Equal(c1, progress[0].ContainerId);
        Assert.True(progress[0].IsActive);
        Assert.Equal(c1, progress[1].ContainerId);
        Assert.False(progress[1].IsActive);
        Assert.Equal(c2, progress[2].ContainerId);
        Assert.True(progress[2].IsActive);
        Assert.Equal(c2, progress[3].ContainerId);
        Assert.False(progress[3].IsActive);
    }
}
