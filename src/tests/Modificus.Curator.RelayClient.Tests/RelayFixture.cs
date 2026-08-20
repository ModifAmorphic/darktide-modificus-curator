using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Per-test fixture: scaffolds a temp Relay dir with a stub
/// <c>mod_relay.exe</c> (so the runtime-dir check passes), and supplies
/// fakes for <see cref="IProfileService"/> + <see cref="ISteamService"/> +
/// <see cref="IProcessLauncher"/>. Builds the internal
/// <see cref="RelayLaunchService"/> with a concrete
/// <see cref="IPlatformLaunchStrategy"/> (backed by the fake launcher) so both
/// the Windows and Linux code paths are exercisable on any CI OS. Disposes the
/// temp tree on teardown so tests are isolated regardless of outcome.
/// </summary>
/// <remarks>
/// Mirrors the Steam library's SteamFixture: resolve the seams as fakes, drive
/// the service under test, assert on the recorded side-effects. The service is
/// constructed via its DI constructor with the chosen strategy; the DI path is
/// covered separately in the service-collection tests.
/// </remarks>
internal sealed class RelayFixture : IDisposable
{
    public string TempRoot { get; }
    public string RuntimeDir { get; }
    public FakeProfileService Profiles { get; } = new();
    public FakeSteamService Steam { get; } = new();
    public FakeProcessLauncher Launcher { get; } = new();
    public FakeGameDirModsHost GameDirHost { get; } = new();
    public FakeConfigLoader ConfigLoader { get; }
    public CuratorConfig Config { get; }

