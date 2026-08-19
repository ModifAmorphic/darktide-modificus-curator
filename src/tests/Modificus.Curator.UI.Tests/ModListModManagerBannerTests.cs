using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The mod-list manager banner state: <see cref="ModListViewModel.IsModManagerActive"/>
/// + <see cref="ModListViewModel.ModManagerBannerText"/> follow the profile
/// service's <see cref="IProfileService.GetActiveModManager"/> result read at
/// every <see cref="ModListViewModel.Reload"/> and on the enable-toggle path
/// (in place, without a row rebuild), with the banner text resolving
/// the manager mod's display name through the loaded rows, the repository
/// container, then the literal "base".
/// </summary>
public sealed class ModListModManagerBannerTests
{
    private static readonly LocalizationService Localization = new();

    private static ModListViewModel Build(
        FakeProfileService profiles,
        FakeProfileSession session,
        FakeModRepository repo) =>
        TestDoubles.BuildModList(profiles, session, repo, localization: Localization);

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    private static ModContainer Seed(FakeModRepository repo, string name) =>
        repo.Seed(new NexusSource { ModId = 246 }, name, "1.0");

    private static string ExpectedBanner(string name) =>
        Localization.Format("ModList_ModManagerBanner", name);

    [Fact]
    public void Active_manager_with_a_loaded_row_shows_the_banner_with_the_row_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = manager.Id, Enabled = true, Order = 0 });
        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.IsModManagerActive);
        Assert.Equal(ExpectedBanner("Alternate Mod Loader"), vm.ModManagerBannerText);
    }

    [Fact]
    public void Null_manager_result_hides_the_banner()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = manager.Id, Enabled = true, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.False(vm.IsModManagerActive);
    }

    [Fact]
    public void Reload_after_the_manager_result_clears_flips_the_banner_off()
    {
        // Simulates disabling (or removing) the manager mod: the next Reload
        // re-derives from the profile service and the banner disappears.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = manager.Id, Enabled = true, Order = 0 });
        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        Assert.True(vm.IsModManagerActive);

        profiles.ActiveModManagerResult = null;
        vm.Reload();

        Assert.False(vm.IsModManagerActive);
    }

    [Fact]
    public void Manager_container_matching_no_loaded_row_falls_back_to_the_repo_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Repo-side Manager");
        // The manager's container exists in the repository but is not in the
        // loaded rows (e.g. the entry was removed between derivation + reload).
        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.IsModManagerActive);
        Assert.Equal(ExpectedBanner("Repo-side Manager"), vm.ModManagerBannerText);
    }

    [Fact]
    public void Manager_container_missing_everywhere_falls_back_to_base_without_throwing()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var stranger = Guid.NewGuid();
        profiles.ActiveModManagerResult = new ActiveModManager(
            stranger, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, new FakeModRepository());

        Assert.True(vm.IsModManagerActive);
        Assert.Equal(ExpectedBanner("base"), vm.ModManagerBannerText);
    }

    // ---- enable-toggle path (no intervening Reload) ---------------------------

    private static ModItemViewModel Row(ModListViewModel vm, Guid containerId) =>
        vm.Mods.Single(m => m.ContainerId == containerId);

    [Fact]
    public void Toggling_the_manager_row_off_clears_the_banner_without_a_reload()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = manager.Id, Enabled = true, Order = 0 });
        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        Assert.True(vm.IsModManagerActive);

        // Disabling the manager mod flips the derivation to null; the toggle
        // path re-derives in place, so no Reload may be needed to clear it.
        profiles.ActiveModManagerResult = null;
        var row = Row(vm, manager.Id);
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.False(vm.IsModManagerActive);
    }

    [Fact]
    public void Toggling_the_manager_row_on_shows_the_banner_without_a_reload()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = manager.Id, Enabled = false, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        Assert.False(vm.IsModManagerActive);

        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var row = Row(vm, manager.Id);
        row.Enabled = true;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.True(vm.IsModManagerActive);
        Assert.Equal(ExpectedBanner("Alternate Mod Loader"), vm.ModManagerBannerText);
    }

    [Fact]
    public void Toggling_an_unrelated_row_leaves_the_banner_state_and_text_unchanged()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var manager = Seed(repo, "Alternate Mod Loader");
        var other = repo.Seed(new NexusSource { ModId = 247 }, "Ordinary Mod", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = manager.Id, Enabled = true, Order = 0 },
            new ModListEntry { ContainerId = other.Id, Enabled = true, Order = 1 });
        profiles.ActiveModManagerResult = new ActiveModManager(
            manager.Id, Path.Combine("staged", "mods", "base", "mod_manager.lua"));
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        Assert.True(vm.IsModManagerActive);
        var text = vm.ModManagerBannerText;

        var row = Row(vm, other.Id);
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        // The re-derivation reads the fake's result, which still names the
        // manager: an unrelated toggle neither clears nor rewrites the banner.
        Assert.True(vm.IsModManagerActive);
        Assert.Equal(text, vm.ModManagerBannerText);
    }
}
