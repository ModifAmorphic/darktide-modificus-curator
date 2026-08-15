namespace Modificus.Curator.General;

/// <summary>
/// Opens a URL or a filesystem folder through the OS shell (the default
/// browser for a URI, the file manager for a folder).
/// </summary>
/// <remarks>
/// <para>
/// <b>Return contract.</b> <c>false</c> means the OS could not start the shell
/// launch (no default handler registered, a headless session, a missing
/// target). Callers surface their own fallback, typically a localized alert
/// carrying the URL/path for manual copy.</para>
/// <para>
/// <b>Exception contract.</b> Only the narrow shell-launch failure set
/// (<see cref="System.ComponentModel.Win32Exception"/>,
/// <see cref="PlatformNotSupportedException"/>,
/// <see cref="System.IO.FileNotFoundException"/>) is mapped to
/// <c>false</c> (caught + logged by the implementation). Every other
/// exception propagates, so a real wiring bug stays visible instead of being
/// swallowed as a launch failure.</para>
/// </remarks>
public interface IExternalLauncher
{
    /// <summary>
    /// Opens <paramref name="uri"/> in the OS-registered handler (the default
    /// browser for http/https). Returns <c>false</c> when the shell launch
    /// could not start.
    /// </summary>
    bool OpenUri(Uri uri);

    /// <summary>
    /// Opens <paramref name="path"/> in the OS file manager. Returns
    /// <c>false</c> when the shell launch could not start.
    /// </summary>
    bool OpenPath(string path);
}
