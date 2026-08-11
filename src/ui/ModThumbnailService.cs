using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;

namespace Modificus.Curator.UI;

/// <summary>
/// Default <see cref="IModThumbnailService"/>. Downloads trusted HTTPS thumbnail
/// URLs to a disk cache, decodes bounded <see cref="IImage"/> instances at
/// <see cref="DecodeWidth"/> physical pixels wide, and keeps successful decodes
/// in an application-lifetime in-memory cache. Concurrent same-URL requests coalesce into one shared load
/// that no single caller can cancel; distinct loads are bounded by a four-slot
/// semaphore. Every expected failure (invalid URL, HTTP, oversize, I/O, decode)
/// logs and returns <c>null</c>. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>ConfigureAwait(false)</c>.</b> Async continuations resume on the
/// captured UI context (the UI-layer convention). CPU-bound decode and prune
/// work runs on <see cref="Task.Run"/>; the continuation that touches observable
/// state stays on the captured context.</para>
/// <para>
/// <b>Per-caller cancellation.</b> The shared load runs with
/// <see cref="CancellationToken.None"/> so no caller can cancel another's load.
/// Every caller (including the one that installed the shared task) awaits it
/// with <c>WaitAsync(ct)</c>: a caller's cancellation propagates only to that
/// caller. The shared load continues to completion and may populate the disk +
/// in-memory caches even when every current caller cancelled. An internal
/// <c>TaskCanceledException</c> (timeout) is treated as an expected load
/// failure returning <c>null</c>, not as a caller cancellation.</para>
/// <para>
/// <b>Retryable failures.</b> Finalization (publishing a success into the
/// in-memory cache + conditionally retiring the in-flight entry by exact
/// <c>Lazy</c> identity) runs INSIDE the shared task's own body, before the
/// task completes to any awaiter. A caller that observes <c>null</c> (or a
/// fault) therefore resumes only after its in-flight entry has already been
/// retired, so an immediate retry starts a fresh load instead of re-awaiting
/// the completed failed task. A corrupt disk entry is deleted, re-downloaded,
/// and re-decoded exactly once; a second decode failure returns <c>null</c>
/// without another network round-trip.</para>
/// </remarks>
internal sealed class ModThumbnailService : IModThumbnailService
{
    /// <summary>
    /// The physical-pixel width the production decode path targets. Large enough
    /// for the widest detailed-row thumbnail (112 DIP) to stay sharp on scaled
    /// displays. Referenced from the production DI wiring
    /// (<see cref="CuratorComposition"/>) so the decode literal lives in one
    /// place. Tests inject their own decode seam and do not read this value.
    /// </summary>
    internal const int DecodeWidth = 256;
    private const int MaxBytes = 8 * 1024 * 1024; // 8 MiB absolute maximum.
    private const int IoBufferSize = 81920;
    private static readonly TimeSpan PruneAge = TimeSpan.FromDays(90);

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly string _cacheDir;
    private readonly Func<Stream, IImage> _decode;
    private readonly ILogger<ModThumbnailService> _logger;
    private readonly Func<DateTimeOffset> _getNow;

    // Application-lifetime success cache: cache key -> decoded image. Kept alive
    // so multiple rows + reloads share one image and no bound row observes a
    // disposed bitmap.
    private readonly ConcurrentDictionary<string, IImage> _successCache = new();

    // In-flight coalescing: one lazy load task per cache key. The Lazy ensures
    // only the winner starts the task; losers await the winner's task. Retired
    // inside the shared task's own body (LoadAndFinalizeAsync), before the task
    // completes to any awaiter, so a caller observing null/fault cannot re-await
    // a still-in-flight entry.
    private readonly ConcurrentDictionary<string, Lazy<Task<IImage?>>> _inFlight = new();

    // Bounds concurrent distinct-key load work (cache-file read, HTTP download,
    // decode) to four slots.
    private readonly SemaphoreSlim _loadLock = new(4);

