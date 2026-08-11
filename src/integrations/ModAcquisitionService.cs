using System.Net.Http;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The default <see cref="IModAcquisitionService"/>. A thin orchestrator over
/// <see cref="INexusClient"/> (the v1 API calls), a plain <c>HttpClient</c> (the
/// CDN archive download), and <see cref="IModImportService"/> (the extract +
/// place into the repository). Holds no per-call state; registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>CDN download uses a plain <c>HttpClient</c>.</b> The typed
/// <see cref="INexusClient"/> targets the API base URL and applies Nexus auth per
/// request; the CDN URL returned by <c>download_link.json</c> is a plain absolute
/// URL (the per-file token is in the query string for free users, or just the
/// session auth for premium), so a factory-created client without a base address
/// is the right tool. This mirrors how NMA fetches the archive.</para>
/// <para>
/// <b>First CDN link wins.</b> Nexus returns CDN URLs in priority order; the
/// first entry is used unconditionally (every client does this; there is no
/// selection logic to maintain here).</para>
/// <para>
/// <b>3 Nexus API calls per acquisition:</b> <c>download_link.json</c> (the CDN
/// URL), <c>mods/{id}.json</c> (the name + the display metadata: summary,
/// thumbnail URL, adult flag), and <c>mods/{id}/files.json</c> (the file's
/// version string + publish timestamp). Display metadata is normalized from the
/// same <c>mods/{id}.json</c> payload the name came from, so persisting it adds
/// no Nexus request. Within rate limits (grounded against the Nexus v1 surface).
/// If any metadata call fails, the acquisition fails with a clear error (no
/// degraded fallback).</para>
/// </remarks>
internal sealed class ModAcquisitionService : IModAcquisitionService
{
    private const int DownloadBufferSize = 81920;

    private readonly INexusClient _nexus;
    private readonly IModImportService _import;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModAcquisitionService> _logger;

    public ModAcquisitionService(
        INexusClient nexus,
        IModImportService import,
        IHttpClientFactory httpClientFactory,
        ILogger<ModAcquisitionService> logger)
    {
        _nexus = nexus ?? throw new ArgumentNullException(nameof(nexus));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain,
        int modId,
        int fileId,
        string? nxmKey = null,
        long? nxmExpires = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);

        // 1. Resolve download links. The free-user overload carries the per-file
        //    key + expiry (the nxm token); the premium overload is auth-only.
        //    The auth header itself is applied by INexusClient's auth factory.
        var cdnUri = await ResolveDownloadUriAsync(gameDomain, modId, fileId, nxmKey, nxmExpires, ct)
            .ConfigureAwait(false);

        // 2. Resolve metadata (name + version + the file's remote-publish
        //    timestamp + the display metadata). No degraded fallback: a failure
        //    here surfaces as a clear error and nothing partial lands. The
        //    publish timestamp is recorded on the imported version so the
        //    update check compares publish dates (the imported file vs the
        //    latest file), NOT Curator's import date (which would always be
        //    newer than any past upload and so mask an outdated install). The
        //    display metadata (summary, thumbnail, adult flag) comes from the
        //    same GetModInfoAsync call the name did, so persisting it adds no
        //    Nexus request.
        var (modName, version, remoteUploadedAt, displayMetadata) =
            await ResolveMetadataAsync(gameDomain, modId, fileId, ct).ConfigureAwait(false);

