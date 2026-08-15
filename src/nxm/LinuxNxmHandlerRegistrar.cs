using System.Runtime.Versioning;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Nxm;

/// <summary>
/// Linux <see cref="INxmHandlerRegistrar"/>. Writes
/// <c>~/.local/share/applications/modificus-curator-nxm-handler.desktop</c> (the source of
/// truth most desktops honor) and best-effort runs
/// <c>xdg-mime default modificus-curator-nxm-handler.desktop x-scheme-handler/nxm</c>. The
/// <c>xdg-mime</c> invocation is best-effort: if the tool is absent the
/// <c>.desktop</c> file is still the registration; the failure is logged.
/// </summary>
/// <remarks>
/// <para>
/// Annotated <c>[SupportedOSPlatform("linux")]</c> and registered ONLY on Linux
/// by <c>AddNxm()</c>. Resolves the applications dir + the Curator-managed
/// handler dir via <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// (the codebase convention for per-user data, honoring <c>$XDG_DATA_HOME</c>).
/// The dirs, the <c>xdg</c> runner, and the <c>$APPIMAGE</c> accessor are
/// overridable for deterministic testing.
/// </para>
/// <para>
/// <b>Sanitized runner.</b> Every <c>xdg-mime</c> invocation runs with a child
/// environment from which only <c>LD_PRELOAD</c> is removed (Steam injects its
/// overlay via <c>LD_PRELOAD</c>, which slows unrelated host utilities by an
/// order of magnitude; Curator's own environment is untouched). The wait is
/// plain and synchronous: a hung desktop helper hangs the probe rather than
/// being masked. That is deliberate (fail loud); sanitization is what keeps
/// the ordinary case fast.
/// </para>
/// <para>
/// <b>Two execution modes.</b> Standalone (no <c>$APPIMAGE</c>): the desktop
/// entry's <c>Exec</c> points directly at the packaged handler exe shipped
/// beside Curator. AppImage (<c>$APPIMAGE</c> present): the packaged handler is
/// copied into a Curator-managed per-user directory (durable across AppImage
/// mounts) and the desktop <c>Exec</c> points at the copy, with a sibling
/// symlink to <c>$APPIMAGE</c> so the handler's cold-start sibling resolution
/// launches the AppImage.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxNxmHandlerRegistrar : INxmHandlerRegistrar
{
    private const string XdgHandlerDesktopEntry = "[Desktop Entry]";

    /// <summary>The per-user data segment under the local-application-data folder, shared
    /// with <c>AppPaths.AppDataDir</c> on Linux so the managed handler dir lives
    /// alongside the rest of Curator's user data.</summary>
    private const string LinuxDataSegment = "Modificus Curator";

    /// <summary>
    /// The only environment variable removed from the <c>xdg-mime</c> child's
    /// environment. Steam injects its game-overlay libraries through
    /// <c>LD_PRELOAD</c>; inherited by an unrelated host utility they slow it by
    /// roughly an order of magnitude. Nothing else is stripped (measured; do
    /// not broaden without re-measuring the target environment).
    /// </summary>
    private const string SteamOverlayPreloadVar = "LD_PRELOAD";

    /// <summary>The managed subfolder holding the copied handler + symlink.</summary>
    private const string ManagedHandlerFolder = "nxm-handler";

    /// <summary>
    /// The name of the sibling symlink placed beside the copied handler,
    /// pointing at <c>$APPIMAGE</c>. Matches the cold-start sibling resolution
    /// in <see cref="NxmHandlerRelay.ResolveCuratorMainExe"/> (which looks for a
    /// sibling <c>Modificus.Curator</c>), so the handler launched from the
    /// managed dir resolves the AppImage through this symlink. The handler
    /// retains a distinct process name (<c>Modificus.Curator.NxmHandler</c>), so
    /// this does not trip Curator's process-name-based single-instance guard.
    /// </summary>
    internal const string ManagedAppImageSymlinkName = "Modificus.Curator";

    private readonly string _handlerExePath;
    private readonly string _applicationsDir;
    private readonly string _managedDir;
    private readonly Func<string, (int exitCode, string output)> _runXdg;
    private readonly Func<string?> _appImagePathAccessor;
    private readonly ILogger<LinuxNxmHandlerRegistrar> _logger;

    public LinuxNxmHandlerRegistrar(
        string handlerExePath,
        ILogger<LinuxNxmHandlerRegistrar> logger,
        string? applicationsDir = null,
        Func<string, (int exitCode, string output)>? runXdg = null,
        string? managedDir = null,
        Func<string?>? appImagePathAccessor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerExePath);
        _handlerExePath = handlerExePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationsDir = applicationsDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications");
        if (string.IsNullOrEmpty(_applicationsDir))
            throw new InvalidOperationException("Could not resolve the local applications directory.");
        _runXdg = runXdg ?? RunXdgDefault;
        _managedDir = managedDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LinuxDataSegment,
                ManagedHandlerFolder);
        _appImagePathAccessor = appImagePathAccessor
            ?? (() => Environment.GetEnvironmentVariable("APPIMAGE"));
    }

    private string DesktopFilePath => Path.Combine(_applicationsDir, NxmHandlerPaths.LinuxDesktopFileId);

    private string ManagedHandlerPath => Path.Combine(_managedDir, NxmHandlerPaths.HandlerExeBaseName);

    private string ManagedSymlinkPath => Path.Combine(_managedDir, ManagedAppImageSymlinkName);

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public bool IsRegistered()
    {
        // The desktop file must exist, AND xdg-mime must report our handler as
        // the default for the nxm scheme. A present file with xdg not pointing
        // at us is "registered on disk but not active": treat as not registered
        // so the caller can re-run Register() to fix it.
        if (!File.Exists(DesktopFilePath))
            return false;

        try
        {
            var (exitCode, output) = _runXdg("query default x-scheme-handler/nxm");
            if (exitCode != 0)
                return false;
            return output.Trim().Equals(NxmHandlerPaths.LinuxDesktopFileId, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // xdg-mime absent or failing: treat as "unknown" rather than throw.
            _logger.LogDebug(ex, "xdg-mime query failed; treating nxm handler as not registered.");
            return false;
        }
    }

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public void Register()
    {
        // Resolve the Exec target: the managed copy for an AppImage run, or the
        // packaged handler directly for the standalone layout. The copy happens
        // BEFORE the desktop file is written, so a disk failure during the copy
        // leaves no desktop entry pointing at a missing handler.
        var execPath = PrepareExecTarget();

        Directory.CreateDirectory(_applicationsDir);
        File.WriteAllText(DesktopFilePath, BuildDesktopEntry(execPath));

        // Best-effort: xdg-mime may be absent on minimal WMs. The .desktop file
        // is the source of truth most desktops honor; a missing xdg is logged,
        // not thrown.
        try
        {
            var (exitCode, _) = _runXdg(
                $"default {NxmHandlerPaths.LinuxDesktopFileId} x-scheme-handler/nxm");
            if (exitCode != 0)
                _logger.LogWarning(
                    "xdg-mime default returned {Exit}; the .desktop file is written regardless.", exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "xdg-mime invocation failed (best-effort); the .desktop file is written.");
        }

        _logger.LogInformation("Registered nxm:// handler desktop file at {Path}.", DesktopFilePath);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public void Unregister()
    {
        if (File.Exists(DesktopFilePath))
        {
            try
            {
                File.Delete(DesktopFilePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete the nxm handler desktop file (best-effort).");
                throw;
            }
        }

        // Remove the managed handler copy + the AppImage symlink + the now-empty
        // managed directory. Best-effort and conservative: only the exact
        // Curator-managed files are touched, never the AppImage target the
        // symlink points at, and the managed dir is removed only when empty.
        TryCleanupManagedFiles();

        _logger.LogInformation("Unregistered nxm:// handler desktop file.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// See <see cref="INxmHandlerRegistrar.MaintainRegistration"/> for the
    /// contract. This implementation is a no-op unless the process is running
    /// from an AppImage (<c>$APPIMAGE</c> resolves to a valid path) AND Curator
    /// already owns the active <c>nxm://</c> association. Ownership is proven by
    /// the desktop file existing AND <c>xdg-mime query default</c> reporting
    /// Curator's exact desktop id. When either check fails, the method returns
    /// without touching anything (it must never claim ownership).
    /// </remarks>
    [SupportedOSPlatform("linux")]
    public void MaintainRegistration()
    {
        var appImage = ResolveAppImagePath();
        if (appImage is null)
        {
            // Standalone: the packaged handler path is already stable, no
            // symlink to refresh. Nothing to maintain.
            return;
        }

        // Guard 1: the desktop file must exist. No desktop file means Curator is
        // not registered at all; maintenance must not create one (that would
        // claim ownership).
        if (!File.Exists(DesktopFilePath))
            return;

        // Guard 2: xdg-mime must report Curator's exact desktop id as the active
        // default. Another manager owning the association means we do NOT touch
        // anything (no xdg-mime default call, no file writes).
        try
        {
            var (exitCode, output) = _runXdg("query default x-scheme-handler/nxm");
            if (exitCode != 0)
                return;
            if (!output.Trim().Equals(NxmHandlerPaths.LinuxDesktopFileId, StringComparison.Ordinal))
                return;
        }
        catch (Exception ex)
        {
            // xdg-mime absent or failing: cannot prove ownership, so do not
            // maintain. Best-effort, non-fatal.
            _logger.LogDebug(ex, "xdg-mime query failed during maintenance; skipping (best-effort).");
            return;
        }

        // Curator owns the active registration. Refresh the persistent handler
        // bytes and the AppImage symlink. All best-effort: a failure is logged
        // and swallowed (non-fatal; the next startup retries).
        try
        {
            RefreshManagedHandler();
            RefreshManagedSymlink(appImage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "nxm handler registration maintenance failed (best-effort).");
        }
    }

    /// <summary>
    /// Resolves the <c>Exec</c> target for <see cref="Register"/>: the managed
    /// copy for an AppImage run, or the packaged handler directly for the
    /// standalone layout. For the AppImage case this also copies the handler and
    /// creates the sibling symlink.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private string PrepareExecTarget()
    {
        var appImage = ResolveAppImagePath();
        if (appImage is null)
        {
            // Standalone: the desktop Exec points directly at the packaged
            // sibling handler. Its path is stable (it ships beside Curator).
            return _handlerExePath;
        }

        // AppImage: copy the packaged handler into the managed dir (durable
        // across AppImage mounts) and create the sibling symlink to $APPIMAGE.
        // The desktop Exec points at the copy, never the temporary mount path.
        AtomicCopyHandler();
        AtomicSymlinkUpdate(ManagedSymlinkPath, appImage);

        _logger.LogInformation(
            "AppImage nxm handler installed at {Handler} (symlink {Link} -> {AppImage}).",
            ManagedHandlerPath, ManagedSymlinkPath, appImage);
        return ManagedHandlerPath;
    }

    /// <summary>
    /// Refreshes the managed handler copy when the packaged source differs (an
    /// AppImage update shipped new bytes) or is missing. No-op when the bytes
    /// already match. The source is the packaged handler under the current
    /// AppImage mount.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void RefreshManagedHandler()
    {
        if (!File.Exists(_handlerExePath))
        {
            // The packaged handler is not reachable (should not happen while the
            // AppImage is mounted, but guard regardless). Skip the refresh
            // rather than delete the existing copy.
            return;
        }

        if (File.Exists(ManagedHandlerPath) && FilesHaveSameContent(_handlerExePath, ManagedHandlerPath))
            return; // already current

        AtomicCopyHandler();
        _logger.LogInformation("Refreshed the managed nxm handler copy at {Path}.", ManagedHandlerPath);
    }

    /// <summary>
    /// Refreshes the managed AppImage symlink when it is missing or points at a
    /// stale path (the user moved the AppImage and launched it from the new
    /// location). No-op when the symlink already points at the current
    /// <c>$APPIMAGE</c>.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void RefreshManagedSymlink(string appImage)
    {
        var current = TryReadSymlinkTarget(ManagedSymlinkPath);
        if (current is not null && PathsEqual(current, appImage))
            return; // already current

        AtomicSymlinkUpdate(ManagedSymlinkPath, appImage);
        _logger.LogInformation("Refreshed the managed AppImage symlink {Link} -> {AppImage}.",
            ManagedSymlinkPath, appImage);
    }

    /// <summary>
    /// Detects a valid AppImage execution from <c>$APPIMAGE</c>: the value must
    /// be nonempty, absolute, and point at an existing file. Returns
    /// <c>null</c> for the standalone layout (no AppImage).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private string? ResolveAppImagePath()
    {
        var raw = _appImagePathAccessor();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!Path.IsPathRooted(raw))
            return null;
        if (!File.Exists(raw))
            return null;
        return raw;
    }

    /// <summary>
    /// Atomically copies the packaged handler (<c>_handlerExePath</c>) to
    /// <see cref="ManagedHandlerPath"/> via a temp file on the same filesystem,
    /// then renames into place. Ensures the owner-executable bit (user-only:
    /// the handler is a per-user integration, no group/other access needed).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void AtomicCopyHandler()
    {
        Directory.CreateDirectory(_managedDir);
        var temp = ManagedHandlerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(_handlerExePath, temp, overwrite: false);
            // Set the executable mode before the rename so the file is never
            // briefly non-executable at its final path. User-only (0700):
            // owner read/write/execute, no group or other bits.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temp, ManagedHandlerPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Atomically creates or replaces a symlink at <paramref name="linkPath"/>
    /// pointing at <paramref name="target"/> via a temp symlink + rename. The
    /// old symlink (if any) is replaced atomically; its target is never followed
    /// or deleted.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static void AtomicSymlinkUpdate(string linkPath, string target)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = linkPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(temp, target);
            // rename(2) atomically replaces the existing symlink at linkPath
            // without following its target. Same filesystem (temp is beside
            // linkPath), so the move is atomic.
            File.Move(temp, linkPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Reads the immediate target of a symlink, or <c>null</c> when the path is
    /// not a symlink or does not exist. Swallows all errors (used for the
    /// stale-symlink comparison, where "cannot read" is treated as "needs
    /// refresh").
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static string? TryReadSymlinkTarget(string linkPath)
    {
        try
        {
            return File.ResolveLinkTarget(linkPath, returnFinalTarget: false)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compares two paths for equality after full-path normalization.</summary>
    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    /// <summary>
    /// Compares two files byte-for-byte. Used by maintenance to skip a
    /// non-destructive copy when the managed handler is already current.
    /// </summary>
    private static bool FilesHaveSameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
            return false;
        return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>
    /// Removes the managed handler copy and the AppImage symlink, then removes
    /// the managed directory only if empty. Never recursive, never follows the
    /// symlink target. Best-effort: individual file failures are logged.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void TryCleanupManagedFiles()
    {
        if (!Directory.Exists(_managedDir))
            return;

        // Delete only the exact managed handler copy.
        TryDeleteManagedFile(ManagedHandlerPath);

        // Delete the symlink itself (unlink), NOT its target. File.Delete on a
        // symlink removes the directory entry; it never resolves or deletes the
        // AppImage the link points at.
        TryDeleteManagedFile(ManagedSymlinkPath);

        // Remove the managed directory only if empty. Never recursive: an
        // unexpected file or subdirectory left behind means we leave the dir in
        // place rather than risk deleting something we do not own.
        try
        {
            if (Directory.Exists(_managedDir) && !Directory.EnumerateFileSystemEntries(_managedDir).Any())
                Directory.Delete(_managedDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove the managed nxm-handler directory (best-effort).");
        }
    }

    /// <summary>
    /// Deletes a single managed file, best-effort. <see cref="File.Delete"/> is
    /// idempotent on an absent path and removes a symlink (not its target).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void TryDeleteManagedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Path} during nxm unregister (best-effort).", path);
        }
    }

    internal string BuildDesktopEntry(string execPath)
    {
        // %u is the Freedesktop URL field code; the desktop environment
        // substitutes the clicked nxm:// URL for it when launching.
        return
            $"{XdgHandlerDesktopEntry}\n" +
            "Type=Application\n" +
            "Name=Modificus Curator NXM Handler\n" +
            $"Exec={FormatExec(execPath)}\n" +
            "NoDisplay=true\n" +
            "MimeType=x-scheme-handler/nxm;\n";
    }

    // The handler exe path may contain spaces (e.g. the "Modificus Curator"
    // data segment); the .desktop Exec spec uses quoting for the executable.
    // Keep it simple and faithful: quote the path, leave the %u field code
    // unquoted so the shell substitutes it verbatim.
    private static string FormatExec(string execPath) => $"\"{execPath}\" %u";

    /// <summary>
    /// Builds the child environment for an <c>xdg-mime</c> invocation: a copy
    /// of <paramref name="source"/> with only <c>LD_PRELOAD</c> removed. The
    /// source dictionary is left untouched, so the parent (Curator's)
    /// environment is unaffected. Pure; unit-tested directly.
    /// </summary>
    internal static Dictionary<string, string?> SanitizeChildEnvironment(IDictionary<string, string?> source)
    {
        var sanitized = new Dictionary<string, string?>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (!string.Equals(pair.Key, SteamOverlayPreloadVar, StringComparison.Ordinal))
                sanitized[pair.Key] = pair.Value;
        }
        return sanitized;
    }

    private (int exitCode, string output) RunXdgDefault(string arguments)
    {
        var psi = new ProcessStartInfo("xdg-mime", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // psi.Environment lazily snapshots the parent env at first access;
        // replace its contents with the sanitized copy so exactly the child
        // (and its descendants) loses LD_PRELOAD.
        var childEnv = SanitizeChildEnvironment(psi.Environment);
        psi.Environment.Clear();
        foreach (var pair in childEnv)
            psi.Environment[pair.Key] = pair.Value;

        // Plain synchronous wait: a hung desktop helper hangs the probe rather
        // than being masked (deliberate, fail loud).
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start xdg-mime.");
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
