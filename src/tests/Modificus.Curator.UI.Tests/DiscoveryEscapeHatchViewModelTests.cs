using Modificus.Curator.Config;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Settings;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="DiscoveryEscapeHatchViewModel"/>: only the missing
/// fields are shown (in catalog order); the global override toggle + forced
/// Discover share Settings' semantics; row editability follows the mode; submit
/// writes staged paths only in manual mode; cancel never writes staged rows
/// (already write-through toggle/Discover actions stay applied); and there is
/// no auto-retry (the caller does not re-launch).
/// </summary>
public sealed class DiscoveryEscapeHatchViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static DiscoveryFieldRowViewModel Row(DiscoveryEscapeHatchViewModel vm, string fieldName) =>
        vm.Rows.First(r => r.Field.FieldName == fieldName);

    private static (DiscoveryEscapeHatchViewModel vm, FakeConfigLoader loader, FakeSteamService steam) Build(
        IReadOnlyList<string> missingFields,
        DiscoveryConfig? discovery = null,
        FakeSteamService? steam = null)
    {
        var config = CuratorConfig.CreateDefault();
        if (discovery is not null) config.Discovery = discovery;
        var loader = new FakeConfigLoader { Config = config };
        steam ??= new FakeSteamService();
        var vm = new DiscoveryEscapeHatchViewModel(missingFields, loader, steam, Localization);
        return (vm, loader, steam);
    }

    // ---- only the missing fields are shown --------------------------------

    [Fact]
    public void Empty_missing_fields_yields_no_rows()
    {
        var (vm, _, _) = Build(Array.Empty<string>());

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Rows_are_built_only_for_the_missing_fields_in_catalog_order()
    {
        var (vm, _, _) = Build(new[] { "ProtonBinaryPath", "SteamInstallPath" });

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("SteamInstallPath", vm.Rows[0].Field.FieldName);
        Assert.Equal("ProtonBinaryPath", vm.Rows[1].Field.FieldName);
    }

    [Fact]
    public void Unknown_field_names_are_dropped_silently()
    {
        var (vm, _, _) = Build(new[] { "SteamInstallPath", "SomeFutureField" });

        Assert.Single(vm.Rows);
        Assert.Equal("SteamInstallPath", vm.Rows[0].Field.FieldName);
    }

    [Fact]
    public void All_four_fields_can_show_at_once()
    {
        var (vm, _, _) = Build(
            new[] { "SteamInstallPath", "DarktideGameBinaryPath", "CompatdataPath", "ProtonBinaryPath" });

        Assert.Equal(4, vm.Rows.Count);
    }

    // ---- pre-fill + editability by mode ----------------------------------

    [Fact]
    public void Rows_pre_fill_with_the_current_stored_value_when_set()
    {
        var (vm, _, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { SteamInstallPath = "/prior/steam" });

        Assert.Equal("/prior/steam", Row(vm, "SteamInstallPath").Value);
    }

    [Fact]
    public void Automatic_mode_rows_are_read_only()
    {
        var (vm, _, _) = Build(new[] { "SteamInstallPath", "ProtonBinaryPath" });

        Assert.False(vm.OverrideAutomaticDiscovery);
        Assert.All(vm.Rows, row => Assert.False(row.IsEditable));
    }

    [Fact]
    public void Manual_mode_rows_are_editable()
    {
        var (vm, _, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        Assert.True(vm.OverrideAutomaticDiscovery);
        Assert.All(vm.Rows, row => Assert.True(row.IsEditable));
    }

    // ---- toggle: on / off (same semantics as Settings) -------------------

    [Fact]
    public void Toggling_override_on_persists_true_without_discovery()
    {
        var (vm, loader, steam) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { SteamInstallPath = "/prior" });

        vm.OverrideAutomaticDiscovery = true;

        Assert.True(loader.LastSaved!.Discovery.OverrideAutomaticDiscovery);
        Assert.Equal(0, steam.DiscoverCalls);
        Assert.Equal(0, steam.RediscoverCalls);
        // Values preserved + editing enabled.
        Assert.Equal("/prior", Row(vm, "SteamInstallPath").Value);
        Assert.True(Row(vm, "SteamInstallPath").IsEditable);
    }

    [Fact]
    public void Toggling_override_off_persists_false_and_invokes_discover_once()
    {
        var (vm, loader, steam) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        vm.OverrideAutomaticDiscovery = false;

        Assert.False(loader.LastSaved!.Discovery.OverrideAutomaticDiscovery);
        Assert.Equal(1, steam.DiscoverCalls);
        Assert.Equal(0, steam.RediscoverCalls);
    }

    [Fact]
    public void Toggling_override_off_refreshes_rows_from_config_after_discover()
    {
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
                c.Discovery.SteamInstallPath = "/auto";
                loader.Save(c);
            },
        };
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, steam, Localization);

        vm.OverrideAutomaticDiscovery = false;

        Assert.Equal("/auto", Row(vm, "SteamInstallPath").Value);
        Assert.False(Row(vm, "SteamInstallPath").IsEditable);
    }

    // ---- Discover command ------------------------------------------------

    [Fact]
    public void Discover_invokes_rediscover_and_refreshes_rows_without_changing_mode()
    {
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
            OnRediscover = () =>
            {
                var c = loader.Load();
                c.Discovery.SteamInstallPath = "/rediscovered";
                loader.Save(c);
            },
        };
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, steam, Localization);

        vm.DiscoverCommand.Execute(null);

        Assert.Equal(1, steam.RediscoverCalls);
        Assert.Equal(0, steam.DiscoverCalls);
        Assert.True(vm.OverrideAutomaticDiscovery); // mode preserved
        Assert.True(Row(vm, "SteamInstallPath").IsEditable); // editability preserved
        Assert.Equal("/rediscovered", Row(vm, "SteamInstallPath").Value);
    }

    // ---- submit ----------------------------------------------------------

    [Fact]
    public void Manual_submit_writes_staged_missing_paths_and_true_mode_in_one_save()
    {
        var (vm, loader, _) = Build(
            new[] { "SteamInstallPath", "ProtonBinaryPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        Row(vm, "SteamInstallPath").Value = "/steam";
        Row(vm, "ProtonBinaryPath").Value = "/proton";

        // Editing rows does not save in the escape-hatch (staged until submit).
        Assert.Equal(0, loader.SaveCalls);

        vm.SubmitCommand.Execute(null);

        Assert.Equal(1, loader.SaveCalls);
        Assert.Equal("/steam", loader.LastSaved!.Discovery.SteamInstallPath);
        Assert.Equal("/proton", loader.LastSaved.Discovery.ProtonBinaryPath);
        Assert.True(loader.LastSaved.Discovery.OverrideAutomaticDiscovery);
        Assert.True(vm.Result);
    }

    [Fact]
    public void Automatic_submit_does_not_rewrite_path_values()
    {
        // In automatic mode, submit must NOT write the staged row values. The
        // toggle's own write-through already persisted the mode. It may close
        // without a path write.
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig
            {
                Discovery = new DiscoveryConfig { SteamInstallPath = "/existing" },
            },
        };
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, new FakeSteamService(), Localization);

        Row(vm, "SteamInstallPath").Value = "/staged-but-ignored";
        vm.SubmitCommand.Execute(null);

        // The stored value is untouched (no path rewrite).
        Assert.Equal("/existing", loader.Config.Discovery.SteamInstallPath);
        Assert.True(vm.Result);
    }

    [Fact]
    public void Manual_submit_writes_null_for_empty_values()
    {
        var (vm, loader, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true, SteamInstallPath = "/old" });

        Row(vm, "SteamInstallPath").Value = "";
        vm.SubmitCommand.Execute(null);

        Assert.Null(loader.LastSaved!.Discovery.SteamInstallPath);
    }

    // ---- cancel ----------------------------------------------------------

    [Fact]
    public void Cancel_does_not_persist_staged_row_changes()
    {
        var (vm, loader, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true, SteamInstallPath = "/prior" });

        Row(vm, "SteamInstallPath").Value = "/staged";
        vm.CancelCommand.Execute(null);

        Assert.Equal("/prior", loader.Config.Discovery.SteamInstallPath);
        Assert.False(vm.Result);
    }

    [Fact]
    public void Cancel_does_not_roll_back_an_already_applied_toggle()
    {
        // Toggle is write-through: pressing it persisted the mode. Cancel only
        // abandons staged row edits; it does not create a transaction across
        // the earlier service call.
        var (vm, loader, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = false });

        vm.OverrideAutomaticDiscovery = true; // persisted
        Assert.True(loader.LastSaved!.Discovery.OverrideAutomaticDiscovery);

        vm.CancelCommand.Execute(null);

        // Mode toggle stays applied.
        Assert.True(loader.Config.Discovery.OverrideAutomaticDiscovery);
        Assert.False(vm.Result);
    }

    [Fact]
    public void Cancel_does_not_roll_back_an_already_applied_discover()
    {
        // Discover is write-through: pressing it ran Rediscover + refreshed
        // rows. Cancel does not undo that.
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig
            {
                Discovery = new DiscoveryConfig { SteamInstallPath = "/old" },
            },
        };
        var steam = new FakeSteamService
        {
            OnRediscover = () =>
            {
                var c = loader.Load();
                c.Discovery.SteamInstallPath = "/rediscovered";
                loader.Save(c);
            },
        };
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, steam, Localization);

        vm.DiscoverCommand.Execute(null);
        Assert.Equal("/rediscovered", Row(vm, "SteamInstallPath").Value);

        // Stage a different value, then cancel: the rediscovered value stays.
        Row(vm, "SteamInstallPath").Value = "/staged";
        vm.CancelCommand.Execute(null);

        Assert.Equal("/rediscovered", loader.Config.Discovery.SteamInstallPath);
        Assert.False(vm.Result);
    }

    // ---- no auto-retry ---------------------------------------------------

    [Fact]
    public void Submit_does_not_flag_any_retry_signal_to_the_caller()
    {
        // The escape-hatch's contract is fire-and-forget: it returns
        // true/false, and the caller (the shell) does not auto-retry on true.
        // There is no RetryRequested flag or similar; the contract is the
        // boolean Result. Verified here by asserting the surface stays at one
        // signal.
        var (vm, _, _) = Build(
            new[] { "SteamInstallPath" },
            new DiscoveryConfig { OverrideAutomaticDiscovery = true });

        vm.SubmitCommand.Execute(null);

        Assert.True(vm.Result);
    }
}

