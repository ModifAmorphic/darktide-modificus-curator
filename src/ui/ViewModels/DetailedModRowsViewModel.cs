using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The Compact/Detailed density coordinator for the Mods destination. Owns the
/// persisted density selection, the current row snapshot, the metadata-backfill
/// invocation, and the thumbnail hydration lifecycle. A focused application-
/// lifetime child of <see cref="ModListViewModel"/>, analogous to
/// <see cref="ImportWorkflowViewModel"/>: it isolates asynchronous
/// metadata/thumbnail orchestration from the already-large parent so the parent
/// does not widen with those mechanisms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generation-based stale-result protection.</b> Every <see cref="SetRowsAsync"/>
/// call cancels the prior generation and starts a new one. Metadata and
/// thumbnail results are applied only when the generation is still current, the
/// mode is still Detailed, and the exact row object is still in the current
/// snapshot with the same ThumbnailUrl. A profile switch, a Compact toggle, or
/// a reload that supersedes the generation prevents stale assignment without
/// aborting the thumbnail service's shared cache load.</para>
/// <para>
/// <b>No <c>ConfigureAwait(false)</c>.</b> All observable row mutations resume on
/// the captured UI context (the UI-layer convention).</para>
/// </remarks>
public partial class DetailedModRowsViewModel : ObservableObject
{
    private readonly IConfigLoader _configLoader;
    private readonly INexusModMetadataService _metadataService;
    private readonly IModRepository _repository;
    private readonly IModThumbnailService _thumbnailService;
    private readonly ILogger<DetailedModRowsViewModel> _logger;

    private ModRowDensity _rowDensity;

    /// <summary>
    /// The persisted density, read + normalized (only <see cref="ModRowDensity.Compact"/>
    /// survives; every other numeric value, including undefined, becomes
    /// <see cref="ModRowDensity.Detailed"/>) from
    /// <see cref="CuratorConfig.Preferences.ModRowDensity"/> at construction.
    /// The setter is private so the only mutation path is <see cref="SetDensityCommand"/>,
    /// which normalizes, persists, and reprocesses the current rows: external
    /// code cannot assign an undefined density or bypass that path.
    /// </summary>
    public ModRowDensity RowDensity
    {
        get => _rowDensity;
        private set
        {
            if (SetProperty(ref _rowDensity, value))
            {
                // Mirror the prior [NotifyPropertyChangedFor] behavior: a real
                // change re-fires both density projections the toolbar binds to.
                OnPropertyChanged(nameof(IsCompact));
                OnPropertyChanged(nameof(IsDetailed));
            }
        }
    }

