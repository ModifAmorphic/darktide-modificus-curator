# Nexus Premium behavior

This reference describes every behavior in Modificus Curator that differs by a
user's Nexus Mods Premium status. It also identifies Nexus features that Curator
supports for regular users, because the distinction between "Nexus-backed" and
"Premium-only" is easy to blur.

The scope is Curator's current behavior. It is not a general list of benefits
included with a Nexus Mods Premium subscription.

## Summary

Curator has three Premium-gated capabilities:

1. **One-click in-app update installation from the mod list.** A Premium user
   clicks a flagged row's update action and Curator downloads and installs the
   update directly. A regular or unknown-Premium user sees the same update
   action button (enabled when an update is available), but clicking it opens
   the mod's Nexus files page rather than installing in-app.
2. **Opt-in automatic update installation.** A Premium user can opt in (in the
   Nexus destination) to have flagged Nexus mod updates installed
   automatically after each update check runs. Regular or unknown-Premium
   accounts see the checkbox but cannot enable it.
3. **Direct DMF download from the install prompt.** When Darktide Mod Framework
   (DMF) is absent from both the active profile and the local repository, a
   Premium user can download it directly under Curator's progress dialog. A
   regular user is directed through the Nexus website and the standard
   `nxm://` download flow, when Curator owns that handler.

Everything else in Curator's Nexus integration is available to regular users,
subject to authentication, profile state, source, version policy, API quota,
and Nexus's own site rules. In particular, authentication, update checks, the
update-action button, Nexus links, and downloads initiated through an `nxm://`
URL are not Premium-gated by Curator.

## Feature matrix

| Capability | Premium account | Regular account | Unknown Premium state |
|---|---|---|---|
| OAuth or API-key authentication | Available | Available | Authentication can remain configured but unverified |
| Nexus account status in Integrations | Shows `Premium` | Shows `Regular` | Shows a method-specific unverified state |
| Automatic and manual mod update checks | Available | Available | Available |
| Update-action button on flagged Nexus Latest rows | Shown (enabled); click installs directly | Shown (enabled); click opens the Nexus files page | Shown (enabled); click opens the Nexus files page |
| Update-action button on non-flagged Nexus Latest rows | Shown, disabled ("Up to date") | Same | Same |
| Opt-in automatic update installation (Integrations) | Checkbox enabled | Checkbox visible but disabled (Premium-required tooltip) | Same as regular |
| Standard `nxm://` download and import | Available, using the URL's per-file token | Same | Same, when auth is configured and the URL is valid |
| DMF prompt, DMF already in repository | Confirm and add existing container | Same | Same |
| DMF prompt, DMF absent | Direct in-app download and add | Browser opens at DMF's Nexus files page (manager-download via `nxm://` or manual import) | Treated like a regular account |
| Local import and ordinary profile management | Available | Available | Available |

"Unknown" means Curator has a configured authentication method but could not
verify the user's membership state. Curator consistently requires an explicit
`IsPremium == true` before selecting a Premium-only user experience.

## How Curator determines Premium status

Premium state originates in `NexusAuthService`, but the source differs by
authentication method.

### API key

`GET /v1/users/validate.json` returns an `is_premium` Boolean. Curator binds it
to `ValidateInfo.IsPremium` and carries it into `NexusAuthResult` and
`NexusAuthState`.

Relevant implementation:

- `src/integrations/NexusTypes.cs`, `ValidateInfo`
- `src/integrations/NexusAuthService.cs`, `LoginWithApiKeyAsync` and
  `GetCurrentStateAsync`

### OAuth

The OAuth access token is a JWT. Its payload carries a `user` claim with the
user's `username` and `membership_roles`. Curator parses the payload
(`NexusAccessTokenClaims.TryParse`) and treats the presence of a `premium` or
`lifetimepremium` role as Premium. The signature is not verified: the token was
obtained over TLS via the authenticated exchange, and Curator makes no
authorization decision on these claims (the bearer itself is validated
server-side by Nexus on each API call). The `/oauth/userinfo` endpoint returns
only `sub` for this client and is not used.

The `Supporter` role does not count as Premium in this calculation. The API-key
path does not reproduce this role calculation; it trusts Nexus's
`is_premium` Boolean. The two authentication methods could therefore disagree
if Nexus classifies an account differently between those responses. No such
disagreement is reconciled in Curator.

Relevant implementation:

- `src/integrations/NexusAccessTokenClaims.cs`, `TryParse` (the JWT payload
  parser) + `NexusMembershipRole` (the typed role model)
- `src/integrations/NexusAuthService.cs`, `LoginWithOAuthAsync` and
  `GetCurrentStateAsync`

