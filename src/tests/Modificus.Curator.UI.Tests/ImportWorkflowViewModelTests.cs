using System.ComponentModel;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="ImportWorkflowViewModel"/> behaviors: the standalone inline
/// import workflow state machine (editing, processing, terminal failure) and
/// the per-item import orchestration, against the same hand-rolled fakes the
/// mod-list VM tests use. Covers state/fields, processing/sequencing,
/// failure/collision, profile-change handling, and localization refresh.
/// </summary>
/// <remarks>
/// The controllably-blocked fake import (<see cref="FakeModImportService.ImportGate"/>)
/// lets the processing state be observed mid-import deterministically (no
/// sleeps) and drives the active-profile-change-during-processing edge without
/// flakiness.
/// </remarks>
public sealed class ImportWorkflowViewModelTests
{
    /// <summary>
    /// Builds a workflow VM over the production-shape fakes, with one active
    /// profile. The import fake shares the repository so the reload-join path
    /// (the mod-list's concern) has somewhere to read from. A fresh
    /// LocalizationService is created per call so short-lived test VMs do not
    /// all subscribe to one static singleton.
    /// </summary>
    private static (ImportWorkflowViewModel Vm, FakeProfileService Profiles, FakeProfileSession Session, FakeModRepository Repo, FakeModImportService Import)
        Build(FakeProfileService? profiles = null,
              FakeProfileSession? session = null,
              FakeModRepository? repo = null,
              FakeModImportService? import = null,
              LocalizationService? localization = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        import ??= new FakeModImportService(repo);
        localization ??= new LocalizationService();
        var vm = new ImportWorkflowViewModel(
            profiles, session, repo, import, new ModCardsGate(), localization,
            NullLogger<ImportWorkflowViewModel>.Instance);
        return (vm, profiles, session, repo, import);
    }

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    /// <summary>
    /// Starts a batch and switches the editing form to Untracked (the simplest
    /// valid metadata: just a name), so Import is enabled without a URL/version.
    /// </summary>
    private static void StartUntracked(ImportWorkflowViewModel vm, params string[] paths)
    {
        vm.StartBatchCommand.Execute(paths);
        vm.SourceChoice = ImportSource.Untracked;
    }

    // ---- default state + availability -------------------------------------

    [Fact]
    public void Default_state_is_inactive_and_not_available()
    {
        var (vm, _, _, _, _) = Build();

        Assert.False(vm.IsActive);
        Assert.False(vm.IsEditing);
        Assert.False(vm.IsProcessing);
        Assert.False(vm.IsFailure);
        Assert.Equal(0, vm.CurrentNumber);
        Assert.Equal(0, vm.TotalCount);
        Assert.Empty(vm.CurrentPath);
        Assert.Empty(vm.HeaderText);
    }

