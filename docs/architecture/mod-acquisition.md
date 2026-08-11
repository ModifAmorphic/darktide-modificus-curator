# Mod acquisition: architecture

When a user clicks "Mod manager download" on a Nexus file page, the
[handler exe](nxm-scheme-handler.md) relays the `nxm://` URL to the running
app, the router dispatches it, and the `NxmModDownloadHandler` orchestrates the
download and import into the active profile. The reusable core is
`IModAcquisitionService` (Integrations), which both the nxm handler and the
per-mod update button call.

The acquisition path is backend-only except one
`IDialogService.ShowAlertAsync` call for error feedback. There is no progress
bar, no notification system, no download-history panel. On success, the mod
appears in the profile's mod list.

> Public surface, exact signatures, and DI registration are documented in the
> [integrations reference](../reference/integrations.md). This
> doc covers the architecture and the why.

## Architecture

```
nxm:// URL  (user clicked "Mod manager download" on Nexus)
    │
    ▼
handler exe  →  IPC  →  NxmRouter  →  INxmModDownloadHandler
                                                │
                    ┌───────────────────────────────┘
                    ▼
            NxmModDownloadHandler  (in the UI assembly)
                │
                ├─ check: auth configured? (NexusAuthMethod != None)
                │     no → IDialogService.ShowAlertAsync("Configure Nexus first"); return
                ├─ check: active profile? (IProfileSession.ActiveProfileId)
                │     no → ShowAlertAsync("No active profile"); return
                ├─ call IModAcquisitionService.AcquireFromNexusAsync(url)
                │     │
                │     ├─ INexusClient.DownloadLinksAsync  (CDN URL; premium or free-user overload)
                │     ├─ INexusClient.GetModInfoAsync     (mod name + Summary/PictureUrl/
                │     │                                     ContainsAdultContent → ModDisplayMetadata
                │     │                                     via the shared mapper)
                │     ├─ INexusClient.ListModFilesAsync   (file version, matched by fileId)
                │     ├─ download to temp  (IProgress<long>)
                │     └─ IModImportService.Import(temp.<ext>, name, NexusSource{ModId}, version,
                │                               remoteUploadedAt, displayMetadata)
                │           → (containerId, versionId)   (metadata lands on container.json)
                │
                ├─ IProfileService.AddMod(profileId, containerId, LatestPolicy)
                └─ ModListViewModel.Reload() on the UI thread  (mod appears in the list)

                on any failure (not cancellation) → ShowAlertAsync("Download failed: <error>")
```

## `IModAcquisitionService`: the reusable core

Lives in the Integrations library (alongside `INexusClient`, which it
consumes). The interface is Nexus-only: it resolves the download link, fetches
the mod's metadata, downloads the archive, and imports it.

```csharp
public interface IModAcquisitionService
{
    Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<long>? progress = null, CancellationToken ct = default);

    Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<long>? progress = null, CancellationToken ct = default);
}
```

The `IProgress<long>` parameter is the per-row progress hook (the nxm handler
passes `null`; the mod-list update path passes `null` for its indeterminate
affordance). The caller handles profile registration; the service does download
plus Import and returns the `(containerId, versionId)`.

`AcquireLatestNexusAsync` is the per-mod Update button's entry point: it knows
the mod id (not the file id) and lets the service pick the current release. It
lists the mod's files via `ListModFilesAsync`, filters to non-archived MAIN
files (Nexus `category_id` 1, universal across games), picks the newest by
`uploaded_timestamp`, then forwards to `AcquireFromNexusAsync` with `null` nxm
key/expires (the premium / auth-only download path). `InvalidOperationException`
surfaces when no MAIN file is available (the caller shows a user-facing alert).
`ModFile` carries an `archived` bool for the filter.

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
   stored under its numeric id as a name is worse than a clean failure
   message) and nothing partial lands. The publish timestamp is converted to a
   `DateTimeOffset?` (null when the wire value is `0` / absent) and forwarded
   as the imported version's `RemoteUploadedAt`, the basis for the
   update-check publish-date comparison.