        // 3. Download the archive to a temp file, then hand it to the import
        //    service. The temp file gets an opaque random name (Path.GetRandomFileName
        //    doesn't create a file, unlike GetTempFileName, so the download
        //    creates it fresh). The import detects the archive format from the
        //    file contents (content-based, format-agnostic), so the temp
        //    filename is irrelevant to detection. The temp is always cleaned up
        //    in the finally once Import returns (the import extracts the content
        //    into the repository, so the source archive is disposable).
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        HttpClient? http = null;
        try
        {
            http = _httpClientFactory.CreateClient();
            await DownloadToFileAsync(http, cdnUri, tempPath, progress, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Acquired Nexus mod {Mod} file {File} ({Version}); importing.",
                modId, fileId, version);

            return _import.Import(
                tempPath, modName, new NexusSource { ModId = modId }, version,
                remoteUploadedAt, displayMetadata);
        }
        finally
        {
            http?.Dispose();
            TryDelete(tempPath);
        }
    }

    /// <inheritdoc />
    public async Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
        string gameDomain,
        int modId,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);

        // List the mod's files, filter to MAIN / non-archived, and pick the
        // newest by upload timestamp. The category_id == 1 convention is
        // universal across Nexus games (MAIN); archived files are excluded so
        // the update targets a current release, not a historical one the author
        // has superseded. The acquisition's own ListModFilesAsync call below
        // re-fetches the same list (for the version string + publish timestamp); this
        // call is the price of resolving the fileId from the mod id alone.
        // NexusModFiles.LatestMain is shared with the update-check thorough pass
        // so both call sites agree on "latest MAIN release".
        var files = await _nexus.ListModFilesAsync(gameDomain, modId, ct).ConfigureAwait(false);
        var latest = NexusModFiles.LatestMain(files.Data);

        if (latest is null)
        {
            throw new InvalidOperationException(
                $"Nexus mod {modId} has no MAIN file available (all files are optional, archived, or absent).");
        }

        // Auth-only / premium download path: no nxm key or expiry. The caller
        // (the per-mod update button) gates on premium before invoking. The
        // ModFile.FileId is long in the v1 response shape; Nexus file ids fit
        // comfortably in int (every other call site treats them as int), so the
        // narrowing cast is safe + matches the established assumption.
        return await AcquireFromNexusAsync(
            gameDomain, modId, (int)latest.FileId, nxmKey: null, nxmExpires: null, progress, ct)
            .ConfigureAwait(false);
    }

    // ---- step 1: resolve the CDN download URI ------------------------------

    private async Task<Uri> ResolveDownloadUriAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey, long? nxmExpires, CancellationToken ct)
    {
        Response<DownloadLink[]> links;
        if (nxmKey is not null && nxmExpires is not null)
        {
            links = await _nexus.DownloadLinksAsync(
                gameDomain, modId, fileId, nxmKey, nxmExpires.Value, ct).ConfigureAwait(false);
        }
        else
        {
            links = await _nexus.DownloadLinksAsync(
                gameDomain, modId, fileId, ct).ConfigureAwait(false);
        }

        // Nexus returns CDN URLs in priority order; the first is used.
        if (links.Data is null || links.Data.Length == 0)
        {
            throw new InvalidOperationException(
                $"Nexus returned no download links for mod {modId} file {fileId}.");
        }

        return links.Data[0].Uri;
    }

    // ---- step 2: resolve name + version + display metadata ----------------

    private async Task<(string Name, string Version, DateTimeOffset? RemoteUploadedAt, ModDisplayMetadata DisplayMetadata)> ResolveMetadataAsync(
        string gameDomain, int modId, int fileId, CancellationToken ct)
    {
        var info = await _nexus.GetModInfoAsync(gameDomain, modId, ct).ConfigureAwait(false);
        var modName = info.Data?.Name;
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new InvalidOperationException(
                $"Nexus returned an empty name for mod {modId}.");
        }

        var files = await _nexus.ListModFilesAsync(gameDomain, modId, ct).ConfigureAwait(false);
        string? version = null;
        long? uploadedTimestamp = null;
        if (files.Data is not null)
        {
            foreach (var f in files.Data)
            {
                if (f.FileId == fileId)
                {
                    version = f.Version;
                    uploadedTimestamp = f.UploadedTimestamp;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(
                $"Nexus did not list file {fileId} for mod {modId} (no version resolved).");
        }

        // The publish timestamp (Unix seconds on ModFile) becomes the imported
        // version's RemoteUploadedAt: provenance for the acquisition layer (when
        // the file was published on Nexus). A zero timestamp (the wire default
        // when the field is absent on every entry, e.g. a stub payload) is
        // treated as "unknown" -> null.
        DateTimeOffset? remoteUploadedAt = (uploadedTimestamp is null or 0)
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(uploadedTimestamp.Value);

        // Display metadata is normalized from the same GetModInfoAsync payload
        // the name came from, so no extra Nexus request is added. The mapper
        // owns the HTTPS/non-empty rules; acquisition and the later backfill
        // share it so the rules cannot drift.
        var displayMetadata = ModDisplayMetadataMapper.ToDisplayMetadata(info.Data!);

        return (modName, version, remoteUploadedAt, displayMetadata);
    }

    // ---- step 3: stream the archive to disk --------------------------------

    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="destinationPath"/>
    /// using an 81920-byte buffered copy. Reports cumulative bytes to
    /// <paramref name="progress"/> when provided.
    /// </summary>
    private static async Task DownloadToFileAsync(
        HttpClient http, Uri uri, string destinationPath,
        IProgress<long>? progress, CancellationToken ct)
    {
        // ResponseHeadersRead so the network stream starts flowing before the
        // whole body is buffered; we copy it ourselves with a bounded buffer.
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);

        var buffer = new byte[DownloadBufferSize];
        long total = 0;
        int read;
        while ((read = await network.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), ct)
            .ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
    }

    /// <summary>
    /// Best-effort file delete that swallows I/O errors (the original exception
    /// is what callers need to see).
    /// </summary>
    private static void TryDelete(string path)
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
            // Best-effort cleanup; the original error is what matters.
        }
    }
}
