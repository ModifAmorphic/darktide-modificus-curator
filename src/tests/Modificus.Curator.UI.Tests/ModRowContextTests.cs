using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The RowContext contract: the row-affecting globals (premium / gaming) live
/// on one shared observable object passed once to every row, and a flip on the
/// context re-fires exactly the row + list-VM properties the former per-flag
/// value pushes re-fired (the derived enabled states + tooltips), with no
/// per-row value copies left to drift. Install-busy state is not on the
/// context (an update in flight is a queue item rendered as the row's
/// download morph; the queue-front behavior is covered by the mod-list update
/// tests).
/// </summary>
public sealed class ModRowContextTests
{
    // BuildModList constructs its own context internally; these tests need rows
    // bound to an externally controlled context, so they build the full wiring
    // by hand around the caller's context (the same fake set BuildModList
    // defaults to).

    private static ModListViewModel BuildWithContext(ModRowContext context)
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var nexus = repo.Seed(new NexusSource { ModId = 8 }, "DMF", "1.0");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = nexus.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });

        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var localization = new LocalizationService();
        var cards = new ModCardsGate();
        var importWorkflow = new ImportWorkflowViewModel(
            profiles, session, repo, new FakeModImportService(repo), cards, localization,
            NullLogger<ImportWorkflowViewModel>.Instance);
        var loadOrder = new LoadOrderImportViewModel(
            profiles, session, new FakeLoadOrderReconciler(), cards,
            new FakeExternalLauncher(), new FakeDialogService(), localization,
            NullLogger<LoadOrderImportViewModel>.Instance);
        var detailedRows = new DetailedModRowsViewModel(
            new FakeConfigLoader(), new FakeNexusModMetadataService(), repo,
            new FakeModThumbnailService(), NullLogger<DetailedModRowsViewModel>.Instance);
        var linkedMods = new LinkedModsViewModel(
            profiles, session, repo, new FakeModImportService(repo), new FakeDialogService(),
            localization, new FakeExternalLauncher(), new GamingModeState(false),
            NullLogger<LinkedModsViewModel>.Instance);
        var updateState = new FakeUpdateStateStore(repo);
        var updateCheck = new FakeUpdateCheckService { StateStore = updateState, RecordProfileId = a.Id };
        var runner = new UpdateCheckRunner(
            session, profiles, updateCheck, new FakeConfigLoader(), new FakeAppStateStore(),
            new FakeAutomaticUpdateService(), NullLogger<UpdateCheckRunner>.Instance);
        var queue = new FakeModDownloadQueue();
        var acquisition = new FakeModAcquisitionService();

        return new ModListViewModel(
            profiles, session, repo, new FakeDialogService(), localization,
            updateState, runner, context, importWorkflow, loadOrder, cards,
            detailedRows, linkedMods,
            new FakeExternalLauncher(), new FakeNxmRegistrationState(),
            queue, new ModUpdateEnqueuer(acquisition, queue, profiles),
            NullLogger<ModListViewModel>.Instance);
    }

    private static ModRowContext MakeContext(bool gaming = false) =>
        new(
            new FakeNexusAuthService(),
            new GamingModeState(gaming),
            NullLogger<ModRowContext>.Instance);

    [Fact]
    public void Premium_flip_on_the_context_refires_row_and_list_properties()
    {
        var context = MakeContext();
        var vm = BuildWithContext(context);
        context.IsPremiumUser = false;

        var row = Assert.Single(vm.Mods);
        var rowFired = new List<string>();
        row.PropertyChanged += (_, e) => rowFired.Add(e.PropertyName!);
        var vmFired = new List<string>();
        vm.PropertyChanged += (_, e) => vmFired.Add(e.PropertyName!);

        context.IsPremiumUser = true;

        // The list VM's forwarding property re-fired...
        Assert.Contains(nameof(ModListViewModel.IsPremiumUser), vmFired);
        // ...and the live row re-fired its forwarding property + the derived
        // members that read it (exactly the set the former push re-fired).
        Assert.Contains(nameof(ModItemViewModel.IsPremiumUser), rowFired);
        Assert.Contains(nameof(ModItemViewModel.UpdateActionEnabled), rowFired);
        Assert.Contains(nameof(ModItemViewModel.UpdateActionTooltip), rowFired);
        Assert.True(row.IsPremiumUser);
        Assert.True(vm.IsPremiumUser);
    }

    [Fact]
    public void Gaming_reads_through_rows_and_the_list_vm_off_the_constant_context()
    {
        var context = MakeContext(gaming: true);
        var vm = BuildWithContext(context);

        var row = Assert.Single(vm.Mods);
        Assert.True(vm.IsGamingMode);
        Assert.True(row.IsGamingMode);
    }

    [Fact]
    public void Rows_dropped_by_a_reload_receive_no_context_notifications()
    {
        var context = MakeContext();
        var vm = BuildWithContext(context);
        var oldRow = Assert.Single(vm.Mods);
        var fired = 0;
        oldRow.PropertyChanged += (_, _) => fired++;

        // A reload rebuilds the rows (e.g. a profile switch); the context's
        // next flip must reach only the live rows, so the dropped row's
        // handlers never fire (no per-row subscription to leak).
        vm.Reload();
        context.IsPremiumUser = true;

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Premium_read_failure_leaves_the_flag_false()
    {
        var auth = new FakeNexusAuthService { ThrowOnGetCurrentState = true };
        var context = new ModRowContext(
            auth,
            new GamingModeState(false),
            NullLogger<ModRowContext>.Instance);

        Assert.False(context.IsPremiumUser);
    }
}
