# Nexus API rate-limiting strategy

Modificus Curator proactively limits its own Nexus API calls via four
mechanisms: a manual sliding-window throttle on the "check now" refresh, an
auto-check interval floor, a persisted last-check interval gate that covers
every automatic trigger, and a persisted 24-hour gate on the display-metadata
backfill that bounds it to one pass per day. These run alongside the reactive
handling that responds to rate-limit signals from the server after a call has
been made; see
[Nexus API rate limiting](../architecture/nexus-rate-limiting.md).

## The manual sliding-window throttle

The manual "check now" refresh (the mod-list header refresh button) carries its
own throttle, independent of every other gate. The first 10 manual refreshes in
a rolling 1-hour window fire freely; once spent, the path throttles to one per
2 minutes until timestamps age out of the window and free mode resumes. A
blocked attempt consumes nothing: no API call, no timestamp stamp. The window
persists across restarts via `app-state.json`
(`IUpdateCheckScheduleState.ManualRefreshTimestamps`), seeded at `UpdateCheckRunner.Start()`
and written back on every successful fire, so closing and reopening the app
does not reset the free-refresh budget.

Owned by `UpdateCheckRunner`. Constants on that class:

- `FreeManualRefreshLimit = 10`
- `ManualRefreshWindow = 1 hour`
- `ManualRefreshThrottleInterval = 2 minutes`