### Failure and null semantics

`NexusAuthState.IsPremium` is nullable. When authentication is configured but a
state verification request fails, `GetCurrentStateAsync` returns a state with a
null name and null Premium value instead of normally throwing. Consumers treat
that null value as not Premium.

This fallback is conservative. It can send a Premium user through the regular
website flow after a transient failure, but it avoids sending a regular user to
an auth-only download request that Nexus may reject.

## Premium capability: one-click mod updates

### The stable update-action button

Every Nexus + Latest row reserves a fixed-width update-action cell so later
controls never shift when the button appears or changes state. The button
itself shows for Nexus + Latest rows regardless of account tier and regardless
of whether an update is currently available:

- **No update available:** the button is visible but disabled (neutral/grey
  download arrow); the tooltip reads "Up to date".
- **Update available:** the button is enabled and the drawn download-arrow path
  becomes accent blue. The tooltip distinguishes the click behavior by account
  tier.

Pinned Nexus and Untracked rows do not show the button, but their reserved cell
stays fixed-width.

### Click behavior by account tier

- **Premium click:** the command acquires the global `UpdateCoordinator` (one
  install at a time, shared with the automatic updater), re-downloads the mod's
  latest MAIN release via `IModAcquisitionService.AcquireLatestNexusAsync` (the
  auth-only / premium path), acknowledges the install (clearing the persisted
  known-update entry immediately, with no extra API check), and reloads.
- **Regular or unknown click:** the command opens the mod's Nexus files page in
  the user's browser via a testable external-launcher seam. The user picks a
  file on Nexus and the registered `nxm://` handler acquires it through the
  standard flow. A launch failure surfaces a user-facing fallback alert (with
  the URL for manual copy) rather than being swallowed.

### Eligibility for the Premium in-app install path

The Premium in-app install runs only when all of these conditions hold:

- the account was verified as Premium;
- the row represents a Nexus-sourced mod;
- the row uses `LatestPolicy`, not a pinned version;
- the profile-scoped known-update state flags the row as having an update;
- the row is not already updating;
- the global `UpdateCoordinator` is free (no other install in flight).

`ModListViewModel.Update` repeats the important conditions as command-level
defenses, so a programmatic invocation cannot bypass them.

Relevant implementation:

- `src/ui/ViewModels/ModItemViewModel.cs`, `IsNexusLatest`, `CanShowUpdateAction`,
  `UpdateActionEnabled`, and `UpdateActionTooltip`
- `src/ui/ViewModels/ModListViewModel.cs`, `IsPremiumUser`, `Update`,
  `UpdatePremiumAsync`, and `OpenFilesPage`
- `src/ui/Views/ModListView.axaml`, the per-row update-action cell
- `src/ui/Session/UpdateCoordinator.cs`

### Regular-user discovery

Regular users retain the discovery half of the feature:

- automatic and manual checks still run;
- the source badge still links to the Nexus mod page;
- a flagged row shows the accent-blue, enabled update-action button;
- clicking the button opens the mod's Nexus files tab.

The user can initiate the download on Nexus, after which Curator's registered
`nxm://` handler can acquire and import it through the regular-user download
path. The update-action button is not hidden for regular users; only the click
behavior (files page vs. in-app install) differs.

### Premium-state lifetime

`ModListViewModel` reads Premium state once, asynchronously, when it is
constructed. It pushes the value down to each row so the per-row tooltip and
click behavior reflect it. Consequences:

- Update-action buttons stay disabled (no update) until the initial read
  completes.
- A user who signs in or upgrades during the session must restart Curator
  before the click behavior switches to in-app install.
- A failed initial read leaves the click behavior on the regular path (open
  files page) for that session.

This is an explicit API-call trade-off in the current implementation. It avoids
another membership lookup every time the Nexus destination is
entered.

## Premium capability: opt-in automatic update installation

A Premium user can opt in (in the Integrations "Update checks" section) to have
flagged Nexus mod updates installed automatically after each update check that
runs. The toggle is `NexusConfig.AutomaticUpdatesEnabled` (default false,
independent of `AutoUpdateCheckEnabled`). Runtime execution additionally
requires a fresh verified `IsPremium == true`, so a configured true value is
preserved (the checkbox stays checked + visible but disabled) if Premium later
becomes unavailable, while no automatic install runs.

### Execution

