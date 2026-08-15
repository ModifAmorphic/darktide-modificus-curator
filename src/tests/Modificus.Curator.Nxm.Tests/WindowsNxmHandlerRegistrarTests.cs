using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="WindowsNxmHandlerRegistrar"/> ownership-safety tests (gated to
/// Windows), driven through the internal base-key seam (a temp subkey under
/// HKCU) so nothing touches the real classes root: unregister is a no-op on an
/// absent key, preserves another program's registration, and deletes only
/// Curator's own.
/// </summary>
public sealed class WindowsNxmHandlerRegistrarTests
{
    private const string HandlerExe = @"C:\Apps\Curator\Modificus.Curator.NxmHandler.exe";
    private const string OtherExe = @"C:\Apps\OtherManager\handler.exe";

    // The registrar's registration path, relative to the injected base key.
    private const string NxmKeyPath = @"Software\Classes\nxm";

    [Fact]
    public void Unregister_is_a_no_op_when_the_key_is_absent()
    {
        if (!OperatingSystem.IsWindows())
            return; // gated: the Windows registrar only runs on Windows.

        var rootPath = CreateTempRootPath();
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(rootPath)!;
            var registrar = BuildRegistrar(root);

            registrar.Unregister();

            Assert.Null(root.OpenSubKey(NxmKeyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Unregister_preserves_a_foreign_handler_registration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rootPath = CreateTempRootPath();
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(rootPath)!;
            WriteCommandValue(root, $"\"{OtherExe}\" \"%1\"");
            var registrar = BuildRegistrar(root);
            Assert.False(registrar.IsRegistered());

            registrar.Unregister();

            // The other program's command value + key tree survive.
            Assert.Equal($"\"{OtherExe}\" \"%1\"", ReadCommandValue(root));
            Assert.NotNull(root.OpenSubKey(NxmKeyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Unregister_deletes_the_tree_when_curator_owns_the_handler()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rootPath = CreateTempRootPath();
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(rootPath)!;
            WriteCommandValue(root, $"\"{HandlerExe}\" \"%1\"");
            var registrar = BuildRegistrar(root);
            Assert.True(registrar.IsRegistered());

            registrar.Unregister();

            Assert.Null(root.OpenSubKey(NxmKeyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsNxmHandlerRegistrar BuildRegistrar(RegistryKey root) =>
        new(HandlerExe, NullLogger<WindowsNxmHandlerRegistrar>.Instance, baseKey: root);

    private static string CreateTempRootPath() =>
        @"Software\curator-nxm-test-" + Guid.NewGuid().ToString("N");

    [SupportedOSPlatform("windows")]
    private static void WriteCommandValue(RegistryKey root, string command)
    {
        using var key = root.CreateSubKey(@"Software\Classes\nxm\shell\open\command")!;
        key.SetValue(null, command);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCommandValue(RegistryKey root)
    {
        using var key = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        return key?.GetValue(null) as string;
    }
}
