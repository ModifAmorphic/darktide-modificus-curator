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
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
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

        Assert.Equal(
            new[] { "--game-binary", FakeDiscovery.WindowsGameBinary,
                    "--mod-path",    @"C:\curator\profiles\abc\staged",
                    "--log-file",    fx.Config.Logging.LogFile },
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
        // (no Z:\ prefix) -- translation is a Linux-only concern.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        const string LogFile = @"C:\curator\logs\curator.log";
        fx.Config.Logging.LogFile = LogFile;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var game = args[IndexOf(args, "--game-binary") + 1];
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(FakeDiscovery.WindowsGameBinary, game);
        Assert.Equal(LogFile, log);
        Assert.DoesNotContain("Z:", game);
        Assert.DoesNotContain("Z:", log);
    }

    [Fact]
    public void Launch_uses_the_bootstrap_resolved_log_file_path()
    {
        // Pins "Relay gets the resolved, process-pinned path": when the bootstrap
        // has resolved a log file, the spawned --log-file arg is that path, not
        // the raw config.Logging.LogFile template. Uses the Windows strategy so
        // the path passes through verbatim (no Z:\ translation).
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        const string Resolved = @"C:\resolved\curator-xyz.log";
        var previous = LoggingBootstrap.CurrentLogFile;
        LoggingBootstrap.CurrentLogFile = Resolved;
        try
        {
            var svc = fx.BuildWindowsService();

            svc.Launch(Guid.NewGuid());

            var args = fx.Launcher.Arguments!;
            var log = args[IndexOf(args, "--log-file") + 1];
            Assert.Equal(Resolved, log);
            Assert.NotEqual(fx.Config.Logging.LogFile, log);
        }
        finally
        {
            LoggingBootstrap.CurrentLogFile = previous;
        }
    }

    [Fact]
    public void Windows_launch_returns_launched_when_process_starts()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        fx.Launcher.Returns = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.Null(result.Message);
    }

    // ---- Linux --------------------------------------------------------------

    [Fact]
    public void Linux_translates_mod_path_and_game_binary_to_wine_paths()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Profiles.PrepareModRootResult = "/home/u/.local/share/Modificus Curator/profiles/abc/staged";
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // The launcher's own flags start after "run" + launcherPath.
        var launcherFlags = args.Skip(2).ToList();

        var game = launcherFlags[IndexOf(launcherFlags, "--game-binary") + 1];
        var mod = launcherFlags[IndexOf(launcherFlags, "--mod-path") + 1];

        Assert.Equal(@"Z:\home\u\.local\share\Modificus Curator\profiles\abc\staged", mod);
        Assert.Equal(
            @"Z:\home\u\.steam\steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe",
            game);
    }

    [Fact]
    public void Linux_translates_log_file_to_wine_path()
    {
        // The launcher runs under Wine and opens --log-file itself, so it must
        // be Z:\-translated on Linux (else the Relay shell log can't be written
        // where Curator expects).
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        const string LogFile = "/home/u/.local/share/Modificus Curator/logs/curator.log";
        fx.Config.Logging.LogFile = LogFile;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(@"Z:\home\u\.local\share\Modificus Curator\logs\curator.log", log);
    }

    [Fact]
    public void Linux_sets_both_steam_compat_env_vars_from_discovery()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Launcher.Returns = true;
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
    }

    // ---- DiscoveryIncomplete ------------------------------------------------

    [Fact]
    public void DiscoveryIncomplete_linux_partial_returns_missing_field_names()
    {
        // Steam + Darktide found, but compatdata + Proton missing on Linux.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux with
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
        fx.Steam.Result = FakeDiscovery.CompleteWindows with
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

    // ---- Profile integration ------------------------------------------------

    [Fact]
    public void Launch_calls_PrepareModRoot_with_profile_id_before_invoking()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        const string PreparedRoot = "/tmp/prepared-mod-root";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
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
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
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
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { EnableLuaLogs = true };
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        Assert.Contains("--lua-logs", args);
        // --lua-logs sits immediately after the --log-file value.
        var logIndex = IndexOf(args, "--log-file");
        Assert.Equal("--lua-logs", args[logIndex + 2]);
    }

    [Fact]
    public void Linux_profile_with_enable_lua_logs_emits_flag()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Profiles.LaunchSettingsResult = new LaunchSettings { EnableLuaLogs = true };
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // Exactly once; the bare flag is not Z:\-translated (only path-valued
        // flags are).
        var lua = Assert.Single(args, a => a == "--lua-logs");
        Assert.Equal("--lua-logs", lua);
        Assert.DoesNotContain("Z:", lua);
        // Sits immediately after the --log-file value (which IS Z:\-translated).
        var logIndex = IndexOf(args, "--log-file");
        Assert.Equal("--lua-logs", args[logIndex + 2]);
    }

    [Fact]
    public void Profile_without_enable_lua_logs_omits_flag()
    {
        // Default LaunchSettings: EnableLuaLogs is false, so no --lua-logs.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        Assert.DoesNotContain("--lua-logs", fx.Launcher.Arguments!);
    }

    [Fact]
    public void Linux_request_contains_profile_env_before_proton_startup()
    {
        // Profile env values must reach Proton's environment. They land in the
        // request's EnvironmentOverrides, applied by the launcher onto the
        // ProcessStartInfo.Environment snapshot before the proton process starts.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
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
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux; // discovery OK, but profile unknown
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
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
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Launcher.Returns = false; // process.Start failed (file missing, perms, etc.)
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal(1, fx.Launcher.Calls); // it tried, but Start returned false
    }

    [Fact]
    public void Error_result_carries_empty_missing_fields()
    {
        // Error (not DiscoveryIncomplete) must always carry an empty missing-fields list.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.DeleteLauncher();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Empty(result.MissingDiscoveryFields);
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
}
