using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="EditImportDetailsViewModel"/> + <see cref="EditImportDetailsFactory"/>:
/// the edit-import-details dialog's validation matrix (Untracked / Nexus,
/// version required, id/URL parsing), the FileId degradation to name-only
/// editing, the inline identity-removal confirm step, the save application
/// through the repository primitive (with its failures surfaced inline), and
/// the factory's unknown/linked screening. The shared
/// <see cref="ImportSourceValidator"/> rules are covered directly in
/// <see cref="ImportSourceValidatorTests"/>; the import card's identical
/// validation stays covered by <see cref="ImportWorkflowViewModelTests"/>.
/// </summary>
public sealed class EditImportDetailsViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static FakeModRepository RepoWithNexus(string tag = "1.0", int? fileId = null)
    {
        var repo = new FakeModRepository();
        var container = repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        repo.AddVersion(container.Id, tag, _ => { }, remoteFileId: fileId);
        return repo;
    }

    private static (EditImportDetailsViewModel Vm, FakeModRepository Repo, ModContainer Container)
        Build(FakeModRepository? repo = null, ModContainer? container = null)
    {
        repo ??= RepoWithNexus();
        container ??= repo.List().First();
        var vm = new EditImportDetailsViewModel(container, repo, new LocalizationService());
        return (vm, repo, container);
    }

    // ---- load from the container's facts --------------------------------------

    [Fact]
    public void Loads_a_nexus_container_with_its_id_and_latest_tag()
    {
        var (vm, _, _) = Build();

        Assert.Equal("WT", vm.Name);
        Assert.Equal(ImportSource.Nexus, vm.SourceChoice);
        Assert.Equal("8", vm.Url);
        Assert.Equal("1.0", vm.Version);
        Assert.False(vm.IsIdentityLocked);
        Assert.True(vm.IsIdEditable);
        Assert.True(vm.IsSourceEditable);
        Assert.Empty(vm.FileIdLockHint);
    }

    [Fact]
    public void Loads_an_untracked_container()
    {
        var repo = new FakeModRepository();
        var container = repo.CreateContainer(new UntrackedSource(), "Local");
        repo.AddVersion(container.Id, "", _ => { });

        var vm = new EditImportDetailsViewModel(
            repo.Get(container.Id)!, repo, new LocalizationService());

        Assert.Equal(ImportSource.Untracked, vm.SourceChoice);
        Assert.Equal(string.Empty, vm.Url);
        Assert.Equal(string.Empty, vm.Version);
        Assert.False(vm.IsRemote);
    }

    [Fact]
    public void Loads_a_nexus_unknown_container_with_an_empty_tag()
    {
        var (vm, _, _) = Build(RepoWithNexus(tag: string.Empty));

        Assert.Equal(ImportSource.Nexus, vm.SourceChoice);
        Assert.Equal(string.Empty, vm.Version);
        // The empty tag is editable (the dialog resolves the unknown state),
        // but saving still requires a non-empty version (CanSave).
        Assert.False(vm.CanSave);
    }

    // ---- the validation matrix -------------------------------------------------

    [Fact]
    public void Untracked_needs_only_a_name()
    {
        var (vm, _, _) = Build();
        vm.SourceChoice = ImportSource.Untracked;

        // Switching to Untracked clears the version field; the id field is
        // ignored entirely.
        Assert.Equal(string.Empty, vm.Version);
        Assert.False(vm.IsVersionVisible);
        Assert.True(vm.CanSave);

        vm.Name = "   ";
        Assert.False(vm.CanSave);
    }

    [Theory]
    [InlineData("8")]                                                  // bare id
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/8")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files")]
    public void Nexus_accepts_a_bare_id_or_a_nexus_url(string url)
    {
        var (vm, _, _) = Build();
        vm.Url = url;
        vm.Version = "2.0";

        Assert.True(vm.CanSave);
        Assert.Empty(vm.UrlValidationMessage);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://example.com/mods/8")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/8")]
    [InlineData("-1")]
    [InlineData("")]
    public void Nexus_rejects_garbage_and_missing_ids(string url)
    {
        var (vm, _, _) = Build();
        vm.Url = url;
        vm.Version = "2.0";

        Assert.False(vm.CanSave);
        Assert.NotEmpty(vm.UrlValidationMessage);
    }

    [Fact]
    public void Nexus_requires_a_version_the_dialog_can_never_create_an_unknown()
    {
        var (vm, _, _) = Build(RepoWithNexus(tag: string.Empty));
        vm.Url = "8";

        vm.Version = "  ";
        Assert.False(vm.CanSave);
        Assert.Equal(Localization["Import_VersionRequired"], vm.VersionValidationMessage);

        vm.Version = "1.4";
        Assert.True(vm.CanSave);
        Assert.Empty(vm.VersionValidationMessage);
    }

    // ---- the FileId degradation ---------------------------------------------------

    [Fact]
    public void A_fileid_grounded_mod_degrades_to_name_only()
    {
        var (vm, repo, container) = Build(RepoWithNexus(fileId: 9001));

        Assert.True(vm.IsIdentityLocked);
        Assert.False(vm.IsIdEditable);
        Assert.False(vm.IsSourceEditable);
        Assert.Equal(Localization["EditDetails_FileIdLockHint"], vm.FileIdLockHint);

        // A same-identity name edit still saves (name is never locked).
        vm.Name = "WT Fixed";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result);
        Assert.Equal("WT Fixed", repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void A_fileid_grounded_multi_version_mod_needs_no_confirm_for_a_rename()
    {
        var repo = RepoWithNexus(fileId: 9001);
        var container = repo.List().First();
        repo.AddVersion(container.Id, "2.0", _ => { },
            new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), 9002);
        var vm = new EditImportDetailsViewModel(
            repo.Get(container.Id)!, repo, new LocalizationService());

        Assert.False(vm.RequiresIdentityConfirm);

        vm.Name = "Renamed";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result);
        Assert.False(vm.IsConfirmStep);
        // No removal: a same-identity edit never removes versions.
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);
    }

    // ---- the identity-change confirm ---------------------------------------------

    [Fact]
    public void An_identity_change_on_a_multi_version_container_swaps_to_the_confirm_step()
    {
        var repo = RepoWithNexus();
        var container = repo.List().First();
        repo.AddVersion(container.Id, "2.0", _ => { },
            new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var vm = new EditImportDetailsViewModel(
            repo.Get(container.Id)!, repo, new LocalizationService());
        vm.Url = "9";
        vm.Version = "9.1";

        Assert.True(vm.RequiresIdentityConfirm);
        Assert.False(vm.IsConfirmStep);

        vm.SaveCommand.Execute(null);

        // The first Save presents the confirm panel; nothing applied yet.
        Assert.True(vm.IsConfirmStep);
        Assert.False(vm.Result);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);
        Assert.Contains("1", vm.ConfirmMessage); // one older version removed

        // Back returns to the form without saving.
        vm.BackCommand.Execute(null);
        Assert.False(vm.IsConfirmStep);
        Assert.False(vm.Result);
        Assert.Equal(2, repo.Get(container.Id)!.Versions.Count);

        // Through the confirm: applies with removal.
        vm.SaveCommand.Execute(null);
        vm.ConfirmSaveCommand.Execute(null);

        Assert.True(vm.Result);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(9, Assert.IsType<NexusSource>(updated.Source).ModId);
        var survivor = Assert.Single(updated.Versions);
        Assert.Equal("9.1", survivor.VersionString);
    }

    [Fact]
    public void An_identity_change_on_a_single_version_container_saves_without_confirm()
    {
        var (vm, repo, container) = Build();
        vm.Url = "9";
        vm.Version = "9.1";

        Assert.False(vm.RequiresIdentityConfirm);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result);
        Assert.False(vm.IsConfirmStep);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(9, Assert.IsType<NexusSource>(updated.Source).ModId);
        Assert.Equal("9.1", Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void Saving_as_untracked_applies_an_empty_tag()
    {
        var (vm, repo, container) = Build();
        vm.SourceChoice = ImportSource.Untracked;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result);
        var updated = repo.Get(container.Id)!;
        Assert.IsType<UntrackedSource>(updated.Source);
        Assert.Equal(string.Empty, Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void The_version_tag_is_trimmed_and_the_url_is_parsed_to_an_id()
    {
        var (vm, repo, container) = Build();
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/42";
        vm.Version = "  3.1  ";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result);
        var updated = repo.Get(container.Id)!;
        Assert.Equal(42, Assert.IsType<NexusSource>(updated.Source).ModId);
        Assert.Equal("3.1", Assert.Single(updated.Versions).VersionString);
    }

    // ---- failure surfacing --------------------------------------------------------

    [Fact]
    public void A_refused_save_surfaces_inline_and_stays_open()
    {
        // The duplicate-identity guard: another container already tracks the
        // typed id.
        var (vm, repo, _) = Build();
        repo.CreateContainer(new NexusSource { ModId = 9 }, "Owner");
        vm.Url = "9";
        vm.Version = "9.1";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Result);
        Assert.NotEmpty(vm.FailureMessage);
        Assert.Contains("9", vm.FailureMessage);

        // Correctable: fixing the id + saving again succeeds.
        vm.Url = "10";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.Result);
        Assert.Empty(vm.FailureMessage);
    }

    [Fact]
    public void Cancel_marks_the_result_false_without_touching_the_repository()
    {
        var (vm, repo, container) = Build();
        vm.Name = "Changed";

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Result);
        Assert.Equal("WT", repo.Get(container.Id)!.Name);
    }

    // ---- the factory ------------------------------------------------------------

    [Fact]
    public void The_factory_builds_from_a_managed_container()
    {
        var repo = RepoWithNexus();
        var container = repo.List().First();
        var factory = new EditImportDetailsFactory(repo, Localization);

        var vm = factory.Create(container.Id);

        Assert.NotNull(vm);
        Assert.Equal("WT", vm!.Name);
    }

    [Fact]
    public void The_factory_returns_null_for_unknown_and_linked_containers()
    {
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(
            new LinkedSource { ExternalPath = "/tmp/x" }, "External");
        var factory = new EditImportDetailsFactory(repo, Localization);

        Assert.Null(factory.Create(Guid.NewGuid()));
        Assert.Null(factory.Create(linked.Id));
    }

    [Fact]
    public void Detach_is_idempotent()
    {
        var (vm, _, _) = Build();
        vm.Detach();
        vm.Detach();
    }
}

