using System.IO;
using Modificus.Curator.Config;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>: discovery fields are pre-filled
/// from config + written back via read-modify-save, and the two Storage
/// section commands open the OS file manager at the Curator data root +
/// profiles root (or surface a failure alert) via the injectable launcher
/// seam.
/// </summary>
public sealed class SettingsViewModelTests
{
    private static readonly ILogger<SettingsViewModel> Logger = NullLogger<SettingsViewModel>.Instance;
    private static readonly LocalizationService Localization = new();

    /// <summary>Builds a VM wired to the supplied (or default) fakes.</summary>
    private static (SettingsViewModel vm, FakeConfigLoader loader, FakeDialogService dialogs) Build(
        DiscoveryConfig? discovery = null,
        string? profilesBaseFolder = null,
        FakeAppUpdateService? appUpdate = null,
        FakeDialogService? dialogs = null,
        Func<string, bool>? launchExternalPath = null)
    {
        var config = CuratorConfig.CreateDefault();
        if (discovery is not null) config.Discovery = discovery;
        if (profilesBaseFolder is not null) config.ProfilesBaseFolder = profilesBaseFolder;
        var loader = new FakeConfigLoader { Config = config };
        dialogs ??= new FakeDialogService();
        var vm = new SettingsViewModel(
            loader, Localization,
            appUpdate ?? new FakeAppUpdateService(),
            dialogs,
            invokeOnUi: static action => action(),
            Logger,
            launchExternalPath);
        return (vm, loader, dialogs);
    }

    private static DiscoveryFieldRowViewModel Row(SettingsViewModel vm, string fieldName) =>
        vm.DiscoveryRows.First(r => r.Field.FieldName == fieldName);

    // ---- pre-fill from config --------------------------------------------

