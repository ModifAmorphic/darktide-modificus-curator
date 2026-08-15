using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.General;

/// <summary>
/// Production <see cref="IExternalLauncher"/>: shells out via
/// <c>Process.Start(ProcessStartInfo)</c> with <c>UseShellExecute = true</c>,
/// which routes a URL to the default browser and a folder to the file manager
/// on every supported platform.
/// </summary>
/// <remarks>
/// The shell-launch failure filter is intentionally narrow
/// (<see cref="Win32Exception"/>, <see cref="PlatformNotSupportedException"/>,
/// <see cref="FileNotFoundException"/>: no default handler, an unsupported
/// platform, a missing target). Each is logged + mapped to a <c>false</c>
/// return so callers can surface their own fallback; every other exception
/// propagates (a programming error must not be silently swallowed as a launch
/// failure). Stateless; registered as a singleton.
/// </remarks>
public sealed class ShellExternalLauncher : IExternalLauncher
{
    private readonly ILogger<ShellExternalLauncher> _logger;

    public ShellExternalLauncher(ILogger<ShellExternalLauncher> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool OpenUri(Uri uri)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            };
            using (Process.Start(psi))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Shell launch of {Uri} failed.", uri);
            return false;
        }
    }

    /// <inheritdoc />
    public bool OpenPath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            using (Process.Start(psi))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Shell launch of {Path} failed.", path);
            return false;
        }
    }
}
