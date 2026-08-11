# Nexus API rate limiting

Modificus Curator talks to the Nexus Mods v1 REST API under a per-user rate
budget. This doc explains how Nexus's quota works, how Curator observes and
reacts to it, how it proactively limits its own calls, what it deliberately
does not do, and the call patterns that consume the budget.

## The quota model

Nexus enforces a per-user (per API key / per OAuth token) rate budget on its v1
REST API at `api.nexusmods.com`. Every authenticated request consumes one unit
from two rolling windows:

- an **hourly** window, and
- a **daily** window.

The remaining counts and reset times for both windows come back on every
response in the `x-rl-*` headers (`x-rl-daily-limit`, `x-rl-daily-remaining`,
`x-rl-daily-reset`, and the matching `x-rl-hourly-*` trio). Nexus publishes a
daily limit of 20,000 requests, with requests throttled to 500 per hour once
the daily limit is reached (per Nexus's
[rate-limit help article](https://help.nexusmods.com/article/105-i-have-reached-a-daily-or-hourly-limit-api-requests-have-been-consumed-rate-limit-exceeded-what-does-this-mean)).
The article does not document a separate Premium tier, and the live per-window
limits may differ by account and over time, so Curator does not hardcode these
numbers. It reads them from the headers.

**The budget is the user's, not Curator's.** The same daily and hourly quota is
shared across everything the user has hitting the Nexus API on their key:
Vortex, MO2, the Nexus Mod App, browser sessions, and Curator. The API does not
break the budget down per client, so Curator cannot know how much of the reported
"remaining" is theoretically its own. A rate-limit hit reported to Curator may
reflect consumption by another tool the user is running, not by Curator.

## How Curator observes

Every Nexus API call goes through `NexusClient.SendAsync`, which:

1. **Parses** the `x-rl-*` headers into a `NexusRateLimits` record
   (`NexusRateLimitsParser.Parse`): six fields, daily limit/remaining/reset and
   hourly limit/remaining/reset. Missing or unparseable headers yield zeros and
   nulls (`NexusRateLimits.Unknown`).
2. **Carries** the parsed limits on the returned `Response<T>.RateLimits`, so
   callers can inspect them.
3. **Logs** the remaining counts at Information level on every successful call
   (`Nexus API call to {uri} ok; remaining: daily=X, hourly=Y`), so the rate
   window draining is visible in the log.

There is no persistent record of the limits across calls. Each response's limits
are used by the immediate caller, or discarded once the response is consumed.

## How Curator reacts

Two reactive paths. Both run after the call has already been made and consumed a
unit.

### The hard wall

`NexusClient.EnsureSuccessAsync` runs on every non-success response. The
rate-limit signal is: HTTP **429** always, or HTTP **403** when the limit
headers are present (`x-rl-*-limit > 0`) and at least one remaining counter is
zero. A 403 with no rate-limit headers, or with a non-zero remaining, is treated
as a permissions error, not rate-limiting (the two-condition rule). On a
rate-limit signal, `NexusClient` throws
`NexusRateLimitException` carrying the parsed `NexusRateLimits`, so a caller
could in principle advise when to retry. The exception propagates to the caller,
which surfaces it as an error.

### The update-check post-call flag

`UpdateCheckService.CheckAsync` (the update check that fires on profile load)
makes one `CheckUpdatesGraphQlAsync` call (the v2 GraphQL `modsByUid` batch
query), then inspects `response.RateLimits`: if a limit was reported and its
remaining is zero (`(DailyLimit > 0 && DailyRemaining <= 0) || (HourlyLimit > 0
&& HourlyRemaining <= 0)`), it returns an `UpdateCheckResult` with
`RateLimited = true`. The `> 0`-on-the-limit guard prevents a false positive when
the headers were absent (`NexusRateLimits.Unknown`, all zeros). A
`NexusRateLimitException` thrown by the client (HTTP 429 / exhausted headers) is
also caught + surfaced as `RateLimited = true`. The UI consumes this flag to
show "check incomplete."

### The metadata-backfill stop

`NexusModMetadataService.BackfillMissingAsync` consumes the same per-response
limits and stops the pass the same way: a thrown `NexusRateLimitException`
stops immediately, and a successful response whose reported daily or hourly
remaining is zero (the same `> 0`-on-the-limit guard) stops after persisting
the metadata from the triggering response. A `NexusNotAuthenticatedException`
also stops the pass (auth was revoked mid-pass); a per-mod `NexusApiException`
is logged and the pass continues. The earliest server-reported reset of an
exhausted window is computed through the shared `NexusRateLimitReset` helper
(the update-check service uses the same helper), so the two cannot drift on
what "the reset is."

Both paths react only after the call has consumed a unit or hit the wall.
Curator complements these reactive paths with proactive call limiting.

## How Curator proactively limits its calls

Alongside the reactive paths, Curator caps its own API call rate before the
wall through four mechanisms. The thresholds and named constants live in
[the rate-limiting strategy reference](../reference/rate-limiting-strategy.md);
the mechanisms:

- **Manual sliding-window throttle.** The manual "check now" refresh carries its
  own throttle that persists across restarts: a rolling free budget per hour,
  then a per-refresh cooldown once spent. A blocked attempt makes no API call.
  Owned by `UpdateCheckRunner`.
- **Auto-check interval floor.** The user-configurable periodic-check interval
  has a named minimum (5 minutes), enforced on save and at tick time. Owned by
  `NexusConfig`.
- **Persisted last-check interval gate.** Every automatic trigger (startup,
  profile switch, periodic timer) shares one interval check against a
  last-check timestamp persisted across restarts, so a rapid open/close loop
  does not fire a call per launch. Owned by `IAppStateStore` and
  `UpdateCheckRunner`.
- **Metadata-backfill 24-hour gate + attempt cap.** The stable-v1 display-
  metadata backfill runs at most one real pass per persisted 24-hour window
  and caps each pass at 25 attempted containers, so it cannot burst the budget
  even on a large repository. The gate timestamp is stamped only after at
  least one `GetModInfoAsync` request is attempted, and is persisted across
  restarts. Owned by `INexusModMetadataService`.

## What Curator does not do

Stated plainly, because the gaps matter as much as the handling:

- **No low-remaining reaction.** Curator reacts at zero (the update-check flag)
  and at the hard wall (the exception). "Low but not zero" gets no throttle, no
  skip, no warning.
- **No cross-call budget tracking.** Remaining is observed per-response and
  discarded once the caller consumes it. There is no running "what is our
  remaining right now" state across operations, so nothing can reason about the
  budget between calls.
- **No shared-quota awareness.** Curator cannot tell how much of the reported
  remaining is theoretically its own (the API does not break it down per
  client), and it does not surface the shared-budget framing to the user. A
  rate-limit hit reads to the user as "Curator failed," not "the user's overall
  Nexus budget is exhausted across tools."
- **No retry/backoff on the hard wall.** A `NexusRateLimitException` propagates
  as a terminal error for the operation; Curator does not wait for the reset
  window and retry.

Net: Curator observes the rate window on every call, proactively caps its own
call rate, and reacts to the wall, but it does not steer by the reported
remaining, surface the shared-budget framing to the user, or recover past the
hard wall with a retry/backoff.

## What consumes the budget

Only authenticated calls to `api.nexusmods.com` count. Per operation:

- **Update check:** 1 `CheckUpdatesGraphQlAsync` call (the v2 GraphQL
  `modsByUid` batch query) per check, plus up to `F` `ListModFilesAsync` calls on
  a cold cache, where `F` is the count of tier-2-only-flagged mods (the
  latest-file-confirmation tier). The batch query covers all checkable mods in
  one call, so the base cost is constant regardless of profile size; the tier-3
  calls are cached per (mod id, page version, updated-at) so a repeat check for
  unchanged mods is back to one call. The batch also returns the current Nexus
  mod `name` for every id sent; the check renames each container whose stored
  name has drifted at zero extra API cost (the name rides along on the one
  batch query). See
  [the update-detection tiers](../reference/rate-limiting-strategy.md#update-detection-tiers).
- **Mod acquisition (download):** about 3 calls per download
  (`DownloadLinksAsync` + `GetModInfoAsync` + `ListModFilesAsync`). This is
  parity with Vortex, which Nexus's help article cites at 3 calls per download.
  This covers the manual per-row Premium update, the automatic Premium
  installer (one download per flagged mod, when opted in), and the `nxm://`
  handler's downloads. The automatic installer chains after an authoritative
  check that found updates, so its downloads ride on a check that already ran
  (no separate check call per install).
- **Premium verification (automatic installer):** the automatic-update service
  makes one `GetCurrentStateAsync` call per batch, ONLY when an authoritative
  check found updates AND `AutomaticUpdatesEnabled` is on. An empty result or a
  disabled setting costs no extra call. The DMF prompt makes a similar
  per-prompt verify call on its download branch.
- **API-key validate:** 1 call (`ValidateAsync`), only when the user validates
  an API key in the Nexus destination. (The OAuth path resolves the display
  name + Premium state from the access token's JWT payload, with no API call.)
- **Stable-v1 metadata backfill:** one `GetModInfoAsync` call per attempted
  missing Nexus container, fired only when the Mods list is in Detailed mode.
  The active profile's containers are prioritized first, then the remaining
  repository containers in deterministic Guid order. The pass stops at the
  first hard rate-limit signal (a thrown `NexusRateLimitException` or a
  successful response whose reported remaining is zero), and stops early when
  the candidate set is exhausted. It makes no calls when there is no Nexus
  auth, the persisted 24-hour gate is still open, or no candidate is missing
  metadata. A real pass stamps the gate even if it stops early, is cancelled,
  or fails partway, so a partial pass still counts as "this day's attempt."
  The metadata is persisted through the repository's atomic
  `TryInitializeDisplayMetadata`, so a container is only ever counted as
  Updated when the check-and-set won. Linked and untracked containers are not
  candidates and never cost a call.
- **The archive CDN download** (the actual file bytes): served from a CDN URL
  returned by `DownloadLinksAsync`, on a separate CDN host. Not an API call, not
  counted.

The update check and the Detailed-mode metadata backfill are the automatic
Nexus calls. The update check fires on startup, on profile switch, and on a
periodic timer (default 10 minutes, floor 5), each interval-gated via a shared
last-check timestamp persisted across restarts; the manual "check now" button
is throttle-gated. The metadata backfill is fired by the density coordinator
when the Mods list is in Detailed mode, gated to one real pass per persisted
24-hour window and capped at 25 attempted containers per pass. See
[the rate-limiting strategy](../reference/rate-limiting-strategy.md) for the
thresholds. A typical session is a handful of these check calls plus a few
calls per download.

## See also

- [Nexus API rate-limiting strategy](../reference/rate-limiting-strategy.md):
  the proactive call-limiting mechanisms (manual throttle, interval floor,
  persisted interval gate) and the budget math.
- [integrations reference](../reference/integrations.md): the
  `INexusClient` surface, the `Response<T>` and `NexusRateLimits` types, and the
  typed `NexusRateLimitException`.
- [Nexus authentication](nexus-authentication.md): the API-key and OAuth auth
  paths the rate-limited calls ride on.
- [Mod acquisition](mod-acquisition.md): the download flow whose ~3 calls per
  acquisition are the main user-initiated budget consumer.
