using System.IO;
using Modificus.Curator.Config;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>: the discovery section's global
/// override mode + forced Discover, platform-gated rows + their editability,
/// manual-mode write-through, the Storage section commands, and the rehydrate
/// behavior. The app-update section has its own test file.
/// </summary>
public sealed class SettingsViewModelTests
{
    private static readonly ILogger<SettingsViewModel> Logger = NullLogger<SettingsViewModel>.Instance;
    private static readonly LocalizationService Localization = new();

    /// <summary>Builds a VM wired to the supplied (or default) fakes.</summary>
    private static (SettingsViewModel vm, FakeConfigLoader loader, FakeDialogService dialogs, FakeSteamService steam) Build(
        DiscoveryConfig? discovery = null,
        string? profilesBaseFolder = null,
        FakeAppUpdateService? appUpdate = null,
        FakeDialogService? dialogs = null,
        Func<string, bool>? launchExternalPath = null,
        FakeSteamService? steam = null)
    {
        var config = CuratorConfig.CreateDefault();
        if (discovery is not null) config.Discovery = discovery;
        if (profilesBaseFolder is not null) config.ProfilesBaseFolder = profilesBaseFolder;
        var loader = new FakeConfigLoader { Config = config };
        dialogs ??= new FakeDialogService();
        steam ??= new FakeSteamService();
        var vm = new SettingsViewModel(
            loader, steam, Localization,
            appUpdate ?? new FakeAppUpdateService(),
            dialogs,
            invokeOnUi: static action => action(),
            Logger,
            launchExternalPath);
        return (vm, loader, dialogs, steam);
    }

    private static DiscoveryConfig Manual(params Action<DiscoveryConfig>[] configure)
    {
        var d = new DiscoveryConfig { OverrideAutomaticDiscovery = true };
        foreach (var c in configure) c(d);
        return d;
    }

    private static DiscoveryFieldRowViewModel Row(SettingsViewModel vm, string fieldName) =>
        vm.DiscoveryRows.First(r => r.Field.FieldName == fieldName);

    // ---- rows + editability by mode --------------------------------------

    [Fact]
    public void Default_automatic_mode_renders_platform_rows_all_read_only()
    {
        var (vm, _, _, _) = Build();

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(4, vm.DiscoveryRows.Count);
        }
        else
        {
            Assert.Equal(2, vm.DiscoveryRows.Count);
        }

