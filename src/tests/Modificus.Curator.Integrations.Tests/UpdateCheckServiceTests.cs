using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises <see cref="UpdateCheckService"/> against canned v2 GraphQL
/// responses and in-memory profile/repository fakes: the LatestPolicy +
/// NexusSource filter, the one-API-call-per-check contract, the
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> mapping (true flags,
/// false + null do not), the rate-limit guard (thrown exception + header-based,
/// including the all-zero <see cref="NexusRateLimits.Unknown"/> fallback), the
/// no-auth short-circuit, the no-checkable-mods short-circuit, the best-effort
/// failure path (API exception does not propagate), and the
/// <see cref="IUpdateCheckService.LastResult"/> +
/// <see cref="IUpdateCheckService.CheckCompleted"/> publishing contract.
/// </summary>
public sealed class UpdateCheckServiceTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static readonly Guid NexusLatestContainer = Guid.NewGuid();
    private static readonly Guid NexusUnlistedContainer = Guid.NewGuid();
    private static readonly Guid NexusPinnedContainer = Guid.NewGuid();
    private static readonly Guid UntrackedContainer = Guid.NewGuid();

    private const int UpdatedModId = 100;
    private const int UnlistedModId = 200;
    private const int PinnedModId = 300;

    // Must match UpdateCheckService.GameId (Darktide = 4943).
    private const int GameId = 4943;

    // ---- happy path + flagging --------------------------------------------

    [Fact]
    public async Task CheckAsync_flags_mods_where_viewerUpdateAvailable_is_true()
    {
        // Three mods in the profile (all Nexus + Latest):
        //  (a) viewerUpdateAvailable = true  -> flagged.
        //  (b) viewerUpdateAvailable = true  -> flagged.
        //  (c) viewerUpdateAvailable = false -> not flagged.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: true),
                    Status(UnlistedModId, viewerUpdateAvailable: true),
                    Status(PinnedModId, viewerUpdateAvailable: false),
                },
                NexusRateLimits.Unknown),
        };

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Updated Mod", "1.0");
        var secondContainer = Guid.NewGuid();
        repository.Containers[secondContainer] =
            NexusContainer(secondContainer, UnlistedModId, "Unlisted Mod", "2.0");
        var thirdContainer = Guid.NewGuid();
        repository.Containers[thirdContainer] =
            NexusContainer(thirdContainer, PinnedModId, "Pinned Mod", "3.0");

        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
                Entry(thirdContainer, new LatestPolicy()),
            },
        };

        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Equal(2, result.Updates.Count);
        Assert.Contains(result.Updates, u => u.ContainerId == NexusLatestContainer && u.ModId == UpdatedModId);
        Assert.Contains(result.Updates, u => u.ContainerId == secondContainer && u.ModId == UnlistedModId);
        Assert.DoesNotContain(result.Updates, u => u.ModId == PinnedModId);
        Assert.False(result.RateLimited);

        // Exactly 1 API call regardless of how many mods are in the profile.
        Assert.Equal(1, nexus.GraphQlCallCount);
        // The Darktide game id + the mod ids are passed through.
        Assert.Equal(GameId, nexus.LastGameId);
        Assert.Equal(new[] { UpdatedModId, UnlistedModId, PinnedModId }, nexus.LastModIds);
    }

    [Fact]
    public async Task CheckAsync_returns_empty_when_no_mods_have_updates()
    {
        // All viewerUpdateAvailable = false (or null): nothing flags.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: false),
                    Status(UnlistedModId, viewerUpdateAvailable: null),
                },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var secondContainer = Guid.NewGuid();
        repository.Containers[secondContainer] =
            NexusContainer(secondContainer, UnlistedModId, "Mod 2", "2.0");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_treats_null_viewerUpdateAvailable_as_no_update()
    {
        // A null viewerUpdateAvailable (server has no download record for the
        // user, e.g. a manually imported mod) is treated as false: not flagged.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: null) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_flags_when_installed_version_differs_even_if_viewerUpdateAvailable_is_false()
    {
        // viewerUpdateAvailable is false (the server's per-user download
        // tracking doesn't reflect the local state: user installed an older
        // version, uses multiple PCs, or imported manually). The version
        // comparison catches this: installed "1.0" vs server "1.2.1" flags.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: false, version: "1.2.1") },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
    }

    [Fact]
    public async Task CheckAsync_does_not_flag_when_versions_match_and_viewerUpdateAvailable_is_false()
    {
        // Both signals agree: no update. viewerUpdateAvailable is false and the
        // installed version matches the server's version.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: false, version: "1.0") },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_does_not_flag_mods_missing_from_response()
    {
        // The API returns fewer mods than expected (a UID did not resolve):
        // the missing mod is simply not flagged (conservative).
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Found Mod", "1.0");
        repository.Containers[NexusUnlistedContainer] =
            NexusContainer(NexusUnlistedContainer, UnlistedModId, "Missing Mod", "2.0");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(NexusUnlistedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        // The missing mod (UnlistedModId) was not flagged.
    }

    [Fact]
    public async Task CheckAsync_populates_LatestUpdateAt_from_updatedAt_field()
    {
        // The v2 updatedAt field is surfaced as ModUpdateInfo.LatestUpdateAt
        // for the UI's display context.
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true, updatedAt) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(updatedAt, flagged.LatestUpdateAt);
        Assert.Equal("1.0", flagged.CurrentVersion);
        Assert.Equal("Mod", flagged.ModName);
    }

    // ---- gating: no auth, no checkable mods --------------------------------

    [Fact]
    public async Task CheckAsync_short_circuits_when_no_auth()
    {
        // AuthMethod.None -> short-circuit before the API call. Even though the
        // canned response WOULD flag a mod, the API is never hit.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, authMethod: NexusAuthMethod.None);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.GraphQlCallCount);
    }

    [Fact]
    public async Task CheckAsync_short_circuits_when_no_nexus_mods()
    {
        // No Nexus mods at all (only untracked) -> nothing to send in the batch
        // -> the API is not called. (A profile with Pinned Nexus mods DOES run
        // the batch for the name sync; that is covered by the name-sync tests.)
        var nexus = new FakeNexusClient(); // unset; would serve an empty default if called
        var repository = new FakeModRepository();
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(UntrackedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.GraphQlCallCount);
    }

    // ---- source / policy filter -------------------------------------------

    [Fact]
    public async Task CheckAsync_skips_untracked_but_sends_pinned_nexus_for_name_sync()
    {
        // A Nexus + Latest mod (would flag) PLUS a pinned Nexus mod and an
        // untracked mod. The Pinned Nexus mod rides along in the batch (its name
        // syncs; Pinned mods are NOT flagged for updates). Untracked is never
        // sent. The 1-API-call contract holds, the batch carries both Nexus ids
        // (Latest + Pinned), and only the Latest one flags.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: true),
                    Status(PinnedModId, viewerUpdateAvailable: true), // would flag, but pinned
                },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod 100", "1.0");
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Mod 300", "1.0");
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(NexusPinnedContainer, new PinnedPolicy("v1")),
                Entry(UntrackedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(1, nexus.GraphQlCallCount);
        // Both Nexus ids sent (Latest + Pinned); untracked excluded.
        Assert.Equal(new[] { UpdatedModId, PinnedModId }, nexus.LastModIds);
        // Names match the Status() helper's "Mod <id>" default -> no rename ->
        // NamesChanged is false (the rename paths are covered by the name-sync
        // tests below).
        Assert.False(result.NamesChanged);
    }

    [Fact]
    public async Task CheckAsync_excludes_linked_mods_from_the_batch_and_never_flags_them()
    {
        // Regression guard: a LinkedSource container in the profile is never
        // sent to Nexus (the update check is Nexus-only) and is never flagged.
        // A Nexus + Latest mod in the SAME profile still flags normally,
        // proving the linked mod is simply filtered out, not a fatal no-mods
        // short-circuit.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var linkedContainer = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod 100", "1.0");
        repository.Containers[linkedContainer] = new ModContainer
        {
            Id = linkedContainer,
            Source = new LinkedSource { ExternalPath = "/home/user/ExternalMod" },
            Name = "ExternalMod",
            Versions = Array.Empty<ModVersion>(), // linked: zero versions
        };
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(linkedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // Only the Nexus mod is in the batch; the linked mod is excluded.
        Assert.Equal(new[] { UpdatedModId }, nexus.LastModIds);
        // Only the Nexus mod flags; the linked mod is never surfaced.
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.False(result.NamesChanged);
    }

    // ---- rate-limit handling ----------------------------------------------

    [Fact]
    public async Task CheckAsync_surfaces_rate_limited_when_client_throws_NexusRateLimitException()
    {
        // The client throws NexusRateLimitException (HTTP 429); the service
        // surfaces it as a rate-limited result (not a generic failure).
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusRateLimitException(429, NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_surfaces_rate_limited_when_daily_remaining_is_zero()
    {
        // DailyRemaining=0 with a real DailyLimit -> rate-limited. The check
        // short-circuits even though the one mod WOULD flag.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 50, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_surfaces_rate_limited_when_hourly_remaining_is_zero()
    {
        // Symmetric proof of the rate-limit guard's other window.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 50, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 0, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_does_not_false_positive_rate_limit_when_headers_absent()
    {
        // NexusRateLimits.Unknown (all zeros, headers absent) must NOT read as
        // rate-limited. A mod that would otherwise flag IS flagged (the > 0
        // guard on the limit prevents a false positive).
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.RateLimited);
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
    }

    [Fact]
    public async Task CheckAsync_rate_limited_from_exception_carries_the_hourly_reset()
    {
        // The thrown NexusRateLimitException carries parsed limits with an
        // hourly reset; the result's RateLimitResetsAt threads it through so
        // the UI can keep the refresh button disabled until the window refills.
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusRateLimitException(429, new NexusRateLimits(
                DailyLimit: 100, DailyRemaining: 50, DailyReset: null,
                HourlyLimit: 100, HourlyRemaining: 0, HourlyReset: reset)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Equal(reset, result.RateLimitResetsAt);
    }

    [Fact]
    public async Task CheckAsync_rate_limited_from_post_call_gate_carries_the_reset()
    {
        // The post-call gate (headers on a 2xx, not a thrown exception) reports
        // an exhausted window; the reset it carries threads through too.
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0, DailyReset: reset,
                    HourlyLimit: 100, HourlyRemaining: 50, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Equal(reset, result.RateLimitResetsAt);
    }

    [Fact]
    public async Task CheckAsync_rate_limited_picks_the_soonest_future_reset()
    {
        // Both windows are exhausted: the daily reset is hours away, the hourly
        // reset is minutes away. The result carries the EARLIER future reset so
        // the refresh button re-enables as soon as the first window refills.
        var now = DateTimeOffset.UtcNow;
        var dailyReset = now.AddHours(20);
        var hourlyReset = now.AddMinutes(5);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0, DailyReset: dailyReset,
                    HourlyLimit: 100, HourlyRemaining: 0, HourlyReset: hourlyReset)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Equal(hourlyReset, result.RateLimitResetsAt);
    }

    [Fact]
    public async Task CheckAsync_rate_limited_with_unknown_limits_has_null_reset()
    {
        // An HTTP 429 carrying no x-rl-* headers surfaces as the all-zero
        // NexusRateLimits.Unknown. No window reports an exhausted budget with a
        // reset, so RateLimitResetsAt is null (the UI falls back to its
        // client-side cooldown).
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusRateLimitException(429, NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Null(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task CheckAsync_rate_limited_drops_a_reset_already_in_the_past()
    {
        // An exhausted window whose reset has already passed yields no FUTURE
        // candidate, so RateLimitResetsAt is null even though the window reads
        // exhausted. The UI fallback cooldown (not a stale instant) governs.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0,
                    DailyReset: DateTimeOffset.UtcNow.AddHours(-1),
                    HourlyLimit: 100, HourlyRemaining: 0,
                    HourlyReset: DateTimeOffset.UtcNow.AddMinutes(-5))),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Null(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task CheckAsync_non_rate_limited_result_has_null_reset()
    {
        // A normal successful result is not rate-limited, so it carries no
        // reset (the field is meaningful only alongside RateLimited = true).
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.RateLimited);
        Assert.Null(result.RateLimitResetsAt);
    }

    // ---- best-effort failure -----------------------------------------------

    [Fact]
    public async Task CheckAsync_api_failure_returns_empty_and_does_not_propagate()
    {
        // A NexusApiException from CheckUpdatesGraphQlAsync is caught: the
        // service returns an empty non-rate-limited result, raises
        // CheckCompleted, and sets LastResult. The caller (fire-and-forget)
        // never sees the throw.
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusApiException(500, "server error"),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var received = new List<UpdateCheckResult?>();
        service.CheckCompleted += (_, r) => received.Add(r);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Same(result, service.LastResult);
        var single = Assert.Single(received);
        Assert.Same(result, single);
    }

    // ---- publishing --------------------------------------------------------

    [Fact]
    public async Task CheckAsync_publishes_result_via_LastResult_and_CheckCompleted()
    {
        // Before the first check, LastResult is null. After a check, LastResult
        // is the returned result and CheckCompleted was raised exactly once
        // with that same result.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        Assert.Null(service.LastResult);

        var received = new List<UpdateCheckResult?>();
        service.CheckCompleted += (_, r) => received.Add(r);

        var result = await service.CheckAsync(ProfileId);

        Assert.Same(result, service.LastResult);
        var single = Assert.Single(received);
        Assert.Same(result, single);
    }

    // ---- thorough vs non-thorough result flag ------------------------------

    [Fact]
    public async Task CheckAsync_returns_thorough_false()
    {
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.Thorough);
    }

    [Fact]
    public async Task CheckThoroughAsync_returns_thorough_true()
    {
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        Assert.True(result.Thorough);
    }

    [Fact]
    public async Task CheckThoroughAsync_same_as_CheckAsync()
    {
        // Both CheckAsync and CheckThoroughAsync run the same v2 batch query
        // and produce the same flagged set; they differ only in the Thorough
        // flag. A mod with viewerUpdateAvailable=true flags under both.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var checkResult = await service.CheckAsync(ProfileId);
        var thoroughResult = await service.CheckThoroughAsync(ProfileId);

        Assert.Single(checkResult.Updates);
        Assert.Single(thoroughResult.Updates);
        Assert.Equal(checkResult.Updates[0].ContainerId, thoroughResult.Updates[0].ContainerId);
        Assert.False(checkResult.Thorough);
        Assert.True(thoroughResult.Thorough);
        // Both made their own API call (2 total).
        Assert.Equal(2, nexus.GraphQlCallCount);
    }

    // ---- tier 3: latest-file-version confirmation -------------------------

    [Fact]
    public async Task CheckAsync_tier3_clears_tier2_false_positive_when_latest_file_matches_installed()
    {
        // The mod-page header version (1.9.1) lags the latest file (1.9.2), and the
        // user has 1.9.2 installed. Tier 2 flags (1.9.2 != 1.9.1); tier 3 resolves
        // the latest MAIN file (1.9.2) and clears the flag because 1.9.2 equals the
        // installed version. This is the mod #1022-style false positive.
        const int modId = 1022;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Laggy Header Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        // Tier 3 ran (one ListModFilesAsync call to confirm the false positive).
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_keeps_flag_when_latest_file_differs_from_installed()
    {
        // Tier 2 flags (installed 1.9.1 != page 1.9.0). The latest MAIN file is
        // 1.9.2, which differs from the installed 1.9.1, so tier 3 cannot clear it:
        // a real update exists. The flag stays.
        const int modId = 1023;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Real Update Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_tier3_skips_tier1_flagged_mods()
    {
        // viewerUpdateAvailable == true is authoritative: even though the installed
        // version (1.9.1) matches the latest file (1.9.1), which would clear a
        // tier-2 flag, tier 1 keeps the mod flagged and tier 3 does not run on it.
        const int modId = 1024;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: true, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                // Would clear a tier-2 flag (1.9.1 == installed), but tier 3 must
                // never read it for this tier-1-flagged mod.
                [modId] = new[] { MainFile("1.9.1", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier1 Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        // Tier 3 was skipped: ListModFilesAsync was never called for this mod.
        Assert.False(nexus.ListModFilesCallCount.ContainsKey(modId));
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_avoids_repeat_call_for_unchanged_mod()
    {
        // Two checks with the same (modId, pageVersion, updatedAt) for a tier-2-only
        // flag. The first resolves + caches; the second hits the cache. Net: exactly
        // one ListModFilesAsync call across both checks.
        const int modId = 1025;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Cached Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        await service.CheckAsync(ProfileId);
        await service.CheckAsync(ProfileId);

        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_invalidates_when_page_version_changes()
    {
        // First check resolves + caches. The second check observes a different page
        // version (one of the cache-key components), so the key no longer matches
        // and tier 3 re-resolves. The updatedAt component is held constant so only
        // the page version invalidates.
        const int modId = 1026;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Cache Invalidate Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        nexus.GraphQlResponse = new Response<ModUpdateStatus[]>(
            new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
            NexusRateLimits.Unknown);
        await service.CheckAsync(ProfileId);
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);

        // Page version changed -> cache key differs -> re-resolve.
        nexus.GraphQlResponse = new Response<ModUpdateStatus[]>(
            new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.0") },
            NexusRateLimits.Unknown);
        await service.CheckAsync(ProfileId);
        Assert.Equal(2, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_re_resolves_after_ttl_expires_on_unchanged_key()
    {
        // The tier-3 cache TTL backstop: even when the cache key (modId,
        // pageVersion, updatedAt) is UNCHANGED, an entry older than 24h is
        // re-resolved. This catches the rare case where an author uploads a new
        // MAIN file without bumping the page version or the updated-at timestamp
        // (the two observable key components). The injected clock drives the TTL
        // age deterministically; without it the 24h wait is untestable.
        const int modId = 1028;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "TTL Backstop Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, getNow: () => now);

        // First check: tier 2 flags (installed 1.9.2 != page 1.9.1); tier 3
        // resolves the latest MAIN file (1.9.2) and clears the flag, caching the
        // verdict at the injected clock's "now".
        await service.CheckAsync(ProfileId);
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);

        // Advance the injected clock past the 24h TTL. The cache key (modId,
        // pageVersion, updatedAt) is unchanged across both checks.
        now = now + TimeSpan.FromHours(25);

        // Second check: same key, but the entry is now older than the TTL, so
        // tier 3 re-resolves despite the unchanged key.
        await service.CheckAsync(ProfileId);
        Assert.Equal(2, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_failure_leaves_flag_and_does_not_fail_check()
    {
        // The tier-3 ListModFilesAsync call throws (network, rate-limit, any error).
        // Tier 3 cannot confirm, so it leaves the mod flagged; the check itself does
        // not fail and is not reported as rate-limited (the tier-1+2 baseline holds).
        const int modId = 1027;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ListModFilesThrows =
            {
                [modId] = new NexusApiException(500, "server error"),
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier3 Failure Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_tier3_rate_limit_leaves_flag_and_does_not_promote_to_rate_limited()
    {
        // A NexusRateLimitException from tier 3's ListModFilesAsync is swallowed
        // like any tier-3 failure: tier 3 cannot confirm, so the mod stays
        // flagged, AND the check result's RateLimited flag stays false. A
        // tier-3-only rate limit does NOT promote to the check-level signal (the
        // tier-1+2 baseline holds). This pins the non-obvious design choice; the
        // sibling failure test throws NexusApiException, this one throws the
        // rate-limit subtype to prove the subtype is treated the same way.
        const int modId = 1029;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ListModFilesThrows =
            {
                [modId] = new NexusRateLimitException(429, NexusRateLimits.Unknown),
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier3 Rate-Limited Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    // ---- name sync (piggybacks on the batch query at zero extra cost) ------

    [Fact]
    public async Task CheckAsync_renames_a_nexus_mod_whose_name_differs_from_the_nexus_name()
    {
        // The batch response carries the current Nexus mod name for every id
        // sent. A mod whose stored Name differs is renamed to match; the
        // container's stored name updates + NamesChanged is true on the result.
        const int modId = 500;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.0", name: "New Author Title") },
                NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Old Stale Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.NamesChanged);
        Assert.Empty(result.Updates); // version matches -> not flagged
        // The container was renamed to the Nexus name.
        var rename = Assert.Single(repository.RenameCalls);
        Assert.Equal(container, rename.ContainerId);
        Assert.Equal("New Author Title", rename.NewName);
        Assert.Equal("New Author Title", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_does_not_rename_when_the_name_matches()
    {
        // A mod whose stored Name already equals the Nexus name is left alone;
        // no rename call lands + NamesChanged is false.
        const int modId = 501;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.0", name: "Same Name") },
                NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Same Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.NamesChanged);
        Assert.Empty(repository.RenameCalls);
        Assert.Equal("Same Name", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_syncs_a_pinned_nexus_mod_name_without_flagging_it()
    {
        // A Pinned Nexus mod rides along in the batch for the name sync (the
        // batch covers ALL NexusSource mods, Latest AND Pinned) but is NOT
        // flagged for an update (Pinned mods are frozen version-wise). Its name
        // syncs when it differs; the Latest-only flag logic is untouched.
        const int modId = 502;
        // viewerUpdateAvailable=true would flag a Latest mod; this one is Pinned
        // so the flag logic skips it. The name still syncs.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: true, version: "1.0", name: "Renamed By Author") },
                NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Old Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new PinnedPolicy("v1")) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // NOT flagged (Pinned), but the batch ran (1 call) + the name synced.
        Assert.Empty(result.Updates);
        Assert.Equal(1, nexus.GraphQlCallCount);
        Assert.True(result.NamesChanged);
        Assert.Equal("Renamed By Author", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_does_not_rename_when_the_response_name_is_empty()
    {
        // An empty (or null) status.Name triggers no rename: never clobber a
        // stored name with an absent one.
        const int modId = 503;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.0", name: "") },
                NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Existing Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.NamesChanged);
        Assert.Empty(repository.RenameCalls);
        Assert.Equal("Existing Name", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_does_not_rename_a_mod_missing_from_the_response()
    {
        // A UID that did not resolve (the API returned no node for it) skips the
        // name sync: there is no name to sync from.
        const int modId = 504;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Kept Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.NamesChanged);
        Assert.Empty(repository.RenameCalls);
        Assert.Equal("Kept Name", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_namesync_runs_after_flagging_so_flagged_modname_uses_the_pre_sync_name()
    {
        // The name sync runs AFTER the tier flag logic, so a mod that is BOTH
        // flagged AND renamed carries its pre-sync name in ModUpdateInfo.ModName
        // (the flag mapping captured the name before the rename). Pins the
        // ordering: the tier logic is unchanged by the name sync.
        const int modId = 505;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: true, version: "2.0", name: "New Name") },
                NexusRateLimits.Unknown),
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Old Name", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // Flagged (installed 1.0 vs server 2.0 + viewerUpdateAvailable=true) AND
        // renamed. The flagged ModName is the PRE-sync name; the container's
        // stored name is the new one.
        var flagged = Assert.Single(result.Updates);
        Assert.Equal("Old Name", flagged.ModName);
        Assert.True(result.NamesChanged);
        Assert.Equal("New Name", repository.Get(container)!.Name);
    }

    [Fact]
    public async Task CheckAsync_namesync_one_failure_does_not_abort_the_rest()
    {
        // Two mods both need a rename; the repository throws on the first rename
        // (simulated by removing the container mid-pass). The second rename
        // still lands, and the result reflects only the rename that succeeded.
        // This pins the defensive per-mod catch: one rename failure must not
        // abort the pass or the check.
        const int firstModId = 506;
        const int secondModId = 507;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(firstModId, viewerUpdateAvailable: false, version: "1.0", name: "First New"),
                    Status(secondModId, viewerUpdateAvailable: false, version: "1.0", name: "Second New"),
                },
                NexusRateLimits.Unknown),
        };
        var firstContainer = Guid.NewGuid();
        var secondContainer = Guid.NewGuid();
        var repository = new ThrowingRenameRepository();
        repository.Containers[firstContainer] =
            NexusContainer(firstContainer, firstModId, "First Old", "1.0");
        repository.Containers[secondContainer] =
            NexusContainer(secondContainer, secondModId, "Second Old", "1.0");
        // The first rename throws (the container was removed before the call).
        repository.ThrowOnRenameOf = firstContainer;
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(firstContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // The check did not throw; the second rename landed; NamesChanged is
        // true (at least one rename succeeded).
        Assert.True(result.NamesChanged);
        Assert.Equal("Second New", repository.Get(secondContainer)!.Name);
    }

    // ---- DI activation (default clock param) ------------------------------

    [Fact]
    public void UpdateCheckService_resolves_from_DI_with_unregistered_optional_clock_param()
    {
        // The clock seam's last constructor parameter (Func<DateTimeOffset>?
        // getNow = null) is intentionally NOT registered in DI. This proves the
        // production registration (AddUpdateCheck's interface-to-impl
        // AddSingleton<IUpdateCheckService, UpdateCheckService>) still activates:
        // Microsoft.Extensions.DependencyInjection honors the parameter's
        // default value for the unresolvable optional param, which the
        // constructor turns into the UtcNow clock. If this ever stops resolving,
        // the registration must switch to a factory delegate that passes
        // getNow: null explicitly (the UpdateCheckRunner registration pattern).
        var nexus = new FakeNexusClient();
        var repository = new FakeModRepository();
        var profiles = new FakeProfileService();
        var configLoader = new FakeConfigLoader { Config = CuratorConfig.CreateDefault() };

        var services = new ServiceCollection();
        services.AddSingleton<INexusClient>(nexus);
        services.AddSingleton<IProfileService>(profiles);
        services.AddSingleton<IModRepository>(repository);
        services.AddSingleton<IConfigLoader>(configLoader);
        services.AddSingleton<ILogger<UpdateCheckService>>(NullLogger<UpdateCheckService>.Instance);
        services.AddSingleton<ILogger<UpdateStateStore>>(NullLogger<UpdateStateStore>.Instance);
        // Mirrors AddUpdateCheck (the interface-to-impl registration under test
        // + the IUpdateStateStore registration that ships alongside it); the
        // unregistered Func<DateTimeOffset>? param must fall back to its default.
        services.AddSingleton<IAppStateStore, AppStateStore>();
        services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IUpdateCheckService>();

        Assert.NotNull(service);
        Assert.IsType<UpdateCheckService>(service);
    }

    // ---- helpers + fakes ---------------------------------------------------

    /// <summary>
    /// Builds a <see cref="ModFile"/> with the given version as a non-archived
    /// MAIN file (category 1) uploaded at <paramref name="uploadedTs"/>. Tier 3
    /// picks the newest MAIN by upload timestamp, so the caller controls which
    /// file "wins" via <paramref name="uploadedTs"/>.
    /// </summary>
    private static ModFile MainFile(string version, long uploadedTs = 0) =>
        new()
        {
            FileId = uploadedTs,
            FileName = version + ".zip",
            Name = version,
            Version = version,
            CategoryId = NexusModFiles.MainFileCategory,
            UploadedTimestamp = uploadedTs,
        };

    /// <summary>
    /// Builds a Nexus-sourced <see cref="ModContainer"/> with a single
    /// IsLatest version.
    /// </summary>
    private static ModContainer NexusContainer(Guid id, int modId, string name, string version) =>
        new()
        {
            Id = id,
            Source = new NexusSource { ModId = modId },
            Name = name,
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = "v1",
                    VersionString = version,
                    IsLatest = true,
                    ImportedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            },
        };

    /// <summary>
    /// Builds a non-Nexus (untracked) <see cref="ModContainer"/> with a single
    /// IsLatest version. Used to prove non-Nexus sources are skipped.
    /// </summary>
    private static ModContainer NonNexusContainer(Guid id, ModSource source, string name) =>
        new()
        {
            Id = id,
            Source = source,
            Name = name,
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = "v1",
                    VersionString = "1.0",
                    IsLatest = true,
                    ImportedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            },
        };

    private static ModListEntry Entry(Guid containerId, ModVersionPolicy policy) =>
        new() { ContainerId = containerId, Enabled = true, Order = 0, Policy = policy };

    /// <summary>
    /// Computes the UID the way the service + the client do:
    /// <c>game_id * 2^32 + mod_id</c>.
    /// </summary>
    private static long Uid(int modId) => (long)GameId * 4294967296L + modId;

    /// <summary>
    /// Builds a <see cref="ModUpdateStatus"/> for <paramref name="modId"/> with
    /// the given <paramref name="viewerUpdateAvailable"/> + optional
    /// <paramref name="updatedAt"/> + <paramref name="version"/> +
    /// <paramref name="name"/>. The version + name default to "" / "Mod {id}"
    /// so the version comparison + name sync are skipped unless a test
    /// explicitly sets them.
    /// </summary>
    private static ModUpdateStatus Status(
        int modId, bool? viewerUpdateAvailable, DateTimeOffset? updatedAt = null,
        string version = "", string? name = null) =>
        new()
        {
            Uid = Uid(modId),
            Name = name ?? "Mod " + modId,
            Version = version,
            UpdatedAt = updatedAt,
            ViewerUpdateAvailable = viewerUpdateAvailable,
        };

    /// <summary>
    /// The <see cref="FakeAppStateStore"/> backing the last <see cref="CreateService"/>
    /// service. New persistence tests read this to assert on recorded state;
    /// existing tests ignore it.
    /// </summary>
    private FakeAppStateStore? _lastAppState;

    /// <summary>
    /// Constructs the service with the given fakes + a <see cref="FakeConfigLoader"/>
    /// whose Nexus auth method is <paramref name="authMethod"/> (ApiKey by
    /// default, so the auth gate passes and the API is reached). The optional
    /// <paramref name="getNow"/> drives the tier-3 cache TTL clock for the TTL
    /// re-resolve test; it defaults to null (the production UtcNow clock). A real
    /// <see cref="UpdateStateStore"/> is wired over a <see cref="FakeAppStateStore"/>
    /// (exposed via <see cref="_lastAppState"/>) so the persistence side-effect of
    /// each check is exercised + assertable.
    /// </summary>
    private UpdateCheckService CreateService(
        FakeNexusClient nexus,
        FakeProfileService profiles,
        FakeModRepository repository,
        NexusAuthMethod authMethod = NexusAuthMethod.ApiKey,
        Func<DateTimeOffset>? getNow = null)
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.AuthMethod = authMethod;
        var configLoader = new FakeConfigLoader { Config = config };
        var appState = new FakeAppStateStore();
        var stateStore = new UpdateStateStore(
            appState, profiles, repository, NullLogger<UpdateStateStore>.Instance);
        _lastAppState = appState;
        return new UpdateCheckService(
            nexus, profiles, repository, configLoader, stateStore,
            NullLogger<UpdateCheckService>.Instance, getNow);
    }

    /// <summary>
    /// A configurable <see cref="INexusClient"/> stub. <see cref="CheckUpdatesGraphQlAsync"/>
    /// is exercised by both check shapes. The v1 methods throw (the update
    /// check no longer calls them). Tracks the call count + the args for the
    /// 1-call contract + game-id/mod-ids assertions.
    /// </summary>
    private sealed class FakeNexusClient : INexusClient
    {
        public Response<ModUpdateStatus[]>? GraphQlResponse { get; set; }
        public Exception? GraphQlThrows { get; set; }
        public int GraphQlCallCount { get; private set; }
        public int? LastGameId { get; private set; }
        public List<int>? LastModIds { get; private set; }

        // Tier-3 (latest-file-version confirmation) support. ListModFilesAsync is
        // the per-tier-2-only-flagged-mod call the service makes to resolve the
        // newest MAIN file. The response map is keyed by mod id; the throw map lets
        // a test simulate a failure; the call counter backs the cache-avoidance +
        // cache-invalidation assertions.
        public Dictionary<int, ModFile[]> ModFilesByModId { get; } = new();
        public Dictionary<int, Exception> ListModFilesThrows { get; } = new();
        public Dictionary<int, int> ListModFilesCallCount { get; } = new();

        public Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(
            int gameId, IReadOnlyList<int> modIds, CancellationToken ct = default)
        {
            GraphQlCallCount++;
            LastGameId = gameId;
            LastModIds = modIds.ToList();
            if (GraphQlThrows is not null)
            {
                return Task.FromException<Response<ModUpdateStatus[]>>(GraphQlThrows);
            }
            return Task.FromResult(
                GraphQlResponse
                    ?? new Response<ModUpdateStatus[]>(Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown));
        }

        public Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId,
            string nxmKey, long expiresEpoch, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<ModInfo>> GetModInfoAsync(
            string gameDomain, int modId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<ModFile[]>> ListModFilesAsync(
            string gameDomain, int modId, CancellationToken ct = default)
        {
            ListModFilesCallCount[modId] =
                ListModFilesCallCount.TryGetValue(modId, out var c) ? c + 1 : 1;

            if (ListModFilesThrows.TryGetValue(modId, out var ex))
            {
                return Task.FromException<Response<ModFile[]>>(ex);
            }

            if (!ModFilesByModId.TryGetValue(modId, out var files))
            {
                throw new NotImplementedException(
                    $"No ModFiles configured for mod {modId}; set FakeNexusClient.ModFilesByModId.");
            }

            return Task.FromResult(new Response<ModFile[]>(files, NexusRateLimits.Unknown));
        }
    }

    /// <summary>
    /// A configurable <see cref="IProfileService"/> stub. Only
    /// <see cref="GetModList"/> is exercised by the service; the other members
    /// throw.
    /// </summary>
    private sealed class FakeProfileService : IProfileService
    {
        public IReadOnlyList<ModListEntry> Mods { get; set; } = Array.Empty<ModListEntry>();

        // Unused stub; only GetModList is exercised. Required by the interface.
        public event EventHandler<ProfileSummary>? ProfileCreated
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ModListEntry> GetModList(Guid id) => Mods;

        public IReadOnlyList<ProfileSummary> ListProfiles()
            => throw new NotImplementedException();
        public Profile GetProfile(Guid id) => throw new NotImplementedException();
        public Profile CreateProfile(string name, string description, LaunchSettings launchSettings) => throw new NotImplementedException();
        public void UpdateProfile(Guid id, string name, string description, LaunchSettings launchSettings) => throw new NotImplementedException();
        public void DeleteProfile(Guid id) => throw new NotImplementedException();
        public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
            => throw new NotImplementedException();
        public void SetModEnabled(Guid id, Guid containerId, bool enabled)
            => throw new NotImplementedException();
        public void SetModOrderLocked(Guid id, Guid containerId, bool orderLocked)
            => throw new NotImplementedException();
        public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
            => throw new NotImplementedException();
        public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
            => throw new NotImplementedException();
        public void RemoveMod(Guid id, Guid containerId)
            => throw new NotImplementedException();
        public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
            => throw new NotImplementedException();
        public LaunchSettings GetLaunchSettings(Guid id) => throw new NotImplementedException();
        public string PrepareModRoot(Guid id) => throw new NotImplementedException();
    }

    /// <summary>
    /// A configurable <see cref="IModRepository"/> stub. Only
    /// <see cref="Get(Guid)"/> + <see cref="RenameContainer"/> are exercised by
    /// the service; the other members throw. Lookups are backed by an in-memory
    /// dictionary keyed by container id. <see cref="RenameContainer"/> updates
    /// the in-memory container + records each call so the name-sync tests can
    /// assert on it.
    /// </summary>
    private class FakeModRepository : IModRepository
    {
        public Dictionary<Guid, ModContainer> Containers { get; } = new();

        /// <summary>
        /// The (containerId, newName) pairs passed to
        /// <see cref="RenameContainer"/>, in call order. Name-sync tests assert
        /// on this to verify the right containers were renamed.
        /// </summary>
        public IReadOnlyList<(Guid ContainerId, string NewName)> RenameCalls { get; } = new List<(Guid, string)>();

        public ModContainer? Get(Guid containerId) =>
            Containers.TryGetValue(containerId, out var c) ? c : null;

        public virtual ModContainer? RenameContainer(Guid containerId, string newName)
        {
            ((List<(Guid, string)>)RenameCalls).Add((containerId, newName));
            if (!Containers.TryGetValue(containerId, out var container))
            {
                return null;
            }

            if (string.Equals(container.Name, newName, StringComparison.Ordinal))
            {
                return container;
            }

            var updated = container with { Name = newName };
            Containers[containerId] = updated;
            return updated;
        }

        public IReadOnlyList<ModContainer> List() => throw new NotImplementedException();
        public ModContainer? FindBySource(ModSource source) => throw new NotImplementedException();
        public ModContainer? FindUntrackedByName(string name) => throw new NotImplementedException();
        public ModContainer CreateContainer(ModSource source, string name)
            => throw new NotImplementedException();
        public ModContainer AddVersion(
            Guid containerId, string versionString, Action<string> populateFolder,
            DateTimeOffset? remoteUploadedAt = null, ModDisplayMetadata? displayMetadata = null)
            => throw new NotImplementedException();
        public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata)
            => throw new NotImplementedException();
        public void RemoveVersion(Guid containerId, string versionFolder)
            => throw new NotImplementedException();
        public string GetVersionFolderPath(Guid containerId, string versionFolder)
            => throw new NotImplementedException();
        public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
            => throw new NotImplementedException();
        public void Rescan() => throw new NotImplementedException();
        public bool IsExternalAvailable(Guid containerId) => throw new NotImplementedException();
    }

    /// <summary>
    /// A <see cref="FakeModRepository"/> whose <see cref="RenameContainer"/>
    /// throws for the container id in <see cref="ThrowOnRenameOf"/> (simulating a
    /// rename failure the production path should never raise, to prove the
    /// per-mod defensive catch keeps the rest of the pass alive).
    /// </summary>
    private sealed class ThrowingRenameRepository : FakeModRepository
    {
        public Guid? ThrowOnRenameOf { get; set; }

        public override ModContainer? RenameContainer(Guid containerId, string newName)
        {
            if (containerId == ThrowOnRenameOf)
            {
                throw new InvalidOperationException("simulated rename failure");
            }
            return base.RenameContainer(containerId, newName);
        }
    }

    // ---- UpdateStateStore: persisted known-update rules ---------------------

    /// <summary>
    /// Builds a <see cref="UpdateStateStore"/> over fresh fakes + returns them
    /// so each test drives the persistence rules directly.
    /// </summary>
    private static (UpdateStateStore Store, FakeAppStateStore AppState, FakeProfileService Profiles, FakeModRepository Repo)
        CreateStateStore()
    {
        var appState = new FakeAppStateStore();
        var profiles = new FakeProfileService();
        var repo = new FakeModRepository();
        var store = new UpdateStateStore(
            appState, profiles, repo, NullLogger<UpdateStateStore>.Instance);
        return (store, appState, profiles, repo);
    }

    /// <summary>
    /// A <see cref="ModContainer"/> with one version at <paramref name="versionString"/>,
    /// the version flagged IsLatest. Used by the state-store self-heal tests.
    /// </summary>
    private static ModContainer ContainerWithVersion(Guid id, ModSource source, string versionString)
    {
        var version = new ModVersion
        {
            Folder = id.ToString("N") + "-v",
            VersionString = versionString,
            IsLatest = true,
            ImportedAt = DateTimeOffset.UtcNow,
        };
        return new ModContainer
        {
            Id = id,
            Source = source,
            Name = "Mod " + id,
            Versions = new[] { version },
        };
    }

    [Fact]
    public void StateStore_success_replaces_the_profile_state_and_clears_on_no_updates()
    {
        var (store, appState, profiles, repo) = CreateStateStore();
        var profile = Guid.NewGuid();
        var container = Guid.NewGuid();
        profiles.Mods = new[] { new ModListEntry { ContainerId = container, Order = 0, Policy = ModVersionPolicy.Latest } };
        repo.Containers[container] = ContainerWithVersion(container, new NexusSource { ModId = 8 }, "1.0");

        // First record a flagged result.
        store.RecordResult(profile, new UpdateCheckResult(
            new[] { new ModUpdateInfo(container, 8, "Mod", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));
        Assert.Contains(container, store.GetKnownUpdateContainerIds(profile));

        // Then an authoritative success with no updates clears it.
        store.RecordResult(profile, new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false,
            Outcome: CheckOutcome.Success));
        Assert.Empty(store.GetKnownUpdateContainerIds(profile));
    }

    [Fact]
    public void StateStore_no_auth_failed_rate_limited_preserve_prior_flags()
    {
        var (store, _, profiles, repo) = CreateStateStore();
        var profile = Guid.NewGuid();
        var container = Guid.NewGuid();
        profiles.Mods = new[] { new ModListEntry { ContainerId = container, Order = 0, Policy = ModVersionPolicy.Latest } };
        repo.Containers[container] = ContainerWithVersion(container, new NexusSource { ModId = 8 }, "1.0");

        // Seed a flagged state via a success.
        store.RecordResult(profile, new UpdateCheckResult(
            new[] { new ModUpdateInfo(container, 8, "Mod", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));
        Assert.Contains(container, store.GetKnownUpdateContainerIds(profile));

        // Each non-authoritative outcome preserves the prior flag.
        foreach (var outcome in new[] { CheckOutcome.NoAuth, CheckOutcome.RateLimited, CheckOutcome.Failed })
        {
            store.RecordResult(profile, new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false, Outcome: outcome));
            Assert.Contains(container, store.GetKnownUpdateContainerIds(profile));
        }
    }

    [Fact]
    public void StateStore_no_nexus_mods_clears_the_profile_state()
    {
        var (store, _, profiles, repo) = CreateStateStore();
        var profile = Guid.NewGuid();
        var container = Guid.NewGuid();
        profiles.Mods = new[] { new ModListEntry { ContainerId = container, Order = 0, Policy = ModVersionPolicy.Latest } };
        repo.Containers[container] = ContainerWithVersion(container, new NexusSource { ModId = 8 }, "1.0");

        store.RecordResult(profile, new UpdateCheckResult(
            new[] { new ModUpdateInfo(container, 8, "Mod", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));
        Assert.Contains(container, store.GetKnownUpdateContainerIds(profile));

        // A no-Nexus-mods result is a local-truth clear.
        store.RecordResult(profile, new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false,
            Outcome: CheckOutcome.NoNexusMods));
        Assert.Empty(store.GetKnownUpdateContainerIds(profile));
    }

    [Fact]
    public void StateStore_profile_scoped_snapshots_never_bleed_between_profiles()
    {
        var (store, appState, profiles, repo) = CreateStateStore();
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var containerA = Guid.NewGuid();
        var containerB = Guid.NewGuid();
        profiles.Mods = Array.Empty<ModListEntry>();
        repo.Containers[containerA] = ContainerWithVersion(containerA, new NexusSource { ModId = 8 }, "1.0");
        repo.Containers[containerB] = ContainerWithVersion(containerB, new NexusSource { ModId = 9 }, "1.0");

        store.RecordResult(profileA, new UpdateCheckResult(
            new[] { new ModUpdateInfo(containerA, 8, "A", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));
        store.RecordResult(profileB, new UpdateCheckResult(
            new[] { new ModUpdateInfo(containerB, 9, "B", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        // The persisted map is keyed by profile id: each profile's entry list is
        // independent (a result from profile A never becomes profile B's state).
        Assert.NotNull(appState.KnownUpdatesData);
        var persisted = appState.KnownUpdatesData!;
        Assert.True(persisted.ContainsKey(profileA));
        Assert.True(persisted.ContainsKey(profileB));
        Assert.Equal(containerA, Assert.Single(persisted[profileA]).ContainerId);
        Assert.Equal(containerB, Assert.Single(persisted[profileB]).ContainerId);
    }

    [Fact]
    public void StateStore_acknowledge_removes_a_single_entry()
    {
        var (store, _, profiles, repo) = CreateStateStore();
        var profile = Guid.NewGuid();
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        profiles.Mods = new[]
        {
            new ModListEntry { ContainerId = c1, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = c2, Order = 1, Policy = ModVersionPolicy.Latest },
        };
        repo.Containers[c1] = ContainerWithVersion(c1, new NexusSource { ModId = 8 }, "1.0");
        repo.Containers[c2] = ContainerWithVersion(c2, new NexusSource { ModId = 9 }, "1.0");

        store.RecordResult(profile, new UpdateCheckResult(
            new[]
            {
                new ModUpdateInfo(c1, 8, "A", "1.0", DateTimeOffset.UtcNow),
                new ModUpdateInfo(c2, 9, "B", "1.0", DateTimeOffset.UtcNow),
            },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        store.AcknowledgeInstall(profile, c1);

        var flags = store.GetKnownUpdateContainerIds(profile);
        Assert.Equal(c2, Assert.Single(flags));
    }

    [Fact]
    public void StateStore_hydration_self_heals_removed_pinned_source_changed_and_version_changed_entries()
    {
        var (store, _, profiles, repo) = CreateStateStore();
        var profile = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var pinned = Guid.NewGuid();
        var sourceChanged = Guid.NewGuid();
        var versionChanged = Guid.NewGuid();
        var stillValid = Guid.NewGuid();

        // Seed all five as flagged.
        store.RecordResult(profile, new UpdateCheckResult(
            new[]
            {
                new ModUpdateInfo(removed, 1, "r", "1.0", DateTimeOffset.UtcNow),
                new ModUpdateInfo(pinned, 2, "p", "1.0", DateTimeOffset.UtcNow),
                new ModUpdateInfo(sourceChanged, 3, "s", "1.0", DateTimeOffset.UtcNow),
                new ModUpdateInfo(versionChanged, 4, "v", "1.0", DateTimeOffset.UtcNow),
                new ModUpdateInfo(stillValid, 5, "k", "1.0", DateTimeOffset.UtcNow),
            },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        // Now set up the live profile + repo state so only stillValid qualifies:
        // - removed: NOT in the profile.
        // - pinned: in the profile but on PinnedPolicy.
        // - sourceChanged: in the profile + Latest, but the container's source
        //   is no longer Nexus with the same ModId.
        // - versionChanged: in the profile + Latest, but the installed version
        //   differs from the snapshot's CurrentVersion.
        // - stillValid: in the profile + Latest + Nexus + matching version.
        profiles.Mods = new[]
        {
            new ModListEntry { ContainerId = pinned, Order = 0, Policy = new PinnedPolicy("v") },
            new ModListEntry { ContainerId = sourceChanged, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = versionChanged, Order = 2, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = stillValid, Order = 3, Policy = ModVersionPolicy.Latest },
        };
        repo.Containers[pinned] = ContainerWithVersion(pinned, new NexusSource { ModId = 2 }, "1.0");
        repo.Containers[sourceChanged] = ContainerWithVersion(sourceChanged, new UntrackedSource(), "1.0");
        repo.Containers[versionChanged] = ContainerWithVersion(versionChanged, new NexusSource { ModId = 4 }, "2.0");
        repo.Containers[stillValid] = ContainerWithVersion(stillValid, new NexusSource { ModId = 5 }, "1.0");

        var flags = store.GetKnownUpdateContainerIds(profile);

        Assert.Equal(stillValid, Assert.Single(flags));
    }
}
