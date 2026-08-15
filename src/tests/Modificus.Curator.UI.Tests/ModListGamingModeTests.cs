using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Gaming Mode gating on the mod list (Steam Deck): the Add split button +
/// its tooltip, the per-row push of the gaming flag (driving the linked
/// badge's enabled state + tooltip), and the programmatic guard on the
/// open-folder command. Built on the same hand-rolled fakes as
/// <see cref="ModListViewModelTests"/> via
/// <see cref="TestDoubles.BuildModList"/>'s gaming-mode seam.
/// </summary>
public sealed class ModListGamingModeTests
{
    private static readonly LocalizationService Localization = new();

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    /// <summary>
    /// Builds the VM over a profile holding one linked + available row (the
    /// surface the Gaming Mode gating disables) and optionally one Nexus row.
    /// </summary>
    private static (ModListViewModel Vm, FakeProfileSession Session) Build(
        GamingModeState gamingMode,
        bool withNexusRow = false)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        var entries = new List<ModListEntry>
        {
            new() { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest },
        };
        if (withNexusRow)
        {
            var nexus = repo.Seed(new NexusSource { ModId = 1234 }, "SoundPack", "1.0");
            entries.Add(new ModListEntry { ContainerId = nexus.Id, Enabled = true, Order = 1, Policy = ModVersionPolicy.Latest });
        }
        profiles.WithMods(a.Id, entries.ToArray());
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = TestDoubles.BuildModList(
            profiles, session, repo,
            localization: Localization,
            gamingMode: gamingMode);
        return (vm, session);
    }

    // ---- Add split button --------------------------------------------------

    [Fact]
    public void Gaming_mode_disables_the_Add_button()
    {
        var (vm, _) = Build(new GamingModeState(true));

        Assert.True(vm.IsGamingMode);
        Assert.False(vm.IsAddEnabled);
    }

    [Fact]
    public void Non_gaming_with_an_idle_workflow_keeps_Add_enabled()
    {
        var (vm, _) = Build(new GamingModeState(false));

        Assert.False(vm.IsGamingMode);
        Assert.True(vm.IsAddEnabled);
    }

    [Fact]
    public void An_active_import_workflow_alone_disables_Add_and_cancel_re_enables()
    {
        // IsAddEnabled is the conjunction of the workflow gate + the gaming
        // gate; the workflow half is live (re-fires on IsActive flips).
        var (vm, _) = Build(new GamingModeState(false));

        vm.ImportWorkflow.StartBatchCommand.Execute(new[] { "/mods/SoundPack" });
        Assert.True(vm.ImportWorkflow.IsActive);
        Assert.False(vm.IsAddEnabled);

        vm.ImportWorkflow.CancelBatchCommand.Execute(null);
        Assert.False(vm.ImportWorkflow.IsActive);
        Assert.True(vm.IsAddEnabled);
    }

    [Fact]
    public void The_Add_tooltip_swaps_to_the_gaming_guidance()
    {
        var (gaming, _) = Build(new GamingModeState(true));
        var (desktop, _) = Build(new GamingModeState(false));

        Assert.Equal(Localization["ModList_AddGamingModeHint"], gaming.AddButtonTooltip);
        Assert.Equal(Localization["ModList_AddButtonTooltip"], desktop.AddButtonTooltip);
    }

    // ---- row push + linked badge -------------------------------------------

    [Fact]
    public void Rows_built_under_gaming_carry_the_gaming_flag_and_linked_tooltip()
    {
        var (vm, _) = Build(new GamingModeState(true), withNexusRow: true);

        var linked = vm.Mods.Single(m => m.Name == "DMF");
        Assert.True(linked.IsGamingMode);
        Assert.True(linked.IsLinkedAvailable);
        Assert.Equal(Localization["GamingMode_FileManagerGuidance"], linked.LinkedBadgeTooltip);

        // The push is not linked-specific: every row receives it.
        var nexus = vm.Mods.Single(m => m.Name == "SoundPack");
        Assert.True(nexus.IsGamingMode);
    }

    [Fact]
    public void Non_gaming_rows_carry_no_gaming_flag_and_keep_the_ordinary_tooltip()
    {
        var (vm, _) = Build(new GamingModeState(false));

        var linked = vm.Mods.Single(m => m.Name == "DMF");
        Assert.False(linked.IsGamingMode);
        Assert.Equal(Localization["ModRow_LinkedOpenTooltip"], linked.LinkedBadgeTooltip);
    }

    [Fact]
    public void Reload_reapplies_the_gaming_flag_to_rebuilt_rows()
    {
        // Rows are rebuilt on every Reload; the gaming push rides the same
        // global-state push as the premium flag so a reload cannot strand a
        // row without it.
        var (vm, session) = Build(new GamingModeState(true));

        vm.Reload();

        Assert.True(vm.Mods.Single(m => m.Name == "DMF").IsGamingMode);

        // And through the session-driven reload path as well.
        session.ActiveProfileId = session.ActiveProfileId;
        vm.Reload();
        Assert.True(vm.Mods.Single(m => m.Name == "DMF").IsGamingMode);
    }

    // ---- open-folder command guard ------------------------------------------

    [Fact]
    public async Task Gaming_mode_OpenFolder_on_a_linked_available_row_never_launches_the_file_manager()
    {
        // The badge is disabled in the view, but the command is the source of
        // truth: a programmatic invocation must not open a file manager.
        var opened = 0;
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var vm = TestDoubles.BuildModList(
            profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo,
            localization: Localization,
            launchExternalPath: _ => { opened++; return true; },
            gamingMode: new GamingModeState(true));
        var row = vm.Mods.Single(m => m.Name == "DMF");
        Assert.True(row.IsLinkedAvailable); // the row is otherwise a valid target

        await vm.OpenFolderCommand.ExecuteAsync(row);

        Assert.Equal(0, opened);
    }
}
