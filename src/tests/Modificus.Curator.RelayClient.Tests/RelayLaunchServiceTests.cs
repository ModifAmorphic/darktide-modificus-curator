using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Launch-path tests for <see cref="RelayLaunchService"/>. All via the fakes
/// in <see cref="RelayFixture"/>: no real process is spawned and no game is
/// required. The concrete Windows/Linux <see cref="IPlatformLaunchStrategy"/>
/// (driven by the fixture's fake <see cref="IProcessLauncher"/>) is injected so
/// both code paths are exercised on any CI OS.
/// </summary>
public sealed class RelayLaunchServiceTests
{
    // ---- Windows ------------------------------------------------------------

    [Fact]
    public void Windows_assembles_correct_args_and_invokes_launcher_directly()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.PrepareModRootResult = @"C:\curator\profiles\abc\staged";
        var profileId = Guid.NewGuid();
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.Launched, result.Status);

        // Invoked the launcher directly -- not proton, no "run" prefix, no env.
        Assert.Equal(fx.LauncherPath, fx.Launcher.FilePath);
        Assert.Empty(fx.Launcher.Environment!);
        Assert.Empty(fx.Launcher.RemovedVariables);
        Assert.DoesNotContain("run", fx.Launcher.Arguments!);

        // Relay writes its own relay-<yyyyMMdd>.log (resolved at launch from the
        // configured RelayLogFile stem), so the --log-file value is that
        // computed path, not the configured stem.
        var expectedRelayLog = RelayLog.ResolveRelayLogPath(fx.Config.Logging.RelayLogFile, DateTime.Now);

        Assert.Equal(
            new[] { "--game-binary", fx.WindowsGameBinary,
                    "--mod-path",    fx.GameDir,
                    "--log-file",    expectedRelayLog,
                    "--log-append" },
            fx.Launcher.Arguments);

        // --log-level is intentionally NOT emitted: the shell's level vocabulary
        // (error/warn/info/debug/trace) differs from Serilog's, so the launcher's
        // info default is used (the two logs are decoupled).
        Assert.DoesNotContain("--log-level", fx.Launcher.Arguments!);
    }

    [Fact]
    public void Windows_paths_are_not_z_translated()
    {
        // Guard: every path-valued flag must pass through unchanged on Windows
        // (no Z:\ prefix) -- translation is a Linux-only concern. The --log-file
        // value is the computed relay-<date>.log resolved from RelayLogFile.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        const string RelayLogFile = @"C:\curator\logs\relay-.log";
        fx.Config.Logging.RelayLogFile = RelayLogFile;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var game = args[IndexOf(args, "--game-binary") + 1];
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(fx.WindowsGameBinary, game);
        Assert.Equal(RelayLog.ResolveRelayLogPath(RelayLogFile, DateTime.Now), log);
        Assert.DoesNotContain("Z:", game);
        Assert.DoesNotContain("Z:", log);
        // The bare --log-append flag is present and untranslated too.
        Assert.DoesNotContain("Z:", args[IndexOf(args, "--log-append")]);
    }

    [Fact]
    public void Launch_passes_the_computed_relay_log_file_path()
    {
        // Relay writes its own relay-<yyyyMMdd>.log (resolved at launch from the
        // configured RelayLogFile stem), not Curator's Serilog log file and
        // not a bootstrap-pinned path. Uses the Windows strategy so the path
        // passes through verbatim (no Z:\ translation).
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        const string ConfiguredRelayLog = @"C:\curator\logs\relay-.log";
        fx.Config.Logging.RelayLogFile = ConfiguredRelayLog;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var log = args[IndexOf(args, "--log-file") + 1];
        // The computed path: relay-<8 digit date>.log in the configured directory.
        Assert.Matches(@"relay-\d{8}\.log$", Path.GetFileName(log));
        Assert.Equal(Path.GetDirectoryName(ConfiguredRelayLog), Path.GetDirectoryName(log));
        Assert.NotEqual(ConfiguredRelayLog, log);
    }

    [Fact]
    public void Windows_launch_returns_launched_when_process_starts()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Launcher.Returns = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.Null(result.Message);
        Assert.NotNull(result.RelayExited);
    }

    // ---- Linux --------------------------------------------------------------

    [Fact]
    public void Linux_translates_mod_path_and_game_binary_to_wine_paths()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // The launcher's own flags start after "run" + launcherPath.
        var launcherFlags = args.Skip(2).ToList();

        var game = launcherFlags[IndexOf(launcherFlags, "--game-binary") + 1];
        var mod = launcherFlags[IndexOf(launcherFlags, "--mod-path") + 1];

        // Game-dir hosting is the default, so --mod-path is the derived GAME_DIR
        // (the parent of the hosted mods tree), Z:\-translated like every
        // path-valued flag; the game binary likewise.
        Assert.Equal(WinePath.ToWine(fx.GameDir), mod);
        Assert.Equal(WinePath.ToWine(fx.LinuxGameBinary), game);
    }

    [Fact]
    public void Linux_translates_log_file_to_wine_path()
    {
        // The launcher runs under Wine and opens --log-file itself, so it must
        // be Z:\-translated on Linux (else the Relay shell log can't be written
        // where Curator expects). The value is the computed relay-<date>.log
        // resolved from the configured RelayLogFile stem.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        const string RelayLogFile = "/home/u/.local/share/Modificus Curator/logs/relay-.log";
        fx.Config.Logging.RelayLogFile = RelayLogFile;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(WinePath.ToWine(RelayLog.ResolveRelayLogPath(RelayLogFile, DateTime.Now)), log);
    }

    [Fact]
    public void Linux_sets_both_steam_compat_env_vars_from_discovery()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment;
        Assert.NotNull(env);
        Assert.Equal(FakeDiscovery.LinuxCompatdata, env!["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(FakeDiscovery.LinuxSteam, env!["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
    }

    [Fact]
    public void Linux_strips_exactly_the_appimage_identity_environment_variables()
    {
        // The five AppImage/desktop-identity variables (APPDIR, APPIMAGE, ARGV0,
        // OWD, BAMF_DESKTOP_FILE_HINT) must be stripped from the inherited
        // environment before proton runs, so KDE Plasma's task manager does not
        // resolve Curator's desktop identity for Darktide. The two STEAM_COMPAT_*
        // overrides must still be applied; nothing unrelated may be requested
        // for removal.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var expectedRemovals = new HashSet<string>(StringComparer.Ordinal)
        {
            "APPDIR", "APPIMAGE", "ARGV0", "OWD", "BAMF_DESKTOP_FILE_HINT",
        };
        Assert.True(
            expectedRemovals.SetEquals(fx.Launcher.RemovedVariables),
            "expected exactly the five AppImage/desktop-identity variables to be removed");
        Assert.Equal(expectedRemovals.Count, fx.Launcher.RemovedVariables.Count);

        // The Steam compat overrides are still present (overrides apply AFTER removals).
        var env = fx.Launcher.Environment!;
        Assert.Equal(FakeDiscovery.LinuxCompatdata, env["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(FakeDiscovery.LinuxSteam, env["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);

        // Only the two overrides are present (no stray additions beyond them).
        Assert.Equal(2, env.Count);
    }

    [Fact]
    public void Linux_invokes_proton_run_with_launcher_not_launcher_alone()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        // The launched command is <proton>, and its argv is [run, launcher.exe, ...flags].
        Assert.Equal(FakeDiscovery.LinuxProton, fx.Launcher.FilePath);
        var args = fx.Launcher.Arguments!;
        Assert.Equal("run", args[0]);
        Assert.Equal(fx.LauncherPath, args[1]);       // native Linux path -- Proton resolves it
        Assert.True(args.Count > 2, "expected launcher flags after the launcher path");
        // --log-level is not emitted (shell level vocabulary != Serilog's).
        Assert.DoesNotContain("--log-level", args);
    }

    [Fact]
    public void Linux_launch_returns_launched_when_process_starts()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Launcher.Returns = true;
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.NotNull(result.RelayExited);
    }

    // ---- Relay exit tracking ------------------------------------------------

    [Fact]
    public async Task Launched_exit_task_completes_when_the_spawned_process_exits_and_disposes_the_handle()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());
        var spawned = fx.Launcher.LastSpawned;
        Assert.NotNull(spawned);

        // Held: the exit task stays pending while the spawned process lives.
        Assert.False(result.RelayExited!.IsCompleted);
        Assert.False(spawned.Disposed);

        spawned.SimulateExit();
        await result.RelayExited;

        // The tracking owns the handle's lifetime: observation ended, so it
        // disposed the handle.
        Assert.True(spawned.Disposed);
    }

    [Fact]
    public async Task Launched_exit_task_completes_and_disposes_when_the_handle_cannot_be_observed()
    {
        // An unobservable process (WaitForExitAsync throws) is treated as
        // exited: the exit task still completes, never faults, and the handle
        // is still disposed.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Launcher.ThrowOnWaitForExit = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());
        var relayExited = result.RelayExited;
        Assert.NotNull(relayExited);

        await relayExited;
        Assert.True(fx.Launcher.LastSpawned!.Disposed);
    }

    [Fact]
    public void Non_launched_results_carry_no_exit_task()
    {
        // Every result but Launched carries a null exit task: only a real
        // spawn has an exit to observe.

        // DiscoveryIncomplete: short-circuits before any spawn.
        using (var fx = new RelayFixture())
        {
            fx.Steam.Result = fx.CompleteLinux with
            {
                ProtonBinaryPath = null,
                ProtonVersion = null,
                Status = DiscoveryStatus.Partial,
            };
            var result = fx.BuildLinuxService().Launch(Guid.NewGuid());

            Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
            Assert.Null(result.RelayExited);
        }

        // StagingFailed: the mod root failed before any spawn.
        using (var fx = new RelayFixture())
        {
            fx.Steam.Result = fx.CompleteLinux;
            fx.Profiles.PrepareModRootThrows = true;
            var result = fx.BuildLinuxService().Launch(Guid.NewGuid());

            Assert.Equal(LaunchStatus.StagingFailed, result.Status);
            Assert.Null(result.RelayExited);
        }

        // Error: the spawn itself failed (a null handle).
        using (var fx = new RelayFixture())
        {
            fx.Steam.Result = fx.CompleteLinux;
            fx.Launcher.Returns = false;
            var result = fx.BuildLinuxService().Launch(Guid.NewGuid());

            Assert.Equal(LaunchStatus.Error, result.Status);
            Assert.Null(result.RelayExited);
        }
    }

    // ---- DiscoveryIncomplete ------------------------------------------------

    [Fact]
    public void DiscoveryIncomplete_linux_partial_returns_missing_field_names()
    {
        // Steam + Darktide found, but compatdata + Proton missing on Linux.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux with
        {
            CompatdataPath = null,
            ProtonBinaryPath = null,
            ProtonVersion = null,
            Status = DiscoveryStatus.Partial,
        };
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        Assert.Equal(
            new[] { nameof(DiscoveryResult.CompatdataPath), nameof(DiscoveryResult.ProtonBinaryPath) },
            result.MissingDiscoveryFields);

        // Short-circuit: PrepareModRoot must NOT run (we can't launch, so don't write mods.lst).
        Assert.Equal(0, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void DiscoveryIncomplete_windows_partial_returns_missing_game_binary()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows with
        {
            DarktideGameBinaryPath = null,
            Status = DiscoveryStatus.Partial,
        };
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        // Compatdata/Proton are NOT required on Windows -- only the game binary is missing.
        Assert.Equal(
            new[] { nameof(DiscoveryResult.DarktideGameBinaryPath) },
            result.MissingDiscoveryFields);
    }

    // ---- Windows: Steam is not a launch input --------------------------------

    [Fact]
    public void Windows_required_fields_with_valid_darktide_and_null_steam_is_empty()
    {
        // Steam is a discovery mechanism, not a Windows launch input: a resolved
        // Darktide binary is enough, so the missing-field list is empty even with
        // Steam null.
        var strategy = new WindowsLaunchStrategy(
            new FakeProcessLauncher(), NullLogger<WindowsLaunchStrategy>.Instance);
        var discovery = FakeDiscovery.CompleteWindows with { SteamInstallPath = null };

        var missing = strategy.RequiredDiscoveryFields(discovery);

        Assert.Empty(missing);
    }

    [Fact]
    public void Windows_required_fields_reports_darktide_missing_regardless_of_steam()
    {
        // Only the Darktide binary is required on Windows. Whether Steam is present
        // or absent, a missing Darktide binary is the sole missing field.
        var strategy = new WindowsLaunchStrategy(
            new FakeProcessLauncher(), NullLogger<WindowsLaunchStrategy>.Instance);

        var withSteam = FakeDiscovery.CompleteWindows with { DarktideGameBinaryPath = null };
        Assert.Equal(
            new[] { nameof(DiscoveryResult.DarktideGameBinaryPath) },
            strategy.RequiredDiscoveryFields(withSteam));

        var noSteam = withSteam with { SteamInstallPath = null };
        Assert.Equal(
            new[] { nameof(DiscoveryResult.DarktideGameBinaryPath) },
            strategy.RequiredDiscoveryFields(noSteam));
    }

    [Fact]
    public void Windows_launch_proceeds_with_valid_darktide_and_null_steam()
    {
        // End-to-end: with a resolved Darktide binary and no Steam path, the
        // Windows launch is not blocked (no DiscoveryIncomplete) and reaches the
        // launcher directly.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows with { SteamInstallPath = null };
        fx.Launcher.Returns = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.Equal(fx.LauncherPath, fx.Launcher.FilePath);
        Assert.Contains(fx.WindowsGameBinary, fx.Launcher.Arguments!);
    }

    [Fact]
    public void Linux_required_fields_still_requires_steam()
    {
        // Regression: Linux still requires Steam (a launch input via
        // STEAM_COMPAT_CLIENT_INSTALL_PATH). Removing it must surface Steam as
        // missing even when everything else resolves.
        var strategy = new LinuxLaunchStrategy(
            new FakeProcessLauncher(), NullLogger<LinuxLaunchStrategy>.Instance);

        var noSteam = FakeDiscovery.CompleteLinux with { SteamInstallPath = null };
        var missing = strategy.RequiredDiscoveryFields(noSteam);

        Assert.Contains(nameof(DiscoveryResult.SteamInstallPath), missing);
    }

    [Fact]
    public void DiscoveryIncomplete_failed_returns_all_os_required_fields()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = new DiscoveryResult(
            SteamInstallPath: null,
            DarktideGameBinaryPath: null,
            CompatdataPath: null,
            ProtonBinaryPath: null,
            ProtonVersion: null,
            Status: DiscoveryStatus.Failed,
            Warnings: Array.Empty<string>());
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        Assert.Equal(
            new[]
            {
                nameof(DiscoveryResult.SteamInstallPath),
                nameof(DiscoveryResult.DarktideGameBinaryPath),
                nameof(DiscoveryResult.CompatdataPath),
                nameof(DiscoveryResult.ProtonBinaryPath),
            },
            result.MissingDiscoveryFields);
    }

    // ---- Relay console window preference (global) --------------------------

    [Fact]
    public void Default_config_hides_the_relay_console_window()
    {
        // ShowRelayConsole defaults to false, so the launch request must carry
        // CreateNoWindow=true (the console window is suppressed). Read live from
        // the config snapshot the fixture injects.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.True(fx.Launcher.CreateNoWindow);
    }

    [Fact]
    public void ShowRelayConsole_true_shows_the_relay_console_window()
    {
        // Opting in (Preferences checkbox on) yields CreateNoWindow=false, so the
        // Relay console window appears. One platform is enough: both strategies
        // thread createNoWindow through the same ProcessLaunchRequest ctor.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Config.Preferences.ShowRelayConsole = true;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.False(fx.Launcher.CreateNoWindow);
    }

    [Fact]
    public void ShowRelayConsole_preference_is_read_live_per_launch()
    {
        // The preference is read from the live config snapshot each launch (no
        // cached value), so flipping it between launches flips the request flag.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());
        Assert.True(fx.Launcher.CreateNoWindow); // default hidden

        fx.Config.Preferences.ShowRelayConsole = true;
        svc.Launch(Guid.NewGuid());
        Assert.False(fx.Launcher.CreateNoWindow); // opted in -> shown
    }

    // ---- Profile integration ------------------------------------------------

    [Fact]
    public void Launch_calls_PrepareModRoot_with_profile_id_before_invoking()
    {
        // External mode: the staged root PrepareModRoot returns IS the
        // --mod-path (Z:\-translated on Linux), preserving the pre-hosting
        // behavior the opt-out restores.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        const string PreparedRoot = "/tmp/prepared-mod-root";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        fx.Config.Preferences.ExternalModHosting = true;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        svc.Launch(profileId);

        Assert.Equal(1, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(profileId, fx.Profiles.LastPrepareModRootId);

        // The returned path is the --mod-path (Z:\-translated on Linux).
        var args = fx.Launcher.Arguments!;
        var modIndex = IndexOf(args, "--mod-path");
        var modPath = args[modIndex + 1];
        Assert.Equal(WinePath.ToWine(PreparedRoot), modPath);
    }

    // ---- Launch settings: environment + game args ---------------------------

    [Fact]
    public void Launch_reads_the_profile_launch_settings_each_launch()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("PROTON_LOG", "1") },
            GameArguments = new[] { "-windowed" },
        };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        // The profile env reached the Windows Relay process overrides.
        var env = fx.Launcher.Environment!;
        Assert.Equal("1", env["PROTON_LOG"]);
        // The game arg reached the argv as a bare -- then the arg.
        var args = fx.Launcher.Arguments!;
        Assert.Equal("--", args[^2]);
        Assert.Equal("-windowed", args[^1]);
    }

    [Fact]
    public void Windows_profile_with_enable_lua_logs_emits_flag()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { EnableLuaLogs = true };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        AssertBareFlag(args, "--log-lua", present: true);
        AssertPrecedesSeparator(args, "--log-lua");
    }

    [Fact]
    public void Linux_profile_with_enable_lua_logs_emits_flag()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { EnableLuaLogs = true };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // Exactly once; the bare flag is not Z:\-translated (only path-valued
        // flags are).
        AssertBareFlag(args, "--log-lua", present: true);
        AssertPrecedesSeparator(args, "--log-lua");
        Assert.DoesNotContain("Z:", args[IndexOf(args, "--log-lua")]);
    }

    [Fact]
    public void Profile_without_enable_lua_logs_omits_flag()
    {
        // Default LaunchSettings: EnableLuaLogs is false, so no --log-lua.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.DoesNotContain("--log-lua", fx.Launcher.Arguments!);
    }

    [Fact]
    public void Windows_profile_with_skip_splash_emits_flag()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { SkipSplash = true };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        AssertBareFlag(args, "--skip-splash", present: true);
        AssertPrecedesSeparator(args, "--skip-splash");
    }

    [Fact]
    public void Linux_profile_with_skip_splash_emits_flag()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { SkipSplash = true };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // Exactly once; the bare flag is not Z:\-translated (only path-valued
        // flags are).
        AssertBareFlag(args, "--skip-splash", present: true);
        AssertPrecedesSeparator(args, "--skip-splash");
        Assert.DoesNotContain("Z:", args[IndexOf(args, "--skip-splash")]);
    }

    [Fact]
    public void Profile_without_skip_splash_omits_flag()
    {
        // Default LaunchSettings: SkipSplash is false, so no --skip-splash.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.DoesNotContain("--skip-splash", fx.Launcher.Arguments!);
    }

    [Fact]
    public void Both_bare_flags_precede_separator_when_both_toggles_on()
    {
        // With both toggles on, each bare flag is present and precedes the --
        // separator (game args follow --). The relative order of the two bare
        // flags is not a Relay contract, so it is not asserted. One platform
        // is enough (the signature is shared).
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnableLuaLogs = true,
            SkipSplash = true,
            GameArguments = new[] { "-g" },
        };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var luaIndex = IndexOf(args, "--log-lua");
        var skipIndex = IndexOf(args, "--skip-splash");
        var sepIndex = IndexOf(args, "--");
        Assert.True(luaIndex >= 0, "expected --log-lua to be present");
        Assert.True(skipIndex >= 0, "expected --skip-splash to be present");
        Assert.True(sepIndex > luaIndex, "expected --log-lua to precede the -- separator");
        Assert.True(sepIndex > skipIndex, "expected --skip-splash to precede the -- separator");
        Assert.Equal("-g", args[^1]);
    }

    [Fact]
    public void Linux_request_contains_profile_env_before_proton_startup()
    {
        // Profile env values must reach Proton's environment. They land in the
        // request's EnvironmentOverrides, applied by the launcher onto the
        // ProcessStartInfo.Environment snapshot before the proton process starts.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("DXVK_HUD", "fps"),
            },
        };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment!;
        Assert.Equal("1", env["PROTON_LOG"]);
        Assert.Equal("fps", env["DXVK_HUD"]);
    }

    [Fact]
    public void Linux_still_removes_all_appimage_identity_variables_with_profile_env_present()
    {
        // The five AppImage/desktop-identity removals hold alongside profile env.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("PROTON_LOG", "1") },
        };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var expectedRemovals = new HashSet<string>(StringComparer.Ordinal)
        {
            "APPDIR", "APPIMAGE", "ARGV0", "OWD", "BAMF_DESKTOP_FILE_HINT",
        };
        Assert.True(
            expectedRemovals.SetEquals(fx.Launcher.RemovedVariables),
            "expected exactly the five AppImage/desktop-identity variables to be removed");
    }

    [Fact]
    public void Linux_still_applies_steam_compat_with_profile_env_present()
    {
        // Curator-owned STEAM_COMPAT_* win: they are layered AFTER profile env,
        // so even a profile env with the same key (blocked by validation in
        // practice) would be overridden. The two compat vars are present with
        // the discovery values, alongside the profile env.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("MY_VAR", "kept"),
            },
        };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment!;
        Assert.Equal(FakeDiscovery.LinuxCompatdata, env["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(FakeDiscovery.LinuxSteam, env["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
        Assert.Equal("1", env["PROTON_LOG"]);
        Assert.Equal("kept", env["MY_VAR"]);
    }

    [Fact]
    public void Windows_request_contains_profile_env_as_overrides()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("MY_VAR", "win-value"),
                new EnvVar("DXVK_HUD", "fps"),
            },
        };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment!;
        Assert.Equal("win-value", env["MY_VAR"]);
        Assert.Equal("fps", env["DXVK_HUD"]);
        // No Steam-compat vars on Windows, no removals (unchanged).
        Assert.False(env.ContainsKey("STEAM_COMPAT_DATA_PATH"));
        Assert.Empty(fx.Launcher.RemovedVariables);
    }

    [Fact]
    public void Windows_request_has_no_env_overrides_when_profile_env_is_empty()
    {
        // Preserves the legacy Windows path: an empty profile env yields no
        // environment overrides on the Relay process (the child inherits the
        // parent env verbatim).
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.Empty(fx.Launcher.Environment!);
    }

    [Fact]
    public void Linux_launch_with_no_settings_launches_as_before()
    {
        // A profile with empty settings launches exactly as the pre-launch-
        // settings path: no profile env beyond STEAM_COMPAT_*, no game args, no --.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment!;
        Assert.Equal(2, env.Count); // only the two STEAM_COMPAT_* overrides
        Assert.DoesNotContain("--", fx.Launcher.Arguments!);
    }

    // ---- Profile integration ------------------------------------------------

    // ---- Error ---------------------------------------------------------------

    [Fact]
    public void Launch_returns_StagingFailed_when_PrepareModRoot_throws()
    {
        // A staging-link creation failure propagates the raised built-in
        // exception from PrepareModRoot (the junction path throws Win32Exception
        // on Windows; the symlink path throws IOException natively; here the fake
        // throws IOException). Launch maps it to StagingFailed, carrying the
        // exception's body on Message (surfaced after the localized framing in
        // the UI), with an empty missing-fields list.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Profiles.PrepareModRootThrows = true;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.StagingFailed, result.Status);
        Assert.Equal("simulated staging-link failure", result.Message);
        Assert.Empty(result.MissingDiscoveryFields);
        Assert.Equal(1, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(0, fx.Launcher.Calls); // never spawned
    }

    [Fact]
    public void Error_unknown_profile_returns_error_not_thrown()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux; // discovery OK, but profile unknown
        fx.Profiles.UnknownProfile = true;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains(profileId.ToString(), result.Message);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void Error_missing_runtime_launcher_returns_error()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.DeleteLauncher(); // Relay not deployed
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains("mod_relay.exe", result.Message);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void Error_process_start_failure_returns_error()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.Launcher.Returns = false; // process.Start failed (file missing, perms, etc.)
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        // Message unchanged from the null-spawn mapping.
        Assert.Equal($"Failed to start the Relay launcher at '{fx.LauncherPath}'.", result.Message);
        Assert.Equal(1, fx.Launcher.Calls); // it tried, but Start returned null
    }

    [Fact]
    public void Error_result_carries_empty_missing_fields()
    {
        // Error (not DiscoveryIncomplete) must always carry an empty missing-fields list.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        fx.DeleteLauncher();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Empty(result.MissingDiscoveryFields);
    }

    // ---- Game-dir hosting ----------------------------------------------------

    [Fact]
    public void Hosting_passes_the_game_dir_as_mod_path_and_ensures_hosting()
    {
        // The default: --mod-path is the derived GAME_DIR (the parent of the
        // hosted mods tree), and the game-dir host ran the ladder for exactly
        // that dir + the staged root.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        const string PreparedRoot = @"C:\curator\profiles\abc\staged";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var ensure = Assert.Single(fx.GameDirHost.EnsureCalls);
        Assert.Equal(fx.GameDir, ensure.GameDir);
        Assert.Equal(PreparedRoot, ensure.StagedRoot);
        Assert.Empty(fx.GameDirHost.RemoveOwnedLinkCalls);

        var args = fx.Launcher.Arguments!;
        Assert.Equal(fx.GameDir, args[IndexOf(args, "--mod-path") + 1]);
    }

    [Fact]
    public void External_mode_passes_the_staged_root_and_removes_the_owned_link()
    {
        // The opt-out restores the staging-only launch AND cleans up a
        // Curator-owned game-dir link, without ever consulting the ladder for
        // hosting.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        const string PreparedRoot = @"C:\curator\profiles\abc\staged";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        fx.Config.Preferences.ExternalModHosting = true;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.Empty(fx.GameDirHost.EnsureCalls);
        Assert.Equal(fx.GameDir, Assert.Single(fx.GameDirHost.RemoveOwnedLinkCalls));

        var args = fx.Launcher.Arguments!;
        Assert.Equal(PreparedRoot, args[IndexOf(args, "--mod-path") + 1]);
    }

    [Fact]
    public void External_mode_preference_is_read_live_per_launch()
    {
        // Flipping the preference between launches flips the mod-path source,
        // exactly like the other launch-affecting preferences.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        const string PreparedRoot = @"C:\curator\profiles\abc\staged";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());
        Assert.Single(fx.GameDirHost.EnsureCalls);
        Assert.Empty(fx.GameDirHost.RemoveOwnedLinkCalls);

        fx.Config.Preferences.ExternalModHosting = true;
        svc.Launch(Guid.NewGuid());
        Assert.Single(fx.GameDirHost.RemoveOwnedLinkCalls);

        var args = fx.Launcher.Arguments!;
        Assert.Equal(PreparedRoot, args[IndexOf(args, "--mod-path") + 1]);
    }

    [Fact]
    public void Foreign_game_dir_mods_returns_GameDirConflict_before_spawning()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.GameDirHost.NextResult = new GameDirHostingResult(
            GameDirHostingOutcome.Conflict,
            Path.Combine(fx.GameDir, "mods"));
        var profileId = Guid.NewGuid();
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.GameDirConflict, result.Status);
        // The message carries the detected path (for the localized consent
        // prompt); GameDirPath carries the dir for the consented takeover.
        Assert.Equal(Path.Combine(fx.GameDir, "mods"), result.Message);
        Assert.Equal(fx.GameDir, result.GameDirPath);
        Assert.Empty(result.MissingDiscoveryFields);
        Assert.Null(result.RelayExited);
        Assert.Equal(0, fx.Launcher.Calls); // never spawned, nothing mutated further
    }

    [Fact]
    public void Hosting_link_failure_returns_Error_with_the_exception_message()
    {
        // Link IO/Win32 failures map to Error, carrying the raised built-in
        // exception's body (a runtime/OS error, not a string we invented).
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows;
        fx.GameDirHost.EnsureThrows = new IOException("simulated link failure");
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains("simulated link failure", result.Message);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void Derived_game_dir_that_does_not_exist_returns_Error()
    {
        // dirname(dirname(binary)) is validated to exist before any hosting
        // mutation.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows with
        {
            DarktideGameBinaryPath = Path.Combine(fx.TempRoot, "missing-game", "binaries", "Darktide.exe"),
        };
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains("game directory", result.Message);
        Assert.Empty(fx.GameDirHost.EnsureCalls);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void External_mode_skips_removal_when_the_game_dir_does_not_exist()
    {
        // Best-effort cleanup: with no derivable game dir there is nothing to
        // remove, and the staging-only launch still proceeds.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteWindows with
        {
            DarktideGameBinaryPath = Path.Combine(fx.TempRoot, "missing-game", "binaries", "Darktide.exe"),
        };
        const string PreparedRoot = @"C:\curator\profiles\abc\staged";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        fx.Config.Preferences.ExternalModHosting = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.Empty(fx.GameDirHost.RemoveOwnedLinkCalls);
        var args = fx.Launcher.Arguments!;
        Assert.Equal(PreparedRoot, args[IndexOf(args, "--mod-path") + 1]);
    }

    [Fact]
    public void DeriveGameDir_returns_the_binary_grandparent_directory()
    {
        // Use the running platform's separator: GetDirectoryName only splits
        // on the platform separators, and the derivation is exercised through
        // real paths on both CI OSes.
        var sep = Path.DirectorySeparatorChar;
        var expected = $"{sep}games{sep}DARKTIDE";
        Assert.Equal(expected, RelayLaunchService.DeriveGameDir($"{expected}{sep}binaries{sep}Darktide.exe"));
    }

    [Fact]
    public void DeriveGameDir_returns_null_for_a_path_with_no_grandparent()
    {
        Assert.Null(RelayLaunchService.DeriveGameDir("Darktide.exe"));
        Assert.Null(RelayLaunchService.DeriveGameDir($"{Path.DirectorySeparatorChar}Darktide.exe"));
    }

    [Fact]
    public void Real_host_end_to_end_creates_the_link_and_launches_from_the_game_dir()
    {
        // The real host against the real platform link primitive: the launch
        // creates <game>/mods -> <staged>/mods and hands Relay the game dir.
        using var fx = new RelayFixture();
        fx.Steam.Result = fx.CompleteLinux;
        var stagedRoot = Path.Combine(fx.TempRoot, "profiles", "abc", "staged");
        var stagedMods = Path.Combine(stagedRoot, "mods");
        Directory.CreateDirectory(stagedMods);
        fx.Profiles.PrepareModRootResult = stagedRoot;
        fx.Profiles.ProfilesRoot = Path.Combine(fx.TempRoot, "profiles");
        var host = new GameDirModsHost(
            CreatePlatformLink(),
            fx.Profiles,
            new AppStateStore(Path.Combine(fx.TempRoot, "app-state.json")),
            NullLogger<GameDirModsHost>.Instance);
        var svc = fx.BuildService(new LinuxLaunchStrategy(fx.Launcher, NullLogger<LinuxLaunchStrategy>.Instance), host);

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        var link = Path.Combine(fx.GameDir, "mods");
        Assert.True(Directory.Exists(link));
        var resolved = new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: false);
        Assert.NotNull(resolved);
        Assert.True(string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved!.FullName)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedMods)),
            StringComparison.Ordinal));

        var args = fx.Launcher.Arguments!;
        var launcherFlags = args.Skip(2).ToList();
        Assert.Equal(WinePath.ToWine(fx.GameDir), launcherFlags[IndexOf(launcherFlags, "--mod-path") + 1]);
    }

    /// <summary>
    /// Resolves the real platform-selective staging-link creator through the
    /// Profiles DI registration (junction on Windows, symlink on Linux), so
    /// the host tests exercise the same primitive production wires.
    /// </summary>
    private static StagingLinkCreator CreatePlatformLink()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddProfiles();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<StagingLinkCreator>();
    }

    /// <summary>
    /// Ordinal index-of for <see cref="IReadOnlyList{T}"/> (no IndexOf on that
    /// interface; Array.IndexOf needs an Array). Used to locate flag positions.
    /// </summary>
    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    // ---- relational argv-flag helpers --------------------------------------
    // Assert Relay's bare-flag contract without pinning absolute argv layout:
    // the flag is present exactly once when its toggle is on, and it precedes
    // the -- separator. No fixed indices, no adjacency to other flags.

    // A bare (value-less) flag is present exactly once when on, absent when off.
    private static void AssertBareFlag(IReadOnlyList<string> args, string flag, bool present)
    {
        if (present) Assert.Single(args, a => a == flag);
        else Assert.DoesNotContain(flag, args);
    }

    // A Relay flag precedes the -- separator (Relay contract #1). No-op when the separator is absent.
    private static void AssertPrecedesSeparator(IReadOnlyList<string> args, string flag)
    {
        var i = IndexOf(args, flag);
        var sep = IndexOf(args, "--");
        Assert.True(i >= 0, $"expected flag {flag} to be present");
        Assert.True(sep < 0 || i < sep, $"expected {flag} to precede the -- separator");
    }
}
