using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="LinuxNxmHandlerRegistrar"/> standalone-mode tests (gated to
/// Linux): Register writes the <c>.desktop</c> file with the expected content +
/// path; IsRegistered reflects the (faked) xdg-mime result; Unregister removes
/// the file without any xdg-mime call; the child-env sanitizer drops only
/// LD_PRELOAD; the default runner bounds a wedged executable (process-tree
/// kill, failure result, prompt return). Each registrar is built with an
/// explicit null <c>$APPIMAGE</c> accessor so the standalone path is exercised
/// regardless of whether the test host itself runs from an AppImage.
/// </summary>
public sealed class LinuxNxmHandlerRegistrarTests
{
    // Forces standalone mode (no $APPIMAGE) so these tests exercise the direct
    // handler path regardless of the test host environment.
    private static Func<string?> NoAppImage => () => null;

    // A distinctive sleep duration so the timeout test can identify its own
    // child processes in the process table without false positives.
    private const int TimeoutTestSleepSeconds = 987;

    [Fact]
    public void Register_writes_desktop_file_with_expected_content()
    {
        if (!OperatingSystem.IsLinux())
            return; // gated: the Linux registrar only runs on Linux.

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();

            var file = Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId);
            Assert.True(File.Exists(file));
            var content = File.ReadAllText(file);
            Assert.Contains("Type=Application", content);
            Assert.Contains("Name=Modificus Curator NXM Handler", content);
            Assert.Contains("Exec=\"/opt/curator/Modificus.Curator.NxmHandler\" %u", content);
            Assert.Contains("NoDisplay=true", content);
            Assert.Contains("MimeType=x-scheme-handler/nxm;", content);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_true_when_desktop_file_present_and_xdg_reports_us()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_false_when_xdg_reports_another_handler()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "some-other-app.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.False(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_false_when_desktop_file_absent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            Assert.False(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void Unregister_removes_desktop_file_and_invokes_no_xdg_command()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var xdgCalls = new List<string>();
            (int, string) RunXdg(string args)
            {
                xdgCalls.Add(args);
                return (0, "");
            }

            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: RunXdg,
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));

            registrar.Unregister();
            Assert.False(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));

            // Register performs the single xdg-mime "default" call; Unregister
            // performs none (the discarded trailing query is gone).
            var call = Assert.Single(xdgCalls);
            Assert.StartsWith("default ", call, StringComparison.Ordinal);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void Register_tolerates_missing_xdg_mime()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            // xdg-mime "missing": the runXdg fake throws, simulating the binary
            // being absent. Register must NOT throw (the .desktop file is still
            // the source of truth).
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => throw new FileNotFoundException("xdg-mime not installed"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    // ---- child environment sanitization ------------------------------------

    [Fact]
    public void SanitizeChildEnvironment_removes_only_LD_PRELOAD()
    {
        if (!OperatingSystem.IsLinux())
            return; // gated: the helper lives on the Linux registrar.

        var source = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LD_PRELOAD"] = "/steam/overlay.so",
            ["PATH"] = "/usr/bin:/bin",
            ["LD_LIBRARY_PATH"] = "/steam/lib", // other LD_* vars survive
            ["APPIMAGE"] = "/home/user/Curator.AppImage",
        };

        var sanitized = LinuxNxmHandlerRegistrar.SanitizeChildEnvironment(source);

        Assert.DoesNotContain("LD_PRELOAD", sanitized.Keys);
        Assert.Equal(3, sanitized.Count);
        Assert.Equal("/usr/bin:/bin", sanitized["PATH"]);
        Assert.Equal("/steam/lib", sanitized["LD_LIBRARY_PATH"]);
        Assert.Equal("/home/user/Curator.AppImage", sanitized["APPIMAGE"]);
        // The source (the parent env) is untouched.
        Assert.Equal("/steam/overlay.so", source["LD_PRELOAD"]);
        Assert.Equal(4, source.Count);
    }

    [Fact]
    public void SanitizeChildEnvironment_is_a_no_op_when_LD_PRELOAD_is_absent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var source = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/user",
        };

        var sanitized = LinuxNxmHandlerRegistrar.SanitizeChildEnvironment(source);

        Assert.Equal(2, sanitized.Count);
        Assert.Equal("/usr/bin", sanitized["PATH"]);
        Assert.Equal("/home/user", sanitized["HOME"]);
    }

    // ---- bounded runner -----------------------------------------------------

    [Fact]
    public void Runner_timeout_kills_the_process_tree_and_fails_the_call()
    {
        if (!OperatingSystem.IsLinux())
            return; // gated: the runner + script are Linux-only.

        var dir = CreateTempApplicationsDir();
        // A shell script standing in for a wedged xdg-mime: it spawns a
        // long-sleeping child so the process-tree kill is exercised.
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "wedged-xdg.sh");
        try
        {
            File.WriteAllText(scriptPath, $"#!/bin/sh\nsleep {TimeoutTestSleepSeconds}\n");
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The desktop file must exist so IsRegistered reaches the runner.
            File.WriteAllText(
                Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId),
                "[Desktop Entry]\nType=Application\n");

            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                appImagePathAccessor: NoAppImage,
                xdgExecutable: scriptPath,
                xdgWaitTimeoutMs: 200);

            var stopwatch = Stopwatch.StartNew();
            var registered = registrar.IsRegistered();
            stopwatch.Stop();

            // The wedged helper maps to not-registered and returns promptly
            // (bounded well below the script's own runtime).
            Assert.False(registered);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
            // No orphan: the script wrapper and its sleep child are both gone.
            Assert.True(NoProcessMatching(scriptPath));
            Assert.True(NoProcessMatching($"sleep {TimeoutTestSleepSeconds}"));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    /// <summary>
    /// Whether no live process carries <paramref name="marker"/> in its command
    /// line (a short poll absorbs kernel reaping lag after the tree kill).
    /// </summary>
    private static bool NoProcessMatching(string marker)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var psi = new ProcessStartInfo("ps", "-eo args=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var ps = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ps.");
            var args = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            if (!args.Contains(marker, StringComparison.Ordinal))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static string CreateTempApplicationsDir() =>
        Path.Combine(Path.GetTempPath(), "curator-nxm-test-" + Guid.NewGuid().ToString("N"));

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