    // One-time prune guard. 0 = not started, 1 = started.
    private int _pruneStarted;

    /// <param name="httpClientFactory">Creates a fresh <see cref="HttpClient"/>
    /// per download. Production wires <c>IHttpClientFactory.CreateClient</c>;
    /// tests pass a factory backed by a stub handler.</param>
    /// <param name="cacheDirOverride">Override cache root for deterministic
    /// tests. Production passes <c>null</c>, which resolves to
    /// <see cref="AppPaths.ModThumbnailCacheDir"/>.</param>
    /// <param name="decode">Decodes a stream into an <see cref="IImage"/>.
    /// Production uses <see cref="Bitmap.DecodeToWidth(Stream, int, BitmapInterpolationMode)"/>
    /// at <see cref="DecodeWidth"/> px; tests pass a fake.</param>
    /// <param name="logger">Structured logger for best-effort failure paths.</param>
    /// <param name="getNow">Clock for the prune age check. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>; tests inject a controllable clock.</param>
    public ModThumbnailService(
        Func<HttpClient> httpClientFactory,
        string? cacheDirOverride,
        Func<Stream, IImage> decode,
        ILogger<ModThumbnailService> logger,
        Func<DateTimeOffset>? getNow = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cacheDir = cacheDirOverride ?? AppPaths.ModThumbnailCacheDir;
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<IImage?> GetThumbnailAsync(string? thumbnailUrl, CancellationToken ct = default)
    {
        // 1. Validate. Non-HTTPS / relative / malformed / empty return null
        //    without creating the cache dir or touching the network.
        if (!TryNormalizeUrl(thumbnailUrl, out var uri))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var key = ComputeCacheKey(uri);

        // 2. Success cache hit (in-memory).
        if (_successCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 3. Coalesce. Lazy<Task> ensures only the winner starts the shared
        //    load; losers await the winner's task without starting their own.
        //    The factory captures no caller's CancellationToken: the shared load
        //    runs with CancellationToken.None semantics so no caller can cancel
        //    another's load. The factory passes the Lazy's own identity into
        //    LoadAndFinalizeAsync so the exact entry can be conditionally
        //    retired (an old lifecycle can never remove a replacement).
        Lazy<Task<IImage?>> lazy = null!;
        lazy = new Lazy<Task<IImage?>>(
            () => LoadAndFinalizeAsync(uri, key, lazy),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var winner = _inFlight.GetOrAdd(key, lazy);

        // 4. Every caller (including the installer) awaits the shared task with
        //    WaitAsync(ct). A caller's cancellation propagates only to that
        //    caller; the shared load continues and may populate caches.
        return await winner.Value.WaitAsync(ct);
    }

    /// <summary>
    /// The shared load pipeline plus in-task finalization. Runs the uncancellable
    /// <see cref="LoadAsync"/> (which returns <c>null</c> for every expected
    /// failure), then publishes a successful image into the in-memory cache and
    /// conditionally retires the in-flight entry by key + exact <c>Lazy</c>
    /// identity BEFORE the returned task completes to any awaiter. Because
    /// finalization is part of the shared task's own body, a caller that observes
    /// <c>null</c> (or a fault) resumes only after its in-flight entry has
    /// already been retired, so an immediate retry starts a fresh load instead of
    /// re-awaiting the completed failed task. The <c>finally</c> guarantees
    /// retirement even on an unexpected fault. Finalization is exception-safe: a
    /// publication or retirement fault is logged and swallowed so it cannot break
    /// ordinary callers or surface an unobserved exception.
    /// </summary>
    private async Task<IImage?> LoadAndFinalizeAsync(
        Uri uri, string key, Lazy<Task<IImage?>> lazy)
    {
        IImage? result = null;
        try
        {
            result = await LoadAsync(uri, key);
            return result;
        }
        finally
        {
            FinalizeLoad(key, lazy, result);
        }
    }

    /// <summary>
    /// Publishes a successful decode into the in-memory cache and conditionally
    /// retires the in-flight entry by key + exact <c>Lazy</c> identity. The exact
    /// identity check (<see cref="ICollection{T}"/>.Remove over the dictionary)
    /// prevents load A's finalization from removing a replacement load B that a
    /// retrying caller installed. Both steps are wrapped so a fault in the
    /// finalization itself is logged, not unobserved.
    /// </summary>
    private void FinalizeLoad(string key, Lazy<Task<IImage?>> lazy, IImage? result)
    {
        if (result is not null)
        {
            try
            {
                _successCache[key] = result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Thumbnail cache publication for key {Key} failed.", key);
            }
        }

        // Conditional retirement by key + exact Lazy reference. ConcurrentDictionary's
        // ICollection<KeyValuePair>.Remove atomically removes only when both key and
        // value match. This prevents load A's finalization from removing a replacement
        // load B that a retrying caller installed after A's in-flight entry was
        // superseded.
        try
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<IImage?>>>>)_inFlight)
                .Remove(new KeyValuePair<string, Lazy<Task<IImage?>>>(key, lazy));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail in-flight retirement for key {Key} failed.", key);
        }
    }

