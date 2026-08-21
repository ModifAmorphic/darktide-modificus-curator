# nxm:// scheme handler: architecture

The `nxm://` URL scheme is how the "Mod manager download" button on a Nexus
Mods file page reaches a mod manager. Curator registers a tiny OS-level handler
that captures those clicks and relays them to the running app, where the URL
is parsed, classified, and dispatched to a pluggable handler. The mod-download
handler plugs into a seam exposed by this plumbing (see
[mod acquisition](mod-acquisition.md)).

> Public surface, exact signatures, and DI registration are documented in the
> [nxm reference](../reference/nxm.md). This doc covers the
> architecture and the why.

## Architecture

```
┌─────────────────────────────┐         ┌──────────────────────────────────────┐
│   OS invokes handler exe    │         │   Curator (main app, primary instance)  │
│   with nxm:// URL as arg    │         │                                       │
└──────────────┬──────────────┘         │   CuratorComposition.Build()            │
               │                        │   ┌──────────────────────────────┐    │
               │                        │   │ 1. SingleInstanceGuard        │    │
               │                        │   │    process enumeration;       │    │
               │  named pipe (fixed     │   │    FATAL on collision         │    │
               │  name: Modificus.Curator.Nxm)      │   │ 2. NxmIpcServer.Bind          │    │
               │  one framed URL msg    │   │    pipe; degrades gracefully  │    │
               ├───────────────────────►│   │    on IOException             │    │
               │                        │   │ 3. accept loop reads framed   │    │
               │                        │   │    URLs + routes them         │    │
               │                        │   └──────────────┬───────────────┘    │
       ┌───────┴───────┐                │                  │                    │
       │ if connect    │                │                  ▼                    │
       │ refused:      │                │   ┌──────────────────────────────┐    │
       │ launch Curator  │───────────────►│   │ NxmRouter.Route(url)         │    │
       │ (no args),    │  process start │   │  parse → classify →          │    │
       │ retry pipe    │                │   │  dispatch to DI handler      │    │
       │ ~250ms / 30s, │                │   └──────────────┬───────────────┘    │
       │ then deliver  │                │                  │                    │
       │ + exit        │                │       ┌──────────┴──────────┐         │
       └───────────────┘                │       ▼                     ▼         │
                                        │  INxmModDownloadHandler   OAuth-cb +  │
                                        │  (real impl; no-op       collection  │
                                        │   default in lib)         URLs logged │
                                        │                           + dropped)  │
                                        └──────────────────────────────────────┘
```

The handler is deliberately dumb: it forwards the raw URL string. Curator owns
URL semantics. This keeps the OS-invoked path fast (the handler is native AOT,
starts in tens of ms, carries no DI graph) and puts all routing logic where it
is unit-testable.

## Two-process model: dumb handler exe, smart IPC server

Two projects implement the path:

- **`Modificus.Curator.NxmHandler`** (`src/nxm-handler/`): the OS-registered
  scheme handler. A native-AOT console exe whose `Program.cs` is one line,
  `NxmHandlerRelay.RunAsync(args)`. It does no parsing and carries no DI graph;
  it forwards the raw URL over the fixed named pipe (`Modificus.Curator.Nxm`), or (cold
  start) launches Curator and retries the pipe until it comes up. AOT keeps it
  tiny and fast, and the relay, framing, and parser stay trim-friendly so the
  trimmer drops everything else from the handler's closure.
- **`Modificus.Curator.Nxm`** (`src/nxm/`): the library. URL types
  and parser, length-prefixed IPC framing, the Curator-side IPC server, the
  single-instance guard, the router and handler seams, the OS registrar, and
  the testable relay helper the handler exe calls.

The two reference implementations in the ecosystem (NexusMods.App and
ModOrganizer2) both use a small separate handler exe rather than the main app,
because the OS invokes the handler on every `nxm://` click and the main app's
startup is too heavy for a relay-then-exit, and spawning a second full app
instance to do single-instance detection and IPC is fragile given Curator's
singleton services (`IModRepository` scans and writes manifests; `ConfigLoader`
does atomic config writes). Curator follows the same pattern.

