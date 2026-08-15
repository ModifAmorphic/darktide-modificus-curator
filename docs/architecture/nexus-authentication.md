# Nexus authentication: architecture

Nexus Mods auth has two user-facing paths, both surfaced in the Nexus
Integrations destination: **OAuth** (the primary, a loopback OIDC flow via
`Duende.IdentityModel.OidcClient`) and **API key** (the alternative, validated
against `GET /v1/users/validate.json`). The user's explicit choice is the
single source of truth for which method is active; there is no fallback. This
is also where the v1 Nexus API client is wired up; acquisition and
update-checks both call through it.

> Public surface, exact signatures, and DI registration are documented in the
> [integrations reference](../reference/integrations.md). This
> doc covers the architecture and the why.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Nexus destination (Nexus-only)                     │
│  [Log in with Nexus] (OAuth)   [API key: ____] [Validate]        │
│  Status: Not signed in / Signed in as <name> (<Premium|Regular>) │
│  AuthMethod tracks the user's EXPLICIT choice (no fallback)       │
└──────────────┬──────────────────────────────────────────────────┘
               │ user initiates OAuth OR pastes API key (their explicit choice)
               ▼
┌─────────────────────────────────────────────────────────────────┐
│  NexusAuthService (the auth orchestrator)                        │
│  - LoginWithOAuthAsync(): OidcClient PKCE → loopback → tokens    │
│  - LoginWithApiKeyAsync(key): speculative write + validate;       │
│    revert on failure                                             │
│  - sets AuthMethod + persists tokens/key; clears the OTHER        │
│    method's credentials on switch; exposes current user           │
└──────────────┬──────────────────────────────────────────────────┘
               │ AuthMethod selects the auth message factory (no probing/fallback)
               ▼
┌──────────────────────────────┐   ┌──────────────────────────────────┐
│  OAuth2MessageFactory        │   │  ApiKeyMessageFactory             │
│  Authorization: Bearer <tok> │   │  apikey: <key>                    │
│  401-reactive refresh;       │   │  no refresh (static)              │
│  selected when               │   │  selected when                    │
│  AuthMethod == OAuth         │   │  AuthMethod == ApiKey             │
└──────────────┬───────────────┘   └──────────────┬───────────────────┘
               └──────────────┬───────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  NexusClient (typed HttpClient)                                  │