    // ---- shared load pipeline (CancellationToken.None semantics) ------------

    /// <summary>
    /// The full load pipeline for one cache key. Runs uncancellable: the shared
    /// load is never cancelled by any caller. Handles lazy prune, success-cache
    /// double-check, disk hit, download, decode, and the corrupt-entry retry-
    /// once path. Never throws: all expected failures return <c>null</c>.
    /// </summary>
    private async Task<IImage?> LoadAsync(Uri uri, string key)
    {
        EnsurePruned();

        // Double-check the success cache (a concurrent load may have populated
        // it between the caller's check and GetOrAdd).
        if (_successCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _loadLock.WaitAsync();
        try
        {
            return await LoadFromDiskOrWebAsync(uri, key);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Disk-hit decode, or download + decode. A corrupt disk entry is deleted
    /// and the download + decode path runs once; a second decode failure returns
    /// null without another network round-trip. Never throws: all expected
    /// failures return <c>null</c>.
    /// </summary>
    private async Task<IImage?> LoadFromDiskOrWebAsync(Uri uri, string key)
    {
        var cacheFile = CacheFilePath(key);

        // Disk hit: decode without HTTP.
        if (File.Exists(cacheFile))
        {
            try
            {
                return await DecodeFromFileAsync(cacheFile);
            }
            catch (Exception ex)
            {
                // Corrupt or unreadable entry: delete, fall through to the
                // download + decode retry (exactly once).
                _logger.LogWarning(ex, "Corrupt thumbnail cache entry at {Path}; deleting and re-downloading.", cacheFile);
                TryDeleteFile(cacheFile);
            }
        }

        // Download to temp, atomic move. Download failure returns null without
        // creating the final file.
        if (await TryDownloadAsync(uri, cacheFile))
        {
            // Decode the freshly-downloaded file (one decode retry after the
            // corrupt-entry deletion above). A failure here returns null; no
            // second network round-trip.
            try
            {
                return await DecodeFromFileAsync(cacheFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downloaded thumbnail for {Url} failed to decode.", uri);
                TryDeleteFile(cacheFile);
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> to a sibling temp file of
    /// <paramref name="cacheFile"/>, enforcing the 8 MiB cap, then atomically
    /// moves the temp into place. Returns <c>true</c> when the final file is
    /// ready for decode; <c>false</c> on any expected failure. Never throws.
    /// </summary>
    private async Task<bool> TryDownloadAsync(Uri uri, string cacheFile)
    {
        EnsureCacheDir();
        var tempFile = cacheFile + ".tmp." + Guid.NewGuid().ToString("N");
        HttpClient? http = null;
        try
        {
            http = _httpClientFactory();
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Content-Length cap: reject before streaming a known-oversize body.
            if (response.Content.Headers.ContentLength is { } declared && declared > MaxBytes)
            {
                _logger.LogWarning(
                    "Thumbnail {Url} declared {Bytes} bytes; exceeds the {Max} byte cap.",
                    uri, declared, MaxBytes);
                return false;
            }

            await using var network = await response.Content.ReadAsStreamAsync();

            // Explicit using block (not a using declaration) so the temp handle
            // closes before File.Move below; Windows rejects renaming an in-use
            // source. The oversize early return also closes the handle before
            // the outer finally cleans up the temp.
            using (var file = new FileStream(
                tempFile, FileMode.Create, FileAccess.Write, FileShare.None,
                IoBufferSize, useAsync: true))
            {
                var buffer = new byte[IoBufferSize];
                long total = 0;
                int read;
                while ((read = await network.ReadAsync(buffer.AsMemory(0, IoBufferSize))) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        _logger.LogWarning(
                            "Thumbnail {Url} streamed past the {Max} byte cap; aborting.",
                            uri, MaxBytes);
                        return false;
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read));
                }

                await file.FlushAsync();
            }

            // Temp handle closed above; rename must happen after closure for
            // Windows compatibility. Same-volume atomic (temp is a sibling).
            File.Move(tempFile, cacheFile);
            return true;
        }
        catch (Exception ex)
        {
            // All exceptions (HTTP failure, timeout/TaskCanceledException, I/O)
            // are expected load failures, not caller cancellations (the shared
            // load runs with CancellationToken.None).
            _logger.LogWarning(ex, "Thumbnail download for {Url} failed.", uri);
            return false;
        }
        finally
        {
            http?.Dispose();
            TryDeleteFile(tempFile);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> read-only and decodes via the injected
    /// decode seam on a background thread (CPU-bound). Without
    /// <c>ConfigureAwait(false)</c> the continuation resumes on the captured UI
    /// context.
    /// </summary>
    private async Task<IImage> DecodeFromFileAsync(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, useAsync: true);
        return await Task.Run(() => _decode(stream));
    }

    // ---- prune --------------------------------------------------------------

    /// <summary>
    /// Starts the once-per-service prune on a background thread. Returns
    /// immediately without awaiting so prune never blocks loading. The prune body
    /// catches/logs all failures so its fire-and-forget task cannot fault
    /// unobserved. The benign prune/load race is accepted: a pruned old file
    /// causes the normal disk-decode fallback/download path.
    /// </summary>
    private void EnsurePruned()
    {
        if (Interlocked.CompareExchange(ref _pruneStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PruneCache();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Thumbnail cache prune failed (best-effort).");
            }
        });
    }

    /// <summary>
    /// Deletes ordinary cache files older than 90 days. Per-file
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> is
    /// logged with the path so one locked file does not silently abort the
    /// sweep; the outer catch handles unexpected failures.
    /// </summary>
    private void PruneCache()
    {
        if (!Directory.Exists(_cacheDir))
        {
            return;
        }

        var cutoff = _getNow().UtcDateTime - PruneAge;
        foreach (var file in Directory.EnumerateFiles(_cacheDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogDebug("Pruned stale thumbnail cache entry {Path}.", file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not prune stale cache file {Path}; skipping.", file);
            }
        }
    }

    // ---- helpers -----------------------------------------------------------

    private string CacheFilePath(string key) => Path.Combine(_cacheDir, key);

    private void EnsureCacheDir()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create thumbnail cache dir {Path}.", _cacheDir);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Validates <paramref name="thumbnailUrl"/> as a non-empty absolute HTTPS
    /// URI and outputs the normalized <see cref="Uri.AbsoluteUri"/> form. Returns
    /// <c>false</c> (without side effects) for null, empty, whitespace,
    /// non-absolute, non-HTTPS, or malformed input.
    /// </summary>
    private static bool TryNormalizeUrl(string? thumbnailUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(thumbnailUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    /// <summary>
    /// Lowercase SHA-256 hex of the normalized URL. A URL change produces a new
    /// key naturally (the hash differs).
    /// </summary>
    private static string ComputeCacheKey(Uri uri)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