The IPC protocol is one framed UTF-8 message per connection: a 4-byte
little-endian length prefix plus the URL payload, capped at 8 KiB. The handler
opens, sends one message, and closes; the server reads one message, routes it,
and disconnects (reusing the same server instance for the next client, so the
pipe name stays claimed for the app's lifetime).

## Single-instance: process enumeration, not the pipe bind

Curator enforces single-instance **before** binding the IPC pipe. The check lives
in `SingleInstanceGuard` and works by **process enumeration**: it asks
`Process.GetProcessesByName` for live processes sharing the current process's
name (excluding self by PID), and throws `NxmSingleInstanceException` if any
other process remains. The composition root catches the exception and exits
(`Environment.Exit(1)`) before any window shows.

This is deliberately decoupled from the pipe bind. The pipe bind is not a
reliable cross-platform single-instance claim: on Linux the transport is a Unix
domain socket, and two processes can both bind the same path. A probe-as-client
(the alternative the original spec considered) works but adds a startup tax on
Linux because the probe pends when no server exists. Process enumeration
directly answers "is another Curator already running?", is fast, unprivileged
(no elevation), and is decoupled from the IPC transport.

**Accepted v1 race.** Two instances starting within milliseconds could both
enumerate, both see no other, both proceed. For a desktop double-launch
(seconds apart, not microseconds) this is negligible; a cross-process mutex or
lock-file on top is not worth the complexity for v1.

## Pipe bind: separate, non-fatal

With single-instance handled separately, the pipe bind is its own check with
its own graceful outcome. `NxmIpcServer.Bind` runs single-instance first (fatal
on collision), then constructs the `NamedPipeServerStream`. If construction
throws `IOException` (a real pipe problem: leftover socket, permissions; not
another instance, which the first check settled), the server logs a warning and
**continues running degraded**, with `IsBound` false and no accept loop. nxm
click-to-download is unavailable that session; everything else (profiles, mods,
launch) is unaffected. The composition root starts the accept loop only when
`IsBound` is true.

Separating the two concerns means single-instance is fast (no probe timeout)
and the pipe is its own check that degrades on failure.

## Cold-start path

When the handler is invoked and Curator is not running, the handler first runs
an advisory process check (the same enumeration the single-instance guard
uses): a detected Curator process means another handler in a click burst
already launched it, so the launch is skipped; otherwise the handler launches
the sibling Curator exe (no args). Either way it retries the pipe every 250 ms
up to 30 s. Once Curator binds the pipe, the handler connects, delivers the
URL, and exits. Curator has no `--nxm` arg and no cold-start branch; its
startup is unaffected by the nxm plumbing, and the handler owns the entire
cold-start orchestration.

If the sibling Curator exe is missing, the handler logs to stderr and exits
non-zero without retrying: there is nothing to retry against, and a headless
handler never raises a desktop dialog. `UseShellExecute` stays `false` on both
OSes (the handler launches the exe directly); `Process.Start` without
`WaitForExit` already detaches, and `CreateNoWindow=true` on Windows keeps the
secondary launch quiet.

**Concurrent cold-start clicks** (several handlers race while Curator is
closed): the advisory process check means the first handler to detect no
Curator launches it, and the rest see that Curator coming up, skip their own
launches, and go straight to the retry loop. A race that slips past the check
(Curator exits between check and connect, or the check under-detects: on
Linux `GetProcessesByName` compares the exact full executable name recovered
from `/proc` cmdline when available, so it is effective in the normal case and
under-detects only in degraded shapes such as a zombie's empty cmdline, an
unreadable cmdline, or an argv0-rewriting launcher) still self-resolves:
single-instance enforcement means only the first Curator becomes primary,
subsequent Curator instances exit, and each handler's retry loop eventually
connects to whichever Curator won the bind.

## OS scheme-handler registration

A single `INxmHandlerRegistrar` interface with two platform implementations,
selected by runtime OS at DI time (mirroring `IPlatformLaunchStrategy`,
`IProcessLookup`, and `SteamRegistryReader`):

- **Windows** (`WindowsNxmHandlerRegistrar`) writes `HKCU\Software\Classes\nxm`
  (per-user, no elevation) with the handler exe as the `shell\open\command`.
- **Linux** (`LinuxNxmHandlerRegistrar`) writes
  `~/.local/share/applications/modificus-curator-nxm-handler.desktop` (the source of truth
  most desktops honor) with `Exec="<handler-exe>" %u` and
  `MimeType=x-scheme-handler/nxm;`, plus a best-effort `xdg-mime default`
  invocation. The `.desktop` file is still the registration if `xdg-mime` is
  absent; the failure is logged, not thrown. In the standalone layout, Exec
  points directly to the sibling handler. In an AppImage run, the registrar
  atomically copies the handler to
  `${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/nxm-handler/`, creates
  a sibling `Modificus.Curator` symlink to `$APPIMAGE`, and points Exec to the
  persistent copy. The handler's existing sibling lookup then cold-starts the
  AppImage without recording a temporary mount path.

**Explicit registration, not startup auto-registration.** Registration is a
user action from the Nexus destination (a "Nexus download links" section with
a status line + a toggle button), not something the app does on startup. The
register path shows a confirmation dialog first: it is a system-wide change
that can take `nxm://` clicks away from Vortex, Mod Organizer 2, Nexus Mod
Manager, or other mod managers, so the user must opt in knowingly. The
unregister path releases only Curator's own registration, and the ownership
safety lives inside the registrar, not the caller: on Windows it is a logged
no-op when Curator is not the current handler; on Linux it removes only
Curator's own desktop file and never touches another manager's registration.
Callers never pre-check. The composition root never registers, but after
single-instance ownership succeeds it performs best-effort maintenance of an
existing Curator-owned AppImage registration. Maintenance refreshes the copied
handler and symlink only when the desktop file exists and
`xdg-mime query default` still reports Curator. It never calls
`xdg-mime default` or takes ownership from another manager.
`INxmHandlerRegistrar` is resolved by the Integrations view model (the
register/release mutations) and by the shared UI registration state
(`INxmRegistrationState`, the sole probe surface: one startup seed, one probe
on entering the Nexus destination, and one publish after each register/release
action). The main-window status strip and the Mods empty-state hint consume
that shared state and update via its `Changed` event (the strip surfaces
"Nexus links: enabled" / "Nexus links: disabled" / "Nexus links:
unavailable"). No polling: consumers read last-known state and accept that the
OS registration rarely changes out-of-band.

The packaged handler-exe path is derived from `AppContext.BaseDirectory` plus
the fixed handler assembly name. The handler ships as a sibling of the main
Curator exe; the AppImage registration copies it to the durable integration
directory described above.

## URL parsing and routing

`NxmUrlParser` is pure (no I/O) and never throws: a static `TryParse` that
returns `false` on malformed input. Three URL kinds, grounded against MO2's
`nxmurl.cpp` and NMA's `OAuth.cs`:

- **`NxmModDownloadUrl`**: `nxm://<game>/mods/<modId>/files/<fileId>` with
  optional `key`, `expires`, `user_id` query parameters. The "Mod manager
  download" button on a Nexus Mods file page produces one.
- **`NxmOAuthCallbackUrl`**: `nxm://oauth/callback?code=<code>&state=<state>`.
  Kept as a parsed type so the router can recognize the shape, then logged and
  dropped. Curator OAuth uses loopback redirect (RFC 8252), not `nxm://`, so in
  normal operation no such URL is delivered over IPC (see
  [Nexus authentication](nexus-authentication.md)).
- **`NxmCollectionUrl`**: `nxm://<game>/collections/<id>/revisions/<rev>`.
  Parsed so the router can log "unsupported in v1" rather than "unknown URL".
  No handler is invoked.

The default `NxmRouter` parses the raw URL, dispatches mod-download URLs to
`INxmModDownloadHandler`, logs OAuth-callback and collection URLs and drops
them, and logs unparseable URLs as a warning. Handler exceptions are caught at
the router boundary so one bad handler invocation cannot kill the IPC accept
loop.

### The mod-download handler seam

The library ships a **no-op default** `INxmModDownloadHandler` that logs the
parsed URL at Information. The real mod-download handler (the
[acquisition flow](mod-acquisition.md): download via the Nexus client, import
into the unified repository) is registered later, in the UI assembly. The
registration is drop-in: the no-op default is registered with plain
`AddSingleton`, and MS DI resolves the last registration, so a later
`AddSingleton<INxmModDownloadHandler, RealImpl>()` after `AddNxm()` supersedes
the default.

The real handler lives in the **UI assembly** (it coordinates UI concerns: the
active-profile session, the error dialog, UI-thread marshaling). Placing it in
Integrations would create a dependency cycle. See
[mod acquisition](mod-acquisition.md) for why.

## See also

- [nxm reference](../reference/nxm.md): public surface, exact
  signatures, DI registration, testing.
- [mod acquisition](mod-acquisition.md): the real handler that plugs into
  the mod-download seam shipped here.
- [Nexus authentication](nexus-authentication.md): OAuth uses loopback, not the
  `nxm://` handler; the OAuth-callback URL kind is parsed and dropped.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level tie-together.
