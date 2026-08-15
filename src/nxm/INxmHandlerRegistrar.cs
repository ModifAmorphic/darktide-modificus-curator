namespace Modificus.Curator.Nxm;

/// <summary>
/// Registers / unregisters / queries the OS scheme handler that routes
/// <c>nxm://</c> clicks to the handler exe. A single interface with two
/// platform implementations (Windows writes <c>HKCU\Software\Classes\nxm</c>;
/// Linux writes a <c>.desktop</c> file + <c>xdg-mime default</c>), selected by
/// runtime OS.
/// </summary>
/// <remarks>
/// Registration is an explicit user action: the register path confirms first
/// (it is a system-wide change that can affect other mod managers). The
/// mutating operations are ownership-safe on their own:
/// <see cref="Unregister"/> never removes another program's registration and
/// touches only Curator's own registration files, so callers never need to
/// pre-check <see cref="IsRegistered"/> before releasing.
/// <see cref="MaintainRegistration"/> runs best-effort after startup but never
/// auto-registers. <see cref="IsRegistered"/> is synchronous and not
/// necessarily cheap: on Linux it may spawn an external process.
/// </remarks>
public interface INxmHandlerRegistrar
{
    /// <summary>
    /// Whether the OS currently routes <c>nxm://</c> to this handler exe.
    /// Synchronous and potentially slow: on Linux this may spawn an
    /// external process, so callers on a UI thread should invoke it
    /// deliberately, not incidentally.
    /// </summary>
    bool IsRegistered();

    /// <summary>
    /// Registers the handler exe as the OS <c>nxm://</c> handler (per-user; no
    /// elevation required). Throws on an unrecoverable failure (permission
    /// denied, disk error). Best-effort steps (e.g. a missing <c>xdg-mime</c>
    /// on Linux) are logged, not thrown.
    /// </summary>
    void Register();

    /// <summary>
    /// Releases only Curator's own registration (deletes the registry key /
    /// <c>.desktop</c> file). Self-guarded: never removes another program's
    /// registration. Whether it is a no-op or removes only Curator's own files
    /// depends on the platform state (Windows logs + skips when Curator is not
    /// the current handler; Linux removes Curator's own desktop file even when
    /// another manager is the active default). Idempotent on an absent
    /// registration.
    /// </summary>
    void Unregister();

    /// <summary>
    /// Best-effort maintenance of an existing Curator-owned registration. Run
    /// once after the process has established single-instance ownership, so the
    /// fatal process-enumeration check has already succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Must never claim ownership.</b> This method refreshes the persistent
    /// handler bytes and the AppImage symlink ONLY when Curator already owns the
    /// active <c>nxm://</c> association (the desktop file exists AND
    /// <c>xdg-mime query default</c> reports Curator's exact desktop id). It
    /// must never call <c>xdg-mime default</c>, create the desktop file, or
    /// replace another mod manager's registration. When Curator does not own the
    /// association, the method is a silent no-op.</para>
    /// <para>
    /// <b>Failure is non-fatal.</b> Any error is logged and swallowed, so a
    /// maintenance failure never breaks Curator startup. The call is
    /// synchronous and may take time on Linux (it can spawn the sanitized
    /// <c>xdg-mime</c> child), so callers should not assume it is
    /// instant.</para>
    /// <para>
    /// <b>Platform no-ops.</b> Windows has no AppImage-style temporary mount, so
    /// its implementation is a no-op. The standalone Linux layout (no
    /// <c>$APPIMAGE</c>) is also a no-op: the packaged handler path is already
    /// stable. Only an AppImage run performs work.</para>
    /// </remarks>
    void MaintainRegistration();
}
