using System.IO;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The import card's edit mode (the correction surface for one container's
/// import details): activation + prefill, mutual exclusion with the batch
/// mode, the shared validation matrix, the FileId degradations (identity lock
/// + the per-record tag lock), the inline identity-removal confirm + its
/// recover paths, refused-save surfacing (guards, the untracked-name
/// conflict, disk failures), the save + reload-notification flow, and the
/// session reset. The batch lifecycle itself is covered by
/// <see cref="ImportWorkflowViewModelTests"/>; the shared parse rules by
/// <see cref="ImportSourceValidatorTests"/>.
/// </summary>
public sealed class ImportWorkflowEditModeTests
{
    private static readonly LocalizationService Localization = new();

    private static readonly DateTimeOffset OldStamp =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset NewStamp =
        new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds the workflow VM over the production-shape fakes with one active
    /// profile (the ImportWorkflowViewModelTests shape, plus the repository
    /// exposed so the edit fixtures can seed grounding facts).
    /// </summary>
    private static (ImportWorkflowViewModel Vm, FakeProfileService Profiles, FakeProfileSession Session, FakeModRepository Repo, FakeModImportService Import)
        Build(FakeProfileService? profiles = null, FakeProfileSession? session = null,
              FakeModRepository? repo = null, LocalizationService? localization = null)
    {
        profiles ??= TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        session ??= new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = profiles.ListProfiles().First().Id };
        repo ??= new FakeModRepository();
        localization ??= new LocalizationService();
        var vm = new ImportWorkflowViewModel(
            profiles, session, repo, new FakeModImportService(repo), localization,
            NullLogger<ImportWorkflowViewModel>.Instance);
        return (vm, profiles, session, repo, new FakeModImportService(repo));
    }

    /// <summary>Seeds an unlocked Nexus mod 8 container with one version.</summary>
    private static ModContainer SeedNexus(FakeModRepository repo, string tag = "1.0") =>
        repo.Seed(new NexusSource { ModId = 8 }, "WT", tag);

    // ---- activation + prefill ---------------------------------------------------

    [Fact]
    public void StartEdit_activates_the_card_prefilled_from_the_container()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.21");

        vm.StartEditCommand.Execute(container.Id);

        Assert.True(vm.IsActive);
        Assert.True(vm.IsEdit);
        Assert.True(vm.IsEditForm);
        Assert.False(vm.IsEditConfirm);
        Assert.Equal(Localization["EditDetails_Title"], vm.HeaderText);
        Assert.Equal("WT", vm.ModName);
        Assert.Equal(ImportSource.Nexus, vm.SourceChoice);
        // The bare id form, not the URL form.
        Assert.Equal("8", vm.Url);
        Assert.Equal("1.21", vm.Version);
        // Policy is per-row, not import details: the picker hides in edit
        // mode even for a Nexus choice.
        Assert.False(vm.IsPolicyVisible);
        Assert.True(vm.CanImport);
        // Defense in depth: the batch's Import command stays unexecutable in
        // edit mode (a programmatic call would index the empty path queue).
        Assert.False(vm.ImportCurrentCommand.CanExecute(null));
    }

    [Fact]
    public void StartEdit_prefills_an_untracked_container()
    {
        var (vm, _, _, repo, _) = Build();
        var container = repo.Seed(new UntrackedSource(), "Local", string.Empty);

        vm.StartEditCommand.Execute(container.Id);

        Assert.True(vm.IsEdit);
        Assert.Equal(ImportSource.Untracked, vm.SourceChoice);
        Assert.Equal(string.Empty, vm.Url);
        Assert.Equal(string.Empty, vm.Version);
        Assert.False(vm.IsRemote);
    }

    [Fact]
    public void StartEdit_on_unknown_or_linked_containers_is_a_noop()
    {
        var (vm, _, _, repo, _) = Build();
        var linked = repo.CreateContainer(
            new LinkedSource { ExternalPath = "/tmp/x" }, "External");

        vm.StartEditCommand.Execute(Guid.NewGuid());
        vm.StartEditCommand.Execute(linked.Id);
        vm.StartEditCommand.Execute(null);

        Assert.False(vm.IsActive);
    }

    [Fact]
    public void StartEdit_with_no_active_profile_is_a_noop()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var (vm, _, _, repo, _) = Build(profiles: profiles, session: session);
        var container = SeedNexus(repo);

        vm.StartEditCommand.Execute(container.Id);

        Assert.False(vm.IsActive);
    }

    // ---- mutual exclusion ----------------------------------------------------------

    [Fact]
    public void A_batch_cannot_start_while_an_edit_is_active()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo);
        vm.StartEditCommand.Execute(container.Id);

        vm.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });

        Assert.True(vm.IsEdit);
        Assert.True(vm.IsEditForm);
        Assert.False(vm.IsProcessing);
        // The batch never captured its paths.
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void An_edit_cannot_start_while_a_batch_is_editing()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo);
        vm.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        Assert.True(vm.IsBatchEditing);

        vm.StartEditCommand.Execute(container.Id);

        Assert.False(vm.IsEdit);
        Assert.True(vm.IsBatchEditing);
    }

    [Fact]
    public async Task An_edit_cannot_start_while_a_batch_is_processing()
    {
        // A gated import fake holds the batch mid-processing; the edit entry
        // is refused for the duration.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo) { ImportGate = new TaskCompletionSource<bool>() };
        var vm = new ImportWorkflowViewModel(
            profiles, session, repo, import, new LocalizationService(),
            NullLogger<ImportWorkflowViewModel>.Instance);
        var container = SeedNexus(repo);

        vm.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        vm.SourceChoice = ImportSource.Untracked;
        var importing = vm.ImportCurrentCommand.ExecuteAsync(null);
        Assert.True(vm.IsProcessing);

        vm.StartEditCommand.Execute(container.Id);
        Assert.False(vm.IsEdit);
        Assert.True(vm.IsProcessing);

        import.ImportGate!.TrySetResult(true);
        await importing;
    }

    [Fact]
    public void The_add_button_and_drops_are_gated_while_an_edit_is_active()
    {
        // The card being active at all (either mode) is what gates Add +
        // drops; IsAddEnabled reads ImportWorkflow.IsActive.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession { ActiveProfileId = profiles.ListProfiles().First().Id };
        var repo = new FakeModRepository();
        var vm = TestDoubles.BuildModList(profiles, session, repo);
        var container = SeedNexus(repo);
        Assert.True(vm.IsAddEnabled);

        vm.ImportWorkflow.StartEditCommand.Execute(container.Id);

        Assert.True(vm.ImportWorkflow.IsActive);
        Assert.False(vm.IsAddEnabled);
    }

    // ---- validation matrix (the shared rules, edit-mode surfaces) -------------------

    [Fact]
    public void Untracked_needs_only_a_name_and_switching_clears_the_version()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);

        vm.SourceChoice = ImportSource.Untracked;

        Assert.Equal(string.Empty, vm.Version);
        Assert.False(vm.IsVersionVisible);
        Assert.True(vm.CanImport);
        Assert.True(vm.SaveEditCommand.CanExecute(null));

        vm.ModName = "   ";
        Assert.False(vm.CanImport);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/8")]
    public void Nexus_accepts_a_bare_id_or_a_nexus_url(string url)
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);

        vm.Url = url;
        vm.Version = "2.0";

        Assert.True(vm.CanImport);
        Assert.Empty(vm.UrlValidationMessage);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/8")]
    [InlineData("")]
    public void Nexus_rejects_garbage_and_missing_ids(string url)
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.Version = "2.0";

        vm.Url = url;

        Assert.False(vm.CanImport);
        Assert.NotEmpty(vm.UrlValidationMessage);
    }

    [Fact]
    public void Nexus_requires_a_version_the_edit_can_never_create_an_unknown()
    {
        var (vm, _, _, repo, _) = Build();
        var container = repo.Seed(new NexusSource { ModId = 8 }, "Unknown", string.Empty);
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "8";

        vm.Version = "  ";
        Assert.False(vm.CanImport);
        Assert.Equal(Localization["Import_VersionRequired"], vm.VersionValidationMessage);

        vm.Version = "1.4";
        Assert.True(vm.CanImport);
    }

    // ---- the FileId degradations ------------------------------------------------------

    [Fact]
    public void A_grounded_identity_disables_the_source_and_id_fields_with_the_hint()
    {
        var (vm, _, _, repo, _) = Build();
        // An OLDER version grounds the identity; the latest is a clean
        // hand-imported copy (the tag stays editable).
        var container = repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        repo.AddVersion(container.Id, "1.0", _ => { }, OldStamp, remoteFileId: 9001);
        repo.AddVersion(container.Id, "2.0", _ => { }, NewStamp);

        vm.StartEditCommand.Execute(container.Id);

        Assert.False(vm.IsSourceEditable);
        Assert.False(vm.IsIdEditable);
        Assert.Equal(Localization["EditDetails_FileIdLockHint"], vm.FileIdLockHint);
        // The latest record carries no FileId of its own: the tag lock does
        // not apply (the migration dedupe's resolvable case).
        Assert.True(vm.IsVersionEditable);
        // A same-identity retag of the ungrounded latest saves cleanly.
        vm.Version = "2.0-hotfix";
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Equal("2.0-hotfix", repo.Get(container.Id)!.Versions.Single(v => v.IsLatest).VersionString);
    }

    [Fact]
    public void A_grounded_latest_additionally_locks_the_version_field()
    {
        var (vm, _, _, repo, _) = Build();
        var container = repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        repo.AddVersion(container.Id, "1.21", _ => { }, OldStamp, remoteFileId: 9001);

        vm.StartEditCommand.Execute(container.Id);

        Assert.False(vm.IsSourceEditable);
        Assert.False(vm.IsIdEditable);
        Assert.False(vm.IsVersionEditable);
        Assert.Equal(Localization["EditDetails_FileIdLockHint"], vm.FileIdLockHint);
        // The name is never locked: an unchanged-tag rename saves cleanly.
        vm.ModName = "WT Fixed";
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Equal("WT Fixed", repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void An_ungrounded_container_has_no_degradation()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");

        vm.StartEditCommand.Execute(container.Id);

        Assert.True(vm.IsSourceEditable);
        Assert.True(vm.IsIdEditable);
        Assert.True(vm.IsVersionEditable);
        Assert.Equal(string.Empty, vm.FileIdLockHint);
    }

    // ---- save + failure surfacing ------------------------------------------------------

    [Fact]
    public void Save_applies_the_primitive_deactivates_the_card_and_raises_the_edited_event()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        Guid? edited = null;
        vm.ImportDetailsEdited += (_, id) => edited = id;

        vm.ModName = "WT Renamed";
        vm.Version = "1.0-hotfix";
        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Equal(container.Id, edited);
        var updated = repo.Get(container.Id)!;
        Assert.Equal("WT Renamed", updated.Name);
        Assert.Equal("1.0-hotfix", Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void Cancel_deactivates_the_card_without_touching_the_repository()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.ModName = "Changed";

        vm.CancelBatchCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Equal("WT", repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void A_profile_change_while_editing_resets_the_card()
    {
        var profiles = TestDoubles.Profiles(
            new ProfileSummary(Guid.NewGuid(), "Alpha", ""),
            new ProfileSummary(Guid.NewGuid(), "Beta", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var (vm, _, _, repo, _) = Build(profiles: profiles, session: session);
        var container = SeedNexus(repo);
        vm.StartEditCommand.Execute(container.Id);

        session.ActiveProfileId = profiles.ListProfiles().Last().Id;

        Assert.False(vm.IsActive);
    }

    [Fact]
    public void A_disk_failure_mid_save_surfaces_inline_instead_of_crashing()
    {
        var (vm, _, session, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        repo.EditImportDetailsThrows = new IOException("disk full");
        vm.ModName = "WT Fixed";

        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsActive);
        Assert.True(vm.IsEditForm);
        Assert.Contains("disk full", vm.EditFailureMessage);
        Assert.Equal("WT", repo.Get(container.Id)!.Name);

        repo.EditImportDetailsThrows = null;
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Equal("WT Fixed", repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void A_refused_save_surfaces_inline_and_is_correctable()
    {
        // The duplicate-identity guard: another container already tracks the
        // typed id.
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        repo.CreateContainer(new NexusSource { ModId = 9 }, "Owner");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";

        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditForm);
        Assert.Contains("9", vm.EditFailureMessage);

        vm.Url = "10";
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void Saving_as_untracked_under_another_untracked_name_is_refused_inline()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        var other = repo.CreateContainer(new UntrackedSource(), "Taken");
        vm.StartEditCommand.Execute(container.Id);
        vm.SourceChoice = ImportSource.Untracked;
        vm.ModName = "Taken";

        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditForm);
        Assert.Contains(
            Localization.Format("EditDetails_UntrackedNameConflict", "Taken"),
            vm.EditFailureMessage);
        // Nothing applied: the container keeps its name + Nexus source, and
        // the other container still owns the name.
        var unchanged = repo.Get(container.Id)!;
        Assert.Equal("WT", unchanged.Name);
        Assert.IsType<NexusSource>(unchanged.Source);
        Assert.Equal(other.Id, repo.FindUntrackedByName("Taken")!.Id);

        vm.ModName = "Free";
        vm.SaveEditCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Equal(container.Id, repo.FindUntrackedByName("Free")!.Id);
    }

    [Fact]
    public void An_untracked_container_saving_under_its_own_name_is_allowed()
    {
        var (vm, _, _, repo, _) = Build();
        var container = repo.Seed(new UntrackedSource(), "Local", string.Empty);
        vm.StartEditCommand.Execute(container.Id);

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Equal(container.Id, repo.FindUntrackedByName("Local")!.Id);
    }

    [Fact]
    public void The_tag_lock_surfaces_inline_when_reached_programmatically()
    {
        // The version field is disabled for a grounded latest, but the command
        // is the source of truth: a programmatic Save with a changed tag
        // surfaces the primitive's refusal inline, never a crash.
        var (vm, _, _, repo, _) = Build();
        var container = repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        repo.AddVersion(container.Id, "1.21", _ => { }, OldStamp, remoteFileId: 9001);
        vm.StartEditCommand.Execute(container.Id);
        Assert.False(vm.IsVersionEditable);

        vm.Version = "1.22";
        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditForm);
        Assert.Contains("version tag is fixed", vm.EditFailureMessage);
        Assert.Equal("1.21", repo.Get(container.Id)!.Versions.Single().VersionString);
    }

    // ---- the removal confirm + its recover paths -----------------------------------

    [Fact]
    public void An_identity_change_on_a_multi_version_container_swaps_to_the_confirm_stage()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { }, NewStamp);
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";

        Assert.True(vm.RequiresIdentityConfirm);

        vm.SaveEditCommand.Execute(null);

        // The first Save presents the confirm panel; nothing applied yet.
        Assert.True(vm.IsEditConfirm);
        Assert.False(vm.IsFormVisible);
        Assert.Contains("1", vm.ConfirmMessage);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);

        // Back returns to the form without saving.
        vm.BackFromEditConfirmCommand.Execute(null);
        Assert.True(vm.IsEditForm);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);

        // Through the confirm: applies with removal.
        vm.SaveEditCommand.Execute(null);
        vm.ConfirmEditSaveCommand.Execute(null);

        Assert.False(vm.IsActive);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(9, Assert.IsType<NexusSource>(updated.Source).ModId);
        Assert.Equal("9.1", Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void An_identity_change_on_a_single_version_container_saves_without_confirm()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";

        Assert.False(vm.RequiresIdentityConfirm);

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.False(vm.IsEditConfirm);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(9, Assert.IsType<NexusSource>(updated.Source).ModId);
    }

    [Fact]
    public void A_version_landing_while_the_card_is_open_swaps_to_the_confirm_stage()
    {
        // The construction-time count goes stale when a download for the same
        // container completes while the card is open: the save-time refresh
        // sees the second version, so the save swaps to the removal confirm
        // instead of applying a silent removal.
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";
        Assert.False(vm.RequiresIdentityConfirm);

        repo.AddVersion(container.Id, "2.0", _ => { }, NewStamp);

        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditConfirm);
        Assert.Empty(vm.EditFailureMessage);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);

        vm.ConfirmEditSaveCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Single(repo.Get(container.Id)!.Versions);
    }

    [Fact]
    public void The_confirm_guard_thrown_mid_save_recovers_onto_the_confirm_stage()
    {
        // The save-time refresh closes the practical window; the typed guard
        // catch covers the read-to-call race.
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";
        repo.EditImportDetailsThrows = new RemovalConfirmationRequiredException(
            "Changing this mod's identity removes the older versions; the caller must confirm.");

        vm.SaveEditCommand.Execute(null);

        Assert.True(vm.IsEditConfirm);
        Assert.Empty(vm.EditFailureMessage);

        repo.EditImportDetailsThrows = null;
        repo.AddVersion(container.Id, "2.0", _ => { }, NewStamp);
        vm.ConfirmEditSaveCommand.Execute(null);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void Saving_as_untracked_applies_an_empty_tag()
    {
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.SourceChoice = ImportSource.Untracked;

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsActive);
        var updated = repo.Get(container.Id)!;
        Assert.IsType<UntrackedSource>(updated.Source);
        Assert.Equal(string.Empty, Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void The_version_tag_is_trimmed_and_the_url_is_parsed_to_an_id()
    {
        // ApplyEdit trims the typed name + tag and parses the id/URL field to
        // the canonical identity; both behaviors are otherwise unpinned.
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/42";
        vm.Version = "  3.1  ";
        vm.ModName = "  WT Renamed  ";

        vm.SaveEditCommand.Execute(null);

        Assert.False(vm.IsActive);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(42, Assert.IsType<NexusSource>(updated.Source).ModId);
        Assert.Equal("WT Renamed", updated.Name);
        Assert.Equal("3.1", Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void A_refused_confirm_save_surfaces_the_failure_without_leaving_the_confirm_stage()
    {
        // The confirm stage's ConfirmEditSave can be refused too (the
        // duplicate-identity guard): the inline failure must show while the
        // stage stays, so the card never appears inert (the catch preserves
        // the stage; only Back or a successful save moves it).
        var (vm, _, _, repo, _) = Build();
        var container = SeedNexus(repo, "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { }, NewStamp);
        repo.CreateContainer(new NexusSource { ModId = 9 }, "Owner");
        vm.StartEditCommand.Execute(container.Id);
        vm.Url = "9";
        vm.Version = "9.1";

        vm.SaveEditCommand.Execute(null);
        Assert.True(vm.IsEditConfirm);
        vm.ConfirmEditSaveCommand.Execute(null);

        Assert.True(vm.IsEditConfirm);
        Assert.True(vm.IsActive);
        Assert.Contains("9", vm.EditFailureMessage);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);

        // Back to the form still shows the failure until the next attempt.
        vm.BackFromEditConfirmCommand.Execute(null);
        Assert.True(vm.IsEditForm);
        Assert.NotEmpty(vm.EditFailureMessage);

        // Correcting the id + confirming applies.
        vm.Url = "10";
        vm.SaveEditCommand.Execute(null);
        vm.ConfirmEditSaveCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.Equal(10, Assert.IsType<NexusSource>(repo.Get(container.Id)!.Source).ModId);
    }
}