│  applies app-identification headers (Application-Name,           │
│  User-Agent, Protocol-Version) + the selected auth factory,      │
│  calls v1 endpoints, parses rate limits, retries once on 401     │
│  after a successful refresh.                                     │
└─────────────────────────────────────────────────────────────────┘
```

## OAuth loopback flow (RFC 8252)

`NexusOAuthTokenStore` owns an `OidcClient` configured with:

- **`Authority = "https://users.nexusmods.com"`**, the OIDC **issuer root**
  (not the `/oauth` path). OidcClient resolves discovery at
  `<Authority>/.well-known/openid-configuration` and reads the authorize,
  token, and jwks endpoints out of the doc; the issuer root is the path Nexus
  serves discovery at. Pointing Authority at `/oauth` 404s the discovery fetch.
  `NormalizeOAuthBaseUrl` strips a user-supplied trailing `/oauth` so a
  misconfigured `OAuthBaseUrl` still resolves to the issuer root.
- **`ClientId = NexusOAuthConstants.ClientId`** (the `"modificus_curator"` const;
  see [OAuth client credentials](#oauth-client-credentials) below).
- **No client secret is sent.** Nexus accepts this client as a public client, so
  Curator sets no `ClientSecret`. The `client_id` is posted in the token request
  body via **`TokenClientCredentialStyle = PostBody`** (OidcClient's default with
  no `ClientSecret` set), and PKCE S256 protects the authorize leg. Nexus's
  discovery doc still advertises only `client_secret_basic` /
  `client_secret_post` under `token_endpoint_auth_methods_supported` (no `none`
  method), but the token endpoint accepts the no-secret request for this client.
- **`RedirectUri = "http://127.0.0.1:<port>/callback"`** (the ephemeral port
  is assigned by the loopback listener, exposed as `LoopbackBrowser.RedirectUri`).
- **`Scope = "openid"`**. `openid` is the OIDC scope OidcClient needs to issue
  the id_token. Nexus's discovery doc declares
  `scopes_supported: ["public", "openid"]`; the `profile`/`email` scopes
  themselves are NOT supported and were rejected by the authorize endpoint.
  The user's display name + membership roles are embedded in the access token's
  JWT payload (under a `user` claim), so Curator reads them from the token
  rather than from `/oauth/userinfo` (which returns only `sub` for this
  client); no additional scopes are needed.
- **PKCE with S256 is automatic in OidcClient 7.x** (there is no
  `Policy.RequirePKCE` flag; the older API the original spec once cited does
  not exist).

`LoopbackBrowser` (the production `IBrowser` impl) pre-grabs an ephemeral
loopback port, binds an `HttpListener` on that port, opens the authorize URL
(built by OidcClient from the PKCE challenge and state) in the user's default
browser via the shared `IExternalLauncher` (the OS shell-open; a launch that
could not start surfaces `BrowserResultType.UnknownError`), awaits the
callback, and returns the authorization response. OidcClient exchanges the
code for tokens; the store persists them. Three-minute flow timeout; on expiry
the service surfaces "Login timed out".

Loopback redirect (not `nxm://`) is the RFC 8252 standard and needs no
scheme-handler involvement. This is independent of the
[nxm:// scheme handler](nxm-scheme-handler.md).

## API key: the user-facing alternative

`NexusAuthService.LoginWithApiKeyAsync` does a **speculative write**
(`AuthMethod = ApiKey` plus the key) so the v1 client's auth factory picks it
up, calls `INexusClient.ValidateAsync` (`GET /v1/users/validate.json`), and
**reverts on failure** so the user keeps their prior session. On success the
display name and premium state come from the validate response.

API key is an explicit alternative, not a fallback. The user chooses one
method in the Nexus destination; switching methods clears the other
method's credentials (clean transition, no stale leftovers). Sign-out resets
to `None`.

The API-key block in the Nexus destination is gated behind the
`ApiKeyAuthEnabled` `config.json` developer toggle (default off). There is no
UI control for it: OAuth is the default and sole sign-in path unless a
developer opts in by setting the flag, in which case the API-key block is
shown on the next visit to the destination (read live from config).

## Auth method selection (no probing, no fallback)

`NexusConfig.AuthMethod` (`None` / `OAuth` / `ApiKey`) is the user's explicit
choice. The `NexusAuthMessageFactorySelector` reads `AuthMethod` live per
request and picks the matching inner factory:

- **`OAuth2MessageFactory`** adds `Authorization: Bearer <access_token>` and
  does 401-reactive refresh. Selected when `AuthMethod == OAuth`.
- **`ApiKeyMessageFactory`** adds `apikey: <key>`. No refresh (static). A 401
  here surfaces a clear "API key invalid/expired" error. Selected when
  `AuthMethod == ApiKey`.
- **`NoneMessageFactory`** adds no auth and reports `IsAuthenticated = false`.
  The v1 client throws `NexusNotAuthenticatedException` before sending the
  request. Selected when `AuthMethod == None`.

**No fallback.** If the selected method's credentials are missing or expired,
the factory surfaces an auth error for **that** method (it does not silently
use the other). The user's explicit choice in the Nexus destination is the
single source of truth for which method is active.

## Token persistence and 401-reactive refresh

Tokens persist in `CuratorConfig.Integrations.Nexus.OAuth` (a
`NexusOAuthTokens` record: access, refresh, scope, expiry), written via
`IConfigLoader.Save`. Config is read live, so a credential change takes effect
on the next request.

**401-reactive refresh** (matches MO2; not proactive). When a request returns
401, the client asks the auth factory to refresh:

- **OAuth factory** calls `INexusTokenStore.RefreshAsync` (OidcClient's refresh
  API using the persisted refresh token, plus the new tokens persisted). Refresh
  is serialized through a semaphore so concurrent 401s coalesce into one
  refresh. On a successful refresh the client retries the failed request once
  with the new access token. A second 401 surfaces as `NexusApiException` (no
  infinite retry loop). If the refresh token is revoked or expired, the factory
  surfaces a re-login prompt (no fallback to API key).
- **API-key factory** has no refresh; a 401 surfaces "API key invalid/expired"
  (no OAuth fallback).

## Nexus destination

A **hosted Nexus destination** in the shell's navigation rail
(one of the five destinations), not a section crammed into Settings.

**Nexus-only; no navigation structure.** The destination houses just the Nexus
section. If a future integration ever warrants a UI, add tab or sidebar
navigation then; do not pre-build it.

The Nexus-section layout (operator-approved):

- A status line: "Not signed in" or "Signed in as `<name>`
  (`<Premium|Regular>`)" (the active-method indicator).
- Primary action: a **"Log in with Nexus"** button (OAuth). Clicking it starts
  the OAuth flow (browser opens, consent, loopback callback, tokens persisted,
  `AuthMethod = OAuth`). On success, the status line updates. On failure or
  timeout, an inline error.
- Secondary action: an **"API key"** field (masked) plus "Validate" button
  (the user-facing alternative). Help link to
  `https://www.nexusmods.com/settings/api-keys` (where the user gets their
  key). Validate calls `/v1/users/validate.json`; on success sets
  `AuthMethod = ApiKey` and updates the status line.
- A "Sign out" button when authenticated (clears the persisted credentials and
  sets `AuthMethod = None`).
- Usable while the game runs (only launch and active-profile changes are
  blocked while Darktide runs; auth is not).
- **Switching methods clears the other method's credentials.** One active
  method at a time, no leftovers.

Conventions: drawn `<Path>` icons (no Unicode glyphs), no em-dashes in strings,
localization through the existing `LocalizationService`.

## OAuth client credentials

`NexusOAuthConstants.ClientId` is the build-time const `"modificus_curator"`,
the SSO slug Nexus issued when Curator was registered for public use. The client
id is public by spec and ships as a const in the binary (the same native-client
model every desktop OAuth app uses).

No client secret is used. Nexus accepts this client as a public client, so
Curator sends no `client_secret`: the `client_id` is posted in the token request
body (`TokenClientCredentialStyle = PostBody`), and PKCE S256 protects the
authorize leg. (Nexus's discovery doc still lists only secret-based auth methods
under `token_endpoint_auth_methods_supported`, but the token endpoint accepts
the no-secret request for this client.)

## App-identification headers and rate limits

Every Nexus request carries the app-identification headers (the MO2 and NMA
convention): `Application-Name: Modificus-Curator`, `Application-Version:
<asm>`, `Protocol-Version: 1.0.0`, `User-Agent: Modificus-Curator/<ver>`.

**Rate limits** are parsed from the `x-rl-*` response headers
(`x-rl-daily-limit` / `x-rl-daily-remaining` / `x-rl-daily-reset` and the
hourly equivalents) into a `NexusRateLimits` carried on every `Response<T>`.
Missing or unparseable headers yield `0` or `null` (never throws). The
update-check service consumes them to back off; the client itself just parses
and logs them. A 429 (or a 403 with `*-remaining: 0`) throws
`NexusRateLimitException`.

## v1 endpoints

Grounded against NMA's `NexusApiClient.cs` and node-nexus-api, mirroring that
shape. v3 is Experimental for the surfaces we need, so v1 + v2 only:

v1 REST:
- `GET /v1/users/validate.json` (API-key validate)
- `GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json`
  (premium download links); same endpoint with
  `?key={nxmKey}&expires={epoch}` for free users
- `GET /v1/games/{domain}/mods/{modId}.json` (mod info)
- `GET /v1/games/{domain}/mods/{modId}/files.json` (mod files; unwrapped from
  `{"files":[...]}`)

v2 GraphQL:
- `POST /v2/graphql` with the `modsByUid` batch query (the update check; 1 call
  for all mods, returns the server-computed `viewerUpdateAvailable` field)

## See also

- [integrations reference](../reference/integrations.md):
  public surface, exact signatures, DI registration, testing.
- [mod acquisition](mod-acquisition.md): the acquisition flow that calls the
  v1 client through the selected auth factory.
- [nxm:// scheme handler](nxm-scheme-handler.md): OAuth uses loopback, not the
  `nxm://` handler; the OAuth-callback URL kind is parsed and dropped.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level tie-together.