    [Fact]
    public void Discovery_rows_are_pre_filled_from_config()
    {
        var discovery = new DiscoveryConfig
        {
            UserSteamInstallPath = "/steam",
            UserDarktideGameBinaryPath = "/darktide.exe",
            UserCompatdataPath = "/compat",
            UserProtonBinaryPath = "/proton",
        };

        var (vm, _, _) = Build(discovery);

        // Steam + Darktide rows always render.
        Assert.Equal("/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        // Compatdata + Proton rows render on Linux only (platform-gated).
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/compat", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/proton", Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_are_empty_when_overrides_are_unset()
    {
        // All-null discovery (the default): every rendered row's TextBox starts
        // empty.
        var (vm, _, _) = Build();

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
        Assert.Equal(string.Empty, Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(string.Empty, Row(vm, "CompatdataPath").Value);
            Assert.Equal(string.Empty, Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_are_pre_filled_after_startup_Discover_populates_config()
    {
        // End-to-end (no real Steam layout, just the persistence contract): the
        // startup Discover populates the persisted overrides (simulating the
        // composition root's startup call writing the healed values), and the
        // Settings VM, which reads the live config, shows them rather than
        // blanks. This is the "Settings shows non-blank fields" guarantee from
        // the validate + heal + persist pipeline (Track C review fix).
        var loader = new FakeConfigLoader { Config = CuratorConfig.CreateDefault() };

        // Simulate the startup Discover having healed every field + persisted it
        // (a single Save carrying the four writes). The persisted values are
        // what the Settings VM should show.
        var healed = new DiscoveryConfig
        {
            UserSteamInstallPath = "/resolved/steam",
            UserDarktideGameBinaryPath = "/resolved/darktide.exe",
            UserCompatdataPath = "/resolved/compatdata",
            UserProtonBinaryPath = "/resolved/proton",
        };
        var persisted = CuratorConfig.CreateDefault();
        persisted.Discovery = healed;
        loader.Save(persisted);

        var vm = new SettingsViewModel(loader, Localization,
            new FakeAppUpdateService(), new FakeDialogService(),
            invokeOnUi: static action => action(), Logger);

        Assert.Equal("/resolved/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/resolved/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/resolved/compatdata", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/resolved/proton", Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_match_the_platforms_expected_fields_in_catalog_order()
    {
        // Platform-gated: Windows renders only the Steam install + Darktide
        // binary rows (the compatdata + Proton overrides are Linux-only:
        // WindowsLaunchStrategy ignores them, so they would be silently
        // ineffective rows). Linux renders all four, in catalog order.
        var (vm, _, _) = Build();

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(4, vm.DiscoveryRows.Count);
            Assert.Equal("SteamInstallPath", vm.DiscoveryRows[0].Field.FieldName);
            Assert.Equal("DarktideGameBinaryPath", vm.DiscoveryRows[1].Field.FieldName);
            Assert.Equal("CompatdataPath", vm.DiscoveryRows[2].Field.FieldName);
            Assert.Equal("ProtonBinaryPath", vm.DiscoveryRows[3].Field.FieldName);
        }
        else
        {
            Assert.Equal(2, vm.DiscoveryRows.Count);
            Assert.Equal("SteamInstallPath", vm.DiscoveryRows[0].Field.FieldName);
            Assert.Equal("DarktideGameBinaryPath", vm.DiscoveryRows[1].Field.FieldName);
        }
    }

    // ---- write-through (discovery fields) --------------------------------

    [Fact]
    public void Editing_a_discovery_field_writes_the_override_via_read_modify_save()
    {
        var (vm, loader, _) = Build();

        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Equal("/new/steam", loader.LastSaved!.Discovery.UserSteamInstallPath);
    }

    [Fact]
    public void Clearing_a_discovery_field_writes_null_so_it_falls_back_to_auto()
    {
        var discovery = new DiscoveryConfig { UserSteamInstallPath = "/old" };
        var (vm, loader, _) = Build(discovery);

        Row(vm, "SteamInstallPath").Value = "";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Null(loader.LastSaved!.Discovery.UserSteamInstallPath);
    }

    [Fact]
    public void Initial_restore_does_not_save_config()
    {
        // Pre-filling the rows must NOT trigger a write (each value already
        // matches what is persisted; re-writing would be a noisy no-op).
        var (_, loader, _) = Build(new DiscoveryConfig { UserSteamInstallPath = "/x" });

        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void Editing_each_field_persists_progressively_via_read_modify_save()
    {
        // Each edit is its own read-modify-save: the second save picks up the
        // first edit's persisted value (FakeConfigLoader mirrors the real
        // loader's round-trip), so both overrides land.
        var (vm, loader, _) = Build();

        Row(vm, "DarktideGameBinaryPath").Value = "/darktide.exe";
        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(2, loader.SaveCalls);
        Assert.Equal("/darktide.exe", loader.LastSaved!.Discovery.UserDarktideGameBinaryPath);
        Assert.Equal("/new/steam", loader.LastSaved.Discovery.UserSteamInstallPath);
    }

    // ---- RefreshFromConfig (hosted rehydrate) ----------------------------
    //
    // The hosted VM stays alive across navigation. When the discovery escape-
    // hatch (or any other surface) writes new overrides + a new CheckOnStartup
    // to config while Settings is away, RefreshFromConfig reflects them on the
    // next visit without persisting.

    [Fact]
    public void RefreshFromConfig_reflects_external_changes_without_saving()
    {
        var (vm, loader, _) = Build();
        Assert.True(vm.CheckOnStartup); // default

        // Simulate external writes to the live config (e.g. the escape-hatch).
        var external = CuratorConfig.CreateDefault();
        external.Discovery = new DiscoveryConfig
        {
            UserSteamInstallPath = "/ext/steam",
            UserDarktideGameBinaryPath = "/ext/darktide.exe",
            UserCompatdataPath = "/ext/compat",
            UserProtonBinaryPath = "/ext/proton",
        };
        external.AppUpdates.CheckOnStartup = false;
        loader.Config = external;

        vm.RefreshFromConfig();

        Assert.Equal("/ext/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/ext/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        // Compatdata + Proton are platform-gated rows; assert only on Linux.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/ext/compat", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/ext/proton", Row(vm, "ProtonBinaryPath").Value);
        }
        Assert.False(vm.CheckOnStartup);
        // Zero saves: rehydrating from config must not write back.
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_preserves_the_same_row_object_instances()
    {
        // Rehydrating must update the existing rows' values in place rather than
        // replacing the collection (which would re-bind + lose any per-row view
        // state on a hosted page).
        var (vm, loader, _) = Build();
        var before = vm.DiscoveryRows.ToArray();

        var external = CuratorConfig.CreateDefault();
        external.Discovery = new DiscoveryConfig { UserSteamInstallPath = "/new/steam" };
        loader.Config = external;

        vm.RefreshFromConfig();

        // Same instances, same order (reference equality).
        Assert.True(before.SequenceEqual(vm.DiscoveryRows));
        Assert.Equal("/new/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_is_safe_and_repeatable()
    {
        // Idempotent + repeatable: calling it multiple times writes nothing.
        var (vm, loader, _) = Build();

        vm.RefreshFromConfig();
        vm.RefreshFromConfig();
        vm.RefreshFromConfig();

        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_does_not_save_when_clearing_an_override()
    {
        // Restoring an empty value (a cleared override) must not save either:
        // the suppress guard covers the row callback regardless of direction.
        var (vm, loader, _) = Build(new DiscoveryConfig { UserSteamInstallPath = "/old" });
        Assert.Equal("/old", Row(vm, "SteamInstallPath").Value);

        loader.Config = CuratorConfig.CreateDefault(); // all overrides cleared

        vm.RefreshFromConfig();

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
        Assert.Equal(0, loader.SaveCalls);
    }

    // ---- Storage: Open Data Folder ---------------------------------------
    //
    // OpenDataFolder targets a static path (AppPaths.AppDataDir, the Curator
    // data root containing mods/, profiles/, logs/, config.json), so the
    // empty-path and missing-dir no-op cases that applied to the old
    // config-driven command don't carry over: the path is never empty, and on
    // a real Curator install the data root exists. The remaining cases: the
    // seam is invoked with the exact AppDataDir, and the two failure alerts
    // (false return + throw). The data root is ensured to exist for
    // deterministic test ordering (Curator's own fixtures create subdirs under
    // it, but a clean host might not have it yet).

    [Fact]
    public async Task OpenDataFolder_calls_the_seam_with_AppPaths_AppDataDir()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        string? received = null;
        var (vm, _, dialogs) = Build(
            launchExternalPath: p => { received = p; return true; });

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(AppPaths.AppDataDir, received);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenDataFolder_alerts_when_the_launcher_returns_false()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var (vm, _, dialogs) = Build(launchExternalPath: _ => false);

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(AppPaths.AppDataDir, alert.Message);
    }

    [Fact]
    public async Task OpenDataFolder_alerts_and_does_not_propagate_when_the_launcher_throws()
    {
        // The launcher seam's default only catches a narrow exception set; a VM
        // that calls it must not let an unexpected throw escape to the UI. The
        // command catches, logs, and surfaces the failure alert instead.
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var (vm, _, dialogs) = Build(
            launchExternalPath: _ => throw new InvalidOperationException("boom"));

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(AppPaths.AppDataDir, alert.Message);
    }

    // ---- Storage: Open Profiles Folder -----------------------------------

    [Fact]
    public async Task OpenProfilesFolder_is_a_no_op_when_ProfilesBaseFolder_is_empty()
    {
        var called = false;
        var (vm, _, dialogs) = Build(
            profilesBaseFolder: "",
            launchExternalPath: _ => called = true);

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenProfilesFolder_is_a_no_op_when_the_directory_does_not_exist()
    {
        var called = false;
        var (vm, _, dialogs) = Build(
            profilesBaseFolder: Path.Combine(Path.GetTempPath(), "curator-does-not-exist-" + Guid.NewGuid()),
            launchExternalPath: _ => called = true);

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenProfilesFolder_launches_the_seam_with_the_current_path()
    {
        string? received = null;
        var (vm, _, dialogs) = Build(
            profilesBaseFolder: Path.GetTempPath(),
            launchExternalPath: p => { received = p; return true; });

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        Assert.Equal(Path.GetTempPath(), received);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenProfilesFolder_alerts_when_the_launcher_returns_false()
    {
        var (vm, _, dialogs) = Build(
            profilesBaseFolder: Path.GetTempPath(),
            launchExternalPath: _ => false);

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(Path.GetTempPath(), alert.Message);
    }

    [Fact]
    public async Task OpenProfilesFolder_alerts_and_does_not_propagate_when_the_launcher_throws()
    {
        var (vm, _, dialogs) = Build(
            profilesBaseFolder: Path.GetTempPath(),
            launchExternalPath: _ => throw new InvalidOperationException("boom"));

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(Path.GetTempPath(), alert.Message);
    }
}
