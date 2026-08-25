# Mod acquisition: architecture

When a user clicks "Mod manager download" on a Nexus file page, the
[handler exe](nxm-scheme-handler.md) relays the `nxm://` URL to the running
app, the router dispatches it, and the `NxmModDownloadHandler` gates the link
and enqueues the download onto the serial download queue. The queue owns the
acquisition (through the reusable `IModAcquisitionService` in Integrations),
the profile registration, and the reload; downloads render as rows in the mod
list (in place on the target mod's row, or appended below the list), with
cancel and retry. The same queue runs premium update installs and the DMF
download: one download engine, one download at a time.

There is no modal spinner and no popup for a mod download. Gate failures (no
auth, no active profile, a non-Darktide link) keep the modal-alert path,
because at gate time there is no row to host them on; everything after the
gate renders on a row.

> Public surface, exact signatures, and DI registration are documented in the
> [integrations reference](../reference/integrations.md) (the acquisition
> service) and the [ui reference](../reference/ui.md#the-download-queue) (the
> queue). This doc covers the architecture and the why.

## Architecture

```
nxm:// URL  (user clicked "Mod manager download" on Nexus)
    │
    ▼
handler exe  →  IPC  →  NxmRouter  →  INxmModDownloadHandler
                                                │
                     ┌───────────────────────────────┘
                     ▼
             NxmModDownloadHandler  (in the UI assembly; the enqueue adapter)
                 │
                 ├─ gate: Darktide domain? (case-insensitive)
                 │     no → IDialogService.ShowAlertAsync("wrong game"); return
                 ├─ gate: auth configured? (NexusAuthMethod != None)
                 │     no → ShowAlertAsync("Configure Nexus first"); return
                 ├─ gate: active profile? (IProfileSession.ActiveProfileId)
                 │     no → ShowAlertAsync("No active profile"); return
                 ├─ peek IModRepository (row name + container id; no API call)
                 └─ IModDownloadQueue.Enqueue(request)  → returns within milliseconds
                                │
                                ▼
             ModDownloadQueue  (ui/Session; one serial worker, FIFO, deduped
                                by game domain + mod id + file id)
                 │
                 ├─ dequeue: auth re-check (a sign-out fails the row inline)
                 ├─ UpdateInstall purpose: eligibility revalidation (silent no-op when stale)
                 ├─ repo hit check: the exact FileId against every version of
                 │  the mod's container → hit: register/acknowledge with NO network
                 ├─ miss: IModAcquisitionService.AcquireFromNexusAsync(url)  (with progress
                 │  + the cancel token; the nxm key/expires ride on the request)
                 │     │
                 │     ├─ INexusClient.DownloadLinksAsync  (CDN URL; premium or free-user overload)
                 │     ├─ INexusClient.GetModInfoAsync     (mod name + Summary/PictureUrl/
                 │     │                                     ContainsAdultContent → ModDisplayMetadata
                 │     │                                     via the shared mapper)
                 │     ├─ INexusClient.ListModFilesAsync   (file version, matched by fileId)
                 │     ├─ download to temp  (IProgress<(Received, Total)>)
                 │     └─ IModImportService.Import(temp.<ext>, name, NexusSource{ModId}, version,
                 │                               remoteUploadedAt, remoteFileId, displayMetadata)
                 │           → NexusAcquisitionResult   (metadata lands on container.json)
                 │
                 ├─ policy: head file → LatestPolicy; non-head file → PinnedPolicy
                 │  pinned to the clicked version (already in profile → SetModPolicy)
                 └─ completion: ProfileAdd (AddMod/SetModPolicy + best-effort
                    acknowledge + reload when still active) | UpdateInstall
                    (acknowledge once + UpdatesApplied)

     rows: a download whose target row is visible morphs it in place;
           everything else appends below the list (one shared status template)
```

## `IModAcquisitionService`: the reusable core

Lives in the Integrations library (alongside `INexusClient`, which it
consumes). The interface is Nexus-only: it resolves the download link, fetches
the mod's metadata, downloads the archive, and imports it. It never touches
profiles; the queue owns registration.

```csharp
public sealed record NexusAcquisitionResult(
    Guid ContainerId,      // repository container (existing reused when known)
    string VersionId,      // the imported version's opaque folder id (the PinnedPolicy key)
    string Version,        // the acquired file's release tag, for display
    bool IsHeadFile);      // the acquired file is the mod's newest non-archived MAIN file

public interface IModAcquisitionService
{
    Task<NexusAcquisitionResult> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<(long Received, long? Total)>? progress = null, CancellationToken ct = default);

    Task<NexusAcquisitionResult> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<(long Received, long? Total)>? progress = null, CancellationToken ct = default);

    Task<(int FileId, string Version)> ResolveLatestNexusAsync(
        string gameDomain, int modId, CancellationToken ct = default);   // no download
}
```

The `IProgress<(long Received, long? Total)>` parameter is the byte-progress
hook: cumulative bytes plus the response `Content-Length` total when the
server sent one (null total = unknown; no separate HEAD call), so the
download row can render determinate progress. The caller (the download queue)
handles profile registration.

`ResolveLatestNexusAsync` resolves the newest non-archived MAIN file WITHOUT
downloading it: one `ListModFilesAsync` call, filtering to MAIN files (Nexus
`category_id` 1, universal across games) that are not archived, picking the
newest by `uploaded_timestamp` (`ModFile` carries an `archived` bool for the
filter). It exists so callers that know the mod id but need a concrete file
id up front (the queue's dedupe key, resolved before any item exists) and
acquire later cannot disagree with `AcquireLatestNexusAsync` on "current
release"; both share one implementation. `InvalidOperationException` surfaces
when no MAIN file is available (the caller shows a user-facing alert).

The service is a singleton (no per-call state; a thin orchestrator over the
client and import service). It resolves `INexusClient`, `IModImportService`,
and `IHttpClientFactory` (for the raw CDN download) from the container.

## Acquisition flow

`ModAcquisitionService.AcquireFromNexusAsync`:

1. **Resolve download links** via `INexusClient.DownloadLinksAsync`. Choose the
   overload: if `nxmKey` and `nxmExpires` are both present, the **free-user**
   overload (the per-file token from the `nxm://` URL); otherwise the
   **premium** (auth-only) overload. The auth header is applied by the client's
   [auth factory](nexus-authentication.md). Use the **first** CDN link
   (`result.Data[0].Uri`); Nexus returns them in priority order (this is what
   every client does).
2. **Resolve metadata** for the Import: `GetModInfoAsync` for the mod name
   (and, from the same payload, the display metadata), `ListModFilesAsync` and
   match by `fileId` for the version string + the matched file's
   `UploadedTimestamp` (Unix seconds). These are 2 API calls (3 total per
   acquisition, within rate limits). The display metadata (the mod's
   `Summary`, `PictureUrl`, and `ContainsAdultContent`) is normalized once
   through the shared `ModDisplayMetadataMapper` from the same
   `GetModInfoAsync` response that resolved the name, so persisting it adds no
   Nexus request; an empty summary stays empty and a non-HTTPS or absent
   `picture_url` becomes a null `ThumbnailUrl`. **No degraded fallback:** if
   the metadata fetch fails, the acquisition fails with a clear error (a mod
   stored under its numeric id as a name is worse than a clean
   failure message) and nothing partial lands. The publish timestamp is
   converted to a `DateTimeOffset?` (null when the wire value is `0` /
   absent) and forwarded as the imported version's `RemoteUploadedAt`; the
   matched file's id is forwarded as the version's `FileId` (the exact
   identity the queue's repository hit check keys on).
3. **Download** from the CDN URI to a temp file
   (`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() +
   Path.GetExtension(fileName))`, where `fileName` is the matched
   `ModFile.FileName`) using a plain `HttpClient` from `IHttpClientFactory`
   plus the 81920-byte buffered copy, reporting the
   `IProgress<(long Received, long? Total)>` tuple (the Content-Length total
   when present, null without one). The real file extension is preserved on
   the temp file for log clarity; archive detection is content-based
   (SharpCompress magic bytes), so the extension is cosmetic. The temp file
   is deleted once Import returns, always, success or failure (no partial
   state).
4. **Import** via `IModImportService.Import(tempPath, modName, new NexusSource
   { ModId = modId }, version, remoteUploadedAt, remoteFileId, displayMetadata)`. The import service validates
   the archive structure (single base folder plus matching `<base>.mod`
   descriptor; archive detection is content-based via SharpCompress), handles
   find-or-create-container (dedup by `NexusSource.ModId`) plus add-version,
   re-evaluates `IsLatest` with the repository's arrival rule, records
   the publish date and the file id on the entry (new or reused), replaces the
   container's `DisplayMetadata` in the same
   manifest update as the version mutation, and extracts into
   `<ModsFolder>/<containerUUID>/<versionFolder>/<baseFolder>/`.
5. **Return** the `NexusAcquisitionResult` (container + version ids, the
   release tag, and whether the file is the mod's head release, computed from
   the files listing the acquisition already reads at zero extra API calls).

A `null` `displayMetadata` argument (the default, including a manual
folder/archive re-import through the picker or drag-and-drop) preserves any
prior `DisplayMetadata`, so a manual re-import never erases a prior Nexus
acquisition or backfill. Every acquisition path (nxm download, manual
per-mod Premium update, automatic Premium install, DMF download) runs through
this one method inside the queue's worker, so all inherit the capture.

The CDN download uses a plain `HttpClient` (not the typed `INexusClient`)
because the CDN URL is an absolute path with the per-file token in the query
string (free users) or just the session auth (premium); no base address or
Nexus-specific headers are needed.

## `NxmModDownloadHandler`: the enqueue adapter

The real `INxmModDownloadHandler` that supersedes the library's no-op default.
The handler performs no acquisition and no profile write: its passing path is
an in-memory repository peek plus one enqueue, so `HandleAsync` returns
within milliseconds and the IPC accept loop is freed immediately (enqueue
order equals click order; a slow return would block every later nxm
delivery). Its gates and flow:

1. **Darktide-only gate**: the URL's game domain must match the Darktide
   domain (case-insensitive) before anything else; a foreign link is refused
   with a localized alert naming the link's game.
2. **Auth check** (live config read): `NexusConfig.AuthMethod != None`
   (required for every download; the `nxm://` key/expires is the per-file
   token for the free-user endpoint, **not** a substitute for auth). On
   `None`, `ShowAlertAsync("Nexus not configured", ...)` and return.
3. **Active-profile check**: `IProfileSession.ActiveProfileId != null`. On
   null, `ShowAlertAsync("No active profile", ...)` and return. The gate
   refusals stay modal alerts because at gate time there is no download row
   to host the failure on.
4. **Peek + enqueue**: the repository lookup by `NexusSource.ModId` names the
   row (the container's stored name, or the localized "Nexus mod #<id>"
   fallback on a miss; no prefetch API call) and carries the container id;
   the profile read supplies the target name captured at enqueue. The request
   (domain, mod id, file id, purpose, container id, display name, target
   profile, nxm key/expires) is admitted onto the queue. Everything
   downstream belongs to the queue, whose failures render inline on the row.

**Policy on completion:** the queue's policy rule is head-relative, both the
repository-hit and the acquired path: a head file (the matched version's
`IsLatest` flag, or the acquisition's `IsHeadFile`) registers `LatestPolicy`
(new mods auto-track the newest release); a non-head file registers
`PinnedPolicy` pinned to the clicked version's folder (downloading an older
file pins to it). When the container is already in the target profile, the
policy is applied through `SetModPolicy` (`AddMod` is policy-idempotent and
would silently keep the old policy; the user's click must win). The user can
change the policy afterwards via the mod-list UI's pin dropdown.

**`ShowAlertAsync` marshaling:** the handler runs on the IPC server's
background task, so the dialog is marshaled to the UI thread via an injectable
`invokeOnUi` seam (`Func<Func<Task>, Task>`). Production wires
`Dispatcher.UIThread.InvokeAsync`; tests inject a pass-through. The
`ShowAlertAsync` itself is a fire-and-forget dialog (OK button only, no return
value).

### The handler lives in the UI assembly

`NxmModDownloadHandler` lives in the UI assembly (`Modificus.Curator.UI.Nxm`),
not Integrations, because it coordinates UI concerns: it reads the active
profile from `IProfileSession` (UI), shows error dialogs through
`IDialogService` (UI), and marshals those dialogs to the UI thread via
`Dispatcher.UIThread` (Avalonia). Placing it in Integrations would create a
dependency cycle (Integrations cannot reference the UI assembly, which is its
consumer). The reusable acquisition service is the backend seam in
Integrations; the handler is the thin UI-coordinating shell.

The handler is registered **after** `AddNxm()` so DI "last registration wins"
supersedes the no-op default (the no-op default is registered with plain
`AddSingleton`, and MS DI resolves the last registration). The queue itself
and the update-path enqueue front (`ModUpdateEnqueuer`) are registered beside
it, so every download path shares one engine.

## One download engine

The queue is the single acquisition gate across every download path:

- the `nxm://` click (the enqueue adapter above),
- the manual per-row Premium update action (resolves the mod's head file via
  `ResolveLatestNexusAsync`, then admits an `UpdateInstall` item through
  `ModUpdateEnqueuer`),
- the automatic Premium update batch (the same enqueue front, one item per
  flagged candidate),
- the DMF prompt's premium branch (resolves DMF's head file at confirm, then
  admits a `ProfileAdd` item).

The queue's serial worker (one download at a time, FIFO) is the mutual
exclusion point; the dedupe key (game domain, mod id, file id) means a click
for a file already live in the queue joins the existing item and pulses its
row instead of starting a second download. Premium update installs have no
separate installer, coordinator, or spinner: the row morph is the busy
surface, the dequeue-time eligibility revalidation (`UpdateEligibility`)
makes a stale flag a silent no-op, and the completion acknowledges the
install once and raises the applied event that reloads the mod list. The row
hosting model (the in-place morph and the appended row) is UI architecture;
see [ui architecture](ui-architecture.md).

## OS registration

Registration as the OS `nxm://` handler is an explicit user action from the
Integrations destination ("Nexus download links" section), not a startup
auto-registration. The register path confirms first because it is a
system-wide change that can take `nxm://` clicks from Vortex, Mod Organizer 2,
Nexus Mod Manager, or other mod managers; the unregister path only releases
Curator's own registration. See
[nxm:// scheme handler](nxm-scheme-handler.md) for the registrar interface and
the platform implementations.

- **Linux** writes a `.desktop` file to `~/.local/share/applications/`
  (`modificus-curator-nxm-handler.desktop`, under the `applications/` subdirectory of the
  local data dir) with `Exec="<handler-exe>" %u` and
  `MimeType=x-scheme-handler/nxm;`, plus a best-effort `xdg-mime default` to
  set it as the default for `x-scheme-handler/nxm`. In an AppImage run, Curator
  atomically copies the native-AOT handler to its per-user data directory and
  creates a sibling symlink to the AppImage, so the persistent desktop entry
  never points into a temporary mount.
- **Windows** writes `HKCU\Software\Classes\nxm` (per-user, no elevation) with
  the handler exe as the `shell\open\command`.

## Darktide-only downloads

Curator supports only Warhammer 40,000: Darktide Nexus downloads. The handler
rejects any `NxmModDownloadUrl` whose game domain is not
the Darktide domain (`NexusGameIdentity.DarktideDomain`, case-insensitive)
before the auth, active-profile, and
enqueue gates, surfacing a localized alert that names the link's game. No
auth read, acquisition call, or profile registration happens for a non-Darktide
link.

## See also

- [integrations reference](../reference/integrations.md):
  `IModAcquisitionService` public surface, the acquisition flow, the
  `NxmModDownloadHandler`, DI registration, testing.
- [ui reference](../reference/ui.md): the download queue (`IModDownloadQueue`,
  `ModUpdateEnqueuer`, `IAutomaticUpdateService`) and the download rows.
- [nxm:// scheme handler](nxm-scheme-handler.md): the plumbing that
  delivers the URL to the handler implemented here.
- [Nexus authentication](nexus-authentication.md): the auth factory the v1
  client uses for the download-link and metadata calls.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level tie-together.