/// <summary>
/// <see cref="ImportSourceValidator"/>: the URL/id parsing + remote-field
/// rules shared by the import card + the edit-details dialog, extracted so
/// both surfaces validate identically.
/// </summary>
public sealed class ImportSourceValidatorTests
{
    [Fact]
    public void Nexus_parses_a_bare_id_or_url_into_a_nexus_source()
    {
        Assert.True(ImportSourceValidator.TryParseUrl(
            ImportSource.Nexus, "42", out var bare));
        Assert.Equal(42, Assert.IsType<NexusSource>(bare).ModId);

        Assert.True(ImportSourceValidator.TryParseUrl(
            ImportSource.Nexus,
            "https://www.nexusmods.com/warhammer40kdarktide/mods/42/",
            out var url));
        Assert.Equal(42, Assert.IsType<NexusSource>(url).ModId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("https://example.com/mods/42")]
    public void Nexus_rejects_missing_or_malformed_input(string url)
    {
        Assert.False(ImportSourceValidator.TryParseUrl(ImportSource.Nexus, url, out _));
    }

    [Fact]
    public void Untracked_never_parses_a_remote_field()
    {
        Assert.False(ImportSourceValidator.TryParseUrl(
            ImportSource.Untracked, "42", out var parsed));
        Assert.IsType<UntrackedSource>(parsed);
    }

    [Fact]
    public void Remote_fields_are_valid_only_with_a_version_and_a_parsable_id()
    {
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "42", ""));
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "", "1.0"));
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "garbage", "1.0"));
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "42", "1.0"));
    }

    [Fact]
    public void Untracked_fields_are_always_valid()
    {
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Untracked, "", ""));
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Untracked, "anything", "anything"));
    }
}
