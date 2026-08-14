using Modificus.Curator.Config;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// <see cref="ISteamService.Discover"/> + <see cref="ISteamService.Rediscover"/>
/// behavior under the automatic/manual discovery mode. Automatic mode runs the
/// discoverer every call and atomically replaces the active-platform snapshot
/// (including nulls that clear stale values). Manual mode validates stored paths
/// without invoking the discoverer and never rewrites the stored input.
/// Rediscover forces one automatic pass regardless of the mode and preserves the
/// mode bool.
/// </summary>
public sealed class SteamServiceOverlayTests
{
    // ---- automatic mode (default): runs discoverer + persists snapshot --------

    [Fact]
    public void Automatic_mode_runs_discoverer_and_persists_full_snapshot()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);

        // The snapshot is persisted (one save carrying all four active fields).
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), fx.Config.Discovery.CompatdataPath);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), fx.Config.Discovery.ProtonBinaryPath);
    }

    [Fact]
    public void Automatic_mode_clears_stale_unresolved_values()
    {
        // Config carries a stale Proton path; the discoverer cannot resolve
        // Proton (no mapping). The stale value is replaced with null so the
        // snapshot reflects reality.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No compat-tool mapping -> Proton unresolved.
        fx.Config.Discovery.ProtonBinaryPath = "/stale/proton";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        // The stale value was replaced with null.
        Assert.Null(fx.Config.Discovery.ProtonBinaryPath);
    }

    [Fact]
    public void Automatic_mode_skips_save_when_snapshot_is_unchanged()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        // First call persists the snapshot.
        fx.Service.Discover();
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);

        // Second call produces the same result -> no new save.
        fx.Service.Discover();
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
    }

    [Fact]
    public void Automatic_mode_follows_changed_discovery_output()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var before = fx.Service.Discover();
        Assert.Equal(fx.SteamRoot, before.SteamInstallPath);

        // Add a second library and move Darktide there. The next automatic pass
        // must reflect the new Darktide path.
        var secondary = Path.Combine(fx.TempRoot, "secondary-lib");
        Directory.CreateDirectory(secondary);
        Directory.Delete(Path.Combine(fx.SteamRoot, "steamapps", "common"), recursive: true);
        fx.WithLibraryFoldersAtSteamRoot(fx.SteamRoot, secondary);
        fx.WithDarktide(secondary);

        var after = fx.Service.Discover();

        Assert.Equal(fx.ExpectedDarktidePath(secondary), after.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedDarktidePath(secondary), fx.Config.Discovery.DarktideGameBinaryPath);
    }

    [Fact]
    public void No_steam_at_all_yields_Failed_and_persists_nulls()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        // Nothing scaffolded: discoverer yields Failed with every field null.
        fx.Config.Discovery.SteamInstallPath = "/stale/steam";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.SteamInstallPath);
        // The stale value was replaced with null.
        Assert.Null(fx.Config.Discovery.SteamInstallPath);
    }

    // ---- platform-gating: Windows writes only Steam + Darktide ----------------

    [Fact]
    public void Windows_writes_only_steam_and_darktide_and_preserves_linux_fields()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        // Leftover Linux values from a prior run: Windows must not touch them.
        fx.Config.Discovery.CompatdataPath = "/leftover/compatdata";
        fx.Config.Discovery.ProtonBinaryPath = "/leftover/proton";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        // The leftover Linux values are preserved.
        Assert.Equal("/leftover/compatdata", fx.Config.Discovery.CompatdataPath);
        Assert.Equal("/leftover/proton", fx.Config.Discovery.ProtonBinaryPath);
        // Steam + Darktide were persisted.
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.DarktideGameBinaryPath);
    }

    // ---- manual mode: validates stored paths, no discoverer, no rewrite --------

    [Fact]
    public void Manual_mode_validates_stored_paths_and_skips_discoverer()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // A real proton on disk so the manual validation passes.
        var protonPath = Path.Combine(fx.TempRoot, "manual-proton");
        Directory.CreateDirectory(Path.GetDirectoryName(protonPath)!);
        File.WriteAllText(protonPath, string.Empty);

        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.DarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);
        fx.Config.Discovery.CompatdataPath = fx.ExpectedCompatdataPath(fx.SteamRoot);
        fx.Config.Discovery.ProtonBinaryPath = protonPath;

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(protonPath, result.ProtonBinaryPath);
        // No discoverer work means no save.
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
        // ProtonVersion is null in manual mode.
        Assert.Null(result.ProtonVersion);
    }

    [Fact]
    public void Manual_mode_invalid_paths_return_null_without_rewriting_stored_input()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = "/does/not/exist/steam";
        fx.Config.Discovery.DarktideGameBinaryPath = "/does/not/exist/darktide.exe";
        fx.Config.Discovery.CompatdataPath = "/does/not/exist/compatdata";
        fx.Config.Discovery.ProtonBinaryPath = "/does/not/exist/proton";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.SteamInstallPath);
        Assert.Null(result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        // The stored input is untouched: manual mode never rewrites.
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
        Assert.Equal("/does/not/exist/steam", fx.Config.Discovery.SteamInstallPath);
        Assert.Equal("/does/not/exist/darktide.exe", fx.Config.Discovery.DarktideGameBinaryPath);
        Assert.Equal("/does/not/exist/compatdata", fx.Config.Discovery.CompatdataPath);
        Assert.Equal("/does/not/exist/proton", fx.Config.Discovery.ProtonBinaryPath);
    }

    [Fact]
    public void Manual_mode_wrong_path_kind_is_invalid()
    {
        // A directory where a file is expected (Darktide) and a file where a
        // directory is expected (Steam): both fail validation.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        Directory.CreateDirectory(fx.SteamRoot);
        var fakeExeDir = Path.Combine(fx.TempRoot, "darktide-dir");
        Directory.CreateDirectory(fakeExeDir);

        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = fx.TempRoot; // valid dir
        fx.Config.Discovery.DarktideGameBinaryPath = fx.SteamRoot; // a directory, not a file

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Equal(fx.TempRoot, result.SteamInstallPath);
        Assert.Null(result.DarktideGameBinaryPath);
    }

    [Fact]
    public void Manual_mode_windows_skips_compatdata_and_proton()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.DarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);
        // Linux-only fields ignored on Windows.
        fx.Config.Discovery.CompatdataPath = "/whatever";
        fx.Config.Discovery.ProtonBinaryPath = "/whatever";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
    }

    // ---- manual mode: Windows does not require Steam (Darktide binary alone) --

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Manual_mode_windows_valid_darktide_with_absent_or_blank_steam_yields_complete(string? storedSteam)
    {
        // Windows launches from the Darktide binary alone; Steam is a discovery
        // mechanism, not a launch input. A null/blank Steam path must not block a
        // launch on Windows.
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = storedSteam;
        fx.Config.Discovery.DarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        // Nothing valid to return for Steam -> null in the result.
        Assert.Null(result.SteamInstallPath);
        // Manual mode never invokes the discoverer + never writes config.
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
    }

    [Fact]
    public void Manual_mode_windows_invalid_steam_string_yields_complete_and_preserves_input()
    {
        // A nonexistent Steam path is validated null in the result, but the stored
        // string is never rewritten (manual mode leaves the user's input intact for
        // correction). Darktide alone still yields Complete on Windows.
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        var invalidSteam = Path.Combine(fx.TempRoot, "does-not-exist-steam");
        fx.Config.Discovery.SteamInstallPath = invalidSteam;
        fx.Config.Discovery.DarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Null(result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
        // The invalid string is preserved exactly as typed.
        Assert.Equal(invalidSteam, fx.Config.Discovery.SteamInstallPath);
    }

    [Fact]
    public void Manual_mode_windows_wrong_kind_steam_yields_complete()
    {
        // A file where a directory is expected (Steam) is invalid, but the Darktide
        // binary alone still yields Complete on Windows. The Steam result field is
        // null while the stored input is preserved.
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        var steamAsFile = Path.Combine(fx.TempRoot, "steam-as-a-file");
        File.WriteAllText(steamAsFile, string.Empty);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = steamAsFile;
        fx.Config.Discovery.DarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Null(result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
        Assert.Equal(steamAsFile, fx.Config.Discovery.SteamInstallPath);
    }

    [Fact]
    public void Manual_mode_windows_no_darktide_and_no_steam_yields_failed()
    {
        // Without the Darktide binary, Windows has nothing to launch. With Steam
        // also absent, the status is Failed (nothing resolved).
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = null;
        fx.Config.Discovery.DarktideGameBinaryPath = null;

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.DarktideGameBinaryPath);
        Assert.Null(result.SteamInstallPath);
    }

    [Fact]
    public void Manual_mode_windows_no_darktide_but_steam_present_yields_partial()
    {
        // Darktide missing with Steam present: Partial (Steam resolved but the
        // launch-critical Darktide binary is missing).
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.DarktideGameBinaryPath = null;

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.DarktideGameBinaryPath);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
    }

    // ---- rediscover: forced automatic, mode preserved -------------------------

    [Fact]
    public void Rediscover_forces_automatic_pass_even_in_manual_mode()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        fx.Config.Discovery.OverrideAutomaticDiscovery = true;

        var result = fx.Service.Rediscover();

        // The discoverer ran (Complete with resolved paths), despite manual mode.
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
        // The mode bool is preserved.
        Assert.True(fx.Config.Discovery.OverrideAutomaticDiscovery);
    }

    [Fact]
    public void Rediscover_replaces_active_fields_including_nulls()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No compat-tool mapping -> Proton unresolved.
        fx.Config.Discovery.ProtonBinaryPath = "/stale/proton";

        var result = fx.Service.Rediscover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Null(fx.Config.Discovery.ProtonBinaryPath);
    }

    // ---- live-read: a Settings write between calls is visible ----------------

    [Fact]
    public void Discover_re_reads_config_so_a_mode_change_between_calls_is_visible()
    {
        // First call: automatic mode resolves everything from the discoverer.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var auto = fx.Service.Discover();
        Assert.Equal(DiscoveryStatus.Complete, auto.Status);

        // Flip to manual mode + set a bogus Steam path. The next call must
        // honor the mode change (no discoverer) + the bogus path validates null.
        fx.Config.Discovery.OverrideAutomaticDiscovery = true;
        fx.Config.Discovery.SteamInstallPath = "/manual/bogus";

        var manual = fx.Service.Discover();
        Assert.Equal(DiscoveryStatus.Failed, manual.Status);
        Assert.Null(manual.SteamInstallPath);
    }
}
