using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Gaming Mode gating on the mod list (Steam Deck): the Add split button +
/// its tooltip, the per-row push of the gaming flag (driving the linked
/// badge's enabled state + tooltip), the programmatic guard on the
/// open-folder command, and the browser-dependent Nexus flows (the Add Nexus
/// Mods command, the regular-tier update action, and the empty-state
/// secondary hint) swapping to Desktop Mode guidance. Built on the same
/// hand-rolled fakes as <see cref="ModListViewModelTests"/> via
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
        bool withNexusRow = false,
        FakeNexusAuthService? auth = null,
        FakeDialogService? dialogs = null,
        FakeNxmRegistrationState? nxmRegistration = null,
        FakeModAcquisitionService? acquisition = null,
        FakeExternalLauncher? launcher = null,
        FakeModDownloadQueue? downloadQueue = null)
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
            auth: auth,
            dialogs: dialogs,
            nxmRegistration: nxmRegistration,
            acquisition: acquisition,
            launcher: launcher,
            downloadQueue: downloadQueue,
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
        var launcher = new FakeExternalLauncher();
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var linked = repo.CreateContainer(new LinkedSource { ExternalPath = "/external/DMF" }, "DMF");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = linked.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });
        var vm = TestDoubles.BuildModList(
            profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo,
            localization: Localization,
            launcher: launcher,
            gamingMode: new GamingModeState(true));
        var row = vm.Mods.Single(m => m.Name == "DMF");
        Assert.True(row.IsLinkedAvailable); // the row is otherwise a valid target

        await vm.LinkedMods.OpenFolderCommand.ExecuteAsync(row);

        Assert.Empty(launcher.OpenedPaths);
    }

    // ---- Add Nexus Mods command (browser guidance) --------------------------

    [Fact]
    public async Task Gaming_mode_AddNexusMods_shows_guidance_instead_of_launching_the_browser()
    {
        var launches = new List<Uri>();
        var dialogs = new FakeDialogService();
        var (vm, _) = Build(new GamingModeState(true),
            dialogs: dialogs,
            launcher: FakeExternalLauncher.RecordingUris(launches));

        await vm.AddNexusModsCommand.ExecuteAsync(null);

        Assert.Empty(launches);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["GamingMode_GuidanceTitle"], alert.Title);
        Assert.Equal(Localization["GamingMode_BrowserGuidance"], alert.Message);
    }

    [Fact]
    public async Task Non_gaming_AddNexusMods_launches_the_browser_without_an_alert()
    {
        var launches = new List<Uri>();
        var dialogs = new FakeDialogService();
        var (vm, _) = Build(new GamingModeState(false),
            dialogs: dialogs,
            launcher: FakeExternalLauncher.RecordingUris(launches));

        await vm.AddNexusModsCommand.ExecuteAsync(null);

        Assert.Single(launches);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- empty-state secondary hint ------------------------------------------

    [Theory]
    [InlineData(true, true, true)]   // gaming + registered -> gaming hint
    [InlineData(true, false, true)]  // gaming + not registered -> gaming hint
    [InlineData(false, true, true)]  // desktop + registered -> nxm hint
    [InlineData(false, false, false)] // desktop + not registered -> hidden
    public void Empty_state_secondary_hint_matrix(bool gaming, bool registered, bool expectedShow)
    {
        // In Gaming Mode the Desktop Mode guidance wins even when Curator owns
        // the nxm handler: the nxm download instruction is exactly what must
        // NOT show there. Outside Gaming Mode the ordinary rule holds (the
        // hint shows only when registered).
        var (vm, _) = Build(
            gaming ? new GamingModeState(true) : new GamingModeState(false),
            nxmRegistration: new FakeNxmRegistrationState
            {
                IsAvailable = true,
                IsRegistered = registered,
            });

        Assert.Equal(expectedShow, vm.ShowNexusHint);
        Assert.Equal(
            gaming
                ? Localization["ModList_EmptyGamingModeHint"]
                : Localization["ModList_NxmDownloadHint"],
            vm.NexusHintText);
    }

    // ---- per-row update action ------------------------------------------------

    [Fact]
    public async Task Gaming_mode_Update_on_a_flagged_regular_row_shows_guidance_without_a_browser()
    {
        var launches = new List<Uri>();
        var dialogs = new FakeDialogService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var (vm, _) = Build(new GamingModeState(true),
            withNexusRow: true,
            auth: auth,
            dialogs: dialogs,
            launcher: FakeExternalLauncher.RecordingUris(launches));
        var row = vm.Mods.Single(m => m.Name == "SoundPack");
        Assert.False(vm.IsPremiumUser);
        row.UpdateAvailable = true; // the flag the persisted store drives

        await vm.UpdateCommand.ExecuteAsync(row);

        Assert.Empty(launches);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["GamingMode_GuidanceTitle"], alert.Title);
        Assert.Equal(Localization["GamingMode_BrowserGuidance"], alert.Message);
    }

    [Fact]
    public async Task Gaming_mode_Update_on_a_flagged_premium_row_enqueues_in_app()
    {
        // Premium installs work in Gaming Mode: the update resolves + enqueues
        // onto the download queue with no guidance alert and no browser.
        var launches = new List<Uri>();
        var dialogs = new FakeDialogService();
        var acquisition = new FakeModAcquisitionService();
        var queue = new FakeModDownloadQueue();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var (vm, _) = Build(new GamingModeState(true),
            withNexusRow: true,
            auth: auth,
            dialogs: dialogs,
            acquisition: acquisition,
            launcher: FakeExternalLauncher.RecordingUris(launches),
            downloadQueue: queue);
        var row = vm.Mods.Single(m => m.Name == "SoundPack");
        Assert.True(vm.IsPremiumUser);
        row.UpdateAvailable = true;

        await vm.UpdateCommand.ExecuteAsync(row);

        var request = Assert.Single(queue.Requests);
        Assert.Equal(DownloadPurpose.UpdateInstall, request.Purpose);
        Assert.Equal(1234, request.ModId);
        Assert.Equal(row.ContainerId, request.ContainerId);
        Assert.Empty(launches);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- per-row update-action tooltip ---------------------------------------

    [Fact]
    public void Gaming_mode_regular_flagged_row_tooltip_reads_the_browser_guidance()
    {
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var (vm, _) = Build(new GamingModeState(true), withNexusRow: true, auth: auth);
        var row = vm.Mods.Single(m => m.Name == "SoundPack");
        row.UpdateAvailable = true;

        Assert.Equal(Localization["GamingMode_BrowserGuidance"], row.UpdateActionTooltip);
    }

    [Fact]
    public void Gaming_mode_premium_flagged_row_tooltip_keeps_the_install_hint()
    {
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var (vm, _) = Build(new GamingModeState(true), withNexusRow: true, auth: auth);
        var row = vm.Mods.Single(m => m.Name == "SoundPack");
        row.UpdateAvailable = true;

        Assert.Equal(Localization["ModRow_UpdateTooltipInstall"], row.UpdateActionTooltip);
    }

    [Fact]
    public void Non_gaming_regular_flagged_row_tooltip_keeps_the_open_files_hint()
    {
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var (vm, _) = Build(new GamingModeState(false), withNexusRow: true, auth: auth);
        var row = vm.Mods.Single(m => m.Name == "SoundPack");
        row.UpdateAvailable = true;

        Assert.Equal(Localization["ModRow_UpdateTooltipOpenFiles"], row.UpdateActionTooltip);
    }
}
