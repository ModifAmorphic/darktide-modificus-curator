using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="LinuxNxmHandlerRegistrar"/> standalone-mode tests (gated to
/// Linux): Register writes the <c>.desktop</c> file with the expected content +
/// path; IsRegistered reflects the (faked) xdg-mime result; Unregister removes
/// the file without any xdg-mime call; the child-env sanitizer drops only
/// LD_PRELOAD. Each registrar is built with an
/// explicit null <c>$APPIMAGE</c> accessor so the standalone path is exercised
/// regardless of whether the test host itself runs from an AppImage.
/// </summary>
public sealed class LinuxNxmHandlerRegistrarTests
{
    // Forces standalone mode (no $APPIMAGE) so these tests exercise the direct
    // handler path regardless of the test host environment.
    private static Func<string?> NoAppImage => () => null;

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

    private static string CreateTempApplicationsDir() =>
        Path.Combine(Path.GetTempPath(), "curator-nxm-test-" + Guid.NewGuid().ToString("N"));

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