        Assert.False(vm.OverrideAutomaticDiscovery);
        Assert.All(vm.DiscoveryRows, row => Assert.False(row.IsEditable));
    }

    [Fact]
    public void Manual_mode_renders_editable_rows()
    {
        var (vm, _, _, _) = Build(new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        Assert.True(vm.OverrideAutomaticDiscovery);
        Assert.All(vm.DiscoveryRows, row => Assert.True(row.IsEditable));
    }

    [Fact]
    public void Discovery_rows_match_the_platforms_expected_fields_in_catalog_order()
    {
        // Platform-gated: Windows renders only the Steam install + Darktide
        // binary rows (the compatdata + Proton fields are Linux-only). Linux
        // renders all four, in catalog order.
        var (vm, _, _, _) = Build();

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

    [Fact]
    public void Discovery_rows_are_pre_filled_from_config()
    {
        var discovery = new DiscoveryConfig
        {
            SteamInstallPath = "/steam",
            DarktideGameBinaryPath = "/darktide.exe",
            CompatdataPath = "/compat",
            ProtonBinaryPath = "/proton",
        };

        var (vm, _, _, _) = Build(discovery);

        Assert.Equal("/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/compat", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/proton", Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_are_empty_when_paths_are_unset()
    {
        var (vm, _, _, _) = Build();

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
        Assert.Equal(string.Empty, Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(string.Empty, Row(vm, "CompatdataPath").Value);
            Assert.Equal(string.Empty, Row(vm, "ProtonBinaryPath").Value);
        }
    }

    // ---- toggle: on ------------------------------------------------------
    //
    // Turning override on persists true, preserves the current snapshot, enables
    // editing, and does NOT discover.

    [Fact]
    public void Toggling_override_on_persists_true_without_discovery()
    {
        var discovery = new DiscoveryConfig
        {
            SteamInstallPath = "/steam",
            OverrideAutomaticDiscovery = false,
        };
        var (vm, loader, _, steam) = Build(discovery);

        vm.OverrideAutomaticDiscovery = true;

        Assert.Equal(1, loader.SaveCalls);
        Assert.True(loader.LastSaved!.Discovery.OverrideAutomaticDiscovery);
        // No discover/rediscover on the on path.
        Assert.Equal(0, steam.DiscoverCalls);
        Assert.Equal(0, steam.RediscoverCalls);
        // Values preserved (not rediscovered, not cleared).
        Assert.Equal("/steam", Row(vm, "SteamInstallPath").Value);
    }

    [Fact]
    public void Toggling_override_on_enables_editing_on_existing_rows()
    {
        var (vm, _, _, _) = Build();
        var row = Row(vm, "SteamInstallPath");
        Assert.False(row.IsEditable);

        vm.OverrideAutomaticDiscovery = true;

        Assert.True(row.IsEditable);
    }

    // ---- toggle: off -----------------------------------------------------
    //
    // Turning override off persists false, runs ordinary Discover (automatic),
    // and refreshes every row from the resulting snapshot.

    [Fact]
    public void Toggling_override_off_persists_false_and_invokes_discover_once()
    {
        var (vm, loader, _, steam) = Build(new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        vm.OverrideAutomaticDiscovery = false;

        // At least one save for the toggle; the FakeSteamService does not write
        // config (it is a passive double), so only the toggle's own save lands.
        Assert.True(loader.SaveCalls >= 1);
        Assert.False(loader.LastSaved!.Discovery.OverrideAutomaticDiscovery);
        Assert.Equal(1, steam.DiscoverCalls);
        Assert.Equal(0, steam.RediscoverCalls);
    }

    [Fact]
    public void Toggling_override_off_refreshes_rows_from_config_after_discover()
    {
        // Simulate the service's Discover persisting a fresh snapshot into the
        // live config (the real service does this; the fake side-effect mirrors
        // it), so the refresh picks up the new values.
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig
            {
                Discovery = new DiscoveryConfig
                {
                    OverrideAutomaticDiscovery = true,
                    SteamInstallPath = "/manual",
                },
            },
        };
        var steam = new FakeSteamService
        {
            OnDiscover = () =>
            {
                var c = loader.Load();
                c.Discovery.SteamInstallPath = "/auto/steam";
                c.Discovery.DarktideGameBinaryPath = "/auto/darktide";
                loader.Save(c);
            },
        };
        var vm = new SettingsViewModel(
            loader, steam, Localization,
            new FakeAppUpdateService(), new FakeDialogService(),
            invokeOnUi: static action => action(), Logger);

        vm.OverrideAutomaticDiscovery = false;

        Assert.False(vm.OverrideAutomaticDiscovery);
        Assert.Equal("/auto/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/auto/darktide", Row(vm, "DarktideGameBinaryPath").Value);
        Assert.All(vm.DiscoveryRows, row => Assert.False(row.IsEditable));
    }

    // ---- Discover command ------------------------------------------------
    //
    // Discover forces a Rediscover regardless of mode, preserves the mode, and
    // refreshes every row including cleared/null fields.

    [Fact]
    public void Discover_command_invokes_rediscover_and_preserves_mode()
    {
        var (vm, loader, _, steam) = Build(Manual());

        vm.DiscoverCommand.Execute(null);

        Assert.Equal(1, steam.RediscoverCalls);
        Assert.Equal(0, steam.DiscoverCalls);
        // Mode unchanged (the fake does not write config, so the live config +
        // the VM still reflect the initial manual mode).
        Assert.True(vm.OverrideAutomaticDiscovery);
        Assert.True(loader.Config.Discovery.OverrideAutomaticDiscovery);
        // Discover itself does not persist (the service owns the snapshot write;
        // the passive fake writes nothing).
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void Discover_command_refreshes_rows_including_cleared_fields()
    {
        // Start with a manual Steam path; the Rediscover side-effect clears it
        // (a partial result). The row should reflect the cleared value.
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig
            {
                Discovery = new DiscoveryConfig
                {
                    OverrideAutomaticDiscovery = true,
                    SteamInstallPath = "/stale",
                },
            },
        };
        var steam = new FakeSteamService
        {
            OnRediscover = () =>
            {
                var c = loader.Load();
                c.Discovery.SteamInstallPath = null;
                loader.Save(c);
            },
        };
        var vm = new SettingsViewModel(
            loader, steam, Localization,
            new FakeAppUpdateService(), new FakeDialogService(),
            invokeOnUi: static action => action(), Logger);

        Assert.Equal("/stale", Row(vm, "SteamInstallPath").Value);

        vm.DiscoverCommand.Execute(null);

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
    }

    // ---- write-through (manual mode only) --------------------------------

    [Fact]
    public void Editing_a_discovery_field_in_manual_mode_writes_neutral_property_via_read_modify_save()
    {
        var (vm, loader, _, _) = Build(Manual());

        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Equal("/new/steam", loader.LastSaved!.Discovery.SteamInstallPath);
        // Other discovery properties + config sections untouched (read-modify-save).
        Assert.Null(loader.LastSaved.Discovery.DarktideGameBinaryPath);
        Assert.Equal(CuratorConfig.CreateDefault().ProfilesBaseFolder, loader.LastSaved.ProfilesBaseFolder);
    }

    [Fact]
    public void Clearing_a_discovery_field_in_manual_mode_writes_null()
    {
        var (vm, loader, _, _) = Build(Manual(d => d.SteamInstallPath = "/old"));

        Row(vm, "SteamInstallPath").Value = "";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Null(loader.LastSaved!.Discovery.SteamInstallPath);
    }

    [Fact]
    public void Programmatic_row_changes_in_automatic_mode_do_not_persist()
    {
        // Defensive: even if a row write callback fires while in automatic mode
        // (e.g. the view two-way binds during a refresh), nothing is saved.
        var (vm, loader, _, _) = Build(); // automatic

        Row(vm, "SteamInstallPath").Value = "/ignored";

        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void Editing_each_field_persists_progressively_in_manual_mode()
    {
        var (vm, loader, _, _) = Build(Manual());

        Row(vm, "DarktideGameBinaryPath").Value = "/darktide.exe";
        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(2, loader.SaveCalls);
        Assert.Equal("/darktide.exe", loader.LastSaved!.Discovery.DarktideGameBinaryPath);
        Assert.Equal("/new/steam", loader.LastSaved.Discovery.SteamInstallPath);
    }

    // ---- RefreshFromConfig (hosted rehydrate) ----------------------------
    //
    // The hosted VM stays alive across navigation. When the discovery escape-
    // hatch (or any other surface) writes new paths + mode to config while
    // Settings is away, RefreshFromConfig reflects them on the next visit
    // without persisting.

    [Fact]
    public void RefreshFromConfig_reflects_external_changes_without_saving()
    {
        var (vm, loader, _, _) = Build();
        Assert.True(vm.CheckOnStartup); // default

        var external = CuratorConfig.CreateDefault();
        external.Discovery = new DiscoveryConfig
        {
            OverrideAutomaticDiscovery = true,
            SteamInstallPath = "/ext/steam",
            DarktideGameBinaryPath = "/ext/darktide.exe",
            CompatdataPath = "/ext/compat",
            ProtonBinaryPath = "/ext/proton",
        };
        external.AppUpdates.CheckOnStartup = false;
        loader.Config = external;

        vm.RefreshFromConfig();

        Assert.True(vm.OverrideAutomaticDiscovery);
        Assert.Equal("/ext/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/ext/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        Assert.All(vm.DiscoveryRows, row => Assert.True(row.IsEditable));
        Assert.False(vm.CheckOnStartup);
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_rehydrates_mode_values_and_editability()
    {
        var (vm, loader, _, _) = Build(new DiscoveryConfig
        {
            OverrideAutomaticDiscovery = false,
            SteamInstallPath = "/auto",
        });
        Assert.False(vm.OverrideAutomaticDiscovery);
        Assert.False(Row(vm, "SteamInstallPath").IsEditable);

        // External surface flips to manual + new values.
        loader.Config = CuratorConfig.CreateDefault();
        loader.Config.Discovery = new DiscoveryConfig
        {
            OverrideAutomaticDiscovery = true,
            SteamInstallPath = "/manual/steam",
        };

        vm.RefreshFromConfig();

        Assert.True(vm.OverrideAutomaticDiscovery);
        Assert.Equal("/manual/steam", Row(vm, "SteamInstallPath").Value);
        Assert.True(Row(vm, "SteamInstallPath").IsEditable);
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_preserves_the_same_row_object_instances()
    {
        var (vm, loader, _, _) = Build();
        var before = vm.DiscoveryRows.ToArray();

        var external = CuratorConfig.CreateDefault();
        external.Discovery = new DiscoveryConfig { SteamInstallPath = "/new/steam" };
        loader.Config = external;

        vm.RefreshFromConfig();

        Assert.True(before.SequenceEqual(vm.DiscoveryRows));
        Assert.Equal("/new/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void RefreshFromConfig_is_safe_and_repeatable()
    {
        var (vm, loader, _, _) = Build();

        vm.RefreshFromConfig();
        vm.RefreshFromConfig();
        vm.RefreshFromConfig();

        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void Construction_does_not_save_config()
    {
        var (_, loader, _, _) = Build(new DiscoveryConfig { SteamInstallPath = "/x" });

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
        var (vm, _, dialogs, _) = Build(
            launchExternalPath: p => { received = p; return true; });

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(AppPaths.AppDataDir, received);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenDataFolder_alerts_when_the_launcher_returns_false()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var (vm, _, dialogs, _) = Build(launchExternalPath: _ => false);

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(AppPaths.AppDataDir, alert.Message);
    }

    [Fact]
    public async Task OpenDataFolder_alerts_and_does_not_propagate_when_the_launcher_throws()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var (vm, _, dialogs, _) = Build(
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
        var (vm, _, dialogs, _) = Build(
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
        var (vm, _, dialogs, _) = Build(
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
        var (vm, _, dialogs, _) = Build(
            profilesBaseFolder: Path.GetTempPath(),
            launchExternalPath: p => { received = p; return true; });

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        Assert.Equal(Path.GetTempPath(), received);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task OpenProfilesFolder_alerts_when_the_launcher_returns_false()
    {
        var (vm, _, dialogs, _) = Build(
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
        var (vm, _, dialogs, _) = Build(
            profilesBaseFolder: Path.GetTempPath(),
            launchExternalPath: _ => throw new InvalidOperationException("boom"));

        await vm.OpenProfilesFolderCommand.ExecuteAsync(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Settings_OpenFolderFailedTitle"], alert.Title);
        Assert.Contains(Path.GetTempPath(), alert.Message);
    }
}
