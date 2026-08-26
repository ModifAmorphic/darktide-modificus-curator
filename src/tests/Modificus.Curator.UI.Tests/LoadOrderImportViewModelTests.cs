using System.Collections.Concurrent;
using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
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

    static LoadOrderImportViewModelTests()
    {
        // Zero inter-query delay: the search queue's human pacing is real
        // production posture, but tests assert the queue's serial contract,
        // not its pacing.
        LoadOrderImportViewModel.SearchQueueDelay = TimeSpan.Zero;
    }

    /// <summary>
    /// Builds the card VM plus a sibling import-workflow VM sharing the same
    /// card gate (the mutual-exclusion tests drive both).
    /// </summary>
    private static (LoadOrderImportViewModel Vm, FakeLoadOrderReconciler Reconciler, FakeProfileService Profiles, FakeProfileSession Session, FakeExternalLauncher Launcher, FakeDialogService Dialogs, FakeNexusSearchClient Nexus, FakeModImportService Imports, FakeNexusAuthService Auth, FakeModAcquisitionService Acquisition, FakeModDownloadQueue Queue, ImportWorkflowViewModel Import)
        Build(FakeProfileService? profiles = null, FakeProfileSession? session = null,
              FakeModRepository? repo = null, FakeLoadOrderReconciler? reconciler = null,
              FakeNexusSearchClient? nexus = null, FakeModImportService? imports = null,
              FakeNexusAuthService? auth = null, FakeModAcquisitionService? acquisition = null,
              FakeExternalLauncher? launcher = null, FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        session ??= new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        repo ??= new FakeModRepository();
        reconciler ??= new FakeLoadOrderReconciler();
        nexus ??= new FakeNexusSearchClient();
        imports ??= new FakeModImportService(repo);
        auth ??= new FakeNexusAuthService();
        acquisition ??= new FakeModAcquisitionService();
        launcher ??= new FakeExternalLauncher();
        dialogs ??= new FakeDialogService();
        var cards = new ModCardsGate();
        var queue = new FakeModDownloadQueue();
        var import = new ImportWorkflowViewModel(
            profiles, session, repo, new FakeModImportService(repo), cards,
            Localization, NullLogger<ImportWorkflowViewModel>.Instance);
        var vm = new LoadOrderImportViewModel(
            profiles, session, reconciler, nexus, imports, auth, acquisition, queue,
            cards, launcher, dialogs,
            Localization, static action => action(),
            NullLogger<LoadOrderImportViewModel>.Instance);
        return (vm, reconciler, profiles, session, launcher, dialogs, nexus, imports, auth, acquisition, queue, import);
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

    // ---- Apply-button enabled notifications ------------------------------------
    // The Apply button binds CanApplyNow; these tests pin that the binding's
    // inputs (card activation + checkbox flips) actually notify CanApplyNow,
    // the notification gap that left the button permanently disabled.

    [Fact]
    public async Task Activation_with_an_included_row_notifies_CanApplyNow()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A Row"));

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA"));

        Assert.Contains(nameof(LoadOrderImportViewModel.CanApplyNow), fired);
        Assert.True(vm.CanApplyNow);
    }

    [Fact]
    public async Task A_checkbox_flip_notifies_CanApplyNow()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, Guid.NewGuid(), "ModB", "B Container"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModB"));

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        vm.Rows[0].IsIncluded = true;

        Assert.Contains(nameof(LoadOrderImportViewModel.CanApplyNow), fired);
        Assert.True(vm.CanApplyNow);
    }

    // ---- activation + table ---------------------------------------------------

    [Fact]
    public async Task StartImport_activates_and_builds_rows_with_the_checkbox_defaults()
    {
        var reorder = Guid.NewGuid();
        var add = Guid.NewGuid();
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, _, _, _, dialogs, _, _, _, _, _, _) = Build();
        var missing = Path.Combine(Path.GetTempPath(), "curator-missing-" + Guid.NewGuid() + ".txt");

        await vm.StartImportCommand.ExecuteAsync(missing);

        Assert.False(vm.IsActive);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(missing, alert.Message);
    }

    [Fact]
    public async Task A_second_start_while_active_refuses()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, _, _, session, _, _, _, _, _, _, import) = Build();
        import.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        Assert.True(import.IsActive);

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        Assert.False(vm.IsActive);
        Assert.Empty(reconciler.Calls);
    }

    [Fact]
    public async Task Import_starts_refuse_while_a_load_order_review_is_active()
    {
        var (vm, reconciler, _, _, session, _, _, _, _, _, _, import) = Build();
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
        var (vm, reconciler, profiles, session, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, profiles, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, profiles, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, profiles, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build(profiles: profiles, session: session);
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
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
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
        var (vm, reconciler, _, _, launcher, dialogs, _, _, _, _, _, _) = Build();
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

    // ---- the resolver tier: the search queue + the identification workspace ----

    /// <summary>Search results shaped for the fake client.</summary>
    private static IReadOnlyList<NexusSearchResult> Results(params (int Id, string Name)[] mods) =>
        mods.Select(m => new NexusSearchResult(m.Id, m.Name, null)).ToArray();

    /// <summary>Starts a review whose only line is unresolved (the search tier's row).</summary>
    private static async Task<LoadOrderRowViewModel> StartUnresolvedAsync(
        LoadOrderImportViewModel vm, FakeLoadOrderReconciler reconciler, string name)
    {
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { name },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile(name + "\n"));
        return Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task A_candidates_arrival_fills_the_top_slot_and_alternates()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "Warp Unbound Timer"), (2, "Other Mod"), (3, "Third"));
        var row = await StartUnresolvedAsync(vm, reconciler, "warp_unbound_timer");

        Assert.True(row.HasCandidates);
        Assert.Equal(1, row.TopCandidate!.ModId);
        Assert.Equal("Warp Unbound Timer", row.TopCandidate.Name);
        Assert.Equal(2, row.AlternateCandidates.Count); // the cap is 5; only 3 arrived
        Assert.True(row.ShowCandidateWorkspace);
        Assert.False(row.IsIdentified);

        // One serial search with the normalized terms.
        var call = Assert.Single(nexus.SearchCalls);
        Assert.Equal("warp unbound timer", call.Terms);
        Assert.Equal("warhammer40kdarktide", call.Domain);
    }

    [Fact]
    public async Task The_candidate_cap_limits_the_proposals()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results(
            (1, "A"), (2, "B"), (3, "C"), (4, "D"), (5, "E"), (6, "F"), (7, "G"));
        var row = await StartUnresolvedAsync(vm, reconciler, "mod");

        Assert.Equal(5, row.Candidates.Count);
        Assert.Equal(4, row.AlternateCandidates.Count);
    }

    [Fact]
    public async Task Search_terms_are_normalized_from_folder_names()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results();
        await StartUnresolvedAsync(vm, reconciler, "Some_Mod-Name  v2");

        var call = Assert.Single(nexus.SearchCalls);
        Assert.Equal("some mod name v2", call.Terms);

        // The pure normalizer pinned directly.
        Assert.Equal("warp unbound timer", LoadOrderImportViewModel.NormalizeSearchTerms("Warp_Unbound--TIMER"));
        Assert.Equal("single", LoadOrderImportViewModel.NormalizeSearchTerms("  single  "));
    }

    [Fact]
    public async Task Identification_enables_the_include_checkbox_and_the_enqueue_path()
    {
        // The transition the bindings live on: an unresolved row's include
        // checkbox + manual-pending gate flip when the row identifies, for
        // BOTH identification kinds (the premium-enqueue path is unreachable
        // through the UI without these).
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "The Real Mod"));
        var row = await StartUnresolvedAsync(vm, reconciler, "realmod");
        Assert.False(row.IsIncludeEnabled);

        vm.AcceptCandidateCommand.Execute(row);
        Assert.True(row.IsIdentified);
        Assert.True(row.IsIncludeEnabled);
        Assert.False(row.IsIncluded); // enabled but still default-excluded

        // Manual: fresh row, same transition.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ghost2" }, Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost2\n"));
        var manual = Assert.Single(vm.Rows);
        Assert.False(manual.IsIncludeEnabled);
        manual.ManualId = "7";
        Assert.True(manual.IsManualPending);
        vm.ApplyManualIdCommand.Execute(manual);
        Assert.True(manual.IsIdentified);
        Assert.True(manual.IsIncludeEnabled);
    }

    [Fact]
    public async Task Accepting_the_top_candidate_marks_the_row_identified()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "The Real Mod"));
        var row = await StartUnresolvedAsync(vm, reconciler, "realmod");

        vm.AcceptCandidateCommand.Execute(row);

        Assert.True(row.IsIdentified);
        Assert.Equal(42, row.IdentifiedModId);
        Assert.Equal("The Real Mod", row.IdentifiedName);
        Assert.Equal(LoadOrderRowViewModel.IdentificationKind.Candidate, row.IdentifiedBy);
        Assert.False(row.ShowCandidateWorkspace); // the workspace collapsed

        // The include checkbox kept its (excluded) default: identification is
        // not consent.
        Assert.False(row.IsIncluded);
    }

    [Fact]
    public async Task Accepting_an_alternate_identifies_with_that_candidate()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "Wrong Guess"), (99, "The Right One"));
        var row = await StartUnresolvedAsync(vm, reconciler, "mod");

        row.IsExpanded = true;
        vm.AcceptAlternateCommand.Execute((row, row.AlternateCandidates[0]));

        Assert.True(row.IsIdentified);
        Assert.Equal(99, row.IdentifiedModId);
        Assert.Equal("The Right One", row.IdentifiedName);
        Assert.False(row.IsExpanded); // collapsed on identify
    }

    [Fact]
    public async Task Manual_entry_identifies_by_bare_id_and_by_url()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        var row = await StartUnresolvedAsync(vm, reconciler, "ghost");

        // A bare id.
        row.ManualId = "77";
        Assert.True(row.IsManualPending); // parses: the Apply button shows
        vm.ApplyManualIdCommand.Execute(row);
        Assert.True(row.IsIdentified);
        Assert.Equal(77, row.IdentifiedModId);
        Assert.Equal(LoadOrderRowViewModel.IdentificationKind.Manual, row.IdentifiedBy);

        // A URL, on a fresh row.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ghost2" }, Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost2\n"));
        var row2 = Assert.Single(vm.Rows);
        row2.ManualId = "https://www.nexusmods.com/warhammer40kdarktide/mods/123";
        vm.ApplyManualIdCommand.Execute(row2);
        Assert.True(row2.IsIdentified);
        Assert.Equal(123, row2.IdentifiedModId);

        // Garbage never identifies (the button's own parse gate).
        row2.ManualId = "not an id";
        Assert.False(row2.IsManualPending);
    }

    [Fact]
    public async Task A_failed_search_leaves_the_row_unresolved_with_the_manual_path()
    {
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextSearchThrows = new InvalidOperationException("cloudflare says no");
        var row = await StartUnresolvedAsync(vm, reconciler, "ghost");

        Assert.False(row.HasCandidates);
        Assert.False(row.IsSearching);
        Assert.False(row.IsIdentified);
        Assert.True(row.IsUnresolved);
        // The manual path is alive: typing a valid id offers the Apply.
        row.ManualId = "5";
        Assert.True(row.IsManualPending);
    }

    [Fact]
    public async Task Cancelling_the_card_stops_the_search_queue()
    {
        // The fake's searches complete inline, so a mid-flight stop is not
        // directly observable; pin the lifecycle contract instead: cancel
        // tears the queue's token (a restart never re-fires the OLD rows'
        // searches; only the new session's rows search), and a new session's
        // queue starts fresh.
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "one", "two" },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\ntwo\n"));
        Assert.Equal(2, nexus.SearchCalls.Count); // both rows searched, serial

        vm.CancelCommand.Execute(null);
        Assert.False(vm.IsActive);

        // A restart searches only the new session's rows: the old queue is
        // gone with the card.
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "three" }, Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());
        var before = nexus.SearchCalls.Count;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("three\n"));
        Assert.Equal(before + 1, nexus.SearchCalls.Count);
        Assert.Equal("three", nexus.SearchCalls[^1].Terms);
    }

    [Fact]
    public async Task Resolved_rows_never_search()
    {
        var (vm, reconciler, profiles, _, _, _, nexus, _, _, _, _, _) = Build();
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        Assert.Empty(nexus.SearchCalls);
    }

    // ---- the apply paths: sibling imports + enqueue batch --------------------

    /// <summary>
    /// Creates a txt in a temp directory surrounded by sibling mod folders
    /// (each carrying <c>name/name.mod</c>) + optional plain junk directories;
    /// returns the txt's path.
    /// </summary>
    private static string WriteLoadOrderWithSiblings(
        string fileContent, params (string Name, bool IsMod)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-loadorder-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        foreach (var (name, isMod) in siblings)
        {
            var modDir = Path.Combine(dir, name);
            Directory.CreateDirectory(modDir);
            if (isMod)
            {
                File.WriteAllText(Path.Combine(modDir, name + ".mod"), name);
            }
        }

        var txt = Path.Combine(dir, "mod_load_order.txt");
        File.WriteAllText(txt, fileContent);
        return txt;
    }

    /// <summary>A plan whose only line is unresolved (the sibling-upgrade input).</summary>
    private static LoadOrderPlan UnresolvedPlan(string name) =>
        LoadOrderPlanner.Build(
            new[] { name },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());

    [Fact]
    public async Task Sibling_folders_resolve_as_import_lines_with_skips()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "RealMod", "AlsoReal", "NoDescriptor", "base" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        var txt = WriteLoadOrderWithSiblings(
            "ModA\nRealMod\nAlsoReal\nNoDescriptor\nbase\n",
            ("RealMod", true), ("AlsoReal", true), ("NoDescriptor", false),
            ("base", true), // a real base/base.mod exists: still skipped (the runtime)
            ("Unlisted", true)); // not named by the file: never scanned into a row

        await vm.StartImportCommand.ExecuteAsync(txt);

        Assert.Equal(5, vm.Rows.Count);
        var real = vm.Rows.Single(r => r.Name == "RealMod");
        Assert.Equal(LoadOrderLineOutcome.SiblingImport, real.Outcome);
        Assert.NotNull(real.SiblingPath);
        Assert.Equal("RealMod", real.MatchText);
        Assert.Equal(Localization["LoadOrder_OutcomeImport"], real.OutcomeText);
        Assert.False(real.IsIncluded); // add default: excluded
        Assert.True(real.IsIncludeEnabled);
        Assert.False(real.IsUnresolved); // never searched

        var also = vm.Rows.Single(r => r.Name == "AlsoReal");
        Assert.Equal(LoadOrderLineOutcome.SiblingImport, also.Outcome);

        // A directory without the descriptor stays unresolved.
        var noDescriptor = vm.Rows.Single(r => r.Name == "NoDescriptor");
        Assert.Equal(LoadOrderLineOutcome.Unresolved, noDescriptor.Outcome);

        // base stays unresolved even with a descriptor on disk.
        var baseRow = vm.Rows.Single(r => r.Name == "base");
        Assert.Equal(LoadOrderLineOutcome.Unresolved, baseRow.Outcome);

        // A resolved line (profile member) is never upgraded even when a
        // same-named folder exists beside the txt.
        Assert.Equal(LoadOrderLineOutcome.Reorder, vm.Rows[0].Outcome);

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task The_version_cell_exists_only_for_identified_sibling_rows()
    {
        // Identified Unresolved rows (no local content) carry NO version
        // cell: the Premium download resolves the real version, non-premium
        // visits Nexus regardless. Both identification kinds, absent.
        var (vm, reconciler, _, _, _, _, nexus, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "A Mod"));
        var accepted = await StartUnresolvedAsync(vm, reconciler, "ghost1");
        Assert.False(accepted.IsVersionCellVisible); // unidentified
        vm.AcceptCandidateCommand.Execute(accepted);
        Assert.True(accepted.IsIdentified);
        Assert.False(accepted.IsVersionCellVisible); // identified, no content

        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ghost2" }, Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost2\n"));
        var manual = Assert.Single(vm.Rows);
        manual.ManualId = "9";
        vm.ApplyManualIdCommand.Execute(manual);
        Assert.True(manual.IsIdentified);
        Assert.False(manual.IsVersionCellVisible); // manual, no content

        // Identified sibling rows: the cell renders (it tags the disk
        // content); unidentified siblings: absent.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = UnresolvedPlan("RealMod");
        var txt = WriteLoadOrderWithSiblings("RealMod\n", ("RealMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            var sibling = Assert.Single(vm.Rows);
            Assert.False(sibling.IsVersionCellVisible); // unidentified sibling
            sibling.ManualId = "42";
            vm.ApplyManualIdCommand.Execute(sibling);
            Assert.True(sibling.IsIdentified);
            Assert.True(sibling.IsVersionCellVisible); // identified sibling

            // The version stays empty + unconsumed for no-content rows (the
            // enqueue path never reads it).
            manual.Version = "1.0"; // setting is harmless; nothing binds it
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    [Fact]
    public async Task Apply_imports_an_identified_sibling_with_nexus_source_and_version()
    {
        var (vm, reconciler, profiles, _, _, _, _, imports, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("RealMod");
        var txt = WriteLoadOrderWithSiblings("RealMod\n", ("RealMod", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        var row = Assert.Single(vm.Rows);

        // Identify (a bare manual id) + include + a version.
        row.ManualId = "42";
        vm.ApplyManualIdCommand.Execute(row);
        row.IsIncluded = true;
        row.Version = "1.4";

        var importedContainer = Guid.NewGuid();
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (importedContainer, "v-folder") });
        await vm.ApplyCommand.ExecuteAsync(null);

        // The import carried the Nexus identity + the typed version.
        var call = Assert.Single(imports.Imports);
        Assert.Equal(row.SiblingPath, call.SourcePath);
        Assert.Equal("RealMod", call.ModName);
        var source = Assert.IsType<NexusSource>(call.Source);
        Assert.Equal(42, source.ModId);
        Assert.Equal("1.4", call.Version);

        // The imported container joined the profile + the order write.
        Assert.Contains(profiles.AddModCalls, c => c.ContainerId == importedContainer);
        Assert.Equal([importedContainer], Assert.Single(profiles.SetModOrderCalls));
        Assert.False(vm.IsActive);

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task Apply_imports_an_unidentified_sibling_untracked_with_empty_version()
    {
        var (vm, reconciler, profiles, _, _, _, _, imports, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("PlainMod");
        var txt = WriteLoadOrderWithSiblings("PlainMod\n", ("PlainMod", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        var row = Assert.Single(vm.Rows);
        row.IsIncluded = true; // not identified: still includeable (the import path needs no identity)

        var importedContainer = Guid.NewGuid();
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (importedContainer, "v-folder") });
        await vm.ApplyCommand.ExecuteAsync(null);

        var call = Assert.Single(imports.Imports);
        Assert.IsType<UntrackedSource>(call.Source);
        Assert.Equal(string.Empty, call.Version); // the version-unknown path
        Assert.Contains(profiles.AddModCalls, c => c.ContainerId == importedContainer);
    }

    [Fact]
    public async Task The_final_order_write_carries_every_container_in_file_order()
    {
        // [reorder, library add (included), sibling import (included)] in file
        // order: the order write lists all three, after membership.
        var (vm, reconciler, profiles, session, _, _, _, imports, _, _, _, _) = Build();
        var reorder = Guid.NewGuid();
        var library = Guid.NewGuid();
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = reorder, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "ModB", "ModC" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            new[] { new LoadOrderRepoCandidate(library, "ModB", false, "B") });
        var txt = WriteLoadOrderWithSiblings("ModA\nModB\nModC\n", ("ModC", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        vm.Rows.Single(r => r.Name == "ModB").IsIncluded = true;
        vm.Rows.Single(r => r.Name == "ModC").IsIncluded = true;

        var imported = Guid.NewGuid();
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (imported, "v-folder") });
        await vm.ApplyCommand.ExecuteAsync(null);

        // ONE order write, listing all three at their file positions.
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal([reorder, library, imported], order);

        // Sequencing pinned through the resulting list: membership precedes
        // the order write (the fake mirrors production's projection), so the
        // imported container lands at its file position rather than appended
        // by AddMod.
        var resulting = profiles.GetModList(session.ActiveProfileId!.Value);
        Assert.Equal([reorder, library, imported], resulting.Select(e => e.ContainerId));

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task A_per_line_import_failure_is_recorded_and_the_rest_continue()
    {
        var (vm, reconciler, profiles, _, _, _, _, imports, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Bad", "Good" },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());
        var txt = WriteLoadOrderWithSiblings("Bad\nGood\n", ("Bad", true), ("Good", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        foreach (var row in vm.Rows)
        {
            row.IsIncluded = true;
        }

        var good = Guid.NewGuid();
        imports.ImportExceptionQueue = new Queue<Exception?>(
            new Exception?[] { new InvalidOperationException("bad archive"), null });
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (good, "v-folder") });
        await vm.ApplyCommand.ExecuteAsync(null);

        var badRow = vm.Rows.Single(r => r.Name == "Bad");
        Assert.NotNull(badRow.LineFailure);
        Assert.Contains("bad archive", badRow.LineFailure);
        // The sibling line's failure did not stop the apply: the good import
        // landed + the reload fired; the review stays open so the failed
        // row's message stays readable.
        Assert.Contains(profiles.AddModCalls, c => c.ContainerId == good);
        Assert.True(vm.IsActive);

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task Premium_enqueues_one_download_per_included_identified_line()
    {
        var (vm, reconciler, profiles, session, _, _, _, _, auth, acquisition, queue, _) = Build();
        var profileId = session.ActiveProfileId!.Value;
        reconciler.NextPlan = UnresolvedPlan("Ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Ghost\n"));
        var row = Assert.Single(vm.Rows);
        row.ManualId = "77";
        vm.ApplyManualIdCommand.Execute(row);
        row.IsIncluded = true;
        acquisition.NextResolve = (1234, "2.0");

        await vm.ApplyCommand.ExecuteAsync(null);

        // The premium gate was verified fresh at apply time.
        Assert.Equal(1, auth.GetCurrentStateCallCount);

        // One ProfileAdd enqueue with no container (the download owns the
        // import + the add) + the resolved head file.
        var request = Assert.Single(queue.Requests);
        Assert.Equal(77, request.ModId);
        Assert.Equal(1234, request.FileId);
        Assert.Equal(DownloadPurpose.ProfileAdd, request.Purpose);
        Assert.Null(request.ContainerId);
        Assert.Equal(profileId, request.TargetProfileId);
        Assert.Single(acquisition.ResolveLatestCalls);

        // No local add/order for the line (nothing exists locally), but the
        // pending flag + reload still fired on success.
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(vm.IsActive);
        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task Non_premium_performs_no_network_action_for_identified_lines()
    {
        var (vm, reconciler, _, _, _, _, _, _, auth, acquisition, queue, _) = Build();
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false);
        reconciler.NextPlan = UnresolvedPlan("Ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Ghost\n"));
        var row = Assert.Single(vm.Rows);
        row.ManualId = "77";
        vm.ApplyManualIdCommand.Execute(row);
        row.IsIncluded = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Equal(1, auth.GetCurrentStateCallCount); // checked, then skipped
        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task A_rate_limit_aborts_the_remaining_enqueues_and_prior_work_stands()
    {
        var (vm, reconciler, profiles, session, _, _, _, _, auth, acquisition, queue, _) = Build();
        var profileId = session.ActiveProfileId!.Value;
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "One", "Two" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "One", "One") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("One\nTwo\n"));

        // Identify + include only the second line (the first is a reorder
        // include, so apply is available + the local write happens).
        var ghost = vm.Rows.Single(r => r.Name == "Two");
        ghost.ManualId = "88";
        vm.ApplyManualIdCommand.Execute(ghost);
        ghost.IsIncluded = true;

        // The enqueue's resolve (the only network call: one identified line)
        // throws 429.
        acquisition.ResolveThrowQueue.Enqueue(
            new NexusRateLimitException(429, new NexusRateLimits(2500, 0, null, 100, 0, null)));

        await vm.ApplyCommand.ExecuteAsync(null);

        // Prior work stands: the order write for the reorder line landed.
        Assert.Single(profiles.SetModOrderCalls);
        // No enqueue admitted (the resolve threw before it).
        Assert.Empty(queue.Requests);
        // The failure is on the card + the card stays open for a re-run, and
        // the reload fired so the landed work shows.
        Assert.True(vm.IsActive);
        Assert.NotNull(vm.ApplyFailure);
        Assert.Contains("rate limit", vm.ApplyFailure, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task A_rate_limit_aborts_the_REMAINING_enqueues_with_prior_ones_landed()
    {
        // Two identified lines beyond the reorder: "Second"'s resolve
        // succeeds + enqueues, "Third"'s throws 429 - the abort skips what
        // REMAINED (Third), keeping Second's landed enqueue.
        var (vm, reconciler, _, session, _, _, _, _, auth, acquisition, queue, _) = Build();
        var profileId = session.ActiveProfileId!.Value;
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "First", "Second", "Third" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "First", "First") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("First\nSecond\nThird\n"));

        foreach (var name in new[] { "Second", "Third" })
        {
            var row = vm.Rows.Single(r => r.Name == name);
            row.ManualId = name == "Second" ? "2" : "3";
            vm.ApplyManualIdCommand.Execute(row);
            row.IsIncluded = true;
        }

        // The second line's resolve succeeds; the third's throws 429.
        acquisition.ResolveThrowQueue.Enqueue(null);
        acquisition.ResolveThrowQueue.Enqueue(
            new NexusRateLimitException(429, new NexusRateLimits(2500, 0, null, 100, 0, null)));

        await vm.ApplyCommand.ExecuteAsync(null);

        // The second line's enqueue landed; the third was skipped.
        Assert.Single(queue.Requests);
        Assert.Equal(2, queue.Requests[0].ModId);
        Assert.True(vm.IsActive); // the re-runnable failure keeps the card open
        Assert.NotNull(vm.ApplyFailure);
    }

    [Fact]
    public async Task A_resolve_failure_on_one_line_is_recorded_and_the_batch_continues()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, acquisition, queue, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Boom", "Fine" },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Boom\nFine\n"));
        foreach (var row in vm.Rows)
        {
            row.ManualId = row.Name == "Boom" ? "1" : "2";
            vm.ApplyManualIdCommand.Execute(row);
            row.IsIncluded = true;
        }

        acquisition.ResolveThrowQueue.Enqueue(new InvalidOperationException("api down"));
        acquisition.NextResolve = (555, "1.0");

        await vm.ApplyCommand.ExecuteAsync(null);

        var boom = vm.Rows.Single(r => r.Name == "Boom");
        Assert.NotNull(boom.LineFailure);
        var fine = vm.Rows.Single(r => r.Name == "Fine");
        Assert.Null(fine.LineFailure);
        Assert.Single(queue.Requests); // the fine line enqueued
        // A per-line failure is not a card-level failure, but the review stays
        // open so the row's message stays readable + a re-run can finish it.
        Assert.True(vm.IsActive);
    }

    [Fact]
    public async Task A_profile_switch_mid_apply_defers_the_reset_and_completes_the_write()
    {
        // The in-flight apply owns its captured profile: a switch mid-apply
        // defers the reset (emptying Rows under the running phases would
        // silently drop the AddMods, the order write, and the enqueues while
        // the imports had landed); the apply completes, then the deferred
        // reset deactivates the card.
        var profiles = TestDoubles.Profiles(
            new ProfileSummary(Guid.NewGuid(), "Alpha", ""),
            new ProfileSummary(Guid.NewGuid(), "Beta", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var (vm, reconciler, _, _, _, _, _, imports, _, _, _, _) = Build(
            profiles: profiles, session: session);
        var captured = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        profiles.WithMods(captured,
            new ModListEntry { ContainerId = reorder, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "Held" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        var txt = WriteLoadOrderWithSiblings("ModA\nHeld\n", ("Held", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        vm.Rows.Single(r => r.Name == "Held").IsIncluded = true;
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (Guid.NewGuid(), "v") });
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        imports.ImportGate = gate;

        var applying = vm.ApplyCommand.ExecuteAsync(null);
        // Switch while the import worker is held inside Import.
        session.ActiveProfileId = profiles.ListProfiles().Last().Id;
        Assert.True(vm.IsActive); // not reset mid-apply

        gate.TrySetResult(true);
        await applying;

        // The full apply completed against the CAPTURED profile: the add +
        // the order write landed on it, and the deferred reset then
        // deactivated the card.
        Assert.Contains(profiles.AddModCalls, c => c.Id == captured);
        Assert.Contains(reorder, Assert.Single(profiles.SetModOrderCalls));
        Assert.False(vm.IsActive);

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task A_deferred_marshal_seam_still_adds_and_orders_the_imported_container()
    {
        // The production race, pinned: the import worker posts the
        // container-id assignment through the marshal seam, and the seam's
        // post may land AFTER the awaited continuation proceeds (the logs
        // showed the order write missing the imported container + AddMod
        // never running). The apply passes the import results as data, so a
        // seam that DEFERS every post until after the apply task completes
        // must still produce the add + the order entry.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession { ActiveProfileId = profiles.ListProfiles().First().Id };
        var repo = new FakeModRepository();
        var reconciler = new FakeLoadOrderReconciler();
        var imports = new FakeModImportService();
        var deferred = new ConcurrentQueue<Action>();

        var vm = new LoadOrderImportViewModel(
            profiles, session, reconciler, new FakeNexusSearchClient(),
            imports, new FakeNexusAuthService { State = null },
            new FakeModAcquisitionService(), new FakeModDownloadQueue(),
            new ModCardsGate(), new FakeExternalLauncher(), new FakeDialogService(),
            Localization, action => deferred.Enqueue(action),
            NullLogger<LoadOrderImportViewModel>.Instance);

        var captured = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        profiles.WithMods(captured,
            new ModListEntry { ContainerId = reorder, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "Held" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        var txt = WriteLoadOrderWithSiblings("ModA\nHeld\n", ("Held", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        vm.Rows.Single(r => r.Name == "Held").IsIncluded = true;
        var importedContainer = Guid.NewGuid();
        imports.NextImportResults =
            new Queue<(Guid, string)>(new[] { (importedContainer, "v") });

        await vm.ApplyCommand.ExecuteAsync(null);

        // Not one deferred post has run (the seam drained nothing); the
        // apply's data path must have carried the container regardless.
        Assert.NotEmpty(deferred);
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Contains(importedContainer, order);
        Assert.Contains(profiles.AddModCalls, c => c.ContainerId == importedContainer);

        // Drain now: the display assignments land + change nothing.
        while (deferred.TryDequeue(out var action))
        {
            action();
        }
        Assert.Equal([reorder, importedContainer], Assert.Single(profiles.SetModOrderCalls));

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task An_active_apply_holds_the_card_gate()
    {
        // The card stays active while the apply runs, so the import workflow
        // refuses to start until the apply finishes (the shared card gate).
        var (vm, reconciler, profiles, _, _, _, _, imports, _, _, _, import) = Build();
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        // Hold the apply mid-flight through the import gate... the reorder
        // plan has no imports, so hold through a sibling import instead.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = UnresolvedPlan("Held");
        var txt = WriteLoadOrderWithSiblings("Held\n", ("Held", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        vm.Rows.Single().IsIncluded = true;
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (Guid.NewGuid(), "v") });
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        imports.ImportGate = gate;

        var applying = vm.ApplyCommand.ExecuteAsync(null);
        // The card (and with it the shared card gate) stays active while the
        // apply runs: the import workflow cannot start mid-apply.
        Assert.True(vm.IsActive);
        import.StartBatchCommand.Execute(new[] { "/tmp/other" });
        Assert.False(import.IsActive);

        gate.TrySetResult(true);
        await applying;
        Assert.False(vm.IsActive);
        import.StartBatchCommand.Execute(new[] { "/tmp/other" });
        Assert.True(import.IsActive);

        try
        {
            Directory.Delete(Path.GetDirectoryName(txt)!, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
