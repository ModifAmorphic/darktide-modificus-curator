using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// <see cref="NexusModMetadataService"/>: the gated, missing-only, sequential
/// v1 backfill pass for display metadata. Covers the auth gate, the 24-hour
/// persisted gate (boundary, future stamp, clock skew, extreme values),
/// candidate selection (priority ordering, distinct ids, deterministic Guid-
/// order remainder, Nexus-only + missing-only filtering), the 25-attempt cap,
/// per-candidate concurrency (TryInitialize atomicity), the stop/continue
/// policy, the never-throws boundary (config/list/get/persistence failures),
/// the immutability of the result, the stamping rules, and serialized
/// overlapping passes.
/// </summary>
public sealed class NexusModMetadataServiceTests
{
    private static readonly DateTimeOffset BaseNow =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void AddIntegrations_registers_INexusModMetadataService_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddIntegrations();

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(INexusModMetadataService));

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(NexusModMetadataService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    // ---- result immutability (Correction 4) --------------------------------

    [Fact]
    public void Result_constructor_defensively_copies_the_updated_map()
    {
        // A mutable input dictionary must not affect the result after
        // construction: the result wraps a defensive copy.
        var input = new Dictionary<Guid, ModDisplayMetadata>
        {
            [Guid.NewGuid()] = new ModDisplayMetadata { Summary = "original" },
        };
        var result = new NexusModMetadataResult(input, 1, false, null);

        // Mutate the input after construction.
        input.Clear();

        Assert.Single(result.Updated);
    }

    [Fact]
    public void Result_Updated_is_not_downcastable_to_Dictionary()
    {
        var result = new NexusModMetadataResult(
            new Dictionary<Guid, ModDisplayMetadata> { [Guid.NewGuid()] = new ModDisplayMetadata() },
            0, false, null);

        Assert.IsNotType<Dictionary<Guid, ModDisplayMetadata>>(result.Updated);
    }

    [Fact]
    public void Result_Empty_is_not_mutable()
    {
        // The shared empty result must not be globally mutable: a caller cannot
        // add to it and affect the next consumer.
        Assert.Empty(NexusModMetadataResult.Empty.Updated);
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<KeyValuePair<Guid, ModDisplayMetadata>>)NexusModMetadataResult.Empty.Updated)
                .Add(new(Guid.NewGuid(), new ModDisplayMetadata())));
        // A second read is still empty.
        Assert.Empty(NexusModMetadataResult.Empty.Updated);
    }

    // ---- no-op short-circuits (no stamp, no API work) ----------------------

    [Fact]
    public async Task No_auth_returns_empty_without_api_work_or_stamp()
    {
        var (service, nexus, _, appState) = CreateService(authMethod: NexusAuthMethod.None);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.GetModInfoCallCount);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Within_24h_gate_returns_empty_without_api_work_or_stamp()
    {
        var lastStamp = BaseNow.AddHours(-23);
        var (service, nexus, _, appState) = CreateService(
            backfillStamp: lastStamp, now: BaseNow);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Equal(0, nexus.GetModInfoCallCount);
        Assert.Equal(lastStamp, appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Exactly_24h_ago_proceeds_past_the_gate()
    {
        var lastStamp = BaseNow.AddHours(-24);
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 1, "Mod1", "1.0"));
        var (service, _, _, _) = CreateService(
            nexus: nexus, repository: repo, backfillStamp: lastStamp, now: BaseNow);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.True(result.AttemptedCount >= 1);
        Assert.True(nexus.GetModInfoCallCount >= 1);
    }

    [Fact]
    public async Task Future_stamp_from_clock_skew_still_gates()
    {
        var futureStamp = BaseNow.AddHours(1);
        var (service, nexus, _, _) = CreateService(backfillStamp: futureStamp, now: BaseNow);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, nexus.GetModInfoCallCount);
    }

    [Fact]
    public async Task No_candidates_returns_empty_without_api_work_or_stamp()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        var c = NexusContainer(Guid.NewGuid(), 1, "Mod1", "1.0");
        repo.Add(c with { DisplayMetadata = new ModDisplayMetadata { Summary = "already" } });

        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Equal(0, nexus.GetModInfoCallCount);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    // ---- extreme-value gate (Correction 3) ---------------------------------

    [Fact]
    public async Task Near_max_value_stamp_does_not_overflow_or_gate()
    {
        // A stamp near DateTimeOffset.MaxValue minus the interval: 24 hours have
        // elapsed relative to a now that is also near MaxValue. The comparison
        // must not overflow.
        var nearMax = DateTimeOffset.MaxValue.AddHours(-25);
        var now = DateTimeOffset.MaxValue.AddHours(-1);
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 1, "Mod", "1.0"));
        var (service, _, _, _) = CreateService(
            nexus: nexus, repository: repo, backfillStamp: nearMax, now: now);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        // Proceeded: 25h > 24h. No overflow, no gate.
        Assert.True(result.AttemptedCount >= 1);
    }

    [Fact]
    public async Task Near_max_value_stamp_within_window_does_not_overflow()
    {
        // A stamp near MaxValue, checked at a now barely after it: the window
        // has not elapsed. The subtraction is safe (now >= lastStamp), and the
        // pass gates.
        var nearMax = DateTimeOffset.MaxValue.AddHours(-1);
        var now = DateTimeOffset.MaxValue;
        var (service, nexus, _, _) = CreateService(
            backfillStamp: nearMax, now: now);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, nexus.GetModInfoCallCount);
    }

    [Fact]
    public async Task Near_min_value_now_with_recent_stamp_gates_safely()
    {
        // now near MinValue, lastStamp slightly after it: within the window. The
        // subtraction is safe (now >= lastStamp is false, so the future-stamp
        // branch gates without subtracting).
        var minStamp = DateTimeOffset.MinValue.AddHours(1);
        var now = DateTimeOffset.MinValue;
        var (service, nexus, _, _) = CreateService(
            backfillStamp: minStamp, now: now);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, nexus.GetModInfoCallCount);
    }

    // ---- candidate selection: ordering + filtering -------------------------

    [Fact]
    public async Task Priority_ids_are_tried_first_in_caller_order()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));
        repo.Add(NexusContainer(id3, 103, "C", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        await service.BackfillMissingAsync(new[] { id3, id1, id2 });

        Assert.Equal(new[] { 103, 101, 102 }, nexus.CallOrder.ToArray());
    }

    [Fact]
    public async Task Duplicate_priority_ids_are_de_duplicated()
    {
        var id1 = Guid.NewGuid();
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        await service.BackfillMissingAsync(new[] { id1, id1, id1 });

        Assert.Equal(1, nexus.GetModInfoCallCount);
    }

    [Fact]
    public async Task Repository_remainder_is_appended_in_deterministic_guid_order()
    {
        var g1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var g2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var g3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(g3, 103, "C", "1.0"));
        repo.Add(NexusContainer(g1, 101, "A", "1.0"));
        repo.Add(NexusContainer(g2, 102, "B", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(new[] { 101, 102, 103 }, nexus.CallOrder.ToArray());
    }

    [Fact]
    public async Task Priority_ids_take_precedence_over_repository_order()
    {
        var g1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var g2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var g3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(g1, 101, "A", "1.0"));
        repo.Add(NexusContainer(g2, 102, "B", "1.0"));
        repo.Add(NexusContainer(g3, 103, "C", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        await service.BackfillMissingAsync(new[] { g3 });

        Assert.Equal(new[] { 103, 101, 102 }, nexus.CallOrder.ToArray());
    }

    [Fact]
    public async Task Non_Nexus_containers_are_skipped()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = new UntrackedSource(),
            Name = "Untracked",
            Versions = new[] { Version("1.0") },
        });
        repo.Add(new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = new LinkedSource { ExternalPath = "/some/path" },
            Name = "Linked",
        });

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public async Task Containers_with_existing_metadata_are_skipped()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        var withMeta = NexusContainer(Guid.NewGuid(), 101, "Has", "1.0");
        repo.Add(withMeta with { DisplayMetadata = new ModDisplayMetadata() });
        repo.Add(NexusContainer(Guid.NewGuid(), 102, "Missing", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(1, result.AttemptedCount);
        Assert.Single(result.Updated);
        Assert.Equal(102, nexus.CallOrder.Single());
    }

    [Fact]
    public async Task Missing_container_id_in_priority_list_is_skipped()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "Real", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { Guid.NewGuid() });

        Assert.Equal(1, result.AttemptedCount);
    }

    // ---- the 25-attempt cap ------------------------------------------------

    [Fact]
    public async Task Pass_stops_after_exactly_25_attempted_requests()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        for (var i = 1; i <= 30; i++)
        {
            repo.Add(NexusContainer(Guid.NewGuid(), 1000 + i, $"Mod{i}", "1.0"));
        }

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(25, result.AttemptedCount);
        Assert.Equal(25, nexus.GetModInfoCallCount);
        Assert.Equal(25, result.Updated.Count);
    }

    [Fact]
    public async Task One_GetModInfo_call_per_attempt_no_retries()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        for (var i = 1; i <= 3; i++)
        {
            repo.Add(NexusContainer(Guid.NewGuid(), 1000 + i, $"Mod{i}", "1.0"));
        }

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(3, nexus.GetModInfoCallCount);
    }

    // ---- successful mapping + persistence ----------------------------------

    [Fact]
    public async Task Successful_response_maps_and_persists_metadata()
    {
        var nexus = new FakeNexusClient();
        nexus.SetModInfo(101, new ModInfo
        {
            ModId = 101,
            Name = "Mod",
            Summary = "  trimmed  ",
            PictureUrl = "https://example.com/thumb.png",
            ContainsAdultContent = true,
        });
        var id = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id, 101, "Mod", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id });

        var written = Assert.Single(result.Updated);
        Assert.Equal(id, written.Key);
        Assert.Equal("trimmed", written.Value.Summary);
        Assert.Equal("https://example.com/thumb.png", written.Value.ThumbnailUrl);
        Assert.True(written.Value.IsAdultContent);

        var stored = repo.Get(id);
        Assert.Equal(written.Value, stored!.DisplayMetadata);
    }

    [Fact]
    public async Task Stamping_persists_after_at_least_one_attempted_request()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "Mod", "1.0"));
        var stampNow = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var (service, _, _, appState) = CreateService(
            nexus: nexus, repository: repo, now: stampNow);

        await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(stampNow, appState.LastNexusMetadataBackfillUtc);
    }

    // ---- concurrency: TryInitialize atomicity (Correction 1) ---------------

    [Fact]
    public async Task Metadata_appearing_before_request_skips_without_attempt()
    {
        var nexus = new FakeNexusClient();
        var id = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id, 101, "Mod", "1.0"));
        repo.OnGet = c =>
        {
            if (c is not null && c.Id == id && nexus.GetModInfoCallCount == 0 && c.DisplayMetadata is null)
            {
                repo._store[id] = c with { DisplayMetadata = new ModDisplayMetadata() };
            }
        };

        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id });

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Metadata_appearing_between_response_and_write_is_not_overwritten()
    {
        // The GetModInfoAsync call succeeds, but a concurrent writer populates
        // the container's metadata during the await. TryInitializeDisplayMetadata
        // returns false (missing-only) and the container is not reported as
        // Updated. The concurrent value survives.
        var nexus = new FakeNexusClient();
        nexus.SetModInfo(101, new ModInfo { ModId = 101, Name = "Mod", Summary = "fetched" });
        var id = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id, 101, "Mod", "1.0"));
        var injected = false;
        nexus.OnAfterGetModInfo = modId =>
        {
            if (modId == 101 && !injected)
            {
                injected = true;
                repo._store[id] = repo._store[id] with
                {
                    DisplayMetadata = new ModDisplayMetadata { Summary = "concurrent" },
                };
            }
        };

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id });

        Assert.Empty(result.Updated); // not reported (TryInitialize returned false)
        Assert.Equal("concurrent", repo.Get(id)!.DisplayMetadata!.Summary);
    }

    // ---- stop/continue policy ---------------------------------------------

    [Fact]
    public async Task Ordinary_NexusApiException_is_isolated_and_pass_continues()
    {
        var nexus = new FakeNexusClient();
        nexus.SetThrow(101, new NexusApiException(404, "not found"));
        nexus.SetModInfo(102, new ModInfo { ModId = 102, Name = "Mod", Summary = "ok" });
        var repo = new FakeModRepository();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));

        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id1, id2 });

        Assert.Equal(2, result.AttemptedCount);
        Assert.Single(result.Updated);
        Assert.Contains(id2, result.Updated.Keys);
        Assert.NotNull(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task NexusRateLimitException_stops_with_reset()
    {
        var reset = BaseNow.AddMinutes(30);
        var limits = new NexusRateLimits(
            DailyLimit: 100, DailyRemaining: 0, DailyReset: reset,
            HourlyLimit: 25, HourlyRemaining: 5, HourlyReset: null);
        var nexus = new FakeNexusClient();
        nexus.SetThrow(102, new NexusRateLimitException(429, limits));
        var repo = new FakeModRepository();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo, now: BaseNow);

        var result = await service.BackfillMissingAsync(new[] { id1, id2 });

        Assert.True(result.RateLimited);
        Assert.Equal(reset, result.RateLimitResetsAt);
        Assert.Single(result.Updated);
        Assert.Contains(id1, result.Updated.Keys);
        Assert.Equal(2, result.AttemptedCount);
    }

    [Fact]
    public async Task Successful_response_with_exhausted_counter_persists_then_stops()
    {
        var reset = BaseNow.AddHours(2);
        var exhausted = new NexusRateLimits(
            DailyLimit: 100, DailyRemaining: 0, DailyReset: reset,
            HourlyLimit: 25, HourlyRemaining: 10, HourlyReset: null);
        var nexus = new FakeNexusClient();
        nexus.SetModInfo(101, new ModInfo { ModId = 101, Name = "Mod", Summary = "s1" }, exhausted);
        nexus.SetModInfo(102, new ModInfo { ModId = 102, Name = "Mod", Summary = "s2" });
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo, now: BaseNow);

        var result = await service.BackfillMissingAsync(new[] { id1, id2 });

        Assert.True(result.RateLimited);
        Assert.Equal(reset, result.RateLimitResetsAt);
        Assert.Single(result.Updated);
        Assert.Contains(id1, result.Updated.Keys);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, nexus.GetModInfoCallCount);
    }

    [Fact]
    public async Task All_zero_headers_on_a_successful_response_do_not_false_positive()
    {
        var nexus = new FakeNexusClient();
        nexus.SetModInfo(101, new ModInfo { ModId = 101, Name = "Mod", Summary = "s1" }, NexusRateLimits.Unknown);
        nexus.SetModInfo(102, new ModInfo { ModId = 102, Name = "Mod", Summary = "s2" }, NexusRateLimits.Unknown);
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "A", "1.0"));
        repo.Add(NexusContainer(Guid.NewGuid(), 102, "B", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.False(result.RateLimited);
        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(2, result.Updated.Count);
    }

    [Fact]
    public async Task NexusNotAuthenticatedException_stops_without_rate_limiting()
    {
        var nexus = new FakeNexusClient();
        nexus.SetThrow(102, new NexusNotAuthenticatedException());
        var repo = new FakeModRepository();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));

        var (service, _, _, _) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id2 });

        Assert.False(result.RateLimited);
        Assert.Null(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task Generic_exception_at_api_call_stops_the_pass_without_throwing()
    {
        var nexus = new FakeNexusClient();
        nexus.SetThrow(101, new HttpRequestException("transport down"));
        var repo = new FakeModRepository();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        repo.Add(NexusContainer(id1, 101, "A", "1.0"));
        repo.Add(NexusContainer(id2, 102, "B", "1.0"));

        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id1 });

        Assert.Equal(1, result.AttemptedCount);
        Assert.False(result.RateLimited);
        Assert.NotNull(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Cancellation_after_an_attempt_propagates_and_stamps()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "A", "1.0"));
        repo.Add(NexusContainer(Guid.NewGuid(), 102, "B", "1.0"));
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        using var cts = new CancellationTokenSource();
        var callCount = 0;
        nexus.GetModInfoHandler = modId =>
        {
            callCount++;
            if (callCount == 1)
            {
                cts.Cancel();
                return Task.FromResult(new Response<ModInfo>(
                    new ModInfo { ModId = modId, Name = "Mod " + modId },
                    NexusRateLimits.Unknown));
            }
            cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new Response<ModInfo>(
                new ModInfo { ModId = modId, Name = "Mod " + modId },
                NexusRateLimits.Unknown));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BackfillMissingAsync(Array.Empty<Guid>(), cts.Token));

        Assert.NotNull(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Cancellation_before_any_attempt_propagates_without_stamping()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "A", "1.0"));
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BackfillMissingAsync(Array.Empty<Guid>(), cts.Token));

        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    // ---- never-throws boundary (Correction 2) ------------------------------

    [Fact]
    public async Task Throwing_config_load_returns_empty_without_stamping()
    {
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(Guid.NewGuid(), 101, "A", "1.0"));
        var throwingConfig = new ThrowingConfigLoader(new InvalidOperationException("config disk gone"));
        var appState = new FakeBackfillState();
        var service = new NexusModMetadataService(
            nexus, repo, throwingConfig, appState,
            NullLogger<NexusModMetadataService>.Instance, getNow: () => BaseNow);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        // Absorbed: empty, not thrown, no stamp.
        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Equal(0, nexus.GetModInfoCallCount);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Throwing_repository_List_returns_empty_without_stamping()
    {
        var nexus = new FakeNexusClient();
        var repo = new ThrowingListRepository(new IOException("mods folder unreadable"));
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Equal(0, nexus.GetModInfoCallCount);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Throwing_repository_initialization_stops_pass_without_throwing()
    {
        // TryInitializeDisplayMetadata throws (disk full, I/O error). The pass
        // stops without throwing and stamps (one attempt was made).
        var nexus = new FakeNexusClient();
        nexus.SetModInfo(101, new ModInfo { ModId = 101, Name = "Mod", Summary = "s" });
        var id = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id, 101, "A", "1.0"));
        repo.TryInitializeThrows = new IOException("disk full");
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id });

        // Absorbed: the attempt was made, the pass stopped, the result is
        // empty/partial and stamped.
        Assert.Equal(1, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.False(result.RateLimited);
        Assert.NotNull(appState.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public async Task Throwing_pre_request_Get_absorbed_and_stops_pass()
    {
        // The pre-request Get (a candidate recheck) throws. The pass absorbs it
        // (the candidate loop's outer boundary catches it) and returns. Since no
        // API call was attempted yet, no stamp.
        var nexus = new FakeNexusClient();
        var id = Guid.NewGuid();
        var repo = new FakeModRepository();
        repo.Add(NexusContainer(id, 101, "A", "1.0"));
        repo.GetThrows = new InvalidOperationException("repo locked");
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo);

        var result = await service.BackfillMissingAsync(new[] { id });

        Assert.Equal(0, result.AttemptedCount);
        Assert.Empty(result.Updated);
        Assert.Null(appState.LastNexusMetadataBackfillUtc);
    }

    // ---- serialized overlapping passes -------------------------------------

    [Fact]
    public async Task Overlapping_passes_serialize_and_second_rechecks_gate()
    {
        var firstCallTcs = new TaskCompletionSource<Response<ModInfo>>();
        var firstCallStarted = new TaskCompletionSource<bool>();
        var firstCall = true;
        var nexus = new FakeNexusClient();
        var repo = new FakeModRepository();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        repo.Add(NexusContainer(firstId, 101, "A", "1.0"));
        repo.Add(NexusContainer(secondId, 102, "B", "1.0"));
        var (service, _, _, appState) = CreateService(nexus: nexus, repository: repo, now: BaseNow);

        nexus.GetModInfoHandler = modId =>
        {
            if (firstCall)
            {
                firstCall = false;
                firstCallStarted.SetResult(true);
                return firstCallTcs.Task;
            }
            return Task.FromResult(new Response<ModInfo>(
                new ModInfo { ModId = modId, Name = "Mod " + modId },
                NexusRateLimits.Unknown));
        };

        var task1 = service.BackfillMissingAsync(new[] { firstId });
        await firstCallStarted.Task;

        var task2 = service.BackfillMissingAsync(new[] { secondId });
        Assert.False(task2.IsCompleted);

        firstCallTcs.SetResult(new Response<ModInfo>(
            new ModInfo { ModId = 101, Name = "Mod 101" }, NexusRateLimits.Unknown));

        var result1 = await task1;
        var result2 = await task2;

        Assert.True(result1.AttemptedCount >= 1);
        Assert.NotNull(appState.LastNexusMetadataBackfillUtc);
        Assert.Equal(0, result2.AttemptedCount);
        Assert.Empty(result2.Updated);
    }

    // ---- helpers + fakes ---------------------------------------------------

    private static ModContainer NexusContainer(Guid id, int modId, string name, string version) =>
        new()
        {
            Id = id,
            Source = new NexusSource { ModId = modId },
            Name = name,
            Versions = new[] { Version(version) },
        };

    private static ModVersion Version(string versionString) =>
        new()
        {
            Folder = versionString,
            VersionString = versionString,
            IsLatest = true,
            ImportedAt = DateTimeOffset.UtcNow,
        };

    private static (NexusModMetadataService Service, FakeNexusClient Nexus, IModRepository Repo, FakeBackfillState AppState)
        CreateService(
            FakeNexusClient? nexus = null,
            IModRepository? repository = null,
            NexusAuthMethod authMethod = NexusAuthMethod.ApiKey,
            DateTimeOffset? backfillStamp = null,
            DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? BaseNow;
        nexus ??= new FakeNexusClient();
        repository ??= new FakeModRepository();
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.AuthMethod = authMethod;
        var configLoader = new FakeConfigLoader { Config = config };
        var appState = new FakeBackfillState { LastNexusMetadataBackfillUtc = backfillStamp };

        var service = new NexusModMetadataService(
            nexus, repository, configLoader, appState,
            NullLogger<NexusModMetadataService>.Instance,
            getNow: () => effectiveNow);
        return (service, nexus, repository, appState);
    }

    private sealed class FakeNexusClient : INexusClient
    {
        private readonly Dictionary<int, ModInfo> _infoByModId = new();
        private readonly Dictionary<int, NexusRateLimits> _limitsByModId = new();
        private readonly Dictionary<int, Exception> _throwsByModId = new();

        public int GetModInfoCallCount { get; private set; }
        public List<int> CallOrder { get; } = new();

        public Func<int, Task<Response<ModInfo>>>? GetModInfoHandler { get; set; }
        public Action<int>? OnAfterGetModInfo { get; set; }

        public void SetModInfo(int modId, ModInfo info, NexusRateLimits? limits = null)
        {
            _infoByModId[modId] = info;
            if (limits is not null) _limitsByModId[modId] = limits;
        }

        public void SetThrow(int modId, Exception ex) => _throwsByModId[modId] = ex;

        public Task<Response<ModInfo>> GetModInfoAsync(string gameDomain, int modId, CancellationToken ct = default)
        {
            GetModInfoCallCount++;
            CallOrder.Add(modId);

            if (GetModInfoHandler is not null)
            {
                return GetModInfoHandler(modId);
            }

            if (_throwsByModId.TryGetValue(modId, out var ex))
            {
                return Task.FromException<Response<ModInfo>>(ex);
            }

            var info = _infoByModId.TryGetValue(modId, out var i)
                ? i
                : new ModInfo { ModId = modId, Name = "Mod " + modId };
            var limits = _limitsByModId.TryGetValue(modId, out var l)
                ? l
                : NexusRateLimits.Unknown;

            OnAfterGetModInfo?.Invoke(modId);
            return Task.FromResult(new Response<ModInfo>(info, limits));
        }

        public Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, string nxmKey, long expiresEpoch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Response<ModFile[]>> ListModFilesAsync(string gameDomain, int modId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(int gameId, IReadOnlyList<int> modIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Response<NexusSearchResult[]>> SearchModsAsync(string gameDomain, string terms, int count, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeModRepository : IModRepository
    {
        public readonly Dictionary<Guid, ModContainer> _store = new();

        public Action<ModContainer?>? OnGet { get; set; }
        public Exception? GetThrows { get; set; }
        public Exception? TryInitializeThrows { get; set; }

        public void Add(ModContainer container) => _store[container.Id] = container;

        public ModContainer? Get(Guid containerId)
        {
            if (GetThrows is not null) throw GetThrows;
            var c = _store.TryGetValue(containerId, out var v) ? v : null;
            OnGet?.Invoke(c);
            return c;
        }

        public ModContainer? EditImportDetails(
            Guid containerId, string name, ModSource source, string versionTag, bool removeOlderVersions)
            => throw new NotImplementedException();

        public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            if (TryInitializeThrows is not null) throw TryInitializeThrows;
            if (!_store.TryGetValue(containerId, out var container)) return false;
            if (container.DisplayMetadata is not null) return false;
            _store[containerId] = container with { DisplayMetadata = metadata };
            return true;
        }

        public IReadOnlyList<ModContainer> List() => _store.Values.ToArray();

        public ModContainer? FindBySource(ModSource source) => throw new NotImplementedException();
        public ModContainer? FindUntrackedByName(string name) => throw new NotImplementedException();
        public ModContainer CreateContainer(ModSource source, string name) => throw new NotImplementedException();
        public ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder, DateTimeOffset? remoteUploadedAt = null, int? remoteFileId = null, ModDisplayMetadata? displayMetadata = null) => throw new NotImplementedException();
        public ModContainer? RenameContainer(Guid containerId, string newName) => throw new NotImplementedException();
        public void RemoveVersion(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public string GetVersionFolderPath(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced) => throw new NotImplementedException();
        public bool IsExternalAvailable(Guid containerId) => throw new NotImplementedException();
    }

    /// <summary>An <see cref="IConfigLoader"/> that throws on every
    /// <see cref="Load"/>.</summary>
    private sealed class ThrowingConfigLoader : IConfigLoader
    {
        private readonly Exception _ex;
        public ThrowingConfigLoader(Exception ex) => _ex = ex;
        public CuratorConfig Load() => throw _ex;
        public void Save(CuratorConfig config) => throw _ex;
    }

    /// <summary>An <see cref="IModRepository"/> whose <see cref="List"/> throws,
    /// for the never-throws candidate-enumeration test.</summary>
    private sealed class ThrowingListRepository : IModRepository
    {
        private readonly Exception _ex;
        public ThrowingListRepository(Exception ex) => _ex = ex;

        public IReadOnlyList<ModContainer> List() => throw _ex;
        public ModContainer? Get(Guid containerId) => throw new NotImplementedException();
        public ModContainer? FindBySource(ModSource source) => throw new NotImplementedException();
        public ModContainer? FindUntrackedByName(string name) => throw new NotImplementedException();
        public ModContainer CreateContainer(ModSource source, string name) => throw new NotImplementedException();
        public ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder, DateTimeOffset? remoteUploadedAt = null, int? remoteFileId = null, ModDisplayMetadata? displayMetadata = null) => throw new NotImplementedException();
        public ModContainer? RenameContainer(Guid containerId, string newName) => throw new NotImplementedException();
        public void RemoveVersion(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public string GetVersionFolderPath(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced) => throw new NotImplementedException();
        public bool IsExternalAvailable(Guid containerId) => throw new NotImplementedException();
        public ModContainer? EditImportDetails(
            Guid containerId, string name, ModSource source, string versionTag, bool removeOlderVersions) => throw new NotImplementedException();
        public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata) => throw new NotImplementedException();
    }
}