    /// <summary>
    /// The current row snapshot. Set by <see cref="SetRowsAsync"/>; read by
    /// generation-checked continuations. Captured as a list so the coordinator
    /// holds a stable reference even if the parent's ObservableCollection
    /// changes.
    /// </summary>
    private IReadOnlyList<ModItemViewModel> _rows = Array.Empty<ModItemViewModel>();

    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates the coordinator and reads the persisted density (normalizing
    /// undefined to Detailed).
    /// </summary>
    public DetailedModRowsViewModel(
        IConfigLoader configLoader,
        INexusModMetadataService metadataService,
        IModRepository repository,
        IModThumbnailService thumbnailService,
        ILogger<DetailedModRowsViewModel> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _thumbnailService = thumbnailService ?? throw new ArgumentNullException(nameof(thumbnailService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        RowDensity = NormalizeDensity(_configLoader.Load().Preferences.ModRowDensity);
    }

    /// <summary>Compact mode is active (the dense one-line row).</summary>
    public bool IsCompact => RowDensity == ModRowDensity.Compact;

    /// <summary>Detailed mode is active (summary + thumbnail rows; the default).</summary>
    public bool IsDetailed => RowDensity == ModRowDensity.Detailed;

    /// <summary>
    /// Normalizes a density value: only <see cref="ModRowDensity.Compact"/>
    /// survives; every other numeric value (including undefined) becomes
    /// <see cref="ModRowDensity.Detailed"/>.
    /// </summary>
    private static ModRowDensity NormalizeDensity(ModRowDensity value) =>
        value == ModRowDensity.Compact ? ModRowDensity.Compact : ModRowDensity.Detailed;

    /// <summary>
    /// Sets the density, persists, and reprocesses the current rows. A
    /// value-equal density (after normalization) is a strict no-op: no save,
    /// no reload, no backfill. An undefined numeric parameter normalizes to
    /// Detailed.
    /// </summary>
    [RelayCommand]
    private void SetDensity(ModRowDensity density)
    {
        var normalized = NormalizeDensity(density);
        if (normalized == RowDensity)
        {
            return;
        }

        SaveDensity(normalized);
        RowDensity = normalized;
        _ = SetRowsAsync(_rows);
    }

    /// <summary>
    /// Hands a new row snapshot to the coordinator. The synchronous setup
    /// (cancel the prior generation, snapshot rows, push density, clear
    /// thumbnails on Compact) runs before the method returns. In Compact mode
    /// the returned task is already completed. In Detailed mode the returned
    /// task represents the complete generation: known-thumbnail hydration, the
    /// metadata backfill, and every thumbnail load started by a backfill
    /// result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent (<see cref="ModListViewModel.Reload"/>) intentionally
    /// discards the returned task: the processing absorbs cancellation and
    /// logs every other failure internally, so a discarded task can never
    /// fault. A caller that awaits the result waits for the whole generation to
    /// settle, which is what the focused tests rely on.</para>
    /// <para>
    /// Cancellation from supersession is absorbed inside the processing task;
    /// the returned task never faults. No <c>ConfigureAwait(false)</c>, so
    /// observable row mutation resumes on the captured UI context (the UI-layer
    /// convention).</para>
    /// </remarks>
    /// <param name="rows">The new row snapshot. Must not be <c>null</c>.</param>
    /// <returns>A task representing the generation. Already completed in Compact
    /// mode; the processing pipeline in Detailed mode.</returns>
    public Task SetRowsAsync(IReadOnlyList<ModItemViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var priorCts = _cts;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var generation = Interlocked.Increment(ref _generation);

        priorCts?.Cancel();

        _rows = rows.ToArray();

        foreach (var row in _rows)
        {
            row.IsDetailed = IsDetailed;
        }

        if (!IsDetailed)
        {
            foreach (var row in _rows)
            {
                row.Thumbnail = null;
            }
            DisposeCts(priorCts);
            return Task.CompletedTask;
        }

        DisposeCts(priorCts);

        // Return the processing task so a caller that awaits it waits for the
        // whole generation. The task absorbs cancellation and logs every other
        // failure, so the parent's intentional `_ =` discard is still safe.
        return ProcessDetailedSafelyAsync(generation, ct);
    }

    /// <summary>
    /// Wraps <see cref="ProcessDetailedAsync"/> so the generation task never
    /// faults: cancellation from supersession is absorbed, and every other
    /// exception is logged. This is the task <see cref="SetRowsAsync"/> returns
    /// in Detailed mode, so an awaiter settles cleanly and an intentional
    /// discard cannot create an unobserved task.
    /// </summary>
    private async Task ProcessDetailedSafelyAsync(int generation, CancellationToken ct)
    {
        try
        {
            await ProcessDetailedAsync(generation, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer generation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detailed mod rows processing failed.");
        }
    }

    /// <summary>
    /// The Detailed-mode pipeline: starts known-thumbnail hydration for rows
    /// with eligible persisted metadata, starts the metadata backfill with the
    /// current row container ids as priority, and awaits both. All observable
    /// row mutations happen in generation-checked continuations on the captured
    /// UI context.
    /// </summary>
    private async Task ProcessDetailedAsync(int generation, CancellationToken ct)
    {
        var thumbnailTasks = _rows
            .Where(r => r.CanLoadThumbnail)
            .Select(r => LoadThumbnailAsync(r, generation, ct))
            .ToList();

        var backfillTask = RunBackfillAsync(generation, ct);

        await Task.WhenAll(thumbnailTasks.Append(backfillTask));
    }

    /// <summary>
    /// Invokes the metadata backfill with the current row container ids in row
    /// order as priority. Applies returned metadata only when the generation is
    /// current, the mode is still Detailed, and the row is still in the
    /// snapshot. Re-reads repository metadata as authoritative before applying.
    /// Starts thumbnail hydration for newly enriched eligible rows and awaits
    /// those loads so the caller's task represents the complete generation.
    /// </summary>
    private async Task RunBackfillAsync(int generation, CancellationToken ct)
    {
        var priorityIds = _rows.Select(r => r.ContainerId).ToList();
        if (priorityIds.Count == 0)
        {
            return;
        }

        NexusModMetadataResult result;
        try
        {
            result = await _metadataService.BackfillMissingAsync(priorityIds, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadata backfill invocation failed.");
            return;
        }

        if (generation != _generation || !IsDetailed)
        {
            return;
        }

        var newThumbnails = new List<Task>();
        foreach (var containerId in result.Updated.Keys)
        {
            if (generation != _generation || !IsDetailed)
            {
                return;
            }

            var row = _rows.FirstOrDefault(r => r.ContainerId == containerId);
            if (row is null)
            {
                continue;
            }

            var container = _repository.Get(containerId);
            var authoritative = container?.DisplayMetadata;
            if (authoritative is null)
            {
                continue;
            }

            row.ApplyDisplayMetadata(authoritative);

            if (row.CanLoadThumbnail)
            {
                newThumbnails.Add(LoadThumbnailAsync(row, generation, ct));
            }
        }

        // Await thumbnail loads launched by this backfill so the generation
        // task does not settle before they finish. Each load absorbs its own
        // cancellation and failures, so this await never faults.
        if (newThumbnails.Count > 0)
        {
            await Task.WhenAll(newThumbnails);
        }

        if (result.RateLimited)
        {
            _logger.LogInformation(
                "Metadata backfill rate-limited; resets at {Reset}.",
                result.RateLimitResetsAt);
        }
    }

    /// <summary>
    /// Loads a thumbnail for one row. The caller's token cancels only the
    /// caller's wait (per the reviewed thumbnail service contract); the shared
    /// cache load may continue. The result is assigned only when the generation,
    /// mode, row membership, and requested URL are all still current.
    /// </summary>
    private async Task LoadThumbnailAsync(ModItemViewModel row, int generation, CancellationToken ct)
    {
        var url = row.ThumbnailUrl;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        IImage? image;
        try
        {
            image = await _thumbnailService.GetThumbnailAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail load failed for {Url}.", url);
            return;
        }

        if (generation != _generation || !IsDetailed)
        {
            return;
        }
        if (!_rows.Contains(row))
        {
            return;
        }
        if (row.ThumbnailUrl != url)
        {
            return;
        }

        row.Thumbnail = image;
    }

    private void SaveDensity(ModRowDensity density)
    {
        try
        {
            var config = _configLoader.Load();
            config.Preferences.ModRowDensity = density;
            _configLoader.Save(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist mod-row density.");
        }
    }

    private static void DisposeCts(CancellationTokenSource? cts)
    {
        try { cts?.Dispose(); }
        catch (Exception) { /* best-effort */ }
    }
}
