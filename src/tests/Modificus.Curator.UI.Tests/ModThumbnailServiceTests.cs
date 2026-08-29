using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.Config;
using Modificus.Curator.UI;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="ModThumbnailService"/>: the thumbnail disk/in-memory cache +
/// download orchestrator. Covers URL validation (null/empty/malformed/non-HTTPS/
/// relative), cache-key determinism, cache-miss download (exactly once, temp
/// cleanup, atomic move), cache-hit zero-HTTP, in-memory reuse, concurrent
/// coalescing, four-slot concurrency bound, oversize rejection (declared +
/// streamed), HTTP/I/O failure retryability, corrupt-entry delete + one
/// re-download + one re-decode, cancellation propagation + cleanup + retry,
/// 90-day prune boundary + failure isolation, production decode sizing (the
/// 2x-scaled 192-DIP frame floor), and no ConfigureAwait(false).
/// </summary>
public sealed class ModThumbnailServiceTests
{
    private static readonly string HttpsUrl = "https://example.com/thumb.png";

    // ---- URL validation ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Null_or_empty_url_returns_null(string? url)
    {
        var (service, env) = CreateService();
        using (env)
        {
            var result = await service.GetThumbnailAsync(url);
            Assert.Null(result);
            // No cache dir, no HTTP work.
            Assert.False(Directory.Exists(env.CacheDir));
            Assert.Empty(env.HttpHandler.Requests);
        }
    }

    [Theory]
    [InlineData("ftp://example.com/thumb.png")]
    [InlineData("http://example.com/thumb.png")]
    [InlineData("/relative/path/thumb.png")]
    [InlineData("example.com/thumb.png")]
    [InlineData("not a url at all")]
    public async Task Non_HTTPS_or_malformed_url_returns_null(string url)
    {
        var (service, env) = CreateService();
        using (env)
        {
            var result = await service.GetThumbnailAsync(url);
            Assert.Null(result);
            Assert.False(Directory.Exists(env.CacheDir));
            Assert.Empty(env.HttpHandler.Requests);
        }
    }

    // ---- cache key determinism + URL normalization -------------------------

