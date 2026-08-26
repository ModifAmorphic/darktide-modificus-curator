using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="LoadOrderImportViewModel"/> (the load-order review card):
/// activation from a picked file, the mutual exclusion with the import
/// workflow card (both directions, through the shared
/// <see cref="ModCardsGate"/>), the table's outcome/checkbox defaults +
/// toggling, the apply contract (exactly one SetModOrder over every matched
/// container + AddMod only for included adds + reload event + deactivate),
/// the empty-file refusal, the open-on-Nexus link (success + failure), and
/// the reset paths (cancel + profile switch). The plan comes from a fake
/// reconciler; the real reconciliation is covered by
/// <c>Modificus.Curator.Profiles.Tests.LoadOrderPlannerTests</c>.
/// </summary>
public sealed class LoadOrderImportViewModelTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds the card VM plus a sibling import-workflow VM sharing the same
    /// card gate (the mutual-exclusion tests drive both).
    /// </summary>
    private static (LoadOrderImportViewModel Vm, FakeLoadOrderReconciler Reconciler, FakeProfileService Profiles, FakeProfileSession Session, FakeExternalLauncher Launcher, FakeDialogService Dialogs, ImportWorkflowViewModel Import)
        Build(FakeProfileService? profiles = null, FakeProfileSession? session = null,
              FakeModRepository? repo = null, FakeLoadOrderReconciler? reconciler = null,
              FakeExternalLauncher? launcher = null, FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        session ??= new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        repo ??= new FakeModRepository();
        reconciler ??= new FakeLoadOrderReconciler();
        launcher ??= new FakeExternalLauncher();
        dialogs ??= new FakeDialogService();
        var cards = new ModCardsGate();
        var import = new ImportWorkflowViewModel(
            profiles, session, repo, new FakeModImportService(repo), cards,
            Localization, NullLogger<ImportWorkflowViewModel>.Instance);
        var vm = new LoadOrderImportViewModel(
            profiles, session, reconciler, cards, launcher, dialogs,
            Localization, NullLogger<LoadOrderImportViewModel>.Instance);
        return (vm, reconciler, profiles, session, launcher, dialogs, import);
    }

    /// <summary>Writes a temp load-order file and returns its path.</summary>
    private static string WriteFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "curator-loadorder-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static LoadOrderPlan Plan(params LoadOrderLine[] lines) =>
        LoadOrderPlanner.Build(
            lines.Select(l => l.Name).ToArray(),
            lines.Where(l => l.Outcome == LoadOrderLineOutcome.Reorder)
                .Select(l => new LoadOrderProfileMod(l.ContainerId!.Value, l.MatchedBaseName!, l.DisplayName!))
                .ToArray(),
            lines.Where(l => l.Outcome == LoadOrderLineOutcome.LibraryAdd)
                .Select(l => new LoadOrderRepoCandidate(l.ContainerId!.Value, l.MatchedBaseName!, false, l.DisplayName!))
                .ToArray());

    // ---- activation + table ---------------------------------------------------

    [Fact]
    public async Task StartImport_activates_and_builds_rows_with_the_checkbox_defaults()
    {
        var reorder = Guid.NewGuid();
        var add = Guid.NewGuid();
        var (vm, reconciler, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A Row"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, add, "ModB", "B Container"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null));

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB\nGhost"));

        Assert.True(vm.IsActive);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(["ModA", "ModB", "Ghost"], vm.Rows.Select(r => r.Name));

        var reorderRow = vm.Rows[0];
        Assert.Equal("A Row", reorderRow.MatchText);
        Assert.Equal(Localization["LoadOrder_OutcomeReorder"], reorderRow.OutcomeText);
        Assert.True(reorderRow.IsIncluded); // reorder default: included
        Assert.True(reorderRow.IsIncludeEnabled);

        var addRow = vm.Rows[1];
        Assert.Equal("B Container", addRow.MatchText);
        Assert.Equal(Localization["LoadOrder_OutcomeAdd"], addRow.OutcomeText);
        Assert.False(addRow.IsIncluded); // add default: excluded (the opt-in)
        Assert.True(addRow.IsIncludeEnabled);

        var unresolvedRow = vm.Rows[2];
        Assert.Equal("-", unresolvedRow.MatchText);
        Assert.Equal(Localization["LoadOrder_OutcomeUnresolved"], unresolvedRow.OutcomeText);
        Assert.False(unresolvedRow.IsIncluded);
        Assert.False(unresolvedRow.IsIncludeEnabled);

        // The picker's file reached the reconciler parsed.
        var call = Assert.Single(reconciler.Calls);
        Assert.Equal(["ModA", "ModB", "Ghost"], call.Names);
    }

    [Fact]
    public async Task An_empty_file_activates_with_the_notice_and_refuses_apply()
    {
        var (vm, reconciler, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlan.Empty;

        await vm.StartImportCommand.ExecuteAsync(WriteFile("-- only comments\n"));

        Assert.True(vm.IsActive);
        Assert.Empty(vm.Rows);
        Assert.True(vm.ShowEmptyNotice);
        Assert.False(vm.HasRows);
        Assert.False(vm.CanApply);

        vm.ApplyCommand.Execute(null);
        Assert.True(vm.IsActive); // no write, no deactivate
    }

    [Fact]
    public async Task An_unreadable_file_alerts_and_stays_inactive()
    {
        var (vm, _, _, _, _, dialogs, _) = Build();
        var missing = Path.Combine(Path.GetTempPath(), "curator-missing-" + Guid.NewGuid() + ".txt");

        await vm.StartImportCommand.ExecuteAsync(missing);

        Assert.False(vm.IsActive);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(missing, alert.Message);
    }

    [Fact]
    public async Task A_second_start_while_active_refuses()
    {
        var (vm, reconciler, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlan.Empty;
        var first = WriteFile("ModA\n");
        var second = WriteFile("ModB\n");
        await vm.StartImportCommand.ExecuteAsync(first);
        Assert.True(vm.IsActive);

        await vm.StartImportCommand.ExecuteAsync(second);

        // Same session: same rows (the second file did not replace them).
        Assert.Equal(first, vm.SourcePath);
    }

    // ---- mutual exclusion (both directions) -----------------------------------

    [Fact]
    public async Task A_load_order_start_refuses_while_the_import_workflow_is_active()
    {
        var (vm, reconciler, _, session, _, _, import) = Build();
        import.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        Assert.True(import.IsActive);

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        Assert.False(vm.IsActive);
        Assert.Empty(reconciler.Calls);
    }

    [Fact]
    public async Task Import_starts_refuse_while_a_load_order_review_is_active()
    {
        var (vm, reconciler, _, session, _, _, import) = Build();
        reconciler.NextPlan = LoadOrderPlan.Empty;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        Assert.True(vm.IsActive);

        // A batch...
        import.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        Assert.False(import.IsActive);
        Assert.False(import.IsBatchEditing);

        // ...and an edit (any container: the guard refuses before the
        // missing-container screening even runs).
        import.StartEditCommand.Execute(Guid.NewGuid());
        Assert.False(import.IsEdit);
    }

    // ---- apply -----------------------------------------------------------------

    [Fact]
    public async Task Apply_orders_all_matched_containers_and_adds_only_included_adds()
    {
        var reorder = Guid.NewGuid();
        var addIncluded = Guid.NewGuid();
        var addExcluded = Guid.NewGuid();
        var (vm, reconciler, profiles, session, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, addIncluded, "ModB", "B"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null),
            new LoadOrderLine("ModC", LoadOrderLineOutcome.LibraryAdd, addExcluded, "ModC", "C"));
        var profileId = session.ActiveProfileId!.Value;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB\nGhost\nModC"));

        // Include the ModB add (file order: after the reorder line).
        vm.Rows[1].IsIncluded = true;
        Assert.True(vm.CanApply);

        var applied = 0;
        vm.OrderApplied += (_, _) => applied++;
        vm.ApplyCommand.Execute(null);

        // ONE SetModOrder over every matched container in file order
        // (includes + the excluded add alike; order application is not
        // optional).
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal([reorder, addIncluded, addExcluded], order);

        // AddMod only for the INCLUDED library add, Latest policy.
        var addCall = Assert.Single(profiles.AddModCalls);
        Assert.Equal(profileId, addCall.Id);
        Assert.Equal(addIncluded, addCall.ContainerId);
        Assert.Equal(ModVersionPolicy.Latest, addCall.Policy);

        Assert.True(session.HasPendingChanges);
        Assert.False(vm.IsActive);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Apply_with_only_reorder_lines_performs_no_adds()
    {
        var reorder = Guid.NewGuid();
        var (vm, reconciler, profiles, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        Assert.True(vm.CanApply); // the reorder line defaults included
        vm.ApplyCommand.Execute(null);

        Assert.Single(profiles.SetModOrderCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task Apply_with_no_included_lines_is_refused()
    {
        var (vm, reconciler, profiles, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, Guid.NewGuid(), "ModB", "B"));

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModB\n"));

        // The only line is an excluded add: apply stays unavailable + guarded.
        Assert.False(vm.CanApply);
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.IsActive);
        Assert.Empty(profiles.SetModOrderCalls);
    }

    [Fact]
    public async Task Cancel_discards_the_review_with_no_writes()
    {
        var (vm, reconciler, profiles, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Empty(vm.Rows);
        Assert.Empty(profiles.SetModOrderCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task A_profile_switch_resets_an_open_review()
    {
        var profiles = TestDoubles.Profiles(
            new ProfileSummary(Guid.NewGuid(), "Alpha", ""),
            new ProfileSummary(Guid.NewGuid(), "Beta", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var (vm, reconciler, _, _, _, _, _) = Build(profiles: profiles, session: session);
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        Assert.True(vm.IsActive);

        session.ActiveProfileId = profiles.ListProfiles().Last().Id;

        Assert.False(vm.IsActive);
    }

    // ---- the open-on-Nexus link --------------------------------------------------

    [Fact]
    public async Task Unresolved_rows_carry_the_keyword_search_url()
    {
        var (vm, reconciler, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("Warp Unbound Timer", LoadOrderLineOutcome.Unresolved, null, null, null));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Warp Unbound Timer\n"));

        var row = Assert.Single(vm.Rows);
        Assert.Equal(
            "https://www.nexusmods.com/games/warhammer40kdarktide/mods/?keyword=Warp%20Unbound%20Timer",
            row.SearchUrl);

        // Resolved rows have no search URL.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        Assert.Null(Assert.Single(vm.Rows).SearchUrl);
    }

    [Fact]
    public async Task OpenOnNexus_launches_the_url_and_alerts_on_failure()
    {
        var (vm, reconciler, _, _, launcher, dialogs, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("Ghost Mod", LoadOrderLineOutcome.Unresolved, null, null, null));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Ghost Mod\n"));
        var row = Assert.Single(vm.Rows);

        await vm.OpenOnNexusCommand.ExecuteAsync(row);
        var uri = Assert.Single(launcher.OpenedUris);
        Assert.Equal(row.SearchUrl, uri.OriginalString);
        Assert.Empty(dialogs.AlertCalls);

        // A launch failure surfaces the fallback alert with the URL.
        launcher.OpenUriResult = _ => false;
        await vm.OpenOnNexusCommand.ExecuteAsync(row);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.NotNull(row.SearchUrl);
        Assert.Contains(row.SearchUrl!, alert.Message);
    }
}
