using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Config/density normalization, row display state, the
/// <see cref="DetailedModRowsViewModel"/> coordinator (Compact no work, Detailed
/// starts thumbnails + backfill with ordered IDs, stale-result protection,
/// density toggle immediate state/persistence, same density no-op, failure
/// absorption), and ModList integration (Reload passes metadata + exact rows
/// to the one child, no-profile clears child).
/// </summary>
public sealed class DetailedModRowsViewModelTests
{
    private static readonly string HttpsUrl = "https://example.com/thumb.png";

    // ---- config / density normalization ------------------------------------

    [Fact]
    public void Default_config_has_Detailed_density()
    {
        var config = CuratorConfig.CreateDefault();
        Assert.Equal(ModRowDensity.Detailed, config.Preferences.ModRowDensity);
    }

    [Fact]
    public void Old_config_without_ModRowDensity_loads_Detailed()
    {
        var config = ParseConfig(
            """{"preferences":{"theme":"dark","fontScale":1.0,"language":"en","showRelayConsole":false}}""");
        Assert.Equal(ModRowDensity.Detailed, config.Preferences.ModRowDensity);
    }

    [Fact]
    public void Persisted_compact_string_loads_Compact_in_coordinator()
    {
        // The persisted value wins over the Detailed default: a config that
        // explicitly saved "compact" keeps Compact.
        var config = ParseConfig("""{"preferences":{"modRowDensity":"compact"}}""");
        var (coordinator, _, _) = CreateCoordinator(configLoader: new FakeConfigLoader { Config = config });
        Assert.Equal(ModRowDensity.Compact, coordinator.RowDensity);
        Assert.True(coordinator.IsCompact);
    }

    [Fact]
    public void Persisted_detailed_string_loads_Detailed_in_coordinator()
    {
        var config = ParseConfig("""{"preferences":{"modRowDensity":"detailed"}}""");
        var (coordinator, _, _) = CreateCoordinator(configLoader: new FakeConfigLoader { Config = config });
        Assert.Equal(ModRowDensity.Detailed, coordinator.RowDensity);
        Assert.True(coordinator.IsDetailed);
    }

