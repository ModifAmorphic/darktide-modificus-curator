using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Mod-list VM behaviors against hand-rolled fakes: load on active profile +
/// reload on active-id change, empty states, enable/disable, reorder (up/down),
/// auto-sort (identity no-op), remove (confirm / cancel), per-mod policy, and the
/// add flow (picker / drag-and-drop) with sequential per-mod modals including
/// cancel-mid-batch, invalid-source peek failure, base-name collision hard-block,
/// and re-add of a mod already in the profile (excluded from the collision
/// check). Source + version badge text is joined from the repository by container id.
/// </summary>
public sealed class ModListViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static ModListViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeModRepository? repo = null,
        FakeModImportService? importService = null,
        FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        return TestDoubles.BuildModList(profiles, session, repo, importService,
            dialogs: dialogs, localization: Localization);
    }

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    /// <summary>
    /// Seeds the repository with a container that has one latest version, for the
    /// badge-join tests. Returns the container.
    /// </summary>
    private static ModContainer Seed(FakeModRepository repo, ModSource source, string name, string versionTag = "1.0")
        => repo.Seed(source, name, versionTag);

    private static ModItemViewModel Row(ModListViewModel vm, string name) =>
        vm.Mods.Single(m => m.Name == name);

    // ---- load on active profile + empty states -----------------------------

    [Fact]
    public void Load_with_an_active_profile_joins_source_and_version_from_the_repo_by_container_id()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new NexusSource { ModId = 1234 }, "DMF", "1.0");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack", "");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Enabled = false, Order = 1 });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.HasActiveProfile);
        Assert.True(vm.HasMods);
        Assert.False(vm.ShowAddModsHint);
        Assert.Equal(2, vm.Mods.Count);
        // Sorted by Order.
        Assert.Equal("DMF", vm.Mods[0].Name);
        Assert.Equal("SoundPack", vm.Mods[1].Name);
        // Source / version joined from the repo by container id.
        Assert.Equal("Nexus #1234 · 1.0", Row(vm, "DMF").SourceBadgeText);
        Assert.Equal("Untracked", Row(vm, "SoundPack").SourceBadgeText);
        Assert.True(Row(vm, "DMF").Enabled);
        Assert.False(Row(vm, "SoundPack").Enabled);
    }

    [Fact]
    public void Load_with_no_active_profile_clears_and_reports_no_active_profile()
    {
        // The no-profile handoff (a HyperlinkButton to Profiles) is owned by the
        // shell in MainWindow.axaml, so this VM no longer exposes a
        // no-profile text. It only reports HasActiveProfile=false + an empty
        // list; the shell link overlays the page when there is no active
        // profile.
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        Assert.False(vm.HasActiveProfile);
        Assert.False(vm.HasMods);
        Assert.Empty(vm.Mods);
    }

    [Fact]
    public void Load_with_an_active_profile_but_no_mods_shows_the_no_mods_empty_state()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id });

        Assert.True(vm.HasActiveProfile);
        Assert.False(vm.HasMods);
        Assert.True(vm.ShowAddModsHint);
        Assert.Empty(vm.Mods);
    }

    // ---- ShowAddModsHint thresholds (zero / one / two mods) -----------------

    [Fact]
    public void ShowAddModsHint_is_true_for_an_active_profile_with_a_single_mod()
    {
        // A DMF-only profile right after onboarding: one mod, but the hint
        // still invites the user to add their own mods alongside it.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = dmf.Id, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.HasActiveProfile);
        Assert.Single(vm.Mods);
        Assert.True(vm.ShowAddModsHint);
    }

    [Fact]
    public void ShowAddModsHint_is_false_for_an_active_profile_with_two_mods()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var other = Seed(repo, new UntrackedSource(), "Other");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = other.Id, Order = 1 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.HasActiveProfile);
        Assert.Equal(2, vm.Mods.Count);
        Assert.False(vm.ShowAddModsHint);
    }

    [Fact]
    public void ShowAddModsHint_is_false_without_an_active_profile_regardless_of_mod_count()
    {
        // No active profile: the shell-owned no-profile handoff shows instead,
        // so the add-mods hint must stay hidden even if Mods somehow had rows.
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        Assert.False(vm.HasActiveProfile);
        Assert.False(vm.ShowAddModsHint);
    }

    // ---- nxm registration probe (drives the empty-state Nexus hint) --------

    [Fact]
    public void IsNxmRegistered_true_when_the_registrar_reports_registered()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        var vm = TestDoubles.BuildModList(profiles, session,
            nxmRegistrar: registrar);

        Assert.True(vm.IsNxmRegistered);
    }

    [Fact]
    public void IsNxmRegistered_false_when_no_registrar_is_wired()
    {
        // The default Build path: no registrar passed, so the probe is a no-op
        // and the empty-state Nexus hint stays hidden.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var vm = TestDoubles.BuildModList(profiles, session);

        Assert.False(vm.IsNxmRegistered);
    }

    [Fact]
    public void A_missing_container_shows_the_not_found_badge()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { ContainerId = Guid.NewGuid(), Enabled = true, Order = 0 });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id });

        Assert.False(vm.Mods[0].Found);
        Assert.Equal("Not found", vm.Mods[0].SourceBadgeText);
    }

    // ---- reload on active-id change ----------------------------------------

    [Fact]
    public void Changing_the_active_profile_reloads_the_list()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var repo = new FakeModRepository();
        var aContainer = Seed(repo, new UntrackedSource(), "A1");
        var bContainer = Seed(repo, new UntrackedSource(), "B1");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = aContainer.Id, Order = 0 });
        profiles.WithMods(b.Id, new ModListEntry { ContainerId = bContainer.Id, Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        Assert.Equal("A1", Assert.Single(vm.Mods).Name);

        session.ActiveProfileId = b.Id;

        Assert.Equal("B1", Assert.Single(vm.Mods).Name);
    }

    [Fact]
    public void Clearing_the_active_profile_empties_the_list()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "A1");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        Assert.Single(vm.Mods);

        session.ActiveProfileId = null;

        Assert.Empty(vm.Mods);
        Assert.False(vm.HasActiveProfile);
    }

    // ---- enable / disable --------------------------------------------------

    [Fact]
    public void ToggleEnabled_applies_the_new_state_via_SetModEnabled()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Enabled = true, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        // The CheckBox two-way binding flips Enabled first; the command applies it.
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.Contains((a.Id, container.Id, false), profiles.SetModEnabledCalls);
    }

    [Fact]
    public void ToggleEnabled_is_a_noop_without_an_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        vm.ToggleEnabledCommand.Execute(new ModItemViewModel(Localization, Guid.NewGuid(), "X",
            new UntrackedSource(), "", true, 0, ModVersionPolicy.Latest, Array.Empty<ModVersion>(), true));

        Assert.Empty(profiles.SetModEnabledCalls);
    }

    // ---- reorder (up / down) -----------------------------------------------

    [Fact]
    public void MoveUp_swaps_with_the_predecessor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveUpCommand.Execute(Row(vm, "SoundPack"));

        // The persisted order has SoundPack's container first.
        Assert.Equal(new[] { sound.Id, dmf.Id }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
        Assert.Equal("DMF", vm.Mods[1].Name);
    }

    [Fact]
    public void MoveDown_swaps_with_the_successor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveDownCommand.Execute(Row(vm, "DMF"));

        Assert.Equal(new[] { sound.Id, dmf.Id }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
    }

    [Fact]
    public void MoveUp_at_the_top_is_a_noop()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveUpCommand.Execute(Row(vm, "DMF"));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    // ---- auto-sort (identity no-op) ----------------------------------------

    [Fact]
    public void AutoSort_runs_the_resolver_and_persists_the_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.AutoSortCommand.Execute(null);

        // Identity resolver returns the current order unchanged (by container id).
        Assert.Equal(new[] { dmf.Id, sound.Id }, Assert.Single(profiles.SetModOrderCalls));
    }

    // ---- remove (confirm / cancel) -----------------------------------------

    [Fact]
    public async Task Remove_confirmed_calls_RemoveMod_and_drops_the_row()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, session, repo, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.Contains((a.Id, container.Id), profiles.RemoveModCalls);
        Assert.Empty(vm.Mods);
        Assert.Contains("DMF", dialogs.LastConfirmMessage);
    }

    [Fact]
    public async Task Remove_cancelled_leaves_the_list_and_service_untouched()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(profiles, session, repo, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.Empty(profiles.RemoveModCalls);
        Assert.Single(vm.Mods);
    }

    [Fact]
    public async Task Remove_is_a_noop_without_an_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null }, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(new ModItemViewModel(Localization, Guid.NewGuid(), "X",
            new UntrackedSource(), "", true, 0, ModVersionPolicy.Latest, Array.Empty<ModVersion>(), true));

        Assert.Empty(profiles.RemoveModCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    // ---- per-mod policy ----------------------------------------------------

    [Fact]
    public void SetPolicyPinned_applies_a_PinnedPolicy_with_the_selected_versionId()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        // The row exposes the container's versions for the dropdown; pick the
        // only one (the dropdown guarantees the id exists in the container).
        Assert.Single(row.AvailableVersions);
        row.SelectedVersion = row.AvailableVersions[0];

        vm.SetPolicyPinnedCommand.Execute(row);

        var (id, containerId, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.Equal(a.Id, id);
        Assert.Equal(container.Id, containerId);
        var pinned = Assert.IsType<PinnedPolicy>(policy);
        Assert.Equal(container.Versions[0].Folder, pinned.VersionId);
        // The reloaded row reflects the new effective policy.
        Assert.True(Row(vm, "DMF").Policy is PinnedPolicy);
    }

    [Fact]
    public void SetPolicyPinned_is_a_noop_when_the_container_has_no_versions()
    {
        // A version-less container's dropdown is empty; pinning is impossible.
        // SetPolicyPinned no-ops rather than creating a phantom pin.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = repo.CreateContainer(new UntrackedSource(), "DMF"); // no versions
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        Assert.Empty(row.AvailableVersions);
        Assert.Null(row.SelectedVersion);

        vm.SetPolicyPinnedCommand.Execute(row);

        Assert.Empty(profiles.SetModPolicyCalls);
    }

    [Fact]
    public void SetPolicyLatest_applies_the_Latest_policy()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        var vId = container.Versions[0].Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(vId) });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");
        Assert.True(row.IsPinned);

        vm.SetPolicyLatestCommand.Execute(row);

        var (_, _, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.IsType<LatestPolicy>(policy);
        Assert.False(Row(vm, "DMF").IsPinned);
    }

    [Fact]
    public void PinnedPolicy_display_text_uses_the_resolved_pinned_version_tag()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        var vId = container.Versions[0].Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(vId) });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        // The display text surfaces the resolved version's readable tag, not the
        // opaque folder id.
        Assert.Contains("1.0", Row(vm, "DMF").PolicyDisplayText);
        Assert.DoesNotContain(vId, Row(vm, "DMF").PolicyDisplayText);
    }

    [Fact]
    public void Row_exposes_the_container_versions_for_the_pin_dropdown()
    {
        // The dropdown's source is the container's version list, each option
        // pairing the readable tag (shown) with the opaque folder id (stored).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { });
        var versions = repo.Get(container.Id)!.Versions;
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.Equal(2, row.AvailableVersions.Count);
        Assert.Contains(row.AvailableVersions, o => o.VersionString == "1.0");
        Assert.Contains(row.AvailableVersions, o => o.VersionString == "2.0");
        // Each option carries the version's folder id (the versionId foreign key).
        Assert.All(row.AvailableVersions, o => Assert.NotEmpty(o.VersionId));
        Assert.Equal(versions.Select(v => v.Folder).ToHashSet(),
            row.AvailableVersions.Select(o => o.VersionId).ToHashSet());
    }

    [Fact]
    public void Pinned_row_pre_selects_the_pinned_version_in_the_dropdown()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { });
        var v1Id = repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "1.0").Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(v1Id) });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.NotNull(row.SelectedVersion);
        Assert.Equal(v1Id, row.SelectedVersion!.VersionId);
    }

    [Fact]
    public void Latest_row_pre_selects_the_isLatest_version_in_the_dropdown()
    {
        // A Latest row pre-selects the resolved (IsLatest) version, so a switch
        // to Pinned offers the actual version rather than a blank.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { }); // becomes IsLatest
        var latestId = repo.Get(container.Id)!.Versions.Single(v => v.IsLatest).Folder;
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.NotNull(row.SelectedVersion);
        Assert.Equal(latestId, row.SelectedVersion!.VersionId);
    }

    // ---- inline import workflow integration -------------------------------

    [Fact]
    public void ImportWorkflow_is_exposed_as_a_read_only_child()
    {
        var vm = Build();

        Assert.NotNull(vm.ImportWorkflow);
        Assert.False(vm.ImportWorkflow.IsActive);
    }

    [Fact]
    public async Task ItemImported_for_the_active_profile_reloads_the_list()
    {
        // A successful workflow import on the active profile must surface in the
        // mod list (the narrow event triggers a reload).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = TestDoubles.BuildModList(profiles, session, repo, import,
            localization: Localization);
        var workflow = vm.ImportWorkflow;

        // Start a batch + confirm with Untracked (simplest valid metadata).
        workflow.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        workflow.SourceChoice = ImportWorkflowViewModel.ImportSource.Untracked;
        await workflow.ImportCurrentCommand.ExecuteAsync(null);

        // The workflow's ItemImported event reloaded the list; the row shows.
        Assert.Single(vm.Mods);
        Assert.Contains(vm.Mods, m => m.Name == "DMF");
    }

    [Fact]
    public async Task ItemImported_for_an_inactive_profile_does_not_misdirect_the_list()
    {
        // An import that lands on a now-inactive profile (the profile changed
        // mid-processing) must NOT reload the list: the list always shows the
        // active profile, and reloading would be a no-op that masks the event's
        // irrelevance.
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo)
        {
            ImportGate = new TaskCompletionSource<bool>(),
        };
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
        };
        var vm = TestDoubles.BuildModList(profiles, session, repo, import,
            localization: Localization);
        var workflow = vm.ImportWorkflow;
        workflow.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        workflow.SourceChoice = ImportWorkflowViewModel.ImportSource.Untracked;
        var task = workflow.ImportCurrentCommand.ExecuteAsync(null);

        // Switch the active profile mid-processing.
        session.ActiveProfileId = b.Id;
        import.ImportGate!.SetResult(true);
        await task;

        // The list shows profile b (empty); the import landed on a but did not
        // reload this list.
        Assert.Empty(vm.Mods);
        Assert.Single(profiles.GetModList(a.Id)); // landed on a
    }

    [Fact]
    public void Add_mode_labels_remain_stable_when_no_workflow_is_active()
    {
        var vm = Build();

        Assert.Equal(ModAddMode.NexusMods, vm.AddMode);
        Assert.Equal("Add Nexus Mods", vm.AddModeLabel);

        vm.AddMode = ModAddMode.Archive;
        Assert.Equal("Add Mod (archive)", vm.AddModeLabel);
    }

    [Fact]
    public async Task End_to_end_workflow_import_lands_a_mod_in_the_profile_and_list()
    {
        // Production-shape fakes: create + activate a profile, begin a local
        // import through the workflow, confirm it, and assert the mod appears in
        // the profile's list via the mod-list VM's reload.
        var profiles = TestDoubles.Profiles();
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var created = profiles.CreateProfile("Main", "", new LaunchSettings());
        session.ActiveProfileId = created.Id;
        var vm = TestDoubles.BuildModList(profiles, session, repo, import,
            localization: Localization);
        var workflow = vm.ImportWorkflow;
        workflow.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        workflow.SourceChoice = ImportWorkflowViewModel.ImportSource.Untracked;

        await workflow.ImportCurrentCommand.ExecuteAsync(null);

        var mods = profiles.GetModList(created.Id);
        Assert.Single(mods);
        Assert.True(repo.Get(mods[0].ContainerId) is not null);
        Assert.Single(vm.Mods); // the reload surfaced it
    }

    // ---- add split-button view state ---------------------------------------

    [Fact]
    public void AddMode_defaults_to_NexusMods_and_the_label_tracks_the_mode()
    {
        var vm = Build();

        Assert.Equal(ModAddMode.NexusMods, vm.AddMode);
        Assert.Equal("Add Nexus Mods", vm.AddModeLabel);

        vm.AddMode = ModAddMode.Archive;
        Assert.Equal("Add Mod (archive)", vm.AddModeLabel);

        vm.AddMode = ModAddMode.Folder;
        Assert.Equal("Add Mod (folder)", vm.AddModeLabel);

        vm.AddMode = ModAddMode.LinkExternal;
        Assert.Equal("Link external folder", vm.AddModeLabel);
    }

    // ---- update-check -> per-row state -------------------------------------

    /// <summary>Builds the VM with explicit update-check + acquisition + auth
    /// fakes so the update-flow tests can shape each one. The profile service +
    /// repository are seeded with a Nexus+Latest mod (containerA) + an
    /// Untracked mod (containerB) so the per-row assertions have distinct rows.
    /// Returns the VM + the two rows' container ids.</summary>
    private static (ModListViewModel Vm, Guid NexusContainerId, Guid UntrackedContainerId, FakeUpdateCheckService UpdateCheck, FakeModAcquisitionService Acquisition, FakeNexusAuthService Auth, UpdateCoordinator Coordinator, FakeUpdateStateStore UpdateState)
        BuildForUpdateFlow(FakeNexusAuthService? auth = null)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        var untracked = repo.Seed(new UntrackedSource(), "SoundPack", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = untracked.Id, Enabled = true, Order = 1, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var updateCheck = new FakeUpdateCheckService();
        var acquisition = new FakeModAcquisitionService();
        var effectiveAuth = auth ?? new FakeNexusAuthService(); // default premium
        var coordinator = new UpdateCoordinator();
        var updateState = new FakeUpdateStateStore(profiles, repo);
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            updateCheck: updateCheck, acquisition: acquisition, auth: effectiveAuth,
            coordinator: coordinator, updateState: updateState);
        return (vm, nexus.Id, untracked.Id, updateCheck, acquisition, effectiveAuth, coordinator, updateState);
    }

    [Fact]
    public void CheckCompleted_sets_per_row_UpdateAvailable_from_the_flagged_container_ids()
    {
        var (vm, nexusId, untrackedId, updateCheck, _, _, _, _) = BuildForUpdateFlow();

        // Raise a result flagging ONLY the Nexus container.
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, ModId: 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow,
            RateLimited: false,
            Thorough: false,
            Outcome: CheckOutcome.Success));

        Assert.True(Row(vm, "DMF").UpdateAvailable);
        Assert.False(Row(vm, "SoundPack").UpdateAvailable);
        Assert.False(vm.IsRateLimited);
    }

    [Fact]
    public void CheckCompleted_with_no_updates_clears_every_row()
    {
        var (vm, nexusId, _, updateCheck, _, _, _, _) = BuildForUpdateFlow();

        // First flag the Nexus row, then raise an empty result: the marker
        // should clear (the badge reflects the latest check, not a stale one).
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));
        Assert.True(Row(vm, "DMF").UpdateAvailable);

        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, Thorough: false,
            Outcome: CheckOutcome.Success));

        Assert.False(Row(vm, "DMF").UpdateAvailable);
    }

    [Fact]
    public void CheckCompleted_with_a_rate_limited_result_sets_the_list_level_notice_flag()
    {
        var (vm, _, _, uc, _, _, _, _) = BuildForUpdateFlow();

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited));

        Assert.True(vm.IsRateLimited);
        Assert.NotEmpty(vm.RateLimitedNoticeText);
    }

    [Fact]
    public void Reload_reapplies_the_last_check_result_to_a_freshly_rebuilt_list()
    {
        var (vm, nexusId, _, updateCheck, _, _, _, _) = BuildForUpdateFlow();

        // Stage a result flagging the Nexus container, then trigger a reload
        // (e.g. a profile edit). The freshly built rows should pick up the last
        // result without waiting for the next check.
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));

        vm.Reload();

        Assert.True(Row(vm, "DMF").UpdateAvailable);
    }

    [Fact]
    public void CheckCompleted_with_NamesChanged_refreshes_row_names_from_the_repo()
    {
        // The name sync piggybacks on the update check. When a result carries
        // NamesChanged, the list refreshes each affected row's displayed name
        // from the repository in place (no full Reload). The VM reads the new
        // name back through the repo by container id.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var updateCheck = new FakeUpdateCheckService();
        var vm = TestDoubles.BuildModList(profiles, session, repo, updateCheck: updateCheck);
        var row = Row(vm, "DMF");
        Assert.Equal("DMF", row.Name);

        // Simulate the check renaming the container in the repo (the production
        // UpdateCheckService does this via RenameContainer) + signaling NamesChanged.
        repo.RenameContainer(nexus.Id, "DMF Remastered");
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, Thorough: false, NamesChanged: true));

        // The row's displayed name refreshed in place from the repo.
        Assert.Equal("DMF Remastered", row.Name);
    }

    [Fact]
    public void CheckCompleted_without_NamesChanged_leaves_row_names_untouched()
    {
        // A result without NamesChanged (the default) does not touch row names,
        // even if the stored name has drifted: the refresh is gated on the flag.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var updateCheck = new FakeUpdateCheckService();
        var vm = TestDoubles.BuildModList(profiles, session, repo, updateCheck: updateCheck);
        var row = Row(vm, "DMF");

        repo.RenameContainer(nexus.Id, "DMF Remastered");
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, Thorough: false));

        // The row name is NOT refreshed (NamesChanged defaults to false).
        Assert.Equal("DMF", row.Name);
    }

    [Fact]
    public void AcknowledgeUpdateAndReload_clears_the_flag_despite_persisted_state()
    {
        // After an nxm install/reinstall, Reload alone would re-apply the
        // persisted known-update state (recorded before the version change) and
        // leave the flag set. AcknowledgeUpdateAndReload clears the persisted
        // entry for the container first, then reloads; the cleared state is what
        // ApplyKnownUpdateState reads back.
        var (vm, nexusId, _, updateCheck, _, _, _, _) = BuildForUpdateFlow();

        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));
        Assert.True(Row(vm, "DMF").UpdateAvailable);

        vm.AcknowledgeUpdateAndReload(nexusId);

        Assert.False(Row(vm, "DMF").UpdateAvailable);
        // Other rows are unaffected by the per-container clear.
        Assert.False(Row(vm, "SoundPack").UpdateAvailable);
    }

    // ---- CheckForUpdatesNow: the IsCheckingNow affordance -------------------

    [Fact]
    public async Task CheckForUpdatesNow_drives_IsCheckingNow_for_the_duration_of_the_thorough_check()
    {
        // The manual trigger awaits the runner's thorough task; IsCheckingNow
        // is true while it runs + cleared after. The view binds the button's
        // IsEnabled + the spinner's IsVisible to it.
        var (vm, _, _, _, _, _, _, _) = BuildForUpdateFlow();
        Assert.False(vm.IsCheckingNow);

        // The fake runner's CheckNowAsync dispatches a thread-pool task that
        // hits the fake service (instant), so the await lands quickly.
        await vm.CheckForUpdatesNowCommand.ExecuteAsync(null);

        Assert.False(vm.IsCheckingNow); // cleared in the finally block.
    }

    [Fact]
    public async Task CheckForUpdatesNow_is_a_noop_when_a_check_is_already_running()
    {
        // Re-entrancy guard: a second invocation while IsCheckingNow is true is
        // a no-op (the command checks the flag itself, not just the button's
        // IsEnabled). Set the flag directly to simulate an in-flight check.
        var (vm, _, _, _, _, _, _, _) = BuildForUpdateFlow();
        vm.IsCheckingNow = true;

        await vm.CheckForUpdatesNowCommand.ExecuteAsync(null);

        // The flag is unchanged (the command returned at the guard; the finally
        // did not run because the await never happened).
        Assert.True(vm.IsCheckingNow);
    }

    // ---- the update flow (one at a time, premium-only) ---------------------

    [Fact]
    public async Task UpdateCommand_success_acquires_reloads_and_toggles_IsUpdating()
    {
        var (vm, nexusId, _, updateCheck, acquisition, _, _, _) = BuildForUpdateFlow();
        // Flag the Nexus row so the command's defenses pass.
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));
        var row = Row(vm, "DMF");
        Assert.True(vm.IsPremiumUser);

        await vm.UpdateCommand.ExecuteAsync(row);

        // The acquisition was called with the game domain + the row's Nexus mod id.
        var call = Assert.Single(acquisition.LatestNexusCalls);
        Assert.Equal("warhammer40kdarktide", call.GameDomain);
        Assert.Equal(8, call.ModId);
        // IsUpdating toggled + AnyRowUpdating re-enabled (no stuck state).
        Assert.False(row.IsUpdating);
        Assert.False(vm.AnyRowUpdating);
    }

    [Fact]
    public async Task UpdateCommand_failure_surfaces_an_alert_and_clears_IsUpdating()
    {
        var (vm, nexusId, _, updateCheck, acquisition, _, _, _) = BuildForUpdateFlow();
        var dialogs = new FakeDialogService();
        // Re-build with this dialogs instance so AlertCalls are captured. The
        // helper builds its own dialogs; swap by constructing directly.
        var profiles = TestDoubles.Profiles(Profile("Alpha"));
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(profiles.ListProfiles()[0].Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = profiles.ListProfiles()[0].Id };
        var uc = new FakeUpdateCheckService();
        var failingAcquisition = new FakeModAcquisitionService
        {
            ThrowNext = new InvalidOperationException("boom"),
        };
        var vm2 = TestDoubles.BuildModList(profiles, session, repo,
            dialogs: dialogs, updateCheck: uc, acquisition: failingAcquisition);
        // Raise AFTER BuildModList so the store is wired (RaiseCheckCompleted
        // records through the store, mirroring the real service).
        uc.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexus.Id, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));
        var row = Row(vm2, "DMF");

        await vm2.UpdateCommand.ExecuteAsync(row);

        // The failure surfaced as an alert naming the mod.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("DMF", alert.Message);
        Assert.Contains("boom", alert.Message);
        // IsUpdating cleared + AnyRowUpdating re-enabled (no stuck state).
        Assert.False(row.IsUpdating);
        Assert.False(vm2.AnyRowUpdating);
    }

    [Fact]
    public async Task UpdateCommand_is_one_at_a_time_a_second_call_while_running_is_a_noop()
    {
        var (vm, nexusId, _, updateCheck, _, _, coordinator, _) = BuildForUpdateFlow();
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusId, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));

        // Simulate "another install is in flight" by acquiring the shared
        // coordinator (the single mutual-exclusion point shared with the
        // automatic updater). The manual command's TryAcquire then fails + the
        // command is a no-op.
        Assert.True(coordinator.TryAcquire(out var busyScope));
        Assert.True(coordinator.IsBusy);
        var row = Row(vm, "DMF");

        await vm.UpdateCommand.ExecuteAsync(row);

        // No acquisition call landed (the command's TryAcquire was rejected).
        Assert.False(row.IsUpdating);
        // The coordinator stays busy (the command did not acquire/release).
        Assert.True(coordinator.IsBusy);
        busyScope?.Dispose();
    }

    [Fact]
    public void UpdateCommand_is_a_noop_for_untracked_rows()
    {
        var (vm, _, _, updateCheck, acquisition, _, _, _) = BuildForUpdateFlow();
        // Even if the check erroneously flagged the Untracked container, the
        // command's IsNexusLatest defense blocks the call.
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, Thorough: false));

        // Run on the Untracked row (no UpdateAvailable, not Nexus).
        var row = Row(vm, "SoundPack");
        Assert.False(row.IsNexusLatest);

        vm.UpdateCommand.Execute(row);

        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task UpdateCommand_is_a_noop_without_an_active_profile()
    {
        var (vm, _, _, updateCheck, acquisition, _, _, _) = BuildForUpdateFlow();
        // Clear the active profile (a fresh build with a null session id is
        // cleaner than mutating the session after build).
        var profiles = TestDoubles.Profiles();
        var vm2 = TestDoubles.BuildModList(profiles, new FakeProfileSession { ActiveProfileId = null },
            updateCheck: updateCheck, acquisition: acquisition);

        // A synthetic row (the empty profile has none) exercises the defense.
        var synthetic = new ModItemViewModel(Localization, Guid.NewGuid(), "X",
            new NexusSource { ModId = 8 }, "", true, 0, ModVersionPolicy.Latest,
            Array.Empty<ModVersion>(), true);
        await vm2.UpdateCommand.ExecuteAsync(synthetic);

        Assert.Empty(acquisition.LatestNexusCalls);
    }

    // ---- per-row source URL resolution -------------------------------------

    [Fact]
    public void SourceUrl_resolves_per_source_type()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        var untracked = repo.Seed(new UntrackedSource(), "Local", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0 },
            new ModListEntry { ContainerId = untracked.Id, Order = 1 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8",
            Row(vm, "DMF").SourceUrl);
        Assert.Null(Row(vm, "Local").SourceUrl);
    }

    [Fact]
    public void UpdatePageUrl_resolves_to_the_nexus_files_tab_for_nexus_rows_only()
    {
        // The update-available marker is a HyperlinkButton to the mod's Nexus
        // files tab (the user's instinct to click the marker lands on the files
        // page). Nexus -> SourceUrl + "?tab=files"; Untracked -> null (the
        // marker no-ops, though the update check never flags non-Nexus rows
        // anyway).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        var untracked = repo.Seed(new UntrackedSource(), "Local", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0 },
            new ModListEntry { ContainerId = untracked.Id, Order = 1 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            Row(vm, "DMF").UpdatePageUrl);
        Assert.Null(Row(vm, "Local").UpdatePageUrl);
    }

    [Fact]
    public void IsNexusLatest_tracks_policy_and_source()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        var vId = repo.Get(nexus.Id)!.Versions[0].Folder;
        var untracked = repo.Seed(new UntrackedSource(), "Local", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = new PinnedPolicy(vId) },
            new ModListEntry { ContainerId = untracked.Id, Order = 1, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        // Nexus but Pinned: NOT IsNexusLatest (the update check skips Pinned).
        Assert.False(Row(vm, "DMF").IsNexusLatest);
        // Untracked: never Nexus.
        Assert.False(Row(vm, "Local").IsNexusLatest);
        // Switch the Nexus row to Latest: now IsNexusLatest.
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = untracked.Id, Order = 1, Policy = ModVersionPolicy.Latest });
        vm.Reload();
        Assert.True(Row(vm, "DMF").IsNexusLatest);
    }

    [Fact]
    public void SourceBadgeText_appends_version_for_nexus_latest_only()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        var vId = repo.Get(nexus.Id)!.Versions[0].Folder;
        var nexusNoVer = repo.Seed(new NexusSource { ModId = 9 }, "NoVer", "");
        var untracked = repo.Seed(new UntrackedSource(), "Local", "");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = new PinnedPolicy(vId) },
            new ModListEntry { ContainerId = nexusNoVer.Id, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = untracked.Id, Order = 2, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        // Nexus + Pinned: plain badge (version is in the pin dropdown).
        Assert.Equal("Nexus #8", Row(vm, "DMF").SourceBadgeText);
        // Nexus + Latest but empty resolved version: plain badge (nothing to append).
        Assert.Equal("Nexus #9", Row(vm, "NoVer").SourceBadgeText);
        // Untracked: never Nexus.
        Assert.Equal("Untracked", Row(vm, "Local").SourceBadgeText);

        // Switch the Nexus row to Latest: now the version appends to the badge.
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = nexusNoVer.Id, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = untracked.Id, Order = 2, Policy = ModVersionPolicy.Latest });
        vm.Reload();
        Assert.Equal("Nexus #8 · 1.0", Row(vm, "DMF").SourceBadgeText);
    }

    [Fact]
    public void NexusModId_returns_the_row_source_mod_id()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 42 }, "DMF", "1.0");
        var untracked = repo.Seed(new UntrackedSource(), "Local", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0 },
            new ModListEntry { ContainerId = untracked.Id, Order = 1 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.Equal(42, Row(vm, "DMF").NexusModId);
        Assert.Null(Row(vm, "Local").NexusModId);
    }

    // ---- manual-refresh throttle (countdown tooltip + disabled button) ------

    /// <summary>
    /// Builds a VM wired with a controllable runner clock (so the sliding-window
    /// throttle is deterministic) + a captured countdown-timer tick (so the test
    /// drives the 1-second tick directly, like the runner tests drive the
    /// periodic tick). The runner is the real UpdateCheckRunner, driven into the
    /// throttle state through CheckNowAsync (no production test-seam). Returns
    /// the VM, the captured tick callback, and a setter for the runner's clock.
    /// </summary>
    private static (ModListViewModel Vm, Action Tick, Action<DateTimeOffset> SetClock)
        BuildForThrottle()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = repo.Seed(new UntrackedSource(), "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var updateCheck = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        Action? capturedTick = null;
        var vm = TestDoubles.BuildModList(
            profiles: profiles,
            session: session,
            repo: repo,
            updateCheck: updateCheck,
            getNow: () => now,
            startCountdownTimer: t => capturedTick ??= t);
        return (vm, () => capturedTick!.Invoke(), value => now = value);
    }

    /// <summary>
    /// Drives the runner into the throttled state by firing 10 manual refreshes
    /// through the VM's CheckForUpdatesNow command (advancing the runner clock
    /// 1s before each so the timestamps are distinct but within 2 minutes of
    /// each other). The 10th fire engages the throttle: ReevaluateRefreshGate
    /// runs after the await + sees the count at 10.
    /// </summary>
    private static async Task DriveIntoThrottleAsync(
        ModListViewModel vm, Action<DateTimeOffset> setClock)
    {
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            setClock(baseTime.AddSeconds(i));
            await vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        }
    }

    [Fact]
    public async Task ManualRefreshThrottle_disables_the_button_when_the_budget_is_spent()
    {
        // After 10 manual refreshes, the sliding window is spent + the VM marks
        // itself throttled: IsManualRefreshThrottled is true + IsRefreshEnabled
        // is false (the button binds IsEnabled to it).
        var (vm, _, setClock) = BuildForThrottle();
        Assert.True(vm.IsRefreshEnabled); // not throttled at construction

        await DriveIntoThrottleAsync(vm, setClock);

        Assert.True(vm.IsManualRefreshThrottled);
        Assert.False(vm.IsRefreshEnabled);
    }

    [Fact]
    public async Task ManualRefreshThrottle_tooltip_shows_the_countdown_while_throttled()
    {
        // While throttled, the tooltip is the throttle string (not the normal
        // "Check for updates now") and carries the operator's exact wording.
        var (vm, _, setClock) = BuildForThrottle();
        var normal = Localization["ModList_CheckNowTooltip"];
        Assert.Equal(normal, vm.ManualRefreshTooltip);

        await DriveIntoThrottleAsync(vm, setClock);

        Assert.NotEqual(normal, vm.ManualRefreshTooltip);
        Assert.Contains("Rate limiting protection enabled", vm.ManualRefreshTooltip);
        Assert.Contains("Manual refresh will be available again in", vm.ManualRefreshTooltip);
    }

    [Fact]
    public async Task ManualRefreshThrottle_countdown_tick_clears_when_cooldown_elapses()
    {
        // Driving the captured countdown tick re-evaluates the runner's
        // NextManualRefreshAllowedAt. While throttled, the tick keeps the throttle
        // string live; once the clock advances past the cooldown (the property
        // returns null), the tick clears IsManualRefreshThrottled + restores the
        // normal tooltip. The 10th timestamp is baseTime+9s, so the unlock is
        // baseTime+2m9s; advancing to baseTime+3m is past it.
        var (vm, tick, setClock) = BuildForThrottle();
        var baseTime = DateTimeOffset.UtcNow;
        await DriveIntoThrottleAsync(vm, setClock);
        Assert.True(vm.IsManualRefreshThrottled);

        // A tick while throttled keeps the throttle string live.
        tick();
        Assert.Contains("Rate limiting protection enabled", vm.ManualRefreshTooltip);

        // Advance the runner's clock past the cooldown so the property clears.
        setClock(baseTime.AddMinutes(3));
        tick();

        Assert.False(vm.IsManualRefreshThrottled);
        Assert.True(vm.IsRefreshEnabled);
        Assert.Equal(Localization["ModList_CheckNowTooltip"], vm.ManualRefreshTooltip);
    }

    [Fact]
    public void ManualRefreshThrottle_normal_tooltip_is_the_check_now_resx_string()
    {
        // When not throttled (the default at construction), the tooltip is the
        // normal "Check for updates now" resx string.
        var (vm, _, _) = BuildForThrottle();

        Assert.False(vm.IsManualRefreshThrottled);
        Assert.Equal(Localization["ModList_CheckNowTooltip"], vm.ManualRefreshTooltip);
    }

    // ---- rate-limit coupling (refresh button + pill gated by active reset) --

    /// <summary>
    /// Builds a VM wired with a controllable clock (shared by the runner + the
    /// VM's rate-limit-active decision) + a captured countdown-timer tick, and
    /// exposes the fake update-check service so a test can raise rate-limited
    /// results. Mirrors <see cref="BuildForThrottle"/> but returns the
    /// <see cref="FakeUpdateCheckService"/> for result injection.
    /// </summary>
    private static (ModListViewModel Vm, FakeUpdateCheckService UpdateCheck, Action Tick, Action<DateTimeOffset> SetClock)
        BuildForRateLimit()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = repo.Seed(new UntrackedSource(), "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var updateCheck = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        Action? capturedTick = null;
        var vm = TestDoubles.BuildModList(
            profiles: profiles,
            session: session,
            repo: repo,
            updateCheck: updateCheck,
            getNow: () => now,
            startCountdownTimer: t => capturedTick ??= t);
        return (vm, updateCheck, () => capturedTick!.Invoke(), value => now = value);
    }

    [Fact]
    public void RateLimited_result_with_server_reset_disables_refresh_and_shows_pill()
    {
        // A rate-limited result carrying a future server reset sets the raw +
        // active rate-limit flags, disables the refresh button, and reads the
        // coupled pill text. The tooltip names the local retry time.
        var (vm, uc, _, setClock) = BuildForRateLimit();
        var now = DateTimeOffset.UtcNow;
        setClock(now);
        var reset = now.AddMinutes(5);

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited, RateLimitResetsAt: reset));

        Assert.True(vm.IsRateLimited);
        Assert.Equal(reset, vm.RateLimitResetsAt);
        Assert.True(vm.IsRateLimitActive);
        Assert.False(vm.IsRefreshEnabled);
        Assert.Equal("Refresh disabled due to rate-limiting", vm.RateLimitedNoticeText);
        Assert.Contains("Try again at", vm.RateLimitedTooltip);
    }

    [Fact]
    public void RateLimited_active_clears_when_clock_advances_past_reset()
    {
        // Driving the countdown tick after advancing the shared clock past the
        // reset flips IsRateLimitActive off, re-enables the refresh button, and
        // would hide the pill (the pill binds IsRateLimitActive).
        var (vm, uc, tick, setClock) = BuildForRateLimit();
        var now = DateTimeOffset.UtcNow;
        setClock(now);
        var reset = now.AddMinutes(5);

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited, RateLimitResetsAt: reset));
        Assert.True(vm.IsRateLimitActive);
        Assert.False(vm.IsRefreshEnabled);

        // A tick before the reset keeps the active state.
        setClock(reset.AddSeconds(-1));
        tick();
        Assert.True(vm.IsRateLimitActive);

        // Advance past the reset: the tick clears the active state.
        setClock(reset.AddSeconds(1));
        tick();

        Assert.False(vm.IsRateLimitActive);
        Assert.True(vm.IsRefreshEnabled);
        Assert.Equal(Localization["ModList_CheckNowTooltip"], vm.ManualRefreshTooltip);
    }

    [Fact]
    public void RateLimited_null_reset_uses_fallback_cooldown_from_checked_at()
    {
        // When Nexus stayed silent about the reset (null), the active state
        // lasts CheckedAt + the fallback cooldown (1 minute). The tooltip then
        // uses the time-free "Try again later." form (no specific time promised).
        var (vm, uc, tick, setClock) = BuildForRateLimit();
        var now = DateTimeOffset.UtcNow;
        setClock(now);

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited, RateLimitResetsAt: null));
        Assert.True(vm.IsRateLimitActive);
        Assert.Null(vm.RateLimitResetsAt);
        Assert.Equal(Localization["ModList_RateLimitedTooltip"], vm.RateLimitedTooltip);
        Assert.Contains("Try again later", vm.RateLimitedTooltip);

        // Halfway through the fallback: still active.
        setClock(now.AddSeconds(30));
        tick();
        Assert.True(vm.IsRateLimitActive);

        // Past the 1-minute fallback: clears.
        setClock(now.AddMinutes(1).AddSeconds(1));
        tick();
        Assert.False(vm.IsRateLimitActive);
        Assert.True(vm.IsRefreshEnabled);
    }

    [Fact]
    public void Non_rate_limited_result_clears_rate_limit_state()
    {
        // A later non-rate-limited result clears IsRateLimited + the reset +
        // the active flag, so the refresh button re-enables immediately (no
        // waiting for a server reset the next check superseded).
        var (vm, uc, _, setClock) = BuildForRateLimit();
        var now = DateTimeOffset.UtcNow;
        setClock(now);

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited, RateLimitResetsAt: now.AddMinutes(5)));
        Assert.True(vm.IsRateLimited);
        Assert.True(vm.IsRateLimitActive);

        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: false, Thorough: false,
            Outcome: CheckOutcome.Success));

        Assert.False(vm.IsRateLimited);
        Assert.Null(vm.RateLimitResetsAt);
        Assert.False(vm.IsRateLimitActive);
        Assert.True(vm.IsRefreshEnabled);
    }

    [Fact]
    public async Task Rate_limit_and_manual_throttle_both_disable_rate_limit_tooltip_takes_precedence()
    {
        // Coexistence: the manual sliding-window throttle engaged AND a
        // rate-limited result active both keep the button disabled. The refresh
        // button tooltip shows the rate-limit reason (the more informative,
        // server-driven cause), not the throttle countdown.
        var (vm, uc, _, setClock) = BuildForRateLimit();
        Assert.True(vm.IsRefreshEnabled); // nothing active at construction

        await DriveIntoThrottleAsync(vm, setClock);
        Assert.True(vm.IsManualRefreshThrottled);

        var now = DateTimeOffset.UtcNow;
        setClock(now);
        uc.RaiseCheckCompleted(new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), now, RateLimited: true, Thorough: false,
            Outcome: CheckOutcome.RateLimited, RateLimitResetsAt: now.AddMinutes(5)));

        Assert.True(vm.IsManualRefreshThrottled);
        Assert.True(vm.IsRateLimitActive);
        Assert.False(vm.IsRefreshEnabled);
        // Rate-limit precedence: the button tooltip equals the rate-limit
        // tooltip, not the throttle countdown string.
        Assert.Equal(vm.RateLimitedTooltip, vm.ManualRefreshTooltip);
        Assert.DoesNotContain("Rate limiting protection enabled", vm.ManualRefreshTooltip);
    }

    // ---- FormatRemaining (pure helper) -------------------------------------

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(90, "1:30")]
    [InlineData(120, "2:00")]
    public void FormatRemaining_formats_a_timespan_as_m_ss(int seconds, string expected)
    {
        Assert.Equal(expected, ModListViewModel.FormatRemaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatRemaining_clamps_a_negative_remaining_to_zero()
    {
        // A tick landing a hair past the unlock instant could yield a tiny
        // negative; the helper clamps so the tooltip never shows a negative.
        Assert.Equal("0:00", ModListViewModel.FormatRemaining(TimeSpan.FromSeconds(-5)));
        Assert.Equal("0:00", ModListViewModel.FormatRemaining(TimeSpan.FromMilliseconds(-1)));
    }

    // ---- stable update-action cell (row UX) --------------------------------

    /// <summary>
    /// Builds a VM with one Nexus+Latest row, one Pinned Nexus row, and one
    /// Untracked row so the per-row visibility + enabled + tooltip assertions
    /// cover every row type. Returns the VM, the launcher-invocation recorder,
    /// and the row lookup helpers.
    /// </summary>
    private static (ModListViewModel Vm, FakeUpdateCheckService UpdateCheck, FakeUpdateStateStore UpdateState, List<Uri> Launches, FakeDialogService Dialogs)
        BuildForRowAction(bool premium = true, Func<Uri, bool>? launchExternal = null)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexusLatest = repo.Seed(new NexusSource { ModId = 8 }, "NexusLatest", "1.0");
        var nexusPinned = repo.Seed(new NexusSource { ModId = 9 }, "NexusPinned", "1.0");
        var pinnedVersion = repo.Get(nexusPinned.Id)!.Versions[0].Folder;
        var untracked = repo.Seed(new UntrackedSource(), "Local", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexusLatest.Id, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = nexusPinned.Id, Order = 1, Policy = new PinnedPolicy(pinnedVersion) },
            new ModListEntry { ContainerId = untracked.Id, Order = 2, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var updateCheck = new FakeUpdateCheckService();
        var updateState = new FakeUpdateStateStore(profiles, repo);
        var dialogs = new FakeDialogService();
        var launches = new List<Uri>();
        var launcher = launchExternal ?? (uri => { launches.Add(uri); return true; });
        var auth = new FakeNexusAuthService
        {
            State = premium
                ? new NexusAuthState(NexusAuthMethod.OAuth, "tester", IsPremium: true)
                : new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            updateCheck: updateCheck, updateState: updateState, auth: auth,
            dialogs: dialogs, launchExternal: launcher);
        return (vm, updateCheck, updateState, launches, dialogs);
    }

    [Fact]
    public void UpdateAction_shows_for_nexus_latest_rows_and_no_update_disables_with_no_update_tooltip()
    {
        // The stable update-action button stays VISIBLE for Nexus + Latest rows
        // even when no update exists, but is logically DISABLED. The view's
        // updateAction:disabled style dims this exact state (CanShowUpdateAction
        // true + UpdateActionEnabled false) to 0.4 opacity. These two assertions
        // are the VM-side guard for that UI correction.
        var (vm, _, _, _, _) = BuildForRowAction();

        var row = Row(vm, "NexusLatest");
        Assert.True(row.CanShowUpdateAction); // visible (Nexus + Latest)
        Assert.False(row.UpdateActionEnabled); // disabled (no update)
        Assert.Contains("Up to date", row.UpdateActionTooltip);
    }

    [Fact]
    public void UpdateAction_stays_visible_but_disabled_while_row_is_updating()
    {
        // During a per-row update the button must STAY VISIBLE in its fixed
        // action cell (CanShowUpdateAction remains true) but be DISABLED
        // (UpdateActionEnabled is false because it includes !IsUpdating). The
        // progress affordance moved to the source-badge area, so the action cell
        // does not shift. This is the VM-side guard for that UI behavior.
        var (vm, updateCheck, _, _, _) = BuildForRowAction(premium: true);
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        var row = Row(vm, "NexusLatest");
        // Baseline: flagged + premium -> visible + enabled.
        Assert.True(row.CanShowUpdateAction);
        Assert.True(row.UpdateActionEnabled);

        // Simulate the in-flight install (the command sets this itself on a real
        // run). The button stays visible but is now disabled.
        row.IsUpdating = true;
        Assert.True(row.CanShowUpdateAction); // still visible
        Assert.False(row.UpdateActionEnabled); // disabled while updating
    }

    [Fact]
    public void UpdateAction_pinned_and_untracked_rows_do_not_expose_an_action()
    {
        var (vm, _, _, _, _) = BuildForRowAction();

        // Pinned Nexus and Untracked rows never show the action button.
        Assert.False(Row(vm, "NexusPinned").CanShowUpdateAction);
        Assert.False(Row(vm, "Local").CanShowUpdateAction);
    }

    [Fact]
    public void UpdateAction_flagged_premium_row_is_enabled_with_install_tooltip()
    {
        var (vm, updateCheck, _, _, _) = BuildForRowAction(premium: true);
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;

        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        var row = Row(vm, "NexusLatest");
        Assert.True(row.UpdateAvailable);
        Assert.True(row.UpdateActionEnabled);
        Assert.Equal(Localization["ModRow_UpdateTooltipInstall"], row.UpdateActionTooltip);
    }

    [Fact]
    public async Task UpdateAction_premium_click_acquires_and_acknowledges_without_a_fresh_check()
    {
        var (vm, updateCheck, updateState, _, _) = BuildForRowAction(premium: true);
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        var callsBefore = updateCheck.CallCount;
        await vm.UpdateCommand.ExecuteAsync(Row(vm, "NexusLatest"));

        // NO fresh post-update CheckAsync was issued (the acknowledgement cleared
        // the flag without an extra API call).
        Assert.Equal(callsBefore, updateCheck.CallCount);
        // The install was acknowledged (the recorded call targeted this container).
        Assert.Contains(updateState.AcknowledgeCalls, c => c.ContainerId == nexusLatestId);
        // The row flag cleared after the reload (the acknowledged store is the
        // source of truth ApplyKnownUpdateState reads back).
        Assert.False(Row(vm, "NexusLatest").UpdateAvailable);
    }

    [Fact]
    public async Task UpdateAction_regular_click_opens_the_nexus_files_page()
    {
        var (vm, updateCheck, _, launches, _) = BuildForRowAction(premium: false);
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        var row = Row(vm, "NexusLatest");
        Assert.False(vm.IsPremiumUser);
        Assert.True(row.UpdateActionEnabled); // enabled for regular too
        Assert.Equal(Localization["ModRow_UpdateTooltipOpenFiles"], row.UpdateActionTooltip);

        await vm.UpdateCommand.ExecuteAsync(row);

        // The files-page URL was opened via the external-launcher seam.
        var opened = Assert.Single(launches);
        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files", opened.AbsoluteUri);
    }

    [Fact]
    public async Task UpdateAction_launcher_failure_surfaces_an_alert()
    {
        var (vm, updateCheck, _, _, dialogs) = BuildForRowAction(
            premium: false,
            launchExternal: _ => false); // simulate launch failure
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        await vm.UpdateCommand.ExecuteAsync(Row(vm, "NexusLatest"));

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("NexusLatest", alert.Message);
        Assert.Contains("nexusmods.com", alert.Message);
    }

    [Fact]
    public void Restart_inside_interval_gate_shows_persisted_flags_before_any_api_call()
    {
        // A persisted known-update entry (seeded directly into the state store,
        // as if loaded from app-state.json) shows as a flag on Reload WITHOUT
        // any CheckAsync call. This is the restart-hydration contract: the
        // interval gate may suppress the opening check, but the prior flag still
        // renders from the persisted state.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var updateCheck = new FakeUpdateCheckService();
        var updateState = new FakeUpdateStateStore(profiles, repo);
        // Seed the persisted flag directly (simulating app-state.json loaded at
        // startup). RecordProfileId is wired by BuildModList so a hydration read
        // scopes to the active profile.
        updateState.SeedFlagged(a.Id, nexus.Id);
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            updateCheck: updateCheck, updateState: updateState);

        // No check fired (CallCount is 0), yet the row shows the flag.
        Assert.Equal(0, updateCheck.CallCount);
        Assert.True(Row(vm, "DMF").UpdateAvailable);
    }

    [Fact]
    public async Task AcknowledgeUpdateAndReload_clears_the_persisted_entry()
    {
        var (vm, updateCheck, updateState, _, _) = BuildForRowAction(premium: true);
        var nexusLatestId = Row(vm, "NexusLatest").ContainerId;
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexusLatestId, 8, "NexusLatest", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));

        vm.AcknowledgeUpdateAndReload(nexusLatestId);

        // The persisted entry for this container was acknowledged (removed).
        var remaining = Assert.Single(updateState.AcknowledgeCalls);
        Assert.Equal(nexusLatestId, remaining.ContainerId);
        Assert.False(Row(vm, "NexusLatest").UpdateAvailable);
    }

    // ---- test-safety: omitted launcher seam never shell-opens --------------

    [Fact]
    public async Task BuildModList_omitted_launcher_defaults_to_a_harmless_noop_not_process_start()
    {
        // SAFETY regression guard: when a test builds the VM through BuildModList
        // WITHOUT passing a launchExternal seam, the builder must wire its
        // harmless no-op recorder (TestLauncher.NoOp), never the production
        // Process.Start fallback. A non-Premium update click triggers the
        // external-open path; proving the shared recorder captured the URL (and
        // no OS process was touched) is the guarantee. The production fallback
        // would NOT record into TestLauncher.Opens, so a non-empty result proves
        // the no-op ran instead. This test performs no OS process.
        TestLauncher.Reset();

        // Build WITHOUT a launchExternal argument (the leak path before the fix).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var updateCheck = new FakeUpdateCheckService();
        var nonPremium = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            updateCheck: updateCheck, auth: nonPremium);
        Assert.False(vm.IsPremiumUser);
        updateCheck.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexus.Id, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, false, Outcome: CheckOutcome.Success));
        var row = Row(vm, "DMF");
        Assert.True(row.UpdateAvailable);

        // The non-Premium branch opens the files page via the launcher seam.
        // Because the omitted seam defaults to TestLauncher.NoOp, this records
        // the URL in memory and shells NOTHING open.
        await vm.UpdateCommand.ExecuteAsync(row);

        var opened = Assert.Single(TestLauncher.Opens);
        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            opened.AbsoluteUri);
        TestLauncher.Reset();
    }

    // ---- add Nexus Mods (AddNexusModsCommand) ------------------------------

    [Fact]
    public void AddNexusModsCommand_opens_nexus_games_page_in_browser()
    {
        var launches = new List<Uri>();
        var vm = TestDoubles.BuildModList(localization: Localization,
            launchExternal: uri => { launches.Add(uri); return true; });

        vm.AddNexusModsCommand.Execute(null);

        var opened = Assert.Single(launches);
        Assert.Equal("https://www.nexusmods.com/games/warhammer40kdarktide", opened.AbsoluteUri);
    }

    [Fact]
    public void AddNexusModsCommand_launcher_failure_surfaces_an_alert()
    {
        var dialogs = new FakeDialogService();
        var vm = TestDoubles.BuildModList(localization: Localization,
            dialogs: dialogs,
            launchExternal: _ => false); // simulate launch failure

        vm.AddNexusModsCommand.Execute(null);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("nexusmods.com", alert.Message);
    }

    // ---- automatic-update per-mod progress ---------------------------------

    /// <summary>
    /// Builds a VM with one Nexus+Latest row and returns it with the wired
    /// <see cref="FakeAutomaticUpdateService"/> so the progress tests can raise
    /// <see cref="FakeAutomaticUpdateService.RaiseModUpdateProgress"/>.
    /// </summary>
    private static (ModListViewModel Vm, Guid NexusContainerId, FakeAutomaticUpdateService AutoUpdate)
        BuildForAutoProgress()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var autoUpdate = new FakeAutomaticUpdateService();
        var vm = TestDoubles.BuildModList(profiles, session, repo, automaticUpdates: autoUpdate);
        return (vm, nexus.Id, autoUpdate);
    }

    [Fact]
    public void AutomaticUpdateProgress_marks_only_the_matching_row_then_clears_it()
    {
        // The ModUpdateProgress event sets IsUpdating on the matching row only.
        // An active=true for the installed container sets it; an active=false
        // clears it.
        var (vm, nexusId, autoUpdate) = BuildForAutoProgress();

        Assert.False(Row(vm, "DMF").IsUpdating);

        // Raise active=true for the DMF row.
        autoUpdate.RaiseModUpdateProgress(nexusId, isActive: true);
        Assert.True(Row(vm, "DMF").IsUpdating);

        // Raise active=false to clear it.
        autoUpdate.RaiseModUpdateProgress(nexusId, isActive: false);
        Assert.False(Row(vm, "DMF").IsUpdating);
    }

    [Fact]
    public void AutomaticUpdateProgress_for_an_unknown_container_is_ignored()
    {
        // An event for a container that is not in the current list (removed by a
        // profile switch / reload between the event + the UI-thread callback) is
        // silently ignored: no exception, no row change.
        var (vm, _, autoUpdate) = BuildForAutoProgress();

        autoUpdate.RaiseModUpdateProgress(Guid.NewGuid(), isActive: true);

        Assert.False(Row(vm, "DMF").IsUpdating);
    }

    [Fact]
    public void AutomaticUpdateProgress_for_a_stale_container_after_reload_is_ignored()
    {
        // Simulate a profile switch mid-batch: the VM reloads (rows rebuilt),
        // then a late progress event for a container that is no longer present
        // lands. The stale event must not set IsUpdating on any current row.
        var (vm, _, autoUpdate) = BuildForAutoProgress();

        vm.Reload();
        autoUpdate.RaiseModUpdateProgress(Guid.NewGuid(), isActive: true);

        Assert.False(Row(vm, "DMF").IsUpdating);
    }

    // ---- link external folder (LinkModsCommand) -----------------------------

    /// <summary>Builds the VM with explicit fakes so the link-flow tests can
    /// shape each one. The profile is seeded with one profile (Alpha) active;
    /// the import service + repo share state so the link flow's reload joins the
    /// freshly created linked container. Returns the VM + the fakes the test
    /// asserts on (the dialogs is always the one wired into the VM).</summary>
    private static (ModListViewModel Vm, FakeProfileService Profiles, FakeModRepository Repo, FakeModImportService Import, FakeDialogService Dialogs, FakeProfileSession Session, Guid ProfileId)
        BuildForLinked(
            FakeModImportService? import = null,
            FakeDialogService? dialogs = null,
            Func<string, bool>? launchExternalPath = null,
            FakeModRepository? repo = null)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        repo ??= new FakeModRepository();
        import ??= new FakeModImportService(repo);
        dialogs ??= new FakeDialogService();
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = TestDoubles.BuildModList(profiles, session, repo, import,
            dialogs: dialogs, localization: Localization, launchExternalPath: launchExternalPath);
        return (vm, profiles, repo, import, dialogs, session, a.Id);
    }

    [Fact]
    public async Task LinkMods_creates_a_linked_container_and_adds_it_with_LatestPolicy()
    {
        var (vm, profiles, repo, import, _, _, a) = BuildForLinked();

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });

        // LinkFolder ran once with the picked path; the linked container was
        // created on the repo.
        Assert.Single(import.LinkFolderCalls);
        Assert.Equal("/external/DMF", import.LinkFolderCalls[0]);
        // AddMod ran once with LatestPolicy.
        var addCall = Assert.Single(profiles.AddModCalls);
        Assert.Equal(a, addCall.Id);
        Assert.IsType<LatestPolicy>(addCall.Policy);
        // The new linked container is in the repo (reload joined it).
        Assert.Single(vm.Mods);
        Assert.True(Row(vm, "DMF").IsLinked);
    }

    [Fact]
    public async Task LinkMods_uses_GetBaseName_for_the_collision_peek()
    {
        // GetBaseName validates the mod-folder shape + returns the base name; the
        // link flow peeks it before LinkFolder (same gate as the copy import).
        var (vm, _, _, import, _, _, _) = BuildForLinked();

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });

        Assert.Single(import.GetBaseNameCalls);
        Assert.Equal("/external/DMF", import.GetBaseNameCalls[0]);
    }

    [Fact]
    public async Task LinkMods_aborts_when_the_source_structure_is_invalid()
    {
        // An invalid folder shape (no matching <base>.mod) throws at the
        // GetBaseName peek: the link flow surfaces an alert naming the path +
        // aborts the remaining batch. Nothing is created.
        var dialogs = new FakeDialogService();
        var (vm, profiles, _, import, _, _, _) = BuildForLinked(dialogs: dialogs);
        import.GetBaseNameFunc = path => throw new InvalidOperationException(
            "Invalid mod folder '/external/Bad': no 'Bad.mod' descriptor found.");

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/Bad" });

        // No LinkFolder, no AddMod, no row.
        Assert.Empty(import.LinkFolderCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(vm.Mods);
        // The bad-shape alert surfaced, naming the path + the detail.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("/external/Bad", alert.Message);
        Assert.Contains("Invalid mod folder", alert.Message);
    }

    [Fact]
    public async Task LinkMods_aborts_when_LinkFolder_throws_containment()
    {
        // A containment rejection (the folder overlaps a Curator root) throws
        // inside LinkFolder: the link flow surfaces an alert + aborts. The peek
        // ran (the shape validated); nothing is added.
        var dialogs = new FakeDialogService();
        var (vm, profiles, _, import, _, _, _) = BuildForLinked(dialogs: dialogs);
        import.LinkFolderExceptionQueue = new Queue<Exception?>(new Exception?[]
        {
            new InvalidOperationException("Cannot link '/external/DMF': it overlaps the mods repository root."),
        });

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });

        // LinkFolder was recorded (then threw); AddMod never ran.
        Assert.Single(import.LinkFolderCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(vm.Mods);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("/external/DMF", alert.Message);
        Assert.Contains("overlaps the mods repository root", alert.Message);
    }

    [Fact]
    public async Task LinkMods_refuses_a_base_name_collision_and_aborts()
    {
        // A linked folder whose base name matches an existing profile mod is
        // REFUSED before anything is created: the link flow peeks the base name,
        // asks the profile for a collision (passing the would-be container to
        // exclude a re-link), and on a hit shows an alert + aborts. No LinkFolder,
        // no AddMod.
        var dialogs = new FakeDialogService();
        var (vm, profiles, repo, import, _, _, a) = BuildForLinked(dialogs: dialogs);
        var conflicting = repo.Seed(new UntrackedSource(), "Existing DMF");
        profiles.GetBaseNameCollisionResult =
            new ModListEntry { ContainerId = conflicting.Id, Enabled = true, Order = 0 };

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/dmf" });

        // The peek + the collision check both ran; FindExistingContainer fed the
        // exclusion (null here, a brand-new linked container).
        Assert.Single(import.GetBaseNameCalls);
        Assert.Single(import.FindExistingContainerCalls);
        var collisionCall = Assert.Single(profiles.GetBaseNameCollisionCalls);
        Assert.Null(collisionCall.ExcludeContainerId);
        // Refused BEFORE LinkFolder: no container write, no profile entry.
        Assert.Empty(import.LinkFolderCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(vm.Mods);
        // The collision alert names the conflicting mod.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("Existing DMF", alert.Message);
    }

    [Fact]
    public async Task LinkMods_re_link_of_the_same_path_is_not_a_collision()
    {
        // Re-linking the same external path resolves to the SAME container
        // (Linked identity is the normalized path). The link flow peeks that
        // container (FindExistingContainer) + passes its id as the collision-
        // check exclusion, so the re-link is NOT flagged: LinkFolder returns the
        // existing id (a refresh) + AddMod is its idempotent no-op. No collision
        // alert across either link.
        var dialogs = new FakeDialogService();
        var (vm, profiles, repo, import, _, _, _) = BuildForLinked(dialogs: dialogs);
        // First link creates the container + adds it.
        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });
        var firstContainer = repo.List().Single(c => c.Source is LinkedSource);
        var firstPassCollisionCalls = profiles.GetBaseNameCollisionCalls.Count;

        // Re-link the same path (a trailing-slash variant resolves to the same
        // normalized container id).
        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF/" });

        // The re-link's collision check carried the existing container id as the
        // exclusion (the second collision call this run).
        Assert.Equal(firstPassCollisionCalls + 1, profiles.GetBaseNameCollisionCalls.Count);
        var reLinkCollisionCall = profiles.GetBaseNameCollisionCalls[^1];
        Assert.Equal(firstContainer.Id, reLinkCollisionCall.ExcludeContainerId);
        // No collision alert across either link.
        Assert.Empty(dialogs.AlertCalls);
        // LinkFolder ran twice (the second is a refresh returning the same id);
        // AddMod ran twice (the second is idempotent for the existing entry).
        Assert.Equal(2, import.LinkFolderCalls.Count);
        Assert.Equal(2, profiles.AddModCalls.Count);
        Assert.Equal(firstContainer.Id, profiles.AddModCalls[1].ContainerId);
        // Still one row (the re-link deduped).
        Assert.Single(vm.Mods);
    }

    [Fact]
    public async Task LinkMods_processes_multiple_paths_sequentially()
    {
        var (vm, profiles, _, import, _, _, _) = BuildForLinked();

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF", "/external/SoundPack" });

        // One peek + one LinkFolder + one AddMod per path, in order.
        Assert.Equal(2, import.GetBaseNameCalls.Count);
        Assert.Equal(2, import.LinkFolderCalls.Count);
        Assert.Equal(2, profiles.AddModCalls.Count);
        Assert.Equal(2, vm.Mods.Count);
    }

    [Fact]
    public async Task LinkMods_aborts_the_batch_on_a_failed_peek()
    {
        // A failed peek on the second path aborts the batch: the first links
        // fine, the third is never reached.
        var dialogs = new FakeDialogService();
        var (vm, profiles, _, import, _, _, _) = BuildForLinked(dialogs: dialogs);
        import.GetBaseNameFunc = path => path.EndsWith("Bad")
            ? throw new InvalidOperationException("Invalid mod folder '/external/Bad'.")
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        await vm.LinkModsCommand.ExecuteAsync(
            new[] { "/external/One", "/external/Bad", "/external/Three" });

        // First linked; second threw; third never reached.
        Assert.Equal(2, import.GetBaseNameCalls.Count);
        Assert.Single(import.LinkFolderCalls);
        Assert.Single(profiles.AddModCalls);
        Assert.Single(vm.Mods);
        Assert.Single(dialogs.AlertCalls);
    }

    [Fact]
    public async Task LinkMods_with_no_active_profile_logs_and_does_nothing()
    {
        var profiles = TestDoubles.Profiles();
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var vm = TestDoubles.BuildModList(profiles,
            new FakeProfileSession { ActiveProfileId = null }, repo, import);

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });

        Assert.Empty(import.LinkFolderCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task LinkMods_empty_path_list_is_a_noop()
    {
        var (vm, profiles, _, import, _, _, _) = BuildForLinked();

        await vm.LinkModsCommand.ExecuteAsync(Array.Empty<string>());

        Assert.Empty(import.LinkFolderCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    // ---- open external folder (OpenFolderCommand) --------------------------

    [Fact]
    public async Task OpenFolder_launches_the_file_manager_at_the_external_path()
    {
        // An available linked row's open-folder click routes the external path
        // to the injectable launcher seam.
        const string externalPath = "/external/DMF";
        var openedPaths = new List<string>();
        var (vm, _, repo, _, _, _, _) = BuildForLinked(launchExternalPath: path =>
        {
            openedPaths.Add(path);
            return true;
        });
        await vm.LinkModsCommand.ExecuteAsync(new[] { externalPath });
        var row = Row(vm, "DMF");

        await vm.OpenFolderCommand.ExecuteAsync(row);

        Assert.Equal(repo.List().Single(c => c.Source is LinkedSource).Id, row.ContainerId);
        var opened = Assert.Single(openedPaths);
        // LinkFolder normalizes via Path.GetFullPath, so the launched path is
        // the canonical form: assert equality on the same normalization rather
        // than a suffix match.
        Assert.Equal(Path.GetFullPath(externalPath), opened);
    }

    [Fact]
    public async Task OpenFolder_shows_an_alert_when_the_launcher_fails()
    {
        var dialogs = new FakeDialogService();
        var (vm, _, _, _, _, _, _) = BuildForLinked(dialogs: dialogs, launchExternalPath: _ => false);
        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });
        var row = Row(vm, "DMF");

        await vm.OpenFolderCommand.ExecuteAsync(row);

        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains("DMF", alert.Message);
    }

    [Fact]
    public async Task OpenFolder_is_a_noop_for_a_non_linked_row()
    {
        var openedPaths = new List<string>();
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Enabled = true, Order = 0 });
        var vm = TestDoubles.BuildModList(profiles,
            new FakeProfileSession { ActiveProfileId = a.Id }, repo,
            launchExternalPath: path => { openedPaths.Add(path); return true; });
        var row = Row(vm, "DMF");

        await vm.OpenFolderCommand.ExecuteAsync(row);

        Assert.Empty(openedPaths);
    }

    [Fact]
    public async Task OpenFolder_is_a_noop_for_a_broken_linked_row()
    {
        var openedPaths = new List<string>();
        var dialogs = new FakeDialogService();
        var (vm, profiles, repo, _, _, _, profileId) = BuildForLinked(
            dialogs: dialogs,
            launchExternalPath: path => { openedPaths.Add(path); return true; });
        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });
        var container = repo.List().Single(c => c.Source is LinkedSource);
        // Mark the external folder unavailable + reload so the row reflects it.
        repo.ExternalUnavailableIds.Add(container.Id);
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = container.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        vm.Reload();
        var row = Row(vm, "DMF");

        Assert.True(row.IsLinkedBroken);
        await vm.OpenFolderCommand.ExecuteAsync(row);

        Assert.Empty(openedPaths);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- row building: linked badge two-state + policy gating ---------------

    [Fact]
    public void Reload_linked_available_row_shows_External_badge_and_disables_policy_edit()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        var row = Row(vm, "DMF");
        Assert.True(row.IsLinked);
        Assert.True(row.IsLinkedAvailable);
        Assert.False(row.IsLinkedBroken);
        Assert.False(row.IsExternalBroken);
        Assert.False(row.IsBadgeHyperlink); // the Nexus/Untracked badge is suppressed
        Assert.False(row.IsPolicyEditable); // policy ComboBox disabled
        Assert.Equal("External", row.SourceBadgeText);
        // A linked container carries no versions, so the update action never shows.
        Assert.False(row.CanShowUpdateAction);
        Assert.Equal("/external/DMF", row.ExternalFolderPath);
    }

    [Fact]
    public void Reload_linked_broken_row_shows_FolderUnavailable_and_warning_state()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        repo.ExternalUnavailableIds.Add(linked.Id); // simulate the folder missing
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        var row = Row(vm, "DMF");
        Assert.True(row.IsLinked);
        Assert.False(row.IsLinkedAvailable);
        Assert.True(row.IsLinkedBroken);
        Assert.True(row.IsExternalBroken);
        Assert.False(row.IsBadgeHyperlink);
        Assert.False(row.IsPolicyEditable);
        Assert.Equal("Folder unavailable", row.SourceBadgeText);
    }

    [Fact]
    public void Reload_managed_row_is_unaffected_by_linked_availability_flag()
    {
        // A Nexus/Untracked row never reports broken (the repo returns true for
        // managed containers); its badge + policy editor are unchanged.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Enabled = true, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        var row = Row(vm, "DMF");
        Assert.False(row.IsLinked);
        Assert.False(row.IsLinkedAvailable);
        Assert.False(row.IsLinkedBroken);
        Assert.False(row.IsExternalBroken);
        Assert.True(row.IsBadgeHyperlink);
        Assert.True(row.IsPolicyEditable);
        Assert.Equal("Nexus #8 · 1.0", row.SourceBadgeText);
    }

    // ---- broken linked row interactions still route to the parent -----------

    [Fact]
    public void Broken_linked_row_enabled_toggle_routes_to_SetModEnabled()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        repo.ExternalUnavailableIds.Add(linked.Id);
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");
        Assert.True(row.IsLinkedBroken);

        // Flip the checkbox bound state + route through the command.
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        var call = Assert.Single(profiles.SetModEnabledCalls);
        Assert.Equal(a.Id, call.Id);
        Assert.Equal(linked.Id, call.ContainerId);
        Assert.False(call.Enabled);
    }

    [Fact]
    public void Broken_linked_row_move_up_routes_to_SetModOrder()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var first = repo.Seed(new UntrackedSource(), "First", "1.0");
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        repo.ExternalUnavailableIds.Add(linked.Id);
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = first.Id, Enabled = true, Order = 0 },
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 1, Policy = ModVersionPolicy.Latest });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var brokenRow = Row(vm, "DMF");
        Assert.True(brokenRow.IsLinkedBroken);

        vm.MoveUpCommand.Execute(brokenRow);

        // SetModOrder ran (reorder not blocked for a broken linked row).
        Assert.Single(profiles.SetModOrderCalls);
    }

    [Fact]
    public async Task Broken_linked_row_remove_routes_to_RemoveMod_after_confirm()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        repo.ExternalUnavailableIds.Add(linked.Id);
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo, dialogs: dialogs);
        var brokenRow = Row(vm, "DMF");
        Assert.True(brokenRow.IsLinkedBroken);

        await vm.RemoveCommand.ExecuteAsync(brokenRow);

        var call = Assert.Single(profiles.RemoveModCalls);
        Assert.Equal(a.Id, call.Id);
        Assert.Equal(linked.Id, call.ContainerId);
    }

    // ---- HasPendingChanges: structural edits flag the session ---------------
    //
    // Each structural / version-affecting edit sets the session's
    // HasPendingChanges so the shell's status strip surfaces a "changes pending"
    // indicator while the game runs (Curator does not re-stage mid-session).

    private static (ModListViewModel Vm, FakeProfileSession Session) BuildWithSession(
        FakeProfileService? profiles = null, FakeModRepository? repo = null,
        FakeModImportService? importService = null, FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles()[0].Id,
        };
        var vm = TestDoubles.BuildModList(profiles, session, repo, importService,
            dialogs: dialogs, localization: Localization);
        return (vm, session);
    }

    private static (ModListViewModel Vm, FakeProfileSession Session, ProfileSummary Profile)
        BuildWithOneMod(FakeDialogService? dialogs = null)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 });
        var (vm, session) = BuildWithSession(profiles, repo, dialogs: dialogs);
        return (vm, session, a);
    }

    [Fact]
    public void ToggleEnabled_sets_HasPendingChanges()
    {
        var (vm, session, _) = BuildWithOneMod();
        Assert.False(session.HasPendingChanges);
        var row = Row(vm, "DMF");

        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public void MoveUp_sets_HasPendingChanges()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var (vm, session) = BuildWithSession(profiles, repo);
        Assert.False(session.HasPendingChanges);

        vm.MoveUpCommand.Execute(Row(vm, "SoundPack"));

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public void SetPolicyLatest_sets_HasPendingChanges()
    {
        var (vm, session, _) = BuildWithOneMod();
        Assert.False(session.HasPendingChanges);

        vm.SetPolicyLatestCommand.Execute(Row(vm, "DMF"));

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task Remove_sets_HasPendingChanges()
    {
        var (vm, session, _) = BuildWithOneMod(
            dialogs: new FakeDialogService { ConfirmResult = true });
        Assert.False(session.HasPendingChanges);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public void AutoSort_sets_HasPendingChanges()
    {
        var (vm, session, _) = BuildWithOneMod();
        Assert.False(session.HasPendingChanges);

        vm.AutoSortCommand.Execute(null);

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task LinkMods_sets_HasPendingChanges_after_a_successful_link()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var (vm, session) = BuildWithSession(profiles, repo, import);
        Assert.False(session.HasPendingChanges);

        await vm.LinkModsCommand.ExecuteAsync(new[] { "/external/DMF" });

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task Update_premium_success_sets_HasPendingChanges()
    {
        // The Premium install branch changes a mod's version, so it flags the
        // session like any other version-affecting edit. Built with a captured
        // session so the flag is assertable.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var uc = new FakeUpdateCheckService();
        var acquisition = new FakeModAcquisitionService();
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            updateCheck: uc, acquisition: acquisition);
        uc.RaiseCheckCompleted(new UpdateCheckResult(
            new[] { new ModUpdateInfo(nexus.Id, 8, "DMF", "1.0", DateTimeOffset.UtcNow) },
            DateTimeOffset.UtcNow, false, Thorough: false, Outcome: CheckOutcome.Success));
        Assert.True(vm.IsPremiumUser);
        Assert.False(session.HasPendingChanges);

        await vm.UpdateCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public void AutomaticUpdatesApplied_sets_HasPendingChanges()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Order = 0, Policy = ModVersionPolicy.Latest });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var automaticUpdates = new FakeAutomaticUpdateService();
        var vm = TestDoubles.BuildModList(profiles, session, repo,
            automaticUpdates: automaticUpdates, localization: Localization);
        Assert.False(session.HasPendingChanges);

        automaticUpdates.RaiseUpdatesApplied();

        Assert.True(session.HasPendingChanges);
    }
}