The `IAutomaticUpdateService` is chained directly from `UpdateCheckRunner`
after a check completes (the runner captures the exact result). It runs only
when all of these hold: the result's outcome is authoritative `Success` with
updates, `AutomaticUpdatesEnabled` is on, the active profile still matches, and
a fresh `GetCurrentStateAsync` returns `IsPremium == true` (the Premium request
fires ONLY when the gates pass). Then it installs sequentially, one at a time
under the shared `UpdateCoordinator`. Per-mod revalidation gates each entry
(membership / policy / source / version still match); a profile switch stops the
whole batch; per-mod failures are isolated and aggregated into one summary
alert. A successful install acknowledges/clears its known-update entry
immediately; a fully successful batch is silent. `ModListViewModel` reloads
after the batch via the service's `UpdatesApplied` event.

This is independent of `AutoUpdateCheckEnabled`: periodic checking being off
never disables automatic installation (startup + switch + manual checks still
drive it).

Relevant implementation:

- `src/ui/Session/IAutomaticUpdateService.cs` + `AutomaticUpdateService.cs`
- `src/ui/Session/UpdateCoordinator.cs`
- `src/ui/Session/UpdateCheckRunner.cs` (the chaining)
- `src/config/NexusConfig.cs`, `AutomaticUpdatesEnabled`

## Premium capability: direct DMF prompt download

The DMF prompt has a Premium difference only in its repository-missing case.
DMF (Darktide Mod Framework) is sourced from Nexus Mods (mod 8).

### DMF already exists in the repository

Premium status is irrelevant. After confirmation, Curator adds the existing DMF
container to the active profile without downloading it.

### DMF is absent

After the user confirms the download offer, `DmfPromptService` gets the current
auth state. Unlike the mod-list gate, this is a fresh membership lookup each
time the prompt reaches this branch.

- **Premium:** Curator calls `AcquireLatestNexusAsync` under a modal progress
  dialog and then adds the returned container to the profile.
- **Regular, unknown, or no auth:** Curator opens DMF's Nexus files page in the
  default browser regardless of whether it owns the `nxm://` handler. If Curator
  owns the handler, the user clicks Download on Nexus, Nexus emits an `nxm://`
  URL containing the per-file token, and Curator's normal handler downloads and
  adds DMF. If Curator does not own the handler, the user downloads the archive
  and imports it via the normal add flow (the confirm message already explained
  the manual-import path). If the browser cannot be opened, Curator shows an
  alert containing the files-page URL.

DMF's files page is
`https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files`.

Relevant implementation:

- `src/ui/Session/DmfPromptService.cs`, `ProcessPendingAsync`,
  `OpenDmfFilesPageInBrowser`, and `DownloadAndAddAsync`
- `src/ui/Nxm/NxmModDownloadHandler.cs`

## Download-link mechanics

The acquisition service does not query Premium status. It selects a Nexus
download-link request solely from its arguments:

| Inputs to `AcquireFromNexusAsync` | Client call | Normal caller |
|---|---|---|
| Both `nxmKey` and `nxmExpires` present | `DownloadLinksAsync` with `key` and `expires` query parameters | Standard `nxm://` handler, including regular users |
| Either value absent | Auth-only `DownloadLinksAsync` | `AcquireLatestNexusAsync`, used by Premium-gated UI flows |

Both client overloads call
`GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json`.
The regular-user form appends the per-file `key` and `expires` values generated
by the Nexus website.

This separation matters for maintenance:

- UI call sites, not `ModAcquisitionService`, own Curator's membership gate.
- `AcquireLatestNexusAsync` always reaches the auth-only form because it passes
  null token values.
- Nexus remains the server-side backstop if a future caller reaches the
  auth-only form without a Premium account.
- A partial token pair also selects the auth-only form. It does not attempt a
  malformed regular-user request.

Relevant implementation:

- `src/integrations/INexusClient.cs`, the two `DownloadLinksAsync` overloads
- `src/integrations/NexusClient.cs`, the corresponding request construction
- `src/integrations/IModAcquisitionService.cs`
- `src/integrations/ModAcquisitionService.cs`, `AcquireLatestNexusAsync` and
  `ResolveDownloadUriAsync`

The repository tests verify request construction and caller selection. They do
not call the live Nexus service, so they do not independently prove which
account classes Nexus currently authorizes for the auth-only request.

## Nexus features that are not Premium-only in Curator

### Authentication

OAuth and API-key sign-in are both available to regular and Premium accounts.
Premium status changes the displayed account label and downstream gates, not
whether the user can authenticate.

### Standard `nxm://` acquisition

`NxmModDownloadHandler` checks that the URL is for Darktide, Nexus auth is
configured, and a profile is active. It does not check Premium status. The URL's
`key` and `expires` values select the token-bearing download-link request.

### Update checks and markers

