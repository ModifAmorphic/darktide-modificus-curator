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
/// <see cref="AutomaticUpdateService"/> behaviors: gating, the sequential
/// batch, per-mod failure isolation, and profile-switch stop. The service is
/// the opt-in Premium automatic installer chained from
/// <see cref="UpdateCheckRunner"/> after each check; each install routes
/// through the shared <see cref="IModUpdateInstaller"/> (the installer's own
/// gating, revalidation, acknowledge, progress, and cancellation contracts are
/// covered by the Integrations-layer ModUpdateInstaller tests).
/// </summary>
public sealed class AutomaticUpdateServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds the service over fresh fakes + returns them so each test drives
    /// the gating + scheduling. The profile has one Nexus+Latest mod by
    /// default; tests adjust the fakes per case.
    /// </summary>
    private static (AutomaticUpdateService Service, FakeProfileSession Session, FakeProfileService Profiles, FakeModRepository Repo, FakeModUpdateInstaller Installer, FakeNexusAuthService Auth, FakeConfigLoader Config, FakeUpdateStateStore State, FakeDialogService Dialogs)
        Build(bool premium = true, bool enabled = true)
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var installer = new FakeModUpdateInstaller();
        var auth = new FakeNexusAuthService
        {
            State = premium
                ? new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true)
                : new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AutomaticUpdatesEnabled = enabled;
        var state = new FakeUpdateStateStore(repo);
        var dialogs = new FakeDialogService();

        var service = new AutomaticUpdateService(
            session, profiles, installer, auth, config,
            dialogs, Localization, NullLogger<AutomaticUpdateService>.Instance);
        return (service, session, profiles, repo, installer, auth, config, state, dialogs);
    }

    private static UpdateCheckResult Success(params ModUpdateInfo[] updates) =>
        new(updates, DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success);

    private static UpdateCheckResult OutcomeResult(CheckOutcome outcome) =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false, Outcome: outcome);

    [Fact]
    public async Task RunAfterCheck_installs_sequentially_when_enabled_premium_with_updates()
    {
        // Two flagged mods: both installed through the installer, one at a
        // time, via the AWAITING install shape (the batch waits its turn under
        // the shared gate rather than refusing).
        var (service, session, profiles, repo, installer, _, _, _, _) = Build();
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Both installs went through the installer, in result order.
        Assert.Equal(2, installer.Calls.Count);
        Assert.Equal(c1, installer.Calls[0].ContainerId);
        Assert.Equal(c2, installer.Calls[1].ContainerId);
        Assert.All(installer.Calls, c => Assert.Equal(nameof(FakeModUpdateInstaller.InstallLatestAsync), c.Method));
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_setting_disabled()
    {
        var (service, session, _, repo, installer, auth, _, _, _) = Build(enabled: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Empty(installer.Calls);
        // Premium was NOT verified (the gate failed before the premium check).
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_non_authoritative_and_rate_limited_results()
    {
        foreach (var outcome in new[] { CheckOutcome.NoAuth, CheckOutcome.RateLimited, CheckOutcome.Failed, CheckOutcome.NoNexusMods })
        {
            var (service, session, _, _, installer, auth, _, _, _) = Build();
            await service.RunAfterCheckAsync(OutcomeResult(outcome), session.ActiveProfileId!.Value);
            Assert.Empty(installer.Calls);
            Assert.Equal(0, auth.GetCurrentStateCallCount); // gated before the premium check
        }
    }

    [Fact]
    public async Task RunAfterCheck_skips_a_successful_result_with_no_updates()
    {
        var (service, session, _, _, installer, auth, _, _, _) = Build();

        await service.RunAfterCheckAsync(Success(), session.ActiveProfileId!.Value);

        Assert.Empty(installer.Calls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_verifies_premium_fresh_only_when_gated()
    {
        // A successful result with updates + enabled: premium is verified.
        var (service, session, _, repo, _, auth, _, _, _) = Build();
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Equal(1, auth.GetCurrentStateCallCount);
    }

    [Fact]
    public async Task RunAfterCheck_skips_when_fresh_premium_check_returns_non_premium()
    {
        var (service, session, _, repo, installer, auth, _, _, _) = Build(premium: true);
        // Override the auth state to non-premium AFTER construction (the fresh
        // check at run time returns non-premium).
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "lapsed", IsPremium: false);
        var nexusId = repo.List().First().Id;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task RunAfterCheck_isolates_per_mod_failures_and_aggregates_an_alert()
    {
        var (service, session, profiles, repo, installer, _, _, _, dialogs) = Build();
        // Two flagged mods; the first install fails, the second succeeds.
        var c1 = repo.Seed(new NexusSource { ModId = 10 }, "Mod10", "1.0").Id;
        var c2 = repo.Seed(new NexusSource { ModId = 11 }, "Mod11", "1.0").Id;
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = c1, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 2, Policy = ModVersionPolicy.Latest });
        installer.OutcomeQueue.Enqueue(new ModInstallOutcome(
            ModInstallStatus.Failed, "boom on first", new InvalidOperationException("boom on first")));
        installer.OutcomeQueue.Enqueue(new ModInstallOutcome(ModInstallStatus.Installed));

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(c1, 10, "Mod10", "1.0", DateTimeOffset.UtcNow),
            new ModUpdateInfo(c2, 11, "Mod11", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        // Both installs were attempted (the first failure did not abort the
        // second).
        Assert.Equal(2, installer.Calls.Count);
        // One aggregated failure alert surfaced.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("Mod10", alert.Message);
    }

    [Fact]
    public async Task RunAfterCheck_stops_scheduling_after_profile_switch()
    {
        var (service, session, profiles, repo, installer, _, _, _, _) = Build();
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

        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task RunAfterCheck_stops_the_batch_when_the_profile_is_deleted_mid_batch()
    {
        // The per-iteration candidate re-pull throws KeyNotFoundException when
        // the profile is gone: the batch stops, nothing is installed, no
        // failure alert is surfaced for it.
        var (service, session, profiles, _, installer, _, _, _, dialogs) = Build();
        var originalProfile = session.ActiveProfileId!.Value;
        profiles.GetModListThrows = new KeyNotFoundException("gone");

        await service.RunAfterCheckAsync(Success(
            new ModUpdateInfo(Guid.NewGuid(), 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            originalProfile);

        Assert.Empty(installer.Calls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task RunAfterCheck_raises_UpdatesApplied_when_an_install_succeeded()
    {
        var (service, session, _, repo, _, _, _, _, _) = Build();
        var nexusId = repo.List().First().Id;
        var raised = 0;
        service.UpdatesApplied += (_, _) => raised++;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RunAfterCheck_does_not_raise_UpdatesApplied_when_an_install_was_not_eligible()
    {
        // A NotEligible outcome (a stale flag the installer's in-gate
        // revalidation rejected) installed nothing: no reload signal.
        var (service, session, _, repo, installer, _, _, _, _) = Build();
        var nexusId = repo.List().First().Id;
        installer.NextOutcome = new ModInstallOutcome(ModInstallStatus.NotEligible, "version changed");
        var raised = 0;
        service.UpdatesApplied += (_, _) => raised++;

        await service.RunAfterCheckAsync(
            Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
            session.ActiveProfileId!.Value);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RunAfterCheck_cancellation_from_the_installer_propagates()
    {
        // Cancellation is not a failure: it propagates out of the batch so the
        // runner sees it (the shutdown posture), and no aggregated alert fires.
        var (service, session, _, repo, installer, _, _, _, dialogs) = Build();
        var nexusId = repo.List().First().Id;
        installer.ThrowNext = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RunAfterCheckAsync(
                Success(new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow)),
                session.ActiveProfileId!.Value));

        Assert.Empty(dialogs.AlertCalls);
    }
}