    [Fact]
    public void StartBatch_with_no_active_profile_is_a_noop()
    {
        var profiles = TestDoubles.Profiles(Profile("Alpha"));
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = null;

        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });

        Assert.False(vm.IsActive);
    }

    [Fact]
    public void StartBatch_with_empty_or_null_paths_is_a_noop()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;

        vm.StartBatchCommand.Execute(Array.Empty<string>());
        Assert.False(vm.IsActive);

        vm.StartBatchCommand.Execute(null);
        Assert.False(vm.IsActive);
    }

    // ---- start copies paths, derives names, applies defaults ---------------

    [Fact]
    public void Start_copies_paths_shows_item_1_of_N_and_applies_Nexus_Latest_defaults()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;

        vm.StartBatchCommand.Execute(new[] { "/mods/DMF", "/mods/SoundPack.zip", "/mods/Extra.7z" });

        Assert.True(vm.IsActive);
        Assert.True(vm.IsEditing);
        Assert.Equal(1, vm.CurrentNumber);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal("/mods/DMF", vm.CurrentPath);
        Assert.Equal("DMF", vm.ModName); // folder name
        // Defaults: Nexus, empty Version/URL, Latest.
        Assert.Equal(ImportSource.Nexus, vm.SourceChoice);
        Assert.True(vm.IsRemote);
        Assert.True(vm.IsVersionVisible);
        Assert.Empty(vm.Version);
        Assert.Empty(vm.Url);
        Assert.Equal(ImportWorkflowViewModel.ImportPolicyChoice.Latest, vm.PolicyChoice);
        // Header resolves the localized template with the position.
        Assert.Contains("1", vm.HeaderText);
        Assert.Contains("3", vm.HeaderText);
    }

    [Theory]
    [InlineData("/mods/Foo", "Foo")]          // folder
    [InlineData("/mods/Foo.zip", "Foo")]      // zip
    [InlineData("/mods/Foo.7z", "Foo")]       // 7z
    [InlineData("/mods/Foo.rar", "Foo")]      // rar
    [InlineData("/mods/Foo.tar.gz", "Foo.tar")] // only the last extension is stripped
    public void Start_derives_the_default_name_from_folder_or_archive_stem(string path, string expected)
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;

        vm.StartBatchCommand.Execute(new[] { path });

        Assert.Equal(expected, vm.ModName);
    }

    [Fact]
    public void Start_captures_an_ordered_copy_so_caller_mutation_does_not_affect_the_batch()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        var source = new List<string> { "/mods/One", "/mods/Two" };

        vm.StartBatchCommand.Execute(source);
        source.Clear(); // mutate the caller's list after StartBatch

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal("/mods/One", vm.CurrentPath);
    }

    // ---- second start rejected in editing, processing, failure ------------

    [Fact]
    public void A_second_start_is_rejected_while_editing()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/One", "/mods/Two" });

        // A second start must not replace the in-flight batch.
        vm.StartBatchCommand.Execute(new[] { "/mods/Other" });

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal("/mods/One", vm.CurrentPath);
    }

    [Fact]
    public async Task A_second_start_is_rejected_while_processing()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        import.ImportGate = new TaskCompletionSource<bool>();
        StartUntracked(vm, "/mods/One", "/mods/Two");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        vm.StartBatchCommand.Execute(new[] { "/mods/Other" });

        // The second start did not replace the batch.
        Assert.Equal(2, vm.TotalCount);
        Assert.True(vm.IsProcessing);

        import.ImportGate!.SetResult(true);
        await task;
    }

    [Fact]
    public async Task A_second_start_is_rejected_while_showing_a_failure()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        import.GetBaseNameFunc = _ => throw new InvalidOperationException("bad source");
        StartUntracked(vm, "/mods/One", "/mods/Two");
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailure);

        vm.StartBatchCommand.Execute(new[] { "/mods/Other" });

        // Still on the original failure: header + path retained.
        Assert.True(vm.IsFailure);
        Assert.Equal(2, vm.TotalCount);
        Assert.Equal("/mods/One", vm.CurrentPath);
    }

    // ---- editing field validation (mirrors the inline import card) --------

    [Fact]
    public void Nexus_source_shows_version_and_url_and_requires_both()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });

        Assert.True(vm.IsRemote);
        Assert.True(vm.IsVersionVisible);
        Assert.False(vm.CanImport); // no version, no url
        Assert.False(vm.ImportCurrentCommand.CanExecute(null));
    }

    [Fact]
    public void Untracked_source_hides_remote_fields_and_needs_only_a_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.SourceChoice = ImportSource.Untracked;

        Assert.False(vm.IsRemote);
        Assert.False(vm.IsVersionVisible);
        Assert.True(vm.CanImport);
        Assert.True(vm.ImportCurrentCommand.CanExecute(null));
    }

    [Fact]
    public void Nexus_with_valid_url_version_and_name_enables_import()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.Version = "1.2";
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/12345";

        Assert.True(vm.CanImport);
        Assert.Empty(vm.VersionValidationMessage);
        Assert.Empty(vm.UrlValidationMessage);
    }

    [Fact]
    public void Nexus_accepts_a_bare_mod_id_in_the_url_field()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.Version = "1.0";
        vm.Url = "12345";

        Assert.True(vm.CanImport);
    }

    [Fact]
    public void Nexus_with_an_invalid_url_disables_import_and_shows_a_message()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.Version = "1.0";
        vm.Url = "not a nexus url";

        Assert.False(vm.CanImport);
        Assert.NotEmpty(vm.UrlValidationMessage);
    }

    [Fact]
    public void Nexus_with_an_empty_version_disables_import_and_shows_the_required_message()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.Version = "   ";
        vm.Url = "12345";

        Assert.False(vm.CanImport);
        Assert.NotEmpty(vm.VersionValidationMessage);
    }

    [Fact]
    public void An_empty_mod_name_disables_import()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });
        vm.ModName = "   ";
        vm.SourceChoice = ImportSource.Untracked;

        Assert.False(vm.CanImport);
    }

    [Fact]
    public void SourceChoiceIndex_and_PolicyChoiceIndex_map_to_and_from_the_enums()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        vm.StartBatchCommand.Execute(new[] { "/mods/DMF" });

        Assert.Equal(1, vm.SourceChoiceIndex); // Nexus default
        vm.SourceChoiceIndex = 0;
        Assert.Equal(ImportSource.Untracked, vm.SourceChoice);

        Assert.Equal(0, vm.PolicyChoiceIndex); // Latest default
        vm.PolicyChoiceIndex = 1;
        Assert.Equal(ImportWorkflowViewModel.ImportPolicyChoice.Pinned, vm.PolicyChoice);
    }

    // ---- cancel while editing ---------------------------------------------

    [Fact]
    public void Cancel_while_editing_clears_the_batch_without_changes()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two", "/mods/Three");

        vm.CancelBatchCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Equal(0, vm.TotalCount);
        Assert.Empty(import.Imports); // nothing imported
        Assert.True(vm.CancelBatchCommand.CanExecute(null) is false); // inactive
    }

    [Fact]
    public async Task Cancel_is_unavailable_while_processing()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        import.ImportGate = new TaskCompletionSource<bool>();
        StartUntracked(vm, "/mods/One");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.False(vm.CancelBatchCommand.CanExecute(null));

        import.ImportGate!.SetResult(true);
        await task;
    }

    // ---- processing + sequencing ------------------------------------------

    [Fact]
    public async Task Success_calls_GetBaseName_collision_check_Import_then_AddMod_in_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        // The full sequence ran once, in order (each list has exactly one entry).
        Assert.Single(import.GetBaseNameCalls);
        Assert.Equal("/mods/DMF", import.GetBaseNameCalls[0]);
        Assert.Single(import.FindExistingContainerCalls);
        Assert.Single(profiles.GetBaseNameCollisionCalls);
        Assert.Single(import.Imports);
        Assert.Equal("DMF", import.Imports[0].ModName);
        var addCall = Assert.Single(profiles.AddModCalls);
        Assert.Equal(a.Id, addCall.Id);
    }

    [Fact]
    public async Task Success_raises_the_event_sets_pending_advances_and_closes_after_last()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        Guid? eventProfile = null;
        vm.ItemImported += (_, id) => eventProfile = id;
        StartUntracked(vm, "/mods/Only");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, eventProfile);
        Assert.True(session.HasPendingChanges);
        Assert.False(vm.IsActive); // closed after the last
    }

    [Fact]
    public async Task Success_advances_to_the_next_item_when_more_remain()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        int events = 0;
        vm.ItemImported += (_, _) => events++;
        StartUntracked(vm, "/mods/One", "/mods/Two");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.Equal(1, events);
        Assert.True(vm.IsActive);
        Assert.True(vm.IsEditing);
        Assert.Equal(2, vm.CurrentNumber);
        Assert.Equal("/mods/Two", vm.CurrentPath);
        Assert.Equal("Two", vm.ModName); // next item's derived name
        // Defaults reset for the new item.
        Assert.Equal(ImportSource.Nexus, vm.SourceChoice);
        Assert.Empty(vm.Version);
        Assert.Empty(vm.Url);
    }

    [Fact]
    public async Task Pinned_uses_the_opaque_version_id_returned_by_Import()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, import) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");
        vm.PolicyChoice = ImportWorkflowViewModel.ImportPolicyChoice.Pinned;

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        var addCall = Assert.Single(profiles.AddModCalls);
        var pinned = Assert.IsType<PinnedPolicy>(addCall.Policy);
        // The pin references the imported version's opaque folder id, not a
        // placeholder empty string.
        Assert.NotEmpty(pinned.VersionId);
    }

    [Fact]
    public async Task Pinned_pins_to_the_repo_version_folder_id_end_to_end()
    {
        // End-to-end shape: the PinnedPolicy version id matches the version the
        // wired repo recorded for the import (the opaque ModVersion.Folder).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        StartUntracked(vm, "/mods/DMF");
        vm.PolicyChoice = ImportWorkflowViewModel.ImportPolicyChoice.Pinned;

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        var addCall = Assert.Single(profiles.AddModCalls);
        var pinned = Assert.IsType<PinnedPolicy>(addCall.Policy);
        var container = repo.Get(addCall.ContainerId);
        Assert.NotNull(container);
        Assert.Contains(container!.Versions, v => v.Folder == pinned.VersionId);
    }

    [Fact]
    public async Task Earlier_successes_remain_when_a_later_item_is_cancelled()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two");

        // Import the first (success), then cancel the second while editing.
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        vm.CancelBatchCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Single(profiles.AddModCalls); // first item still in the profile
        var mods = profiles.GetModList(a.Id);
        Assert.Single(mods);
    }

    [Fact]
    public async Task Earlier_successes_remain_when_a_later_item_collides()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var conflicting = repo.Seed(new UntrackedSource(), "Conflict");
        var import = new FakeModImportService(repo);
        var (vm, _, session, _, _) = Build(profiles: profiles, repo: repo, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two");

        // First imports cleanly.
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        // Arm a collision for the second item, then re-select Untracked
        // (LoadCurrentItem reset the fields to the Nexus default) and confirm.
        profiles.GetBaseNameCollisionResult =
            new ModListEntry { ContainerId = conflicting.Id, Enabled = true, Order = 0 };
        vm.SourceChoice = ImportSource.Untracked;
        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        // The first item's AddMod survived; the colliding second was refused
        // (no Import, no AddMod for it).
        var addCall = Assert.Single(profiles.AddModCalls);
        Assert.NotEqual(conflicting.Id, addCall.ContainerId);
    }

    [Fact]
    public async Task Earlier_successes_remain_when_a_later_import_throws()
    {
        // A late failure inside Import (after the base name validated) on the
        // second item: the first imported mod stays.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportExceptionQueue = new Queue<Exception?>(new Exception?[]
            {
                null, // first item succeeds
                new IOException("extract failed"),
            }),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Bad");

        await vm.ImportCurrentCommand.ExecuteAsync(null); // One ok
        vm.SourceChoice = ImportSource.Untracked;
        await vm.ImportCurrentCommand.ExecuteAsync(null); // Bad: Import throws

        Assert.True(vm.IsFailure);
        Assert.Single(profiles.AddModCalls); // only One
    }

    [Fact]
    public async Task Earlier_successes_remain_when_a_later_item_fails()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = path => path.EndsWith("Bad")
                ? throw new InvalidOperationException("Invalid source")
                : "ok",
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Bad", "/mods/Three");

        // Import the first (One succeeds). LoadCurrentItem then resets the
        // second item's fields to the Nexus default, so re-select Untracked
        // (the user sets metadata per item) before confirming the second.
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.Equal(2, vm.CurrentNumber);
        vm.SourceChoice = ImportSource.Untracked;
        await vm.ImportCurrentCommand.ExecuteAsync(null); // Bad fails

        Assert.True(vm.IsFailure);
        Assert.Single(profiles.AddModCalls); // only One was added
    }

    [Fact]
    public async Task Processing_exposes_busy_state_while_a_blocked_import_runs()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportGate = new TaskCompletionSource<bool>(),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        // Start the import; the worker is blocked inside Import on the gate.
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);

        // The processing state is observable while the worker is blocked.
        Assert.True(vm.IsProcessing);
        Assert.False(vm.IsEditing);
        Assert.False(vm.IsFailure);
        Assert.NotNull(import.ImportGate);

        // Release the worker; the continuation returns to the editing/inactive state.
        import.ImportGate!.SetResult(true);
        await task;

        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public async Task ImportCurrent_cannot_execute_concurrently()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportGate = new TaskCompletionSource<bool>(),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);

        // While processing, the command reports not-executable (state gate +
        // the AsyncRelayCommand's own concurrency guard).
        Assert.False(vm.ImportCurrentCommand.CanExecute(null));

        import.ImportGate!.SetResult(true);
        await task;
    }

    // ---- failure + collision ----------------------------------------------

    [Fact]
    public async Task Base_name_validation_failure_becomes_terminal_inline_failure()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new InvalidOperationException("Invalid mod source"),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/Bad", "/mods/Two");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        Assert.True(vm.IsActive); // failure is active (card shows)
        Assert.NotEmpty(vm.FailureMessage);
        Assert.Contains("/mods/Bad", vm.FailureMessage);
        Assert.Contains("Invalid mod source", vm.FailureMessage);
        // Nothing created for the failed item; the remainder never ran.
        Assert.Empty(import.Imports);
        Assert.Empty(profiles.AddModCalls);
        // Header + path retained on the failure card.
        Assert.Equal("/mods/Bad", vm.CurrentPath);
        Assert.Equal(2, vm.TotalCount);
    }

    [Fact]
    public async Task Late_import_failure_becomes_terminal_inline_failure()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportExceptionQueue = new Queue<Exception?>(new Exception?[]
            {
                new IOException("disk full"),
            }),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        Assert.Contains("disk full", vm.FailureMessage);
        // GetBaseName ran (validated ok), but Import threw: no AddMod.
        Assert.Single(import.GetBaseNameCalls);
        Assert.Single(import.Imports); // Import was called (recorded) before throwing
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task Collision_names_path_base_and_conflicting_mod_with_no_Import_or_AddMod()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var conflicting = repo.Seed(new UntrackedSource(), "Existing DMF");
        profiles.GetBaseNameCollisionResult =
            new ModListEntry { ContainerId = conflicting.Id, Enabled = true, Order = 0 };
        var import = new FakeModImportService(repo);
        var (vm, _, session, _, _) = Build(profiles: profiles, repo: repo, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/dmf.zip");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        // The collision message names the path, the base folder, and the
        // conflicting mod.
        Assert.Contains("/mods/dmf.zip", vm.FailureMessage);
        Assert.Contains("Existing DMF", vm.FailureMessage);
        // The collision was detected at the peek + collision check, BEFORE Import.
        Assert.Single(import.GetBaseNameCalls);
        Assert.Single(profiles.GetBaseNameCollisionCalls);
        Assert.Empty(import.Imports);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task Re_add_of_the_same_container_is_excluded_from_collision_and_succeeds()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var existing = repo.Seed(new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = existing.Id, Enabled = true, Order = 0 });
        var (vm, _, session, _, _) = Build(profiles: profiles, repo: repo, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        // The existing container id was passed as the collision exclusion.
        var collisionCall = Assert.Single(profiles.GetBaseNameCollisionCalls);
        Assert.Equal(existing.Id, collisionCall.ExcludeContainerId);
        // The re-add succeeded: Import (refresh) + AddMod (idempotent).
        Assert.Single(import.Imports);
        Assert.Single(profiles.AddModCalls);
        Assert.False(vm.IsFailure);
    }

    [Fact]
    public async Task No_local_import_failure_path_calls_a_dialog()
    {
        // The workflow VM has no IDialogService dependency, so a local-import
        // failure is structurally incapable of surfacing a modal alert. The
        // terminal failure shows inline via FailureMessage instead.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new InvalidOperationException("bad"),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/Bad");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        // The failure is inline, not a dialog. (No IDialogService was even
        // passed to the VM; this assertion documents that contract.)
        Assert.True(vm.IsFailure);
        Assert.NotEmpty(vm.FailureMessage);
    }

    [Fact]
    public async Task Close_clears_terminal_failure_and_permits_a_new_batch()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new InvalidOperationException("bad"),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/Bad");
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailure);

        vm.CloseFailureCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Empty(vm.FailureMessage);

        // A new batch can start.
        vm.StartBatchCommand.Execute(new[] { "/mods/Again" });
        Assert.True(vm.IsActive);
        Assert.True(vm.IsEditing);
        Assert.Equal("/mods/Again", vm.CurrentPath);
    }

    // ---- profile-change handling ------------------------------------------

    [Fact]
    public void Active_profile_change_while_editing_resets_the_workflow()
    {
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var (vm, _, session, _, _) = Build(profiles: profiles);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two");
        Assert.True(vm.IsActive);

        session.ActiveProfileId = b.Id;

        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task Active_profile_change_while_failure_resets_the_workflow()
    {
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new InvalidOperationException("bad"),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/Bad");
        await vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailure);

        session.ActiveProfileId = b.Id;

        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task Active_profile_change_during_processing_finishes_current_aborts_rest_no_pending()
    {
        // The confirmed item finishes against the CAPTURED profile (an imported
        // version keeps its profile reference); the remaining queue is aborted;
        // the workflow resets; the NEW active profile's pending indicator is
        // never set for the old profile's success.
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
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        StartUntracked(vm, "/mods/One", "/mods/Two");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        // The active profile switches mid-processing.
        session.ActiveProfileId = b.Id;

        // Release the blocked import: it finishes against the captured (old)
        // profile, then the workflow aborts the rest and resets.
        import.ImportGate!.SetResult(true);
        await task;

        // The current item landed on the captured profile (a), not the new (b).
        var addCall = Assert.Single(profiles.AddModCalls);
        Assert.Equal(a.Id, addCall.Id);
        // Only one item imported (the second path was aborted).
        Assert.Single(import.Imports);
        // The workflow reset.
        Assert.False(vm.IsActive);
        // The new active profile was NOT marked pending for the old success.
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public async Task Success_after_profile_change_still_fires_the_event_with_captured_id()
    {
        // The notification always fires on success (carrying the captured
        // profile id); the consumer decides whether a reload is relevant.
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
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        Guid? eventProfile = null;
        vm.ItemImported += (_, id) => eventProfile = id;
        StartUntracked(vm, "/mods/One");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);

        session.ActiveProfileId = b.Id;
        import.ImportGate!.SetResult(true);
        await task;

        Assert.Equal(a.Id, eventProfile); // captured, not the new active
    }

    [Fact]
    public async Task Active_profile_change_during_processing_then_collision_resets_not_failure()
    {
        // When the profile changes mid-processing and the confirmed item then
        // hits a collision, the workflow resets rather than showing a failure
        // card over the newly active profile.
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var repo = new FakeModRepository();
        var conflicting = repo.Seed(new UntrackedSource(), "Conflict");
        profiles.GetBaseNameCollisionResult =
            new ModListEntry { ContainerId = conflicting.Id, Enabled = true, Order = 0 };
        var import = new FakeModImportService(repo)
        {
            GetBaseNameGate = new TaskCompletionSource<bool>(),
        };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        StartUntracked(vm, "/mods/DMF");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        // The profile switches while GetBaseName is blocked on the worker.
        session.ActiveProfileId = b.Id;

        // Release: GetBaseName returns, the collision check runs (on the UI
        // context), and the collision is detected. Because the profile changed,
        // the workflow resets instead of showing a failure card.
        import.GetBaseNameGate!.SetResult(true);
        await task;

        Assert.False(vm.IsActive);
        Assert.False(vm.IsFailure);
        Assert.False(vm.IsProcessing);
        Assert.Empty(import.Imports); // collision refused; no Import ran
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task Active_profile_change_during_processing_then_import_failure_resets_not_failure()
    {
        // When the profile changes mid-processing and the confirmed item then
        // fails inside Import, the workflow resets rather than showing a failure
        // card over the newly active profile.
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportGate = new TaskCompletionSource<bool>(),
            ImportExceptionQueue = new Queue<Exception?>(new Exception?[]
            {
                new IOException("extract failed"),
            }),
        };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var (vm, _, _, _, _) = Build(profiles, session, import: import);
        StartUntracked(vm, "/mods/DMF");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        session.ActiveProfileId = b.Id;
        import.ImportGate!.SetResult(true);
        await task;

        Assert.False(vm.IsActive);
        Assert.False(vm.IsFailure);
        Assert.False(vm.IsProcessing);
        Assert.False(session.HasPendingChanges);
    }

    // ---- unexpected-exception recovery -------------------------------------

    [Fact]
    public async Task An_unexpected_exception_from_Import_surfaces_a_recoverable_failure()
    {
        // A plain Exception (not one of the expected import families) must not
        // crash through the AsyncRelayCommand path or strand Processing. It
        // recovers to a generic inline failure that hides raw technical details.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportExceptionQueue = new Queue<Exception?>(new Exception?[]
            {
                new Exception("boom"), // not an expected import exception
            }),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        Assert.False(vm.IsProcessing);
        Assert.NotEmpty(vm.FailureMessage);
        // The generic message does not expose raw technical details.
        Assert.DoesNotContain("boom", vm.FailureMessage);
        // Close recovers to inactive.
        vm.CloseFailureCommand.Execute(null);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task An_unexpected_exception_from_GetBaseName_surfaces_a_recoverable_failure()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new Exception("unexpected peek"), // not expected
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        Assert.False(vm.IsProcessing);
        Assert.DoesNotContain("unexpected peek", vm.FailureMessage);
    }

    [Fact]
    public async Task AddMod_failure_after_successful_import_recovers_without_event_or_pending()
    {
        // The import succeeded in the repository but AddMod failed: the workflow
        // recovers to a generic failure, does NOT emit ItemImported, and does
        // NOT mark pending (the profile reference never landed).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        profiles.AddModThrows = new Exception("profile write failed");
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        var eventFired = false;
        vm.ItemImported += (_, _) => eventFired = true;
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailure);
        Assert.False(vm.IsProcessing);
        Assert.False(eventFired);
        Assert.False(session.HasPendingChanges);
        Assert.DoesNotContain("profile write failed", vm.FailureMessage);
    }

    [Fact]
    public async Task An_unexpected_exception_during_abort_processing_resets_not_failure()
    {
        // If the profile changed mid-processing and the import then throws
        // unexpectedly, the workflow resets (no failure card over the new
        // profile), never stranding Processing.
        var a = Profile("Alpha");
        var b = Profile("Beta");
        var profiles = TestDoubles.Profiles(a, b);
        var import = new FakeModImportService(new FakeModRepository())
        {
            ImportGate = new TaskCompletionSource<bool>(),
            ImportExceptionQueue = new Queue<Exception?>(new Exception?[]
            {
                new Exception("boom"), // unexpected
            }),
        };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var (vm, _, _, _, _) = Build(profiles, session, import: import);
        StartUntracked(vm, "/mods/DMF");
        var task = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        session.ActiveProfileId = b.Id; // abort
        import.ImportGate!.SetResult(true); // Import throws unexpectedly
        await task;

        Assert.False(vm.IsActive);
        Assert.False(vm.IsFailure);
        Assert.False(vm.IsProcessing);
    }

    // ---- end-to-end shape over the production fakes -----------------------

    [Fact]
    public async Task End_to_end_create_and_import_lands_the_mod_in_the_target_profile()
    {
        // Production-shape fakes: create + activate a profile, begin a local
        // import, confirm it, and assert the mod appears in that profile's list.
        var profiles = TestDoubles.Profiles();
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var created = profiles.CreateProfile("Main", "", new LaunchSettings());
        session.ActiveProfileId = created.Id;
        var (vm, _, _, _, _) = Build(profiles, session, repo, import);
        StartUntracked(vm, "/mods/DMF");

        await vm.ImportCurrentCommand.ExecuteAsync(null);

        var mods = profiles.GetModList(created.Id);
        var entry = Assert.Single(mods);
        Assert.True(repo.Get(entry.ContainerId) is not null);
        Assert.IsType<LatestPolicy>(entry.Policy);
    }

    // ---- localization refresh ---------------------------------------------

    [Fact]
    public void Localization_change_refreshes_workflow_labels_without_mutating_fields_or_position()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var localization = new LocalizationService();
        var (vm, _, session, _, _) = Build(profiles: profiles, localization: localization);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/One", "/mods/Two");
        vm.ModName = "Renamed";
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        // Flip the culture; the derived labels re-resolve.
        localization.SetCulture("fr");

        Assert.Contains(nameof(ImportWorkflowViewModel.HeaderText), fired);
        Assert.Contains(nameof(ImportWorkflowViewModel.ProcessingText), fired);
        // The position + fields are untouched.
        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(1, vm.CurrentNumber);
        Assert.Equal("Renamed", vm.ModName);
        Assert.Equal(ImportSource.Untracked, vm.SourceChoice);
    }

    [Fact]
    public async Task Localization_change_refreshes_the_inline_failure_message()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var localization = new LocalizationService();
        var import = new FakeModImportService(new FakeModRepository())
        {
            GetBaseNameFunc = _ => throw new InvalidOperationException("bad"),
        };
        var (vm, _, session, _, _) = Build(profiles: profiles, import: import, localization: localization);
        session.ActiveProfileId = a.Id;
        StartUntracked(vm, "/mods/Bad");
        await vm.ImportCurrentCommand.ExecuteAsync(null);

        // The failure message is derived from a durable descriptor through the
        // live LocalizationService, not stored preformatted. The descriptor
        // drives the content (the path is present).
        Assert.Contains("/mods/Bad", vm.FailureMessage);

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        // Flip the culture; the derived getter re-resolves (the descriptor is
        // durable, so the message recomputes and the path is still present).
        localization.SetCulture("fr");

        Assert.Contains(nameof(ImportWorkflowViewModel.FailureMessage), fired);
        Assert.Contains("/mods/Bad", vm.FailureMessage);

        // Close clears the descriptor so the derived getter yields empty.
        vm.CloseFailureCommand.Execute(null);
        Assert.Empty(vm.FailureMessage);
    }
}
