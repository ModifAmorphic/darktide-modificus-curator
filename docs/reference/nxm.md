# Nxm (`Modificus.Curator.Nxm`): reference

> The `nxm://` scheme-handler plumbing: URL parsing, length-prefixed IPC framing
> between the tiny handler exe and the running app, the Curator-side single-instance
> guard + IPC server, the router, a pluggable mod-download handler seam (a no-op
> default that the real handler supersedes via DI last-registration-wins), the OS
> scheme-handler registration service, and the testable relay helper the handler
> exe calls.
>
> The OAuth-callback seam that once lived here has been removed (Curator OAuth
> uses loopback redirect, not `nxm://`; see the
> [integrations reference](integrations.md#nexus-client--auth)). The
> mod-download handler seam itself is what the real acquisition flow plugs into.

Three projects implement the nxm path:

- **`Modificus.Curator.Nxm`** (this library): URL types + parser, IPC framing, the
  Curator-side IPC server, the single-instance guard, the router + handler seams,
  the OS scheme-handler registrar, and the testable relay helper. Class library,
  AOT-compatible (`<IsAotCompatible>true</IsAotCompatible>`).
- **`Modificus.Curator.NxmHandler`** (`src/nxm-handler/`): the OS-registered
  scheme handler. A native-AOT console exe whose `Program.cs` is one line,
  `NxmHandlerRelay.RunAsync(args)`. It does no parsing and carries no DI graph;
  it forwards the raw URL over the fixed named pipe, or (cold start) launches
  Curator and retries the pipe until it comes up.
- **`Modificus.Curator.Nxm.Tests`** (xUnit): covers the parser, framing, IPC server
  resilience + single-instance guard, router, relay helper, the Linux registrar,
  and the `AddNxm` DI wiring.

The handler is deliberately dumb: it forwards the raw URL string. Curator owns URL
semantics via `NxmUrlParser` + `INxmRouter`. This keeps the OS-invoked path fast
(tens of ms to start, native AOT, no DI) and puts all routing logic where it is
unit-testable.

## Public surface

### URL types + parser

Pure, no I/O. Mirrors the `ModSourceParser` style: a static `TryParse` that
returns `false` on malformed input and never throws.

```csharp
public abstract record NxmUrl
{
    public string Raw { get; init; }   // verbatim URL as received over IPC
}

public sealed record NxmModDownloadUrl(
    string Raw, string Game, int ModId, int FileId,
    string? Key, long? Expires, long? UserId) : NxmUrl;

public sealed record NxmOAuthCallbackUrl(
    string Raw, string Code, string State) : NxmUrl;

public sealed record NxmCollectionUrl(
    string Raw, string Game, string CollectionId, int Revision) : NxmUrl;

public static class NxmUrlParser
{
    public static bool TryParse(string raw, [MaybeNullWhen(false)] out NxmUrl url);
}
```

Three URL kinds, grounded against MO2 `nxmurl.cpp` and NMA `OAuth.cs`:

- **`NxmModDownloadUrl`**: `nxm://<game>/mods/<modId>/files/<fileId>` with
  optional `key`, `expires` (epoch seconds), `user_id` query parameters. The "Mod
  manager download" button on a Nexus Mods file page produces one.
  `modId`/`fileId` must be positive integers; empty or non-numeric query values
  parse to `null` rather than rejecting the whole URL.
- **`NxmOAuthCallbackUrl`**: `nxm://oauth/callback?code=<code>&state=<state>`.
  Both `code` and `state` are required and must be non-empty. Kept as a parsed
  type so the router can recognize the shape; the router **logs + drops** these
  (Curator OAuth uses loopback redirect, not `nxm://`, per RFC 8252). In normal
  operation no such URL is delivered over IPC.
- **`NxmCollectionUrl`**: `nxm://<game>/collections/<id>/revisions/<rev>`. Parsed
  so the router can log "unsupported in v1" rather than "unknown URL". No handler
  is invoked.

The scheme is matched case-insensitively. Anything that does not match one of the
three shapes fails to parse, and the router logs it as a warning and drops it.

### IPC framing

Length-prefixed UTF-8 framing for the one-message-per-connection IPC protocol.
AOT-safe: only raw byte and UTF-8 IO.

```csharp
public static class NxmIpcFraming
{
    public const int MaxPayloadBytes = 8 * 1024;   // 8 KiB cap

    public static Task WriteUrlAsync(Stream stream, string url, CancellationToken ct = default);
    public static Task<string?> ReadUrlAsync(Stream stream, CancellationToken ct = default);
}

public sealed class NxmIpcFramingException : Exception;   // malformed frame
```

Frame layout: `[4 bytes: little-endian uint32 payload length N][N bytes: UTF-8
URL string]`. The 8 KiB cap is a defense against a misbehaving or hostile client
asking the server to buffer unbounded data, and is enforced on read and write,
before any large allocation. `WriteUrlAsync` throws `NxmIpcFramingException` if
the UTF-8 encoding exceeds the cap. `ReadUrlAsync` returns the decoded URL, or
`null` for a clean close before any bytes (no message), and throws
`NxmIpcFramingException` on a malformed length prefix, a zero length, or a
mid-frame close.

### Single-instance guard

The single-instance check, decoupled from the IPC pipe. `NxmIpcServer.Bind` calls
it before any pipe work, so single-instance enforcement is its own concern with
its own (fatal) outcome, and the pipe bind is free to degrade gracefully without
being overloaded as a single-instance proxy.

```csharp
public sealed class SingleInstanceGuard
{
    public delegate int[] OtherInstanceEnumerator(string processName, int ownPid);

    public SingleInstanceGuard(OtherInstanceEnumerator? enumerate = null, ILogger? logger = null);
    public void EnsureOnlyInstance(string ipcPipeName);
    public bool IsAnotherInstanceRunning(string processName, int excludingPid);
}
```

`EnsureOnlyInstance` obtains the current process's name and PID via
`Process.GetCurrentProcess`, invokes the enumerator with that name, and throws
`NxmSingleInstanceException` if any other live process shares the name. The IPC
pipe name is carried on the exception as context.

`IsAnotherInstanceRunning` is the non-throwing query over the same
enumeration: true when any live process other than `excludingPid` shares
`processName`. Callers checking for "another me" pass the current process's
name and PID; callers checking for a different executable pass that
executable's name (`NxmHandlerRelay` asks whether Curator is up before its
cold-start launch, reusing this guard instead of duplicating the
enumeration).

- **Production enumerator** (`EnumerateOthers`, the default): uses
  `Process.GetProcessesByName(processName)` and excludes self by PID. Enumeration
  failures (`Win32Exception`, `InvalidOperationException`, e.g. the runner lacks
  process-query rights) degrade to an empty result, so a glitch does not block a
  legitimate launch under a false negative.
- **Test seam**: the enumerator is an injectable delegate, so tests inject a fake
  that returns a populated array (another instance exists) or an empty array
  (alone), deterministically and without spawning real processes.

**Why process enumeration, not a pipe probe or the pipe bind.** The pipe bind is
not a reliable cross-platform single-instance claim: on Linux the transport is a
Unix domain socket, and two processes can both bind the same path. A
probe-as-client works but adds a startup tax on Linux (the probe pends when no
server exists). Process enumeration directly answers "is another Curator already
running?", is fast, unprivileged (no elevation), and is decoupled from the IPC
transport.

**Accepted v1 race.** Two instances starting within milliseconds could both
enumerate, both see no other, both proceed. For a desktop double-launch (the
realistic case: seconds apart, not microseconds) this is negligible; a
cross-process mutex or lock-file on top is not worth the complexity for v1.
Documented and accepted.

### IPC server

```csharp
public sealed class NxmIpcServer : IDisposable, IAsyncDisposable
{
    public const string DefaultPipeName = "Modificus.Curator.Nxm";

    public NxmIpcServer(
        INxmRouter router,
        ILogger<NxmIpcServer> logger,
        string? pipeName = null,
        SingleInstanceGuard? singleInstance = null,
        Func<string, NamedPipeServerStream>? createServerStream = null);

    public bool IsBound { get; }
    public void Bind();                              // two startup checks
    public Task RunAsync(CancellationToken ct);      // accept loop until cancelled
}

public sealed class NxmSingleInstanceException : Exception
{
    public string PipeName { get; }
}
```

- **Fixed pipe name `Modificus.Curator.Nxm`.** No per-user suffix: single-user gaming-app
  context. Cross-platform via `NamedPipeServerStream`.
- **Startup is two separate checks with two separate outcomes** (single-instance
  is no longer overloaded onto the pipe bind):
  1. **Single-instance** via `SingleInstanceGuard.EnsureOnlyInstance` (process
     enumeration). If another Curator process is found, `Bind` throws
     `NxmSingleInstanceException`. This is fatal: it propagates out of
     `CuratorComposition.Build` to `App.OnFrameworkInitializationCompleted`, which
     catches it and exits before any window shows.
  2. **IPC pipe bind**, only after single-instance passes. The
     `NamedPipeServerStream` is constructed for `RunAsync` to accept on. On
     `IOException` (a real pipe problem: leftover socket, permissions; not
     another instance, which check 1 settled), the server **degrades gracefully**:
     it logs a warning ("nxm click-to-download from Nexus will be unavailable this
     session"), leaves `IsBound` false, and returns. The app continues without
     the IPC server; profiles, mods, and launch are unaffected.
- **`IsBound`** is true only after a successful pipe bind. The composition root
  checks it to decide whether to start the accept loop.
- **Accept loop** (`RunAsync`): `WaitForConnectionAsync`, read one framed URL via
  `NxmIpcFraming.ReadUrlAsync`, route it via `INxmRouter.RouteAsync`, then
  `Disconnect` between clients (the same server instance accepts the next client;
  no rebind, the pipe name stays claimed for the app's lifetime). Per-connection
  exceptions are logged and swallowed so one bad client cannot kill the server.
  The loop processes one connection at a time, which rests on an explicit
  invariant: `RouteAsync` must return promptly. Request processing is the routed
  handler's responsibility and must not run inline on the accept loop (the
  handler enqueues or refuses; it does not work inline). Cancellation shuts the
  server down.

Separating the two concerns means single-instance is fast (no probe timeout) and
the pipe is its own check that degrades on failure.

### Router + pluggable handler

```csharp
public interface INxmRouter
{
    Task RouteAsync(string rawUrl, CancellationToken ct = default);
}

public interface INxmModDownloadHandler
{
    Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default);
}
```

The default `NxmRouter` (internal) parses the raw URL via `NxmUrlParser`,
dispatches mod-download URLs to `INxmModDownloadHandler`, logs OAuth-callback
URLs as "handled by the loopback listener, not the nxm handler" + drops them
(Curator OAuth uses loopback redirect, independent of the `nxm://` handler, per
RFC 8252), logs collection URLs as "unsupported in v1", and logs unparseable
URLs as a warning. Handler exceptions are caught at the router boundary so one
bad handler invocation cannot kill the IPC accept loop.

Both seams carry a prompt-return contract: `RouteAsync` runs inline on the IPC
accept loop and must return promptly, and `HandleAsync` must enqueue the
download (or refuse it via its gate) rather than work inline. A slow return
blocks every later nxm delivery.

The library ships a **no-op default implementation** of the mod-download handler
(internal, `NoOpNxmModDownloadHandler`): it logs the parsed URL at Information
and returns. A real implementation (the acquisition flow) supersedes it via DI
last-registration-wins (see [DI wiring](#di-registration)).

**The `INxmOAuthCallbackHandler` seam has been removed.** An earlier form of
this library shipped one expecting the OAuth flow to ride on `nxm://`; Curator
OAuth instead uses loopback redirect (RFC 8252), independent of the `nxm://`
handler. The `NxmOAuthCallbackUrl` parsed type stays (so the parser keeps
recognizing the shape rather than classifying it as unknown); the router just
drops it.

### Handler-exe relay helper

The testable core the OS-registered handler exe calls. Every external dependency
is an injectable seam so the relay is fully testable without real processes or
pipes.

```csharp
public static class NxmHandlerRelay
{
    public static readonly TimeSpan DefaultConnectTimeout;   // 1s
    public static readonly TimeSpan DefaultRetryInterval;    // 250ms
    public static readonly TimeSpan DefaultRetryTimeout;     // 30s

    public static Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<(bool connected, Stream? stream)>>? pipeConnect = null,
        Func<ProcessStartInfo>? launchCuratorFactory = null,
        Func<bool>? curatorRunningCheck = null,
        TimeSpan? retryInterval = null,
        TimeSpan? retryTimeout = null,
        CancellationToken ct = default);
}

public sealed class CuratorMainExeNotFoundException : Exception
{
    public string ExpectedPath { get; }
    public string BaseDirectory { get; }
}
```

The 1s connect timeout (not less) is deliberate: a connected pipe frees quickly
once the routed handler returns promptly, but the enqueue gates read config
from disk inside the connected window, so the headroom is cheap insurance
against slow-disk false cold-starts.

Returns the process exit code (0 on success, non-zero on no-arg or unrecoverable
failure). Behavior:

1. Extract the URL: the first non-flag arg (one that does not start with `-` or
   `/`). If none, log to stderr and return `1`.
2. Hot path (Curator running): try `pipeConnect`. On success, write the framed URL,
   close, return `0`.
3. Cold start (Curator not running): on refused connect, consult
   `curatorRunningCheck`. When a Curator process is already running (another
   handler in a launch burst got there first), skip the launch and log "Curator
   is already running; waiting for the pipe." Otherwise locate the sibling Curator
   exe via `launchCuratorFactory`, start it detached (no args). Either way, retry
   `pipeConnect` every `retryInterval` (250ms) until success or `retryTimeout`
   (30s) elapses, then deliver and return `0`.
4. On timeout: log "Curator did not start within Ns" to stderr and return `3`.

The pre-launch check is advisory with no synchronization: races (Curator
exiting between the check and the connect, or the check under-detecting; the
Linux kernel truncates process names to 15 characters) fall through to the
existing retry/timeout semantics, and a missed detection means one extra
launch attempt that the single-instance guard already handles. The default
check reuses `SingleInstanceGuard.IsAnotherInstanceRunning` over the Curator
exe name.

`UseShellExecute` stays `false` on both OSes (the handler launches the exe
directly). Setting it to `true` on Linux for detached launch routes through
`xdg-open`, which pops a desktop error dialog if the path is ever missing;
`Process.Start` without `WaitForExit` already detaches, so no `UseShellExecute`
is needed. `CreateNoWindow=true` on Windows keeps the secondary launch quiet.

**Cold start is owned by the handler, not Curator.** Curator has no `--nxm` arg and
no cold-start branch; its startup is untouched by the handler. The handler owns
the entire cold-start orchestration.

**Missing sibling exe.** `ResolveCuratorMainExe` (called by the default launch
factory) verifies the sibling Curator exe exists and throws
`CuratorMainExeNotFoundException` if it does not. The relay logs this to stderr and
exits non-zero immediately, without entering the retry loop: there is nothing to
retry against, and a headless handler must never hand a bad path to the shell (no
desktop dialog).

### OS scheme-handler registration service

```csharp
public interface INxmHandlerRegistrar
{
    bool IsRegistered();
    void Register();
    void Unregister();
    void MaintainRegistration();
}
```

A single interface with two platform implementations, selected by runtime OS at
DI registration time (mirroring `IPlatformLaunchStrategy`, `IProcessLookup`, and
`SteamRegistryReader`):

- **`WindowsNxmHandlerRegistrar`** (`[SupportedOSPlatform("windows")]`): writes
  `HKCU\Software\Classes\nxm` (per-user, no elevation) with
  `(Default) = "URL:Nexus Mods Link"` and `URL Protocol = ""`, and
  `HKCU\Software\Classes\nxm\shell\open\command` with
  `(Default) = "<handler-exe>" "%1"`. `IsRegistered` checks the command value
  points at the handler exe; `Unregister` is self-guarded: it deletes the key
  tree only when `IsRegistered` is true (Curator's own registration), is a
  logged no-op when the key belongs to another program, and idempotent on an
  absent key.
- **`LinuxNxmHandlerRegistrar`** (`[SupportedOSPlatform("linux")]`): writes
  `~/.local/share/applications/modificus-curator-nxm-handler.desktop` (the source of truth
  most desktops honor) with `Exec="<handler-exe>" %u` and
  `MimeType=x-scheme-handler/nxm;`, then best-effort runs
  `xdg-mime default modificus-curator-nxm-handler.desktop x-scheme-handler/nxm`. The
  `xdg-mime` invocation is best-effort: if the tool is absent, the `.desktop`
  file is still the registration, and the failure is logged, not thrown.
  `IsRegistered` requires both the file present and `xdg-mime query` reporting
  our handler as the default. In a standalone run, the desktop Exec points at
  the packaged sibling handler. In an AppImage run, registration atomically
  copies that handler to `<local-data>/Modificus Curator/nxm-handler/`, gives it
  owner-only executable permissions, creates a sibling `Modificus.Curator`
  symlink to the validated absolute `$APPIMAGE` path, and records the persistent
  copied handler in Exec. `Unregister` removes only those exact managed files
  and removes the managed directory only when empty; it invokes no `xdg-mime`
  command; it never follows the symlink or recursively deletes unexpected
  content.

**Sanitized `xdg-mime` runner (Linux).** Every `xdg-mime` invocation
(query, default, maintenance) runs through one runner that:

- **Sanitizes the child environment only**: the child's environment is the
  parent's with exactly `LD_PRELOAD` removed (nothing else). Steam injects its
  game-overlay libraries through `LD_PRELOAD`, and an unrelated host utility
  inheriting it runs roughly an order of magnitude slower (measured ~2.3 s vs
  ~0.1 s per query under Steam). Curator's own environment is untouched; the
  removal is the same child-only pattern Steam Runtime's own host helpers use.
- Keeps the arguments exactly the single-string `xdg-mime` invocation (no
  shell), with stdout redirected and stderr unredirected.

The wait is plain and synchronous: a hung desktop helper hangs the probe
rather than being masked. That is deliberate (fail loud; the sanitization is
what keeps the ordinary case fast). `IsRegistered` is therefore synchronous
and potentially slow on Linux (it spawns the external process); UI consumers
read shared last-known state instead of probing incidentally (see the
[ui reference](ui.md#shared-nxm-registration-state)).

`MaintainRegistration()` is a no-op on Windows and standalone Linux. In an
AppImage run it refreshes changed handler bytes and a stale AppImage symlink
only after proving Curator owns the active registration: the desktop file must
exist and `xdg-mime query default x-scheme-handler/nxm` must return Curator's
exact desktop id. It never creates the desktop file, calls `xdg-mime default`,
or replaces another manager's association. Failures are logged and swallowed;
the call is synchronous and non-fatal (the sanitized runner), so a failure
never breaks startup and a hung desktop helper surfaces as a hang rather than
being masked.

The handler-exe path is derived from `AppContext.BaseDirectory` plus the fixed
handler assembly name via `NxmHandlerPaths.GetHandlerExePath()` (the handler ships
as a sibling of the main Curator exe). `NxmHandlerPaths.LinuxDesktopFileId`
(`modificus-curator-nxm-handler.desktop`) is the shared desktop-file id.

**Explicit registration, not startup auto-registration.** Registration as the
OS `nxm://` handler is an explicit user action from the Nexus destination (a
"Nexus download links" section with a status line + a toggle), not something
`CuratorComposition.Build()` does on startup. The register path confirms first
(it is a system-wide change that can affect Vortex, Mod Organizer 2, Nexus Mod
Manager, or other mod managers); the unregister path never removes another
program's registration and touches only Curator's own registration files (the
registrar self-guards ownership, so callers never pre-check; whether it is a
no-op or removes Curator's own files depends on the platform state). The
composition root never calls `Register()` automatically. It does call
`MaintainRegistration()` once after the fatal single-instance check succeeds;
that operation cannot claim ownership. The registrar is resolved by the
Integrations view model (the register/release mutations) and by the shared UI
registration state (see the [ui reference](ui.md#shared-nxm-registration-state)),
which is the sole probe surface for the UI.

## DI registration

```csharp
public static IServiceCollection AddNxm(this IServiceCollection services);
```

Registers `INxmRouter` → `NxmRouter`, `NxmIpcServer`, and the no-op handler
defaults as singletons. The platform `INxmHandlerRegistrar` is selected via
`OperatingSystem.IsWindows()` / `OperatingSystem.IsLinux()` (the canonical CA1416
guards), each behind a `[SupportedOSPlatform]`-annotated factory helper.
Resolving `INxmHandlerRegistrar` on any other platform fails fast (an honest
failure rather than a silent no-op).

**Handler override convention (last registration wins).** The no-op
mod-download default is registered with plain `AddSingleton` (not `TryAdd`).
The composition root registers a real implementation AFTER `AddNxm()` via
`services.AddSingleton<INxmModDownloadHandler, ...>()`; MS DI resolves the LAST
registration, so the real handler supersedes the no-op. The router captures
whichever handler is resolved at its (singleton) construction.

(The parallel OAuth-callback override convention is gone with the
`INxmOAuthCallbackHandler` seam; see the "Router + pluggable handler" section
above.)

## Composition wiring

The composition root binds and starts the IPC server after building the provider:

1. `CuratorComposition.Build()` calls `AddNxm()` during registration, then calls
   `StartNxmServer(provider, logger)`.
2. `StartNxmServer` resolves `NxmIpcServer`, calls `Bind()` (the two startup
   checks), and only if `IsBound` starts `RunAsync` on a fire-and-forget
   background task.
3. `Bind`'s single-instance check throws `NxmSingleInstanceException` on
   collision. This is intentionally NOT caught in the composition root, so it
   propagates out of `Build()` to the caller.
4. `App.OnFrameworkInitializationCompleted` catches
   `NxmSingleInstanceException` around `Build()`, writes a stderr line, and calls
   **`Environment.Exit(1)`**. Not `desktopLifetime.Shutdown`: calling `Shutdown`
   from inside `OnFrameworkInitializationCompleted` breaks Avalonia's MainLoop
   (`StartCore` pushes a frame after the dispatcher shut down, raising an
   unhandled `InvalidOperationException` that SIGABRTs the process). The abrupt
   exit is safe because nothing is initialized at that point (no window, no
   background tasks; the single-instance check runs first in `Bind`, before the
   pipe or accept loop).
5. After `StartNxmServer` returns, the composition root calls the best-effort
   registration-maintenance hook. A single-instance exception bypasses this
   call. A degraded pipe bind still returns normally, so maintenance can repair
   an existing AppImage registration even when IPC is unavailable this session.

On a degraded pipe bind, `StartNxmServer` logs that the IPC server is not running
and skips the accept loop; the app continues without nxm IPC.

The composition root does **not** register the OS handler. Registration is an
explicit user action from the Nexus destination. Startup only maintains an
already-owned AppImage registration.

## On-disk / process layout

```
<AppContext.BaseDirectory>/
  Modificus.Curator(.exe)            the main app (UI + composition root)
  Modificus.Curator.NxmHandler(.exe)           the OS-registered scheme handler (native AOT)
  Modificus.Curator.Nxm.dll          shared library (router, server, framing, relay)
```

The handler exe and the main app ship as siblings. The handler resolves the main
exe by relative path (`NxmHandlerRelay.ResolveCuratorMainExe`), and the OS
registration writes whichever absolute path `NxmHandlerPaths.GetHandlerExePath()`
resolves. Both derive from `AppContext.BaseDirectory`.

Process model:

- **Curator running, user clicks "Mod manager download" on Nexus:** the OS invokes
  the handler exe with the `nxm://` URL; the handler connects to the `Modificus.Curator.Nxm`
  pipe, writes one framed URL, and exits. Curator's accept loop reads it, routes
  it, and the resolved handler acts on it (the no-op default logs it; the real
  handler acquires the mod).
- **Curator not running (cold start):** the handler's connect is refused, so it
  consults the advisory pre-check. With no Curator process running it launches
  the sibling Curator exe (no args); with one already running (a burst of
  "Mod manager download" clicks, where an earlier handler launched Curator
  seconds ago) it skips the launch and waits. Either way it retries the pipe
  every 250ms up to 30s. Once Curator's `Bind` succeeds, the handler connects,
  delivers the URL, and exits. Curator starts normally; it has no `--nxm` arg
  and no cold-start branch.

## Dependencies

- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions` (10.0.9, matching the codebase
  convention for libraries with DI seams).
- **BCL only otherwise.** `System.IO.Pipes` is in-box for net10.0.
  `Microsoft.Win32.Registry` is in-box on Windows (gated by
  `[SupportedOSPlatform("windows")]`; no NuGet reference, mirroring
  `SteamRegistryReader`).
- The library is `<IsAotCompatible>true</IsAotCompatible>` so the handler exe can
  publish native AOT against it. The handler path (relay + framing + parser) uses
  only raw byte and UTF-8 IO; the router, server, and registrar types are
  DI-activated only by the main app, so the trimmer drops them from the handler's
  AOT closure.

## Testing

`Modificus.Curator.Nxm.Tests` covers:

- **`NxmUrlParser`**: valid mod-download, OAuth-callback, and collection URLs;
  malformed rejections (wrong scheme, missing or non-numeric or non-positive ids,
  OAuth missing `code`/`state`, garbage).
- **`NxmIpcFraming`**: write/read round-trip over a real named-pipe pair; the
  8 KiB cap on write and read; mid-frame close; clean-close-at-boundary `null`.
- **`NxmIpcServer`**: a client message is routed to the router; a garbage frame
  does not kill the server (the next connection still works); a throwing router
  does not kill the server; a degraded pipe bind (the `createServerStream` seam
  throws `IOException`) leaves `IsBound` false without throwing.
- **`SingleInstanceGuard`**: the injected `OtherInstanceEnumerator` reports
  another instance (throws `NxmSingleInstanceException`) and reports alone
  (proceeds); the `IsAnotherInstanceRunning` query answers true/false over the
  same enumeration and passes the caller's name + excluding pid through; the
  production enumerator path is not exercised against real processes.
- **`NxmRouter`**: a mod-download URL routes to the mod handler with parsed
  fields; an OAuth callback URL is logged + dropped (the OAuth-callback
  handler seam is gone; Curator OAuth uses loopback); collection and unparseable URLs route to neither; a throwing
  handler does not propagate.
- **`NxmHandlerRelay`**: hot path (connect first try, no launch), cold start
  (refuse, launch exactly once, retry, deliver), the cold-start pre-check split
  (a reported running Curator skips the launch, prints the waiting stderr line,
  and still delivers on retry or times out without launching), cold-start
  timeout, no-URL arg, multi-arg, and the missing-sibling-exe path
  (`CuratorMainExeNotFoundException` exits non-zero without retrying).
- **`LinuxNxmHandlerRegistrar`** (Linux-gated): Register writes the `.desktop`
  file with the expected content; `IsRegistered` reflects the faked `xdg-mime`;
  standalone registration stays direct; AppImage registration copies the
  executable handler and creates the cold-start symlink; maintenance is
  ownership-gated and atomically refreshes changed bytes and moved-AppImage
  links; conservative unregister preserves unexpected files and the AppImage
  target and invokes no `xdg-mime` command; a missing `xdg-mime` is tolerated;
  the child-env sanitizer drops exactly `LD_PRELOAD` (every other variable
  preserved, absent-key no-op, source untouched).
- **`WindowsNxmHandlerRegistrar`** (Windows-gated, through the base-key test
  seam over a temp subkey): `Unregister` is a no-op on an absent key, preserves
  a foreign handler's registration, and deletes the tree only when the command
  points at Curator's handler.
- **`AddNxm`** (service collection): the no-op mod-download default, router,
  server, and the platform registrar are registered; the override
  (last-registration-wins) convention is exercised for the mod-download handler.
  (The OAuth-callback handler resolution is gone with the seam.)

The assembly is annotated
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`: real named
pipes are an OS-level shared resource, and serializing the suite (well under a
second) is worth the determinism. The internal router, server, handlers, and
registrars are visible to tests via `InternalsVisibleTo`.

```sh
dotnet test src/modificus-curator.sln -c Release
```

The handler exe publishes native AOT:

```sh
dotnet publish src/nxm-handler -c Release     # stripped native binary
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md): the
  [nxm:// scheme handler](../architecture/MODIFICUS-CURATOR.md#nxm-scheme-handler)
  section.
- [integrations](integrations.md): the Nexus client/auth + the acquisition
  service that plugs the real mod-download handler into the seam shipped here.