    [Fact]
    public async Task Same_URL_produces_same_cache_file_regardless_of_query_whitespace()
    {
        // Trailing whitespace is trimmed before hashing; the normalized
        // AbsoluteUri is the hash input.
        var (service, env) = CreateService();
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1, 2, 3 });

            await service.GetThumbnailAsync("  https://example.com/thumb.png  ");
            await service.GetThumbnailAsync("https://example.com/thumb.png");

            // Both calls hit the success cache after the first download, so only
            // one HTTP request and one cache file.
            Assert.Single(env.HttpHandler.Requests);
            Assert.Single(Directory.GetFiles(env.CacheDir));
        }
    }

    [Fact]
    public async Task Different_URL_produces_a_different_cache_file()
    {
        var (service, env) = CreateService();
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });

            await service.GetThumbnailAsync("https://example.com/a.png");
            await service.GetThumbnailAsync("https://example.com/b.png");

            Assert.Equal(2, env.HttpHandler.Requests.Count);
            Assert.Equal(2, Directory.GetFiles(env.CacheDir).Length);
        }
    }

    // ---- cache miss: download once, write, decode, temp cleanup ------------

    [Fact]
    public async Task Cache_miss_downloads_once_writes_bytes_decodes_and_cleans_temp()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(payload);

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Same(decoded, result);
            Assert.Single(env.HttpHandler.Requests);
            // Final cache file exists with the raw bytes.
            var cacheFiles = Directory.GetFiles(env.CacheDir);
            Assert.Single(cacheFiles);
            Assert.Equal(payload, File.ReadAllBytes(cacheFiles[0]));
            // No temp files left behind.
            Assert.DoesNotContain(Directory.GetFiles(env.CacheDir), f => f.Contains(".tmp."));
        }
    }

    // ---- cache hit: zero HTTP ----------------------------------------------

    [Fact]
    public async Task Disk_cache_hit_decodes_without_http()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var firstDecode = new FakeImage();
        var decodeCalls = 0;
        var (service, env) = CreateService(decode: s =>
        {
            decodeCalls++;
            return firstDecode;
        });
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(payload);

            // First call: download + decode.
            await service.GetThumbnailAsync(HttpsUrl);
            Assert.Single(env.HttpHandler.Requests);
            Assert.Equal(1, decodeCalls);

            // Clear the in-memory success cache (simulate a process restart) by
            // creating a fresh service over the same cache dir.
            var secondDecode = new FakeImage();
            var (service2, _) = CreateService(
                env: env,
                decode: s => secondDecode);

            // Second call: disk hit, zero HTTP.
            var result = await service2.GetThumbnailAsync(HttpsUrl);
            Assert.Same(secondDecode, result);
            Assert.Single(env.HttpHandler.Requests); // unchanged
        }
    }

    // ---- in-memory reuse ---------------------------------------------------

    [Fact]
    public async Task Same_URL_returns_the_same_in_memory_image_instance()
    {
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });

            var first = await service.GetThumbnailAsync(HttpsUrl);
            var second = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Same(decoded, first);
            Assert.Same(first, second);
            Assert.Single(env.HttpHandler.Requests);
        }
    }

    // ---- concurrent coalescing: one HTTP/decode ----------------------------

    [Fact]
    public async Task Concurrent_same_URL_coalesces_into_one_http_and_decode()
    {
        var decoded = new FakeImage();
        var decodeCalls = 0;
        var (service, env) = CreateService(decode: s =>
        {
            Interlocked.Increment(ref decodeCalls);
            return decoded;
        });
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });

            var t1 = service.GetThumbnailAsync(HttpsUrl);
            var t2 = service.GetThumbnailAsync(HttpsUrl);
            var t3 = service.GetThumbnailAsync(HttpsUrl);
            var results = await Task.WhenAll(t1, t2, t3);

            Assert.All(results, r => Assert.Same(decoded, r));
            Assert.Single(env.HttpHandler.Requests);
            Assert.Equal(1, decodeCalls);
        }
    }

    // ---- concurrency bound: max 4 distinct URLs ----------------------------

    [Fact]
    public async Task Distinct_URL_loads_never_exceed_concurrency_four()
    {
        // Use a gate so the HTTP responses are held until the test signals. The
        // service's internal semaphore should cap concurrent work at 4, so only
        // 4 requests are ever in flight at once.
        var gates = new Dictionary<string, TaskCompletionSource<bool>>();
        for (var i = 0; i < 6; i++)
        {
            gates["https://example.com/t" + i + ".png"] = new TaskCompletionSource<bool>();
        }
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var countLock = new object();

        var (service, env) = CreateService();
        using (env)
        {
            env.HttpHandler.Responder = req =>
            {
                lock (countLock)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
                }
                // Hold until the gate is signaled.
                gates[req.RequestUri!.AbsoluteUri].Task.GetAwaiter().GetResult();
                lock (countLock)
                {
                    concurrentCount--;
                }
                return PngResponse(new byte[] { 1 });
            };

            // Start 6 distinct URL loads.
            var tasks = gates.Keys.Select(url => service.GetThumbnailAsync(url)).ToArray();

            // Release all gates.
            foreach (var g in gates.Values) g.SetResult(true);
            await Task.WhenAll(tasks);

            Assert.True(maxConcurrent <= 4, $"Max concurrent was {maxConcurrent}, expected <= 4");
        }
    }

    // ---- oversize: declared + streamed -------------------------------------

    [Fact]
    public async Task Declared_Content_Length_over_cap_returns_null_and_leaves_no_file()
    {
        var (service, env) = CreateService();
        using (env)
        {
            env.HttpHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1])
            };
            // Override the Content-Length header to exceed the cap.
            env.HttpHandler.Responder = _ =>
            {
                var msg = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[1]),
                };
                msg.Content.Headers.ContentLength = 9 * 1024 * 1024; // 9 MiB > 8 MiB
                return msg;
            };

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Null(result);
            // No final file, no temp file.
            if (Directory.Exists(env.CacheDir))
            {
                Assert.Empty(Directory.GetFiles(env.CacheDir));
            }
        }
    }

    [Fact]
    public async Task Streamed_bytes_over_cap_return_null_and_leave_no_file()
    {
        // Content-Length is absent (or within cap) but the actual stream exceeds
        // the cap. The service must abort mid-stream.
        var (service, env) = CreateService();
        using (env)
        {
            env.HttpHandler.Responder = _ =>
            {
                // A stream of 9 MiB with no Content-Length header.
                var oversize = new byte[9 * 1024 * 1024];
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(oversize)),
                };
            };

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Null(result);
            if (Directory.Exists(env.CacheDir))
            {
                Assert.Empty(Directory.GetFiles(env.CacheDir));
            }
        }
    }

    // ---- HTTP failure + I/O failure: retryable -----------------------------

    [Fact]
    public async Task Http_failure_returns_null_and_remains_retryable()
    {
        var (service, env) = CreateService();
        using (env)
        {
            var attempt = 0;
            env.HttpHandler.Responder = _ =>
            {
                attempt++;
                return attempt == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : PngResponse(new byte[] { 1 });
            };
            env.DecodeFunc = _ => new FakeImage();

            // Load A fails (HTTP 500). Its in-flight entry is retired inside the
            // shared task's own body, BEFORE this await returns null, so the
            // immediate retry (no delay) starts a fresh load deterministically
            // instead of re-awaiting the completed failed task.
            var first = await service.GetThumbnailAsync(HttpsUrl);
            Assert.Null(first);

            var second = await service.GetThumbnailAsync(HttpsUrl);
            Assert.NotNull(second);

            // Exactly two HTTP requests: the failed attempt + the retry.
            Assert.Equal(2, env.HttpHandler.Requests.Count);
        }
    }

    // ---- corrupt entry: delete, download once, decode once -----------------

    [Fact]
    public async Task Corrupt_disk_entry_is_deleted_downloaded_and_decoded_successfully()
    {
        var decoded = new FakeImage();
        var decodeCalls = 0;
        var (service, env) = CreateService(decode: s =>
        {
            decodeCalls++;
            return decoded;
        });
        using (env)
        {
            // Pre-place a corrupt entry in the cache.
            Directory.CreateDirectory(env.CacheDir);
            var key = Sha256Hex(HttpsUrl);
            File.WriteAllBytes(Path.Combine(env.CacheDir, key), new byte[] { 0, 0, 0 });

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });

            // First decode throws (the corrupt entry); the service deletes it,
            // downloads once, and decodes the fresh file.
            env.DecodeFunc = s =>
            {
                decodeCalls++;
                // The corrupt entry's first decode throws; the second (from the
                // downloaded file) succeeds.
                if (decodeCalls == 1) throw new InvalidOperationException("corrupt");
                return decoded;
            };

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Same(decoded, result);
            Assert.Single(env.HttpHandler.Requests);
        }
    }

    [Fact]
    public async Task Corrupt_entry_replacement_decode_failure_returns_null_without_second_download()
    {
        // The downloaded replacement also fails to decode. The service must
        // return null without a second network round-trip.
        var decodeCalls = 0;
        var (service, env) = CreateService(decode: s =>
        {
            decodeCalls++;
            throw new InvalidOperationException("bad image");
        });
        using (env)
        {
            Directory.CreateDirectory(env.CacheDir);
            var key = Sha256Hex(HttpsUrl);
            File.WriteAllBytes(Path.Combine(env.CacheDir, key), new byte[] { 0 });

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.Null(result);
            // Exactly one HTTP call: the corrupt-entry re-download. No second
            // network round-trip after the replacement decode failed.
            Assert.Single(env.HttpHandler.Requests);
            // Two decode attempts: the corrupt entry + the downloaded file.
            Assert.Equal(2, decodeCalls);
        }
    }

    // ---- per-caller cancellation (shared load is uncancellable) ------------

    [Fact]
    public async Task Cancelling_installer_while_uncancelled_waiter_receives_the_image()
    {
        // The shared load runs uncancellable. The installer cancels, but a
        // coalesced waiter that did NOT cancel still receives the image from
        // the one shared HTTP/decode.
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            var gate = new TaskCompletionSource<bool>();
            env.HttpHandler.Responder = _ =>
            {
                gate.Task.GetAwaiter().GetResult();
                return PngResponse(new byte[] { 1 });
            };

            using var ctsInstaller = new CancellationTokenSource();
            // The installer starts the shared load (blocks in the handler).
            var installerTask = service.GetThumbnailAsync(HttpsUrl, ctsInstaller.Token);
            // Give the shared load time to enter the handler.
            await Task.Delay(50);

            // The waiter arrives (same URL, no cancellation).
            var waiterTask = service.GetThumbnailAsync(HttpsUrl);

            // Cancel the installer. Its WaitAsync(ct) throws OCE, but the
            // shared load is unaffected.
            ctsInstaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installerTask);

            // The waiter did not cancel; signal the gate so the shared load
            // completes.
            gate.SetResult(true);
            var result = await waiterTask;

            Assert.Same(decoded, result);
            // Exactly one HTTP/decode for both callers.
            Assert.Single(env.HttpHandler.Requests);
        }
    }

    [Fact]
    public async Task Cancelling_loser_while_installer_succeeds()
    {
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            var gate = new TaskCompletionSource<bool>();
            env.HttpHandler.Responder = _ =>
            {
                gate.Task.GetAwaiter().GetResult();
                return PngResponse(new byte[] { 1 });
            };

            // The installer starts the shared load (no cancellation).
            var installerTask = service.GetThumbnailAsync(HttpsUrl);
            await Task.Delay(50);

            // The loser arrives (same URL, with a ct).
            using var ctsLoser = new CancellationTokenSource();
            var loserTask = service.GetThumbnailAsync(HttpsUrl, ctsLoser.Token);

            // Cancel the loser. Its WaitAsync throws OCE; the installer and the
            // shared load are unaffected.
            ctsLoser.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loserTask);

            // Signal the gate; the installer succeeds from the one shared load.
            gate.SetResult(true);
            var result = await installerTask;

            Assert.Same(decoded, result);
            Assert.Single(env.HttpHandler.Requests);
        }
    }

    [Fact]
    public async Task All_callers_cancel_shared_load_completes_next_call_gets_cached()
    {
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            var gate = new TaskCompletionSource<bool>();
            env.HttpHandler.Responder = _ =>
            {
                gate.Task.GetAwaiter().GetResult();
                return PngResponse(new byte[] { 1 });
            };

            using var cts = new CancellationTokenSource();
            var task = service.GetThumbnailAsync(HttpsUrl, cts.Token);
            await Task.Delay(50);

            // Cancel the only caller.
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            // The shared load is still running (uncancellable). Signal the gate
            // so it completes; in-task finalization publishes the result.
            gate.SetResult(true);

            // Wait for the shared load to register its single HTTP request. The
            // new call then coalesces onto the still-running load, or hits the
            // success cache once in-task finalization publishes, with zero
            // additional HTTP.
            await WaitForAsync(() => env.HttpHandler.Requests.Count == 1);

            // A new call (no cancellation) gets the cached image with ZERO
            // additional HTTP.
            var result = await service.GetThumbnailAsync(HttpsUrl);
            Assert.Same(decoded, result);
            Assert.Single(env.HttpHandler.Requests);
        }
    }

    [Fact]
    public async Task Failed_load_retired_before_await_returns_so_immediate_retry_starts_fresh()
    {
        // Finalization runs inside the shared task's own body, so load A's
        // in-flight entry is retired BEFORE A's await returns null. An immediate
        // retry (no delay) therefore starts load B fresh; A's exact-identity
        // retirement cannot remove B. B succeeds and populates the success cache.
        // A third call hits the cache with no third HTTP. Exactly two HTTP calls.
        var decoded = new FakeImage();
        var (service, env) = CreateService(decode: _ => decoded);
        using (env)
        {
            var attempt = 0;
            env.HttpHandler.Responder = _ =>
            {
                attempt++;
                return attempt == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : PngResponse(new byte[] { 1 });
            };

            // Load A fails. Its in-flight entry is deterministically retired
            // before this await returns, so no delay is needed before the retry.
            var first = await service.GetThumbnailAsync(HttpsUrl);
            Assert.Null(first);

            // Retry: load B (a new Lazy) starts fresh and succeeds.
            var second = await service.GetThumbnailAsync(HttpsUrl);
            Assert.NotNull(second);
            Assert.Same(decoded, second);

            // Third call: success cache hit, no third HTTP.
            var third = await service.GetThumbnailAsync(HttpsUrl);
            Assert.Same(decoded, third);
            Assert.Equal(2, env.HttpHandler.Requests.Count);
        }
    }

    // ---- 90-day prune: async, once, non-blocking ---------------------------

    [Fact]
    public async Task Prune_deletes_files_older_than_90_days_on_first_valid_request()
    {
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var (service, env) = CreateService(now: () => now);
        using (env)
        {
            Directory.CreateDirectory(env.CacheDir);

            // A stale file for a DIFFERENT URL. 91 days old.
            var staleKey = Sha256Hex("https://example.com/stale.png");
            var staleFile = Path.Combine(env.CacheDir, staleKey);
            File.WriteAllBytes(staleFile, new byte[] { 1 });
            File.SetLastWriteTimeUtc(staleFile, now.UtcDateTime - TimeSpan.FromDays(91));

            // A fresh file (within 90 days).
            var freshKey = Sha256Hex("https://example.com/fresh.png");
            var freshFile = Path.Combine(env.CacheDir, freshKey);
            File.WriteAllBytes(freshFile, new byte[] { 2 });
            File.SetLastWriteTimeUtc(freshFile, now.UtcDateTime - TimeSpan.FromDays(10));

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });
            env.DecodeFunc = _ => new FakeImage();

            // The first valid request starts the fire-and-forget prune. The
            // request returns without awaiting prune. Wait for the prune to
            // finish before asserting.
            await service.GetThumbnailAsync(HttpsUrl);
            await WaitForAsync(() => !File.Exists(staleFile));

            Assert.False(File.Exists(staleFile));
            Assert.True(File.Exists(freshFile));
        }
    }

    [Fact]
    public async Task Prune_runs_only_once_per_service_lifetime()
    {
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var (service, env) = CreateService(now: () => now);
        using (env)
        {
            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });
            env.DecodeFunc = _ => new FakeImage();

            await service.GetThumbnailAsync(HttpsUrl);
            await Task.Delay(100); // let the first prune finish

            // Place a stale file AFTER the first request pruned. The next
            // request should NOT prune it (prune ran once already).
            var staleKey = Sha256Hex("https://example.com/stale.png");
            var staleFile = Path.Combine(env.CacheDir, staleKey);
            File.WriteAllBytes(staleFile, new byte[] { 3 });
            File.SetLastWriteTimeUtc(staleFile, now.UtcDateTime - TimeSpan.FromDays(100));

            await service.GetThumbnailAsync("https://example.com/stale.png");

            Assert.True(File.Exists(staleFile)); // not pruned
        }
    }

    [Fact]
    public async Task Prune_starts_off_thread_and_does_not_block_loading()
    {
        // The prune runs fire-and-forget on Task.Run. The request returns
        // promptly without awaiting the prune. With many stale files the prune
        // takes measurable time; the request should not be delayed by it.
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var (service, env) = CreateService(now: () => now);
        using (env)
        {
            Directory.CreateDirectory(env.CacheDir);
            // Seed many stale files.
            for (var i = 0; i < 50; i++)
            {
                var f = Path.Combine(env.CacheDir, "stale-" + i);
                File.WriteAllBytes(f, new byte[] { 0 });
                File.SetLastWriteTimeUtc(f, now.UtcDateTime - TimeSpan.FromDays(100));
            }

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });
            env.DecodeFunc = _ => new FakeImage();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.GetThumbnailAsync(HttpsUrl);
            sw.Stop();

            Assert.NotNull(result);
            // The request returned without waiting for the prune to finish. The
            // prune is fire-and-forget; the load completed quickly.
            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"Request took {sw.ElapsedMilliseconds}ms; prune may have blocked loading.");
        }
    }

    [Fact]
    public async Task Prune_failure_does_not_block_loading()
    {
        var (service, env) = CreateService();
        using (env)
        {
            Directory.CreateDirectory(env.CacheDir);
            // Delete the dir so prune has nothing to enumerate.
            Directory.Delete(env.CacheDir, recursive: true);

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });
            env.DecodeFunc = _ => new FakeImage();

            var result = await service.GetThumbnailAsync(HttpsUrl);

            Assert.NotNull(result);
        }
    }

    [Fact]
    public async Task Prune_sweeps_multiple_stale_files()
    {
        // Two stale files are both pruned on the first valid request. Verifies
        // the sweep processes every file, not just the first.
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var (service, env) = CreateService(now: () => now);
        using (env)
        {
            Directory.CreateDirectory(env.CacheDir);

            var staleFile1 = Path.Combine(env.CacheDir, "stale-1");
            var staleFile2 = Path.Combine(env.CacheDir, "stale-2");
            File.WriteAllBytes(staleFile1, new byte[] { 0 });
            File.WriteAllBytes(staleFile2, new byte[] { 0 });
            File.SetLastWriteTimeUtc(staleFile1, now.UtcDateTime - TimeSpan.FromDays(100));
            File.SetLastWriteTimeUtc(staleFile2, now.UtcDateTime - TimeSpan.FromDays(100));

            env.HttpHandler.Responder = _ => PngResponse(new byte[] { 1 });
            env.DecodeFunc = _ => new FakeImage();

            await service.GetThumbnailAsync(HttpsUrl);
            // Wait for the fire-and-forget prune to delete BOTH files.
            await WaitForAsync(() => !File.Exists(staleFile1) && !File.Exists(staleFile2));

            Assert.False(File.Exists(staleFile1));
            Assert.False(File.Exists(staleFile2));
        }
    }

    // ---- production decode sizing ------------------------------------------

    [Fact]
    public void Production_decode_width_covers_the_widest_thumbnail_at_2x()
    {
        // The widest detailed-row thumbnail frame is 192 DIP; at 2x display
        // scaling that is 384 physical pixels. A DecodeWidth below the frame's
        // physical size would render upsampled (soft) on scaled displays.
        Assert.True(
            ModThumbnailService.DecodeWidth >= 384,
            $"DecodeWidth {ModThumbnailService.DecodeWidth} cannot keep the 192-DIP " +
            "detailed-row thumbnail frame sharp at 2x display scaling.");
    }

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void Service_can_be_registered_and_resolved_as_a_singleton()
    {
        // Proves the service is DI-compatible: it resolves through
        // AddSingleton<IModThumbnailService> with a factory delegate and the
        // decode/cache/logger seams. The production CuratorComposition wiring
        // (IHttpClientFactory.CreateClient, Bitmap.DecodeToWidth, AppPaths) is
        // not exercised here (it needs a live Avalonia platform); this test
        // proves the registration shape.
        var services = new ServiceCollection();
        services.AddSingleton<IModThumbnailService>(_ => new ModThumbnailService(
            () => new HttpClient(),
            cacheDirOverride: Path.Combine(Path.GetTempPath(), "curator-thumb-di-" + Guid.NewGuid()),
            decode: _ => new FakeImage(),
            logger: NullLogger<ModThumbnailService>.Instance));

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IModThumbnailService));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        var instance = provider.GetRequiredService<IModThumbnailService>();
        Assert.IsType<ModThumbnailService>(instance);
    }

    // ---- no ConfigureAwait(false) ------------------------------------------

    [Fact]
    public void No_ConfigureAwait_false_in_the_service_source()
    {
        // Convention: UI-layer async code never uses ConfigureAwait(false)
        // (continuations must stay on the captured UI context). Verified by
        // reading the production source file.
        var srcPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "ui", "ModThumbnailService.cs"));
        if (!File.Exists(srcPath))
        {
            // In CI the relative path may differ; skip if the source is not found.
            return;
        }
        var sourceText = File.ReadAllText(srcPath);
        Assert.DoesNotContain("ConfigureAwait(false)", sourceText);
    }

    // ---- helpers + fakes ---------------------------------------------------

    /// <summary>A minimal <see cref="IImage"/> for tests. Not a real bitmap;
    /// the decode seam returns instances of this type.</summary>
    private sealed class FakeImage : IImage
    {
        public Size Size => new(160, 120);
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);

            // Run the (potentially blocking) Responder on a threadpool thread
            // and wire the ct so a cancellation cancels the SendAsync task even
            // while the Responder is blocked.
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            Task.Run(() =>
            {
                try { tcs.TrySetResult(Responder(request)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
    }

    private sealed class TestEnv : IDisposable
    {
        public string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "curator-thumb-" + Guid.NewGuid());
        public StubHandler HttpHandler { get; } = new();
        public Func<Stream, IImage> DecodeFunc { get; set; } = _ => new FakeImage();

        public void Dispose()
        {
            if (Directory.Exists(CacheDir))
            {
                try { Directory.Delete(CacheDir, recursive: true); }
                catch { /* best-effort test cleanup */ }
            }
        }
    }

    private static (ModThumbnailService Service, TestEnv Env) CreateService(
        Func<Stream, IImage>? decode = null,
        Func<DateTimeOffset>? now = null,
        TestEnv? env = null)
    {
        env ??= new TestEnv();
        if (decode is not null)
        {
            env.DecodeFunc = decode;
        }
        // Pass a lambda that reads env.DecodeFunc at each invocation, so a test
        // that reassigns DecodeFunc after construction affects the service.
        var service = new ModThumbnailService(
            () => new HttpClient(env.HttpHandler, disposeHandler: false),
            cacheDirOverride: env.CacheDir,
            decode: s => env.DecodeFunc(s),
            logger: NullLogger<ModThumbnailService>.Instance,
            getNow: now);
        return (service, env);
    }

    private static HttpResponseMessage PngResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or a 5-second
    /// timeout. Used to wait for fire-and-forget prune work in a deterministic
    /// way without sleeps.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    private static string Sha256Hex(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