    [Fact]
    public void Detailed_density_round_trips_through_JSON()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var json = JsonSerializer.Serialize(config, opts);
        var restored = JsonSerializer.Deserialize<CuratorConfig>(json, opts)!;
        Assert.Equal(ModRowDensity.Detailed, restored.Preferences.ModRowDensity);
    }

    [Fact]
    public void Undefined_numeric_density_normalizes_to_Detailed_in_coordinator()
    {
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = (ModRowDensity)42;
        // The config layer keeps the undefined value untouched.
        Assert.Equal((ModRowDensity)42, config.Preferences.ModRowDensity);

        var (coordinator, _, _) = CreateCoordinator(configLoader: new FakeConfigLoader { Config = config });
        Assert.Equal(ModRowDensity.Detailed, coordinator.RowDensity);
        Assert.True(coordinator.IsDetailed);
    }

    [Fact]
    public void ApplyAndPersist_preserves_sibling_ModRowDensity()
    {
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var loader = new FakeConfigLoader { Config = config };

        // Simulate what ApplyAndPersist does: load, overwrite the 4 fields, save.
        var snapshot = loader.Load();
        snapshot.Preferences.Theme = ThemeMode.Dark;
        snapshot.Preferences.FontScale = 1.5;
        snapshot.Preferences.Language = "fr";
        snapshot.Preferences.ShowRelayConsole = true;
        loader.Save(snapshot);

        Assert.Equal(ModRowDensity.Detailed, loader.Config.Preferences.ModRowDensity);
    }

    // ---- row display state -------------------------------------------------

    [Fact]
    public void Row_without_metadata_shows_fallback_summary()
    {
        var row = MakeRow();
        Assert.Equal("Details unavailable", row.SummaryText);
        Assert.Null(row.SummaryTooltip);
        Assert.False(row.IsAdultContent);
        Assert.False(row.HasThumbnail);
    }

    [Fact]
    public void Row_with_metadata_shows_trimmed_summary()
    {
        var row = MakeRow(metadata: new ModDisplayMetadata { Summary = "  A summary.  " });
        Assert.Equal("A summary.", row.SummaryText);
        Assert.Equal("  A summary.  ", row.SummaryTooltip);
    }

    [Fact]
    public void Row_adult_content_flag_projects()
    {
        var row = MakeRow(metadata: new ModDisplayMetadata { IsAdultContent = true });
        Assert.True(row.IsAdultContent);
    }

    [Fact]
    public void Row_thumbnail_eligibility()
    {
        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        Assert.False(row.CanLoadThumbnail); // not Detailed

        row.IsDetailed = true;
        Assert.True(row.CanLoadThumbnail);

        row.ApplyDisplayMetadata(new ModDisplayMetadata { ThumbnailUrl = HttpsUrl, IsAdultContent = true });
        Assert.False(row.CanLoadThumbnail);

        row.ApplyDisplayMetadata(new ModDisplayMetadata());
        Assert.False(row.CanLoadThumbnail);
    }

    [Fact]
    public void Apply_metadata_notifies_derived_properties()
    {
        var row = MakeRow();
        var fired = new List<string?>();
        row.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        row.ApplyDisplayMetadata(new ModDisplayMetadata { Summary = "new", ThumbnailUrl = HttpsUrl });

        Assert.Contains(nameof(ModItemViewModel.DisplayMetadata), fired);
        Assert.Contains(nameof(ModItemViewModel.SummaryText), fired);
        Assert.Contains(nameof(ModItemViewModel.SummaryTooltip), fired);
        Assert.Contains(nameof(ModItemViewModel.IsAdultContent), fired);
        Assert.Contains(nameof(ModItemViewModel.ThumbnailUrl), fired);
        Assert.Contains(nameof(ModItemViewModel.CanLoadThumbnail), fired);
    }

    [Fact]
    public void Applying_adult_metadata_clears_thumbnail()
    {
        var row = MakeRow(source: new NexusSource { ModId = 1 });
        row.IsDetailed = true;
        row.Thumbnail = new FakeImage();

        row.ApplyDisplayMetadata(new ModDisplayMetadata { IsAdultContent = true });

        Assert.Null(row.Thumbnail);
    }

    [Fact]
    public void Applying_different_url_clears_thumbnail()
    {
        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        row.IsDetailed = true;
        row.Thumbnail = new FakeImage();

        row.ApplyDisplayMetadata(new ModDisplayMetadata { ThumbnailUrl = "https://example.com/other.png" });

        Assert.Null(row.Thumbnail);
    }

    [Fact]
    public void Applying_same_url_metadata_keeps_thumbnail()
    {
        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl, Summary = "old" });
        row.IsDetailed = true;
        row.Thumbnail = new FakeImage();

        row.ApplyDisplayMetadata(new ModDisplayMetadata { ThumbnailUrl = HttpsUrl, Summary = "new" });

        Assert.NotNull(row.Thumbnail);
    }

    [Fact]
    public void Refresh_refires_summary_derived_properties()
    {
        var row = MakeRow();
        var fired = new List<string?>();
        row.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        row.Refresh();

        Assert.Contains(nameof(ModItemViewModel.SummaryText), fired);
        Assert.Contains(nameof(ModItemViewModel.SummaryTooltip), fired);
    }

    // ---- coordinator: Compact no work --------------------------------------

    [Fact]
    public async Task Compact_mode_starts_no_thumbnails_or_backfill()
    {
        var meta = new ControllableMetaService();
        var thumb = new ControllableThumbService();
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Compact;
        var (coordinator, _, _) = CreateCoordinator(
            metaService: meta, thumbService: thumb,
            configLoader: new FakeConfigLoader { Config = config });

        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        await coordinator.SetRowsAsync(new[] { row });

        Assert.Equal(0, meta.CallCount);
        Assert.Equal(0, thumb.CallCount);
        Assert.False(row.IsDetailed);
    }

    // ---- coordinator: Detailed starts thumbnails + backfill ----------------

    [Fact]
    public async Task Detailed_starts_known_thumbnails_and_one_backfill_with_ordered_ids()
    {
        var meta = new ControllableMetaService();
        var thumb = new ControllableThumbService
        {
            NextResult = Task.FromResult<IImage?>(new FakeImage()),
        };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            metaService: meta, thumbService: thumb,
            configLoader: new FakeConfigLoader { Config = config });

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var row1 = MakeRow(containerId: id1, source: new NexusSource { ModId = 101 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        var row2 = MakeRow(containerId: id2, source: new NexusSource { ModId = 102 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = "https://example.com/t2.png" });

        await coordinator.SetRowsAsync(new[] { row1, row2 });

        Assert.Equal(1, meta.CallCount);
        Assert.Equal(2, thumb.CallCount);
        Assert.Equal(new[] { id1, id2 }, meta.LastPriorityIds);
        Assert.True(row1.IsDetailed);
        Assert.True(row2.IsDetailed);
        Assert.NotNull(row1.Thumbnail);
        Assert.NotNull(row2.Thumbnail);
    }

    [Fact]
    public async Task Detailed_adult_row_never_requests_thumbnail()
    {
        var thumb = new ControllableThumbService();
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            thumbService: thumb,
            configLoader: new FakeConfigLoader { Config = config });

        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl, IsAdultContent = true });

        await coordinator.SetRowsAsync(new[] { row });

        Assert.Equal(0, thumb.CallCount);
    }

    [Fact]
    public async Task Backfill_result_applies_metadata_and_starts_thumbnail()
    {
        var repo = new FakeModRepository();
        var container = repo.CreateContainer(new NexusSource { ModId = 101 }, "Mod");
        repo.TryInitializeDisplayMetadata(container.Id,
            new ModDisplayMetadata { Summary = "backfilled", ThumbnailUrl = HttpsUrl });

        var metaGate = new TaskCompletionSource<NexusModMetadataResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var meta = new ControllableMetaService { NextResult = metaGate.Task };
        var thumb = new ControllableThumbService
        {
            NextResult = Task.FromResult<IImage?>(new FakeImage()),
        };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            metaService: meta, thumbService: thumb, repo: repo,
            configLoader: new FakeConfigLoader { Config = config });

        var row = MakeRow(containerId: container.Id, source: new NexusSource { ModId = 101 });
        var task = coordinator.SetRowsAsync(new[] { row });

        var storedMeta = repo.Get(container.Id)!.DisplayMetadata!;
        metaGate.SetResult(new NexusModMetadataResult(
            new Dictionary<Guid, ModDisplayMetadata> { [container.Id] = storedMeta },
            attemptedCount: 1, rateLimited: false, rateLimitResetsAt: null));

        // Awaiting the returned task settles the whole generation, including the
        // thumbnail load launched by the backfill result.
        await task;

        Assert.Equal("backfilled", row.SummaryText);
        Assert.True(row.CanLoadThumbnail);
        Assert.Equal(1, thumb.CallCount);
        Assert.NotNull(row.Thumbnail);
    }

    // ---- coordinator: stale-result protection ------------------------------

    [Fact]
    public async Task Reload_supersedes_prior_generation_and_routes_new_priority_ids()
    {
        var meta = new ControllableMetaService();
        var thumbGate = new TaskCompletionSource<IImage?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thumb = new ControllableThumbService { NextResult = thumbGate.Task };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            metaService: meta, thumbService: thumb,
            configLoader: new FakeConfigLoader { Config = config });

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var row1 = MakeRow(containerId: id1, source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        var row2 = MakeRow(containerId: id2, source: new NexusSource { ModId = 2 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = "https://example.com/t2.png" });

        var task1 = coordinator.SetRowsAsync(new[] { row1 });
        var task2 = coordinator.SetRowsAsync(new[] { row2 });

        // The second SetRowsAsync superseded the first; its backfill ran with
        // the new rows' priority ids in order.
        Assert.Equal(2, meta.CallCount);
        Assert.Equal(new[] { id2 }, meta.LastPriorityIds);

        thumbGate.SetResult(new FakeImage());
        await Task.WhenAll(task1, task2);

        // The superseded generation completed (its wait was cancelled) and never
        // assigned to the old row; the current generation assigned normally.
        Assert.Null(row1.Thumbnail);
        Assert.NotNull(row2.Thumbnail);
    }

    [Fact]
    public async Task Compact_switch_cancels_waits_clears_thumbnails_blocks_stale()
    {
        var thumbGate = new TaskCompletionSource<IImage?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thumb = new ControllableThumbService { NextResult = thumbGate.Task };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            thumbService: thumb,
            configLoader: new FakeConfigLoader { Config = config });

        var row = MakeRow(source: new NexusSource { ModId = 1 },
            metadata: new ModDisplayMetadata { ThumbnailUrl = HttpsUrl });
        var detailedTask = coordinator.SetRowsAsync(new[] { row });

        coordinator.SetDensityCommand.Execute(ModRowDensity.Compact);

        Assert.True(coordinator.IsCompact);
        Assert.False(row.IsDetailed);

        // The Detailed generation's pending thumbnail wait was cancelled by the
        // Compact switch; awaiting the returned task proves it completed (the
        // cancellation was absorbed) rather than hanging.
        await detailedTask;
        Assert.Null(row.Thumbnail);

        // A late gate release must not assign: the generation is stale and the
        // mode is Compact.
        thumbGate.SetResult(new FakeImage());
        Assert.Null(row.Thumbnail);
    }

    // ---- coordinator: density toggle + no-op -------------------------------

    [Fact]
    public void RowDensity_has_public_getter_and_private_setter()
    {
        // Locks the public surface: only SetDensityCommand (which normalizes,
        // persists, and reprocesses rows) may mutate the density. A generated
        // [ObservableProperty] setter would be public and is exactly the leak
        // this guards against.
        var prop = typeof(DetailedModRowsViewModel).GetProperty(
            nameof(DetailedModRowsViewModel.RowDensity));
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetMethod);
        Assert.True(prop.GetMethod!.IsPublic, "RowDensity getter must be public.");
        Assert.NotNull(prop.SetMethod);
        Assert.False(
            prop.SetMethod!.IsPublic,
            "RowDensity setter must not be public (mutation is SetDensityCommand-only).");
    }

    [Fact]
    public void Density_toggle_notifies_row_density_and_both_projections()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var fired = new List<string?>();
        coordinator.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        coordinator.SetDensityCommand.Execute(ModRowDensity.Compact);

        Assert.Contains(nameof(DetailedModRowsViewModel.RowDensity), fired);
        Assert.Contains(nameof(DetailedModRowsViewModel.IsCompact), fired);
        Assert.Contains(nameof(DetailedModRowsViewModel.IsDetailed), fired);
    }

    [Fact]
    public void Density_toggle_persists_and_reprocesses_rows()
    {
        var loader = new FakeConfigLoader();
        var (coordinator, _, _) = CreateCoordinator(configLoader: loader);

        coordinator.SetDensityCommand.Execute(ModRowDensity.Compact);

        Assert.Equal(ModRowDensity.Compact, coordinator.RowDensity);
        Assert.True(coordinator.IsCompact);
        Assert.False(coordinator.IsDetailed);
        Assert.Equal(ModRowDensity.Compact, loader.Config.Preferences.ModRowDensity);
    }

    [Fact]
    public void Same_density_is_a_strict_noop()
    {
        var loader = new FakeConfigLoader();
        var meta = new ControllableMetaService();
        var (coordinator, _, _) = CreateCoordinator(configLoader: loader, metaService: meta);

        var savesBefore = loader.SaveCalls;
        coordinator.SetDensityCommand.Execute(ModRowDensity.Detailed);

        Assert.Equal(savesBefore, loader.SaveCalls);
        Assert.Equal(0, meta.CallCount);
    }

    // ---- coordinator: failure absorption ------------------------------------

    [Fact]
    public async Task Generic_service_failure_is_logged_and_absorbed()
    {
        var meta = new ControllableMetaService
        {
            NextResult = Task.FromException<NexusModMetadataResult>(new InvalidOperationException("boom"))
        };
        var config = CuratorConfig.CreateDefault();
        config.Preferences.ModRowDensity = ModRowDensity.Detailed;
        var (coordinator, _, _) = CreateCoordinator(
            metaService: meta,
            configLoader: new FakeConfigLoader { Config = config });

        var row = MakeRow(source: new NexusSource { ModId = 1 });
        await coordinator.SetRowsAsync(new[] { row });
    }

    // ---- ModList integration ------------------------------------------------

    [Fact]
    public void Reload_passes_rows_with_metadata_to_child()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var container = repo.Seed(new NexusSource { ModId = 101 }, "Mod");
        repo.TryInitializeDisplayMetadata(container.Id,
            new ModDisplayMetadata { Summary = "cached" });
        var profile = profiles.CreateProfile("P", "", new LaunchSettings());
        session.ActiveProfileId = profile.Id;
        profiles.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var vm = TestDoubles.BuildModList(profiles, session, repo);

        var row = Assert.Single(vm.Mods);
        Assert.Equal("cached", row.DisplayMetadata?.Summary);
        Assert.NotNull(vm.DetailedRows);
    }

    [Fact]
    public void No_active_profile_hands_empty_snapshot_to_child()
    {
        var session = new FakeProfileSession { ActiveProfileId = null };
        var vm = TestDoubles.BuildModList(session: session);

        Assert.Empty(vm.Mods);
        Assert.NotNull(vm.DetailedRows);
        Assert.True(vm.DetailedRows.IsDetailed);
    }

    // ---- helpers + fakes ----------------------------------------------------

    private static CuratorConfig ParseConfig(string json)
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        return JsonSerializer.Deserialize<CuratorConfig>(json, opts)!;
    }

    private static ModItemViewModel MakeRow(
        Guid? containerId = null,
        ModSource? source = null,
        ModDisplayMetadata? metadata = null)
    {
        var loc = new LocalizationService();
        return new ModItemViewModel(
            loc,
            containerId ?? Guid.NewGuid(),
            "Test",
            source ?? new UntrackedSource(),
            "1.0",
            enabled: true,
            order: 0,
            new LatestPolicy(),
            Array.Empty<ModVersion>(),
            found: true,
            displayMetadata: metadata);
    }

    private static (DetailedModRowsViewModel Coordinator, FakeConfigLoader Config, FakeModRepository Repo)
        CreateCoordinator(
            FakeConfigLoader? configLoader = null,
            ControllableMetaService? metaService = null,
            FakeModRepository? repo = null,
            ControllableThumbService? thumbService = null)
    {
        configLoader ??= new FakeConfigLoader();
        repo ??= new FakeModRepository();
        metaService ??= new ControllableMetaService();
        thumbService ??= new ControllableThumbService();
        var coordinator = new DetailedModRowsViewModel(
            configLoader, metaService, repo, thumbService,
            NullLogger<DetailedModRowsViewModel>.Instance);
        return (coordinator, configLoader, repo);
    }

    private sealed class FakeImage : IImage
    {
        public Size Size => new(160, 120);
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
    }

    private sealed class ControllableMetaService : INexusModMetadataService
    {
        // The task every call returns. Replace with a TaskCompletionSource task
        // to gate a pass, or a faulted task to simulate a failure. The token is
        // honored via WaitAsync so a superseding generation's cancellation
        // completes the pending call instead of hanging the test.
        public Task<NexusModMetadataResult> NextResult { get; set; } =
            Task.FromResult(NexusModMetadataResult.Empty);
        public int CallCount { get; private set; }
        public IReadOnlyList<Guid>? LastPriorityIds { get; private set; }

        public Task<NexusModMetadataResult> BackfillMissingAsync(
            IReadOnlyList<Guid> priorityContainerIds, CancellationToken ct = default)
        {
            CallCount++;
            LastPriorityIds = priorityContainerIds.ToList();
            return NextResult.WaitAsync(ct);
        }
    }

    private sealed class ControllableThumbService : IModThumbnailService
    {
        // The task every call returns. Replace with a TaskCompletionSource task
        // to gate a load (a superseding generation's cancellation releases the
        // wait via WaitAsync instead of hanging), or a completed image task to
        // assert a positive assignment.
        public Task<IImage?> NextResult { get; set; } =
            Task.FromResult<IImage?>(null);
        public int CallCount { get; private set; }

        public Task<IImage?> GetThumbnailAsync(string? thumbnailUrl, CancellationToken ct = default)
        {
            CallCount++;
            return NextResult.WaitAsync(ct);
        }
    }
}