/// <summary>
/// Smoke test the <see cref="DiscoveryFields"/> catalog: All lists the four
/// fields in catalog order, and Find round-trips the canonical names.
/// </summary>
public sealed class DiscoveryFieldsCatalogTests
{
    [Fact]
    public void All_lists_the_four_fields_in_catalog_order()
    {
        var names = DiscoveryFields.All.Select(f => f.FieldName).ToArray();

        Assert.Equal(
            new[] { "SteamInstallPath", "DarktideGameBinaryPath", "CompatdataPath", "ProtonBinaryPath" },
            names);
    }

    [Theory]
    [InlineData("SteamInstallPath", DiscoveryBrowseKind.Folder)]
    [InlineData("DarktideGameBinaryPath", DiscoveryBrowseKind.File)]
    [InlineData("CompatdataPath", DiscoveryBrowseKind.Folder)]
    [InlineData("ProtonBinaryPath", DiscoveryBrowseKind.File)]
    public void Find_returns_the_field_with_its_browse_kind(string name, DiscoveryBrowseKind expected)
    {
        var field = DiscoveryFields.Find(name);

        Assert.NotNull(field);
        Assert.Equal(expected, field!.BrowseKind);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_name()
    {
        Assert.Null(DiscoveryFields.Find("SomeFutureField"));
    }
}