When throttled, the refresh button disables and shows a live `m:ss` countdown
tooltip ("Rate limiting protection enabled. Manual refresh will be available
again in {time}."). The countdown reads `UpdateCheckRunner.NextManualRefreshAllowedAt`,
the single source of truth for the button's enable state and the tooltip.

## The auto-check interval floor

The minimum user-configurable periodic-check interval is 5 minutes. This is a
named policy bound, `NexusConfig.MinAutoUpdateCheckIntervalMinutes = 5`; the
default is 10, the maximum 1440 (`MaxAutoUpdateCheckIntervalMinutes`). The floor
is enforced on save (the Nexus destination) and at tick time (the runner, via
`Math.Max`), so a value below the floor is raised before it can drive an API
call. At the 5-minute floor the periodic check tops out at 288 calls/day.

Owned by `NexusConfig` (the bounds) and applied by the Nexus destination and
`UpdateCheckRunner`.

## The persisted last-check interval gate

Every automatic trigger (startup, active-profile switch, and the periodic timer)
passes through one shared interval check: a check fires only when the configured
interval has elapsed since the last check of any kind. The last-check timestamp
is persisted to `app-state.json` (`IUpdateCheckScheduleState.LastUpdateCheckUtc`) and seeded
at `UpdateCheckRunner.Start()`, so the gate survives a close/reopen: a rapid
open/close loop does not fire a call per launch.

The `AutoUpdateCheckEnabled` toggle gates only the periodic timer; startup and
switch fire regardless of the toggle when the interval has elapsed.

`UpdateCheckRunner.CheckNowAsync` (the manual path) bypasses the interval gate,
since it carries its own sliding window instead, but a successful manual fire
still re-stamps the shared last-check timestamp so the periodic clock backs off
after it.

Owned by `IUpdateCheckScheduleState` (the persisted timestamp) and `UpdateCheckRunner` (the
gate).

## Metadata backfill gate

The display-metadata backfill (the service that fills in the summary, thumbnail
URL, and adult flag for existing Nexus containers imported before the capture
was wired) carries its own proactive limit, separate from the update-check
gates: at most **one real pass per 24-hour window**, persisted to
`app-state.json` (`INexusMetadataBackfillState.LastNexusMetadataBackfillUtc`). The pass runs
only when the Mods destination is in Detailed mode and encounters rows missing
metadata; Compact mode never invokes it (see
[ui: mod list density](ui.md#mod-list-density--detailed-rows)).

- **Persisted 24-hour gate.** A pass stamps the timestamp only after it
  attempted at least one API request; a no-auth, already-gated, or no-candidate
  no-op attempts zero requests and does **not** stamp (so the next real pass is
  not blocked by a no-op). The gate is rechecked after the service's
  serialization semaphore is acquired, so an overlapping call that arrived while
  the first was running returns empty rather than starting a second pass inside
  the window.
- **Repository-wide.** The gate is not profile-scoped: the backfill covers every
  Nexus container in the repository missing metadata, regardless of which
  profile is active. It is unrelated to the update-check interval state
  (`LastUpdateCheckUtc`).
- **Sequential, capped at 25.** One `GetModInfoAsync` call per candidate, at
  most 25 attempted calls per pass. Candidates are ordered: the active
  profile's container ids first (in row order), then the remaining repository
  containers in deterministic `Guid` order; only `NexusSource` containers whose
  `DisplayMetadata` is `null` are candidates.
- **Stop policy.** `NexusApiException` (a per-mod failure, e.g. one removed mod)
  continues to the next candidate; `NexusRateLimitException`, exhausted
  counters, `NexusNotAuthenticatedException`, and generic transport/repository
  failures stop the pass. A stopped pass still stamps the gate (it attempted at
  least one request). See [integrations: metadata backfill service](integrations.md#metadata-backfill-service).
- **Timestamp behavior.** Backward compatible on disk: an old `app-state.json`
  without `LastNexusMetadataBackfillUtc` deserializes it to `null`, so the first
  Detailed-mode encounter after upgrade runs the pass normally (see
  [general](general.md#iappstatestore--appstatestore)).

Owned by `INexusMetadataBackfillState` (the persisted timestamp) and `INexusModMetadataService`
(the gate + the pass).

## Update-detection tiers

The update check flags a mod when any of three tiers fire. The first two come
from the single `modsByUid` batch query (no extra calls); the third is a
best-effort refinement that costs extra calls only on the subset it targets.

- **Tier 1: `viewerUpdateAvailable`.** The server's authoritative "updated since
  the viewer last downloaded" signal. True flags the mod.
- **Tier 2: mod-level version compare.** The installed file version vs the
  mod-page header `version` field. A mismatch flags the mod (catches
  older-version-installed, multi-PC, and manual-import cases the server's
  per-user tracking misses).
- **Tier 3: latest-file-version confirmation.** Scoped to mods flagged solely by
  tier 2 (tier 1 is authoritative and untouched). It resolves the newest
  non-archived MAIN file via `NexusModFiles.LatestMain` (the same filter the
  download path uses) and clears the flag when that file's version equals the
  installed version. This clears the tier-2 false positive where a mod author's
  page-header `version` lags their latest file (the header says 1.9.1 but the
  latest file is 1.9.2, and the user has 1.9.2 installed). A different file
  version (a real update) or an unresolved / failed resolution leaves the flag.
  The resolved version is cached per (mod id, page version, updated-at) with a
  24h TTL backstop, in memory and session-scoped, so a repeat check for an
  unchanged mod makes zero extra calls.

Tier 3 is additive: it only ever removes flags, never adds. Both check shapes
(the periodic `CheckAsync` and the manual `CheckThoroughAsync`) inherit it via
the shared `RunCheckAsync`. See [integrations reference](integrations.md) for
the per-step check flow.

## Mod name sync (free, piggybacks on the batch)

The same `modsByUid` batch query returns the current Nexus mod `name` for every
id sent. The check compares each returned name to the container's stored
`ModContainer.Name` and renames the container when they differ (the Nexus name
wins; identity `Id` is unchanged). This covers EVERY Nexus-sourced mod in the
profile, Latest AND Pinned, and adds zero extra API calls: the name rides along
on the one batch query the check already makes. Pinned mods get name sync but
are never flagged for an update (the flag logic stays Latest-only). When a check
renames at least one container, the result carries `NamesChanged = true` so the
mod-list UI refreshes the affected rows' displayed names in place.

## Budget

Worst case for a determined user: 10 free plus 30 throttled manual refreshes is
40 manual calls per hour, plus roughly 12 automatic calls at the 5-minute floor
is roughly 52 per hour, about 10.4% of the 500/hour Nexus budget.

The tier-3 confirmation is additive to each check: a cold-cache check makes
`1 + F` calls, where `F` is the count of tier-2-only-flagged mods (the batch
query plus one file-listing call per tier-2-only flag). A warm cache drops the
per-mod calls back to zero, so a repeat check for unchanged mods is back to the
single batch call. `F` is bounded by the profile's Nexus + Latest mod count and
is typically small (only the mods whose page-header version differs from the
installed file version). Because the tier-3 cache is in-memory and
session-scoped (not persisted across restarts), the cold cost re-pays on the
first check after each app restart; a user who closes and reopens frequently
re-pays the `1 + F` spike on the first check of each session.

The metadata backfill adds at most **25 calls per 24 hours** when Detailed mode
encounters Nexus containers missing display metadata (see
[Metadata backfill gate](#metadata-backfill-gate)). New Nexus acquisitions
already capture the metadata through the existing acquisition `GetModInfoAsync`
call, so the backfill only targets containers imported before that capture was
wired; over time the candidate set shrinks to zero. The 25 calls are a burst on
the first Detailed pass inside the 24-hour window (a short sequential run), then
nothing for the rest of the window; averaged across the day the contribution is
small (25/24h is roughly 1 call/hour), but on the pass itself the burst is
real. Because the Nexus daily/hourly budget is shared across all of the user's
Nexus tools and Curator cannot know how much of the reported remaining is its
own, the backfill's stop policy treats any exhausted window or rate-limit
response as a hard stop (the metadata from the triggering response is persisted
first). Acquisition captures + the backfill are the two paths that populate
display metadata; they do not compound (a captured container is no longer a
backfill candidate).

The Nexus daily budget is 20,000/day (resets 00:00 GMT) and the hourly budget is
500/hour (resets each hour), per API key or OAuth token. The budget is the
user's, shared across all their Nexus tools (Vortex, MO2, the Nexus Mod App,
browser sessions, and Curator). The API does not break the budget down per
client, so Curator cannot know how much of the reported remaining is its own;
these proactive limits bound only Curator's own contribution.

## See also

- [Nexus API rate limiting](../architecture/nexus-rate-limiting.md): the reactive
  handling (the `x-rl-*` observation, the 429/403 hard wall, the update-check
  `RateLimited` flag).
- [The update check runner](ui.md): the `UpdateCheckRunner` surface.