3. **Download** from the CDN URI to a temp file
   (`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() +
   Path.GetExtension(fileName))`, where `fileName` is the matched
   `ModFile.FileName`) using a plain `HttpClient` from `IHttpClientFactory`
   plus the 81920-byte buffered copy and `IProgress<long>` pattern. The real
   file extension is preserved on
   the temp file for log clarity; archive detection is content-based
   (SharpCompress magic bytes), so the extension is cosmetic. The temp file
   is deleted once Import returns, always, success or failure (no partial
   state).
4. **Import** via `IModImportService.Import(tempPath, modName, new NexusSource
   { ModId = modId }, version, remoteUploadedAt, displayMetadata)`. The import service validates
   the archive structure (single base folder plus matching `<base>.mod`
   descriptor; archive detection is content-based via SharpCompress), handles
   find-or-create-container (dedup by `NexusSource.ModId`) plus add-version
   plus the `IsLatest` flip, records the publish date on the new entry as
   `RemoteUploadedAt`, replaces the container's `DisplayMetadata` in the same
   manifest update as the version mutation, and extracts into
   `<ModsFolder>/<containerUUID>/<versionFolder>/<baseFolder>/`.
5. **Return** `(containerId, versionId)`.

A `null` `displayMetadata` argument (the default, including a manual
folder/archive re-import through the picker or drag-and-drop) preserves any
prior `DisplayMetadata`, so a manual re-import never erases a prior Nexus
acquisition or backfill. The common acquisition paths (nxm download, manual
per-mod Premium update, automatic Premium install, DMF download) all inherit
the capture: each forwards the normalized metadata through `Import`, which
forwards it to `AddVersion`, which replaces it in the manifest.

The CDN download uses a plain `HttpClient` (not the typed `INexusClient`)
because the CDN URL is an absolute path with the per-file token in the query
string (free users) or just the session auth (premium); no base address or
Nexus-specific headers are needed.

## `NxmModDownloadHandler`

The real `INxmModDownloadHandler` that supersedes the library's no-op default.
The handler's pre-flight checks and flow:

1. **Auth check** (live config read): `NexusConfig.AuthMethod != None`
   (required for every download; the `nxm://` key/expires is the per-file
   token for the free-user endpoint, **not** a substitute for auth). On `None`,
   `ShowAlertAsync("Nexus not configured", ...)` and return.
2. **Active-profile check**: `IProfileSession.ActiveProfileId != null`. On
   null, `ShowAlertAsync("No active profile", ...)` and return.
3. **Acquire, register, and refresh**: call
   `AcquireFromNexusAsync(url.Game, url.ModId, url.FileId, url.Key, url.Expires,
   ct: ct)`, then `IProfileService.AddMod(profileId, containerId,
   ModVersionPolicy.Latest)`, then `ModListViewModel.Reload()` on the UI thread
   (via the handler's `refreshModList` callback) so the new mod appears
   immediately without a profile switch.
4. **On failure** (not cancellation): `ShowAlertAsync("Download failed",
   ex.Message)`. Cancellation propagates as `OperationCanceledException`.

**Policy on `AddMod`:** `LatestPolicy` (new mods auto-track the newest
downloaded version). The user can switch to `PinnedPolicy` later via the
mod-list UI's pin dropdown. This matches the behavior for locally-imported
mods (the local import path also uses `LatestPolicy` for new mods).

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
`AddSingleton`, and MS DI resolves the last registration).

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
`warhammer40kdarktide` (case-insensitive) before the auth, active-profile, and
acquisition gates, surfacing a localized alert that names the link's game. No
auth read, acquisition call, or profile registration happens for a non-Darktide
link.

## See also

- [integrations reference](../reference/integrations.md):
  `IModAcquisitionService` public surface, the acquisition flow, the
  `NxmModDownloadHandler`, DI registration, testing.
- [nxm:// scheme handler](nxm-scheme-handler.md): the plumbing that
  delivers the URL to the handler implemented here.
- [Nexus authentication](nexus-authentication.md): the auth factory the v1
  client uses for the download-link and metadata calls.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level tie-together.