`UpdateCheckService` does not read Premium state. Nexus Latest mods are checked
the same way for regular and Premium users. Pinned mods are intentionally not
flagged, but that policy is unrelated to membership.

### Nexus links and local management

Source links, files-page links, local import, profile membership, ordering,
enablement, pinning, and removal do not depend on Premium status.

## Account-status refresh behavior

Premium state does not have one shared session lifetime. Each surface reads it
independently:

| Surface | Read timing | Mid-session behavior |
|---|---|---|
| Nexus destination (incl. automatic-updates checkbox enable) | On enter and after auth actions | Status can refresh while the app remains open |
| Mod-list update-action click behavior + tooltip | Once when `ModListViewModel` is constructed | Sign-in or upgrade requires restart before the click switches to in-app install |
| DMF prompt download branch | When the confirmed prompt reaches the download decision | Uses the latest state available at that moment |
| Automatic-update service execution | Fresh `GetCurrentStateAsync` after each check that authoritatively reports updates + has auto-update enabled | Re-verified every batch; a lapsed Premium account stops installing |

This means the Nexus destination and DMF prompt can recognize a
newly Premium account while an already-created mod list still hides one-click
Update buttons.

## Rate limits

Premium status does not change Curator's rate-limit algorithms. Nexus publishes
a daily limit of 20,000 requests, with requests throttled to 500 per hour once
the daily limit is reached (per Nexus's
[rate-limit help article](https://help.nexusmods.com/article/105-i-have-reached-a-daily-or-hourly-limit-api-requests-have-been-consumed-rate-limit-exceeded-what-does-this-mean)).
The article does not document a separate Premium tier, and the live per-window
limits may differ by account and over time, so Curator reads them from the
`x-rl-*` headers rather than hardcoding a budget. The same automatic-check
interval gate and manual-refresh throttle apply to every account, and Curator
neither assumes a larger Premium quota nor spends one.

See [Nexus API rate limiting](../architecture/nexus-rate-limiting.md) for the
full observation and reaction model.

## Test coverage

The behavior is covered primarily by these suites:

- `Modificus.Curator.Integrations.Tests/NexusClientTests.cs`: Premium and
  token-bearing download-link request shapes; API-key and OAuth response
  parsing.
- `Modificus.Curator.Integrations.Tests/NexusAuthServiceTests.cs`: Premium state
  propagation through both authentication methods.
- `Modificus.Curator.Integrations.Tests/ModAcquisitionServiceTests.cs`: argument-
  driven selection between auth-only and token-bearing download paths, including
  `AcquireLatestNexusAsync`.
- `Modificus.Curator.UI.Tests/ModListViewModelTests.cs`: one-click update flow,
  one-at-a-time behavior, and the non-Premium command defense.
- `Modificus.Curator.UI.Tests/IntegrationsViewModelTests.cs`: Premium, regular,
  and unverified status text.
- `Modificus.Curator.UI.Tests/DmfPromptServiceTests.cs`: Premium direct download,
  regular and unknown browser flow (opened regardless of nxm handler state),
  and browser-open failure fallback.

Known coverage limits:

- The stable update-action cell's XAML (the fixed-width reservation, the
  dual neutral/accent download-arrow paths, and the enabled/visibility
  bindings) is verified by source inspection, not by a rendered-view test.
- There is no focused test proving that an OAuth `Supporter`-only role remains
  non-Premium.
- Tests use fake HTTP responses and do not verify Nexus's live authorization
  rules or live quota differences.

## Maintenance rules

When adding another Premium-dependent capability:

1. Require an explicit `IsPremium == true`; preserve the safe behavior for an
   unknown state.
2. Document whether membership is read once, cached, or refreshed at the point
   of use.
3. Keep regular-user discovery and manual flows available unless Nexus itself
   forbids them.
4. Gate at the user-facing caller and retain a command or service defense where
   bypass would create a bad request.
5. Do not describe `ModAcquisitionService` as membership-aware. Its current
   routing is based on token presence.
6. Test Premium, regular, and unknown states separately, plus the normal
   `nxm://` fallback where applicable.
7. Attribute live Nexus restrictions to a current Nexus source or mark them as
   an external assumption. Local request-shape tests are not proof of current
   server policy.

## Related documentation

- [Nexus authentication architecture](../architecture/nexus-authentication.md)
- [Mod acquisition architecture](../architecture/mod-acquisition.md)
- [UI architecture](../architecture/ui-architecture.md)
- [Nexus API rate limiting](../architecture/nexus-rate-limiting.md)
- [Integrations API reference](integrations.md)
- [UI reference](ui.md)
- [`nxm://` reference](nxm.md)