    public RelayFixture()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "curator-relay-" + Guid.NewGuid().ToString("N"));
        RuntimeDir = Path.Combine(TempRoot, "relay");
        Directory.CreateDirectory(RuntimeDir);

        // Deploy a stub launcher.exe so the runtime-dir existence check passes
        // for the success-path tests. Tests that need it absent call DeleteLauncher().
        LauncherPath = Path.Combine(RuntimeDir, RelayLaunchService.LauncherExecutableName);
        File.WriteAllText(LauncherPath, string.Empty);

        // A real game tree the launch flow can derive a game dir from: the
        // game-dir hosting step requires dirname(dirname(binary)) to exist,
        // so the fixture-scoped discovery results point the Darktide binary
        // here instead of the static fakes' machine-foreign paths. Tests that
        // never launch may keep using the static FakeDiscovery fixtures.
        GameDir = Path.Combine(TempRoot, "game");
        var binaries = Path.Combine(GameDir, "binaries");
        Directory.CreateDirectory(binaries);
        WindowsGameBinary = Path.Combine(binaries, "Darktide.exe");
        LinuxGameBinary = WindowsGameBinary;
        File.WriteAllText(WindowsGameBinary, string.Empty);
        CompleteWindows = FakeDiscovery.CompleteWindows with { DarktideGameBinaryPath = WindowsGameBinary };
        CompleteLinux = FakeDiscovery.CompleteLinux with { DarktideGameBinaryPath = LinuxGameBinary };

        Config = CuratorConfig.CreateDefault();
        Config.RelayDir = RuntimeDir;
        // Redirect the Relay log stem into the temp tree too: Launch now resolves
        // + prunes Relay's relay-*.log in this directory from RelayLogFile, so the
        // default app-data logs dir must not be touched by a test run.
        Config.Logging.RelayLogFile = Path.Combine(TempRoot, "logs", "relay-.log");
        // The fake returns the same mutable Config instance on each Load(), so a
        // test may mutate fx.Config between launches and the next Launch sees it.
        ConfigLoader = new FakeConfigLoader { Config = Config };
    }

    /// <summary>The full path to the stub launcher in the temp runtime dir.</summary>
    public string LauncherPath { get; }

    /// <summary>
    /// The real temp game dir shared by the fixture-scoped discovery results
    /// (<c>&lt;TempRoot&gt;/game</c>, holding the stub binary under
    /// <c>binaries/</c>).
    /// </summary>
    public string GameDir { get; }

    /// <summary>The fixture-scoped game binary path (inside <see cref="GameDir"/>).</summary>
    public string WindowsGameBinary { get; }

    /// <summary>The fixture-scoped Linux game binary path (inside <see cref="GameDir"/>).</summary>
    public string LinuxGameBinary { get; }

    /// <summary>
    /// Complete discovery fixtures whose Darktide binary lives in
    /// <see cref="GameDir"/> (so the derived game dir exists). The static
    /// <see cref="FakeDiscovery"/> constants stay machine-foreign for the
    /// strategy-only tests that never launch.
    /// </summary>
    public DiscoveryResult CompleteWindows { get; }

    /// <summary>See <see cref="CompleteWindows"/>.</summary>
    public DiscoveryResult CompleteLinux { get; }

    /// <summary>
    /// Builds the service under test wired for a Windows launch (direct
    /// invocation, untranslated args) - the real <see cref="WindowsLaunchStrategy"/>
    /// driven by the fixture's fake <see cref="IProcessLauncher"/>.
    /// </summary>
    public RelayLaunchService BuildWindowsService() =>
        BuildService(new WindowsLaunchStrategy(Launcher, NullLogger<WindowsLaunchStrategy>.Instance));

    /// <summary>
    /// Builds the service under test wired for a Linux launch (<c>proton run</c>
    /// + both <c>STEAM_COMPAT_*</c> env vars + <c>Z:\</c>-translated args) - the
    /// real <see cref="LinuxLaunchStrategy"/> driven by the fixture's fake
    /// <see cref="IProcessLauncher"/>.
    /// </summary>
    public RelayLaunchService BuildLinuxService() =>
        BuildService(new LinuxLaunchStrategy(Launcher, NullLogger<LinuxLaunchStrategy>.Instance));

    /// <summary>Builds the service under test with an explicit strategy.</summary>
    public RelayLaunchService BuildService(IPlatformLaunchStrategy strategy) =>
        new(Profiles, Steam, ConfigLoader, strategy, GameDirHost, NullLogger<RelayLaunchService>.Instance);

    /// <summary>
    /// Builds the service under test with an explicit strategy AND an explicit
    /// game-dir host, for tests that drive the real host end-to-end through
    /// the launch flow.
    /// </summary>
    public RelayLaunchService BuildService(IPlatformLaunchStrategy strategy, IGameDirModsHost gameDirHost) =>
        new(Profiles, Steam, ConfigLoader, strategy, gameDirHost, NullLogger<RelayLaunchService>.Instance);

    /// <summary>Removes the stub launcher so the runtime-dir check fails.</summary>
    public void DeleteLauncher()
    {
        if (File.Exists(LauncherPath))
        {
            File.Delete(LauncherPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(TempRoot))
        {
            // The real-host end-to-end test leaves a <game>/mods junction under
            // TempRoot (the game-dir hosting link); a naive
            // Directory.Delete(root, recursive: true) throws
            // UnauthorizedAccessException when it reaches a directory junction
            // on Windows, so teardown walks the tree entry-by-entry and removes
            // reparse points as LINKS (never following them into the staged
            // tree). Mirrors ProfileServiceFixture.DeleteTree/DeleteEntry +
            // ProfileService's staged-entry delete. Cross-platform by
            // construction: the same attribute check handles Linux symlinks.
            try { DeleteTree(TempRoot); }
            catch (IOException) { /* best-effort: temp dirs are harmless if left */ }
            catch (UnauthorizedAccessException) { /* best-effort: temp dirs are harmless if left */ }
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            DeleteEntry(entry);
        }

        Directory.Delete(root); // empty (links + children removed above)
    }

    private static void DeleteEntry(string entry)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(entry);
        }
        catch (FileNotFoundException) { return; } // raced away
        catch (DirectoryNotFoundException) { return; }

        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Junction/symlink: remove the link only, never follow into its target.
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        else if ((attrs & FileAttributes.Directory) != 0)
        {
            DeleteTree(entry);
        }
        else
        {
            File.Delete(entry);
        }
    }
}
