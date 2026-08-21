# Reference

Reference material, organized by category. Updated as we learn more.

## Library reference

Per-library API reference for the Modificus Curator backend libraries + the UI
(public interfaces, key types, DI registration, cross-platform notes).

- [config](config.md): the global configuration schema, a POCO model
  (`CuratorConfig`) with platform-appropriate defaults bound from JSON.
- [general](general.md): cross-cutting infrastructure, structured logging
  (Serilog), JSON config loading, runtime app-state persistence, and the DI
  registration that wires all three.
- [profiles](profiles.md): the profile data model, on-disk persistence, and
  per-profile mod-list management projected into a staged mod root + `mods.lst`.
- [mods](mods.md): the unified mod repository, UUID containers per
  `(source, identity)`, the version-policy + mod-source provenance models, and
  the local-import service.
- [steam](steam.md): Steam / Darktide / Proton discovery and game-running
  detection.
- [integrations](integrations.md): the Nexus Mods v1 client + OAuth/API-key
  auth, the download + extract + place acquisition service, and the Nexus-only
  update-check service.
- [relay-client](relay-client.md): the v1 launch facade over Mod Relay,
  resolving the profile + Steam discovery, assembling the launcher args, and
  invoking the relay.
- [nxm](nxm.md): the `nxm://` scheme-handler plumbing, URL parsing, IPC framing,
  the single-instance guard + IPC server, the router + mod-download seam, the
  OS scheme-handler registration, and the relay helper.
- [ui](ui.md): the Avalonia 12 front end, the shell, profile management, the
  mod list + its download rows, every dialog, global preferences + i18n, the
  serial download queue, the DMF prompt coordinator, the update-check runner,
  and the app self-update service.

## Strategy & release

- [nexus-premium.md](nexus-premium.md): the complete Premium versus regular
  Nexus behavior matrix, including one-click updates, the DMF prompt,
  token-bearing `nxm://` downloads, Premium-state lifetime, and external-policy
  uncertainties.
- [rate-limiting-strategy.md](rate-limiting-strategy.md): the proactive Nexus
  API call-limiting mechanisms (manual sliding-window throttle, auto-check
  interval floor, persisted interval gate) and the worst-case budget math.
- [release-strategy.md](release-strategy.md): how Curator releases are
  produced, attested, scanned, and installed (release-please workflow,
  post-release AV/VT scan, PR gate, Linux installer, sandbox rehearsal).

[darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay) --
game-binary reference (LuaJIT, `lua_State` offsets, discovery methodology)
and the existing modding-ecosystem audit, now live with the runtime.
