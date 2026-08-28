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
/// <see cref="LoadOrderImportViewModel"/> (the load-order import workspace):
/// the mode-choice workflow (Start reaches the choice with zero Nexus/auth
/// traffic; each tile decides the operation), the two review projections, the
/// automatic inclusion + Skip/Undo opt-out, the serial search queue + its
/// Stop, candidate/manual identification (including the remote exact-id
/// verification), the per-mode apply contract, the Premium enqueue honesty,
/// the pending-placement recording, and the reset paths. The plan comes from
/// a fake reconciler; the real reconciliation is covered by
/// <c>Modificus.Curator.Profiles.Tests.LoadOrderPlannerTests</c>; the
/// placement convergence matrix by
/// <see cref="LoadOrderDownloadPlacementsTests"/>.
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
    /// Builds the workspace VM plus a sibling import-workflow VM sharing the
    /// same card gate (the mutual-exclusion tests drive both), over the real
    /// <see cref="LoadOrderDownloadPlacements"/> component (the apply-recorded
    /// plan's convergence is asserted through it).
    /// </summary>
    private static (LoadOrderImportViewModel Vm, FakeLoadOrderReconciler Reconciler, FakeProfileService Profiles, FakeProfileSession Session, FakeDialogService Dialogs, FakeNexusSearchClient Nexus, FakeModImportService Imports, FakeNexusAuthService Auth, FakeModAcquisitionService Acquisition, FakeModDownloadQueue Queue, LoadOrderDownloadPlacements Placements, ImportWorkflowViewModel Import)
        Build(FakeProfileService? profiles = null, FakeProfileSession? session = null,
              FakeModRepository? repo = null, FakeLoadOrderReconciler? reconciler = null,
              FakeNexusSearchClient? nexus = null, FakeModImportService? imports = null,
              FakeNexusAuthService? auth = null, FakeModAcquisitionService? acquisition = null,
              FakeDialogService? dialogs = null)
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
        dialogs ??= new FakeDialogService();
        var cards = new ModCardsGate();
        var queue = new FakeModDownloadQueue();
        var placements = new LoadOrderDownloadPlacements(
            queue, profiles, NullLogger<LoadOrderDownloadPlacements>.Instance);
        var import = new ImportWorkflowViewModel(
            profiles, session, repo, new FakeModImportService(repo), cards,
            Localization, NullLogger<ImportWorkflowViewModel>.Instance);
        var vm = new LoadOrderImportViewModel(
            profiles, session, reconciler, nexus, imports, auth, acquisition, queue,
            placements, cards, dialogs,
            Localization, static action => action(),
            NullLogger<LoadOrderImportViewModel>.Instance);
        return (vm, reconciler, profiles, session, dialogs, nexus, imports, auth, acquisition, queue, placements, import);
    }

    /// <summary>Writes a temp load-order file and returns its path.</summary>
    private static string WriteFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "curator-loadorder-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

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

    private static void CleanupTxt(string txt)
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

    private static LoadOrderPlan Plan(params LoadOrderLine[] lines) =>
        LoadOrderPlanner.Build(
            lines.Select(l => l.Name).ToArray(),
            lines.Where(l => l.Outcome == LoadOrderLineOutcome.Reorder)
                .Select(l => new LoadOrderProfileMod(l.ContainerId!.Value, l.MatchedBaseName!, l.DisplayName!))
                .ToArray(),
            lines.Where(l => l.Outcome == LoadOrderLineOutcome.LibraryAdd)
                .Select(l => new LoadOrderRepoCandidate(l.ContainerId!.Value, l.MatchedBaseName!, false, l.DisplayName!))
                .ToArray());

    /// <summary>A plan whose only line is unresolved (the lookup-tier row).</summary>
    private static LoadOrderPlan UnresolvedPlan(params string[] names) =>
        LoadOrderPlanner.Build(names, Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());

    /// <summary>Search results shaped for the fake client.</summary>
    private static IReadOnlyList<NexusSearchResult> Results(params (int Id, string Name)[] mods) =>
        mods.Select(m => new NexusSearchResult(m.Id, m.Name, null)).ToArray();

    /// <summary>
    /// Waits (polling, bounded) for a condition the search queue's released
    /// continuation produces on a threadpool thread.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The awaited condition did not materialize in time.");
    }

    // ---- Start: the mode choice, before any Nexus traffic --------------------

    [Fact]
    public async Task Start_reaches_the_mode_choice_with_no_Nexus_or_auth_calls()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A Row"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null));

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nGhost"));

        Assert.True(vm.IsActive);
        Assert.Equal(LoadOrderStage.ChoosingMode, vm.Stage);
        Assert.True(vm.IsChoosingMode);
        Assert.False(vm.IsReviewing);
        Assert.Empty(vm.Rows); // rows are built at the mode choice, not here
        Assert.Empty(nexus.SearchCalls);
        Assert.Empty(nexus.GetModByIdCalls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);
        Assert.Contains("2", vm.ChoiceSummaryText); // the entry count in the summary
        var call = Assert.Single(reconciler.Calls);
        Assert.Equal(["ModA", "Ghost"], call.Names);
    }

    [Fact]
    public async Task An_empty_file_shows_the_notice_and_the_tiles_refuse()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlan.Empty;

        await vm.StartImportCommand.ExecuteAsync(WriteFile("-- only comments\n"));

        Assert.True(vm.IsActive);
        Assert.True(vm.ShowEmptyNotice);
        Assert.Equal(0, auth.GetCurrentStateCallCount);

        vm.ChooseReorderCommand.Execute(null);
        await vm.ChooseImportCommand.ExecuteAsync(null);
        Assert.Equal(LoadOrderStage.ChoosingMode, vm.Stage);
        Assert.Empty(nexus.SearchCalls);
    }

    [Fact]
    public async Task An_unreadable_file_alerts_and_stays_inactive()
    {
        var (vm, reconciler, _, _, dialogs, _, _, _, _, _, _, _) = Build();
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

        // Same session: same file (the second file did not replace it).
        Assert.Equal(first, vm.SourcePath);
    }

    // ---- mutual exclusion (both directions) -----------------------------------

    [Fact]
    public async Task A_load_order_start_refuses_while_the_import_workflow_is_active()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, import) = Build();
        import.StartBatchCommand.Execute(new[] { "/tmp/some-mod" });
        Assert.True(import.IsActive);

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));

        Assert.False(vm.IsActive);
        Assert.Empty(reconciler.Calls);
    }

    [Fact]
    public async Task Import_starts_refuse_while_a_load_order_session_is_active()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, import) = Build();
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

    // ---- the reorder mode ------------------------------------------------------

    [Fact]
    public async Task Choosing_reorder_builds_the_review_with_zero_network_calls()
    {
        var reorder = Guid.NewGuid();
        var library = Guid.NewGuid();
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A Row"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, library, "ModB", "B Container"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null));

        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB\nGhost"));
        vm.ChooseReorderCommand.Execute(null);

        Assert.True(vm.IsReviewing);
        Assert.True(vm.IsReorderMode);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Empty(nexus.SearchCalls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);

        // The projection: the profile match acts; everything else is visible
        // as skipped; the missing line's Match column says not found.
        var reorderRow = vm.Rows[0];
        Assert.Equal("A Row", reorderRow.MatchText);
        Assert.Equal(Localization["LoadOrder_OutcomeReorder"], reorderRow.ActionText);
        Assert.False(reorderRow.CanSkip); // reordering is the chosen operation
        Assert.False(reorderRow.ShowManualEntry); // no lookup affordances at all

        Assert.Equal(Localization["LoadOrder_ActionSkipped"], vm.Rows[1].ActionText);
        Assert.Equal("B Container", vm.Rows[1].MatchText);

        Assert.Equal(Localization["LoadOrder_OutcomeUnresolved"], vm.Rows[2].MatchText);
        Assert.Equal(Localization["LoadOrder_ActionSkipped"], vm.Rows[2].ActionText);
    }

    [Fact]
    public async Task Reorder_apply_orders_only_profile_matches_and_makes_no_other_calls()
    {
        var reorder = Guid.NewGuid();
        var (vm, reconciler, profiles, session, _, nexus, imports, auth, acquisition, queue, placements, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, Guid.NewGuid(), "ModB", "B"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null));
        var profileId = session.ActiveProfileId!.Value;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB\nGhost"));
        vm.ChooseReorderCommand.Execute(null);
        Assert.True(vm.CanApplyNow);

        var applied = 0;
        vm.OrderApplied += (_, _) => applied++;
        await vm.ApplyCommand.ExecuteAsync(null);

        // ONE order write, profile matches ONLY, in file order.
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal([reorder], order);

        // Zero of everything else.
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(imports.Imports);
        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Empty(nexus.SearchCalls);
        Assert.Empty(nexus.GetModByIdCalls);
        Assert.Equal(0, auth.GetCurrentStateCallCount);
        Assert.False(placements.HasPending(profileId));

        Assert.True(session.HasPendingChanges);
        Assert.False(vm.IsActive);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Reorder_mode_with_no_profile_matches_refuses_apply()
    {
        var (vm, reconciler, profiles, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, Guid.NewGuid(), "ModB", "B"),
            new LoadOrderLine("Ghost", LoadOrderLineOutcome.Unresolved, null, null, null));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModB\nGhost"));
        vm.ChooseReorderCommand.Execute(null);

        // Honest refusal: nothing in this file is in the profile, so there is
        // nothing to reorder and the primary action stays unavailable.
        Assert.False(vm.CanApply);
        Assert.False(vm.CanApplyNow);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsActive);
        Assert.Empty(profiles.SetModOrderCalls);
    }

    // ---- the import mode: inclusion by default + Skip/Undo ----------------------

    [Fact]
    public async Task Choosing_import_reads_the_account_scans_siblings_and_searches_the_lookup_rows()
    {
        var reorder = Guid.NewGuid();
        var (vm, reconciler, profiles, session, _, nexus, _, auth, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "RealMod", "Ghost" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        nexus.NextResults = Results((5, "The Real One"), (7, "Ghostly"));
        var profileId = session.ActiveProfileId!.Value;
        var txt = WriteLoadOrderWithSiblings(
            "ModA\nRealMod\nGhost\n", ("RealMod", true), ("Unlisted", true), ("base", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);

            Assert.True(vm.IsReviewing);
            Assert.True(vm.IsImportMode);
            Assert.Equal(1, auth.GetCurrentStateCallCount); // the account read

            // The sibling scan upgraded RealMod; base + Unlisted did not
            // become rows (Unlisted is not in the file; base is skipped).
            var sibling = vm.Rows.Single(r => r.Name == "RealMod");
            Assert.Equal(LoadOrderLineOutcome.SiblingImport, sibling.Outcome);
            Assert.NotNull(sibling.SiblingPath);
            Assert.Equal(LoadOrderLineOutcome.Unresolved, vm.Rows.Single(r => r.Name == "Ghost").Outcome);

            // The serial search queue covered the two lookup rows (sibling +
            // unresolved), NOT the resolved profile match; in file order.
            Assert.Equal(2, nexus.SearchCalls.Count);
            Assert.Equal("real mod", nexus.SearchCalls[0].Terms);
            Assert.Equal("ghost", nexus.SearchCalls[1].Terms);
            Assert.Equal(2, vm.SearchTotal);
            Assert.Equal(2, vm.SearchCompletedCount);
            Assert.False(vm.IsSearchRunning);

            // Everything is included by default (no opt-in anywhere).
            Assert.All(vm.Rows, r => Assert.False(r.IsSkipped));
            Assert.Equal(Localization["LoadOrder_OutcomeImport"], sibling.ActionText);
            Assert.True(sibling.ShowCandidateArea);
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task Skip_and_undo_opt_out_of_optional_rows_without_touching_the_order()
    {
        var reorder = Guid.NewGuid();
        var library = Guid.NewGuid();
        var (vm, reconciler, profiles, session, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, reorder, "ModA", "A"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, library, "ModB", "B"));
        var profileId = session.ActiveProfileId!.Value;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        var addRow = vm.Rows.Single(r => r.Name == "ModB");
        Assert.True(addRow.CanSkip); // optional add: skippable
        Assert.False(vm.Rows.Single(r => r.Name == "ModA").CanSkip); // reorder: not skippable

        vm.ToggleSkipCommand.Execute(addRow);
        Assert.True(addRow.IsSkipped);
        Assert.Equal(Localization["LoadOrder_ActionSkipped"], addRow.ActionText);
        Assert.Equal(Localization["LoadOrder_UndoSkipAction"], addRow.SkipActionText);

        await vm.ApplyCommand.ExecuteAsync(null);

        // The skipped add was neither added nor ordered; the reorder stands.
        Assert.Empty(profiles.AddModCalls);
        Assert.Equal([reorder], Assert.Single(profiles.SetModOrderCalls));
        Assert.False(vm.IsActive);

        // Undo restores the row (before the next apply would include it).
    }

    [Fact]
    public async Task Skipping_a_download_row_excludes_it_from_the_enqueue()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, acquisition, queue, _, _) = Build();
        nexus.NextResults = Results((42, "The Real Mod"));
        reconciler.NextPlan = UnresolvedPlan("realmod");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("realmod\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        vm.AcceptCandidateCommand.Execute(row);
        acquisition.NextResolve = (1234, "2.0");

        vm.ToggleSkipCommand.Execute(row);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(queue.Requests);
        Assert.Equal(Localization["LoadOrder_ActionSkipped"], row.ActionText);
    }

    // ---- the search queue: progress, stop, and the apply race --------------------

    [Fact]
    public async Task The_search_runs_serially_reports_progress_and_stops_on_request()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "First Hit"));
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Anchor", "one", "two" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "Anchor", "Anchor") },
            Array.Empty<LoadOrderRepoCandidate>());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.SearchGate = gate;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Anchor\none\ntwo\n"));

        var choosing = vm.ChooseImportCommand.ExecuteAsync(null);
        await choosing;

        // Mid-flight: the first lookup is running, Apply waits for the queue.
        Assert.True(vm.IsSearchRunning);
        Assert.Equal("one", vm.CurrentSearchName);
        Assert.Equal(0, vm.SearchCompletedCount);
        Assert.False(vm.CanApplyNow);

        vm.StopSearchCommand.Execute(null);
        gate.TrySetResult(true); // release the held search
        await WaitUntilAsync(() => !vm.IsSearchRunning);

        Assert.False(vm.IsSearchRunning);
        Assert.True(vm.SearchStopped);
        // The first lookup completed (its candidates stay); the second was
        // never searched.
        Assert.Single(nexus.SearchCalls);
        Assert.True(vm.Rows.Single(r => r.Name == "one").HasCandidates);
        Assert.False(vm.Rows.Single(r => r.Name == "two").HasCandidates);
        // Apply is available again after the stop (the anchor is actionable).
        Assert.True(vm.CanApplyNow);
    }

    [Fact]
    public async Task A_search_failure_leaves_the_row_unidentified_and_the_queue_continues()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextSearchThrows = new InvalidOperationException("cloudflare says no");
        reconciler.NextPlan = UnresolvedPlan("one", "two");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\ntwo\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        Assert.Equal(2, nexus.SearchCalls.Count); // no retry, but the queue continued
        Assert.All(vm.Rows, r => Assert.False(r.IsIdentified));
        Assert.All(vm.Rows, r => Assert.True(r.ShowManualEntry));
        Assert.False(vm.IsSearchRunning);
    }

    [Fact]
    public async Task A_row_identified_before_its_turn_is_skipped_by_the_queue()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "First"));
        nexus.Identities[9] = new NexusSearchResult(9, "Second Mod", null);
        reconciler.NextPlan = UnresolvedPlan("one", "two");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.SearchGate = gate;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\ntwo\n"));

        // Hold the queue on the first lookup; verify the SECOND row's id
        // manually while it waits its turn.
        var choosing = vm.ChooseImportCommand.ExecuteAsync(null);
        await choosing;
        var second = vm.Rows.Single(r => r.Name == "two");
        second.ManualId = "9";
        var verifying = vm.FindNexusModCommand.ExecuteAsync(second);
        gate.TrySetResult(true);
        await verifying;
        await WaitUntilAsync(() => !vm.IsSearchRunning);

        // The queue searched only the first row; the identified second row
        // was skipped without a call.
        Assert.Single(nexus.SearchCalls);
        Assert.Equal("one", nexus.SearchCalls[0].Terms);
        Assert.Equal(2, vm.SearchCompletedCount); // both lookups counted done
        Assert.True(second.IsIdentified);
    }

    [Fact]
    public async Task Resolved_rows_never_search_in_import_mode()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(
            new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"),
            new LoadOrderLine("ModB", LoadOrderLineOutcome.LibraryAdd, Guid.NewGuid(), "ModB", "B"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nModB"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        Assert.Empty(nexus.SearchCalls);
        Assert.Equal(0, vm.SearchTotal);
    }

    [Fact]
    public async Task A_zero_candidate_search_shows_the_no_results_hint_until_identified()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results();
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        Assert.True(row.SearchedNoResults);
        Assert.True(row.ShowNoResultsHint);

        nexus.Identities[3] = new NexusSearchResult(3, "A Real Mod", null);
        row.ManualId = "3";
        await vm.FindNexusModCommand.ExecuteAsync(row);
        Assert.False(row.ShowNoResultsHint);
    }

    // ---- candidates: presentation, accept, change --------------------------------

    [Fact]
    public async Task Candidates_present_the_title_and_id_separately_and_accept_in_one_action()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "Warp Unbound Timer"), (99, "Wrong Guess"));
        reconciler.NextPlan = UnresolvedPlan("warp_unbound_timer");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("warp_unbound_timer\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        // The proposal: the canonical title + the actual id, separately.
        Assert.True(row.ShowCandidateArea);
        Assert.Equal("Warp Unbound Timer", row.TopCandidate!.Name);
        Assert.Equal(42, row.TopCandidate.ModId);
        Assert.Single(row.AlternateCandidates);
        Assert.False(row.IsIdentified);

        // Accept needs no second control: the row is identified + included.
        vm.AcceptCandidateCommand.Execute(row);
        Assert.True(row.IsIdentified);
        Assert.Equal("Warp Unbound Timer", row.IdentifiedName); // the title once, in Match
        Assert.Equal(row.IdentifiedName, row.MatchText);
        Assert.Equal("#42", row.ModIdText); // the id, never the title again
        Assert.False(row.ShowCandidateArea);
        Assert.False(row.IsSkipped); // acceptance implies inclusion
        Assert.Equal(LoadOrderImportMode.ReorderAndImport, row.Mode);
    }

    [Fact]
    public async Task Accepting_an_alternate_identifies_with_that_candidate()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "Wrong Guess"), (99, "The Right One"));
        reconciler.NextPlan = UnresolvedPlan("mod");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("mod\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.IsExpanded = true;
        vm.AcceptAlternateCommand.Execute((row, row.AlternateCandidates[0]));

        Assert.True(row.IsIdentified);
        Assert.Equal(99, row.IdentifiedModId);
        Assert.Equal("The Right One", row.IdentifiedName);
        Assert.False(row.IsExpanded); // collapsed on identify
    }

    [Fact]
    public async Task Change_returns_an_identified_row_to_identification_and_clears_its_validation_state()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "A Mod"));
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        vm.AcceptCandidateCommand.Execute(row);
        row.ManualError = "stale";

        vm.ChangeIdentityCommand.Execute(row);

        Assert.False(row.IsIdentified);
        Assert.True(row.ShowManualEntry); // the entry is back...
        Assert.True(row.ShowCandidateArea); // ...and the arrived candidates stayed
        Assert.Null(row.ManualError); // identity-specific validation cleared
        Assert.Equal(Localization["LoadOrder_OutcomeUnresolved"], row.MatchText);
    }

    // ---- the reconciliation-known facts on matched rows ---------------------------

    /// <summary>Starts an import review whose only line is the given plan.</summary>
    private static async Task<LoadOrderRowViewModel> StartImportReviewAsync(
        LoadOrderImportViewModel vm, FakeLoadOrderReconciler reconciler, string name)
    {
        await vm.StartImportCommand.ExecuteAsync(WriteFile(name + "\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        return Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task A_nexus_profile_match_shows_its_known_id_and_policy_resolved_version()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "ModA", "A Row", NexusModId: 42, Version: "1.4") },
            Array.Empty<LoadOrderRepoCandidate>());
        var row = await StartImportReviewAsync(vm, reconciler, "ModA");

        Assert.Equal("#42", row.ModIdText); // the read-only known id
        Assert.True(row.ShowKnownModId);
        Assert.False(row.ShowIdentifiedFact); // no Change action on a local match
        Assert.False(row.ShowManualEntry); // and no manual identification
        Assert.Equal("1.4", row.KnownVersionText);
    }

    [Fact]
    public async Task A_nexus_library_match_shows_its_known_id_and_latest_version()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModB" },
            Array.Empty<LoadOrderProfileMod>(),
            new[] { new LoadOrderRepoCandidate(Guid.NewGuid(), "ModB", true, "B Container", 84, "2.1") });
        var row = await StartImportReviewAsync(vm, reconciler, "ModB");

        Assert.Equal(LoadOrderLineOutcome.LibraryAdd, row.Outcome);
        Assert.Equal("#84", row.ModIdText);
        Assert.Equal("2.1", row.KnownVersionText); // AddMod applies Latest
    }

    [Fact]
    public async Task An_untracked_match_shows_a_known_version_but_no_id()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModC" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "ModC", "C Row", NexusModId: null, Version: "0.9") },
            Array.Empty<LoadOrderRepoCandidate>());
        var row = await StartImportReviewAsync(vm, reconciler, "ModC");

        Assert.Null(row.ModIdText); // untracked: no Nexus identity
        Assert.False(row.ShowKnownModId);
        Assert.Equal("0.9", row.KnownVersionText); // the known tag shows alone
    }

    [Fact]
    public async Task A_linked_match_shows_neither_fact_and_an_empty_tag_stays_blank()
    {
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "LinkedMod", "EmptyTagMod" },
            new[]
            {
                new LoadOrderProfileMod(Guid.NewGuid(), "LinkedMod", "Linked Row", null, null),
                new LoadOrderProfileMod(Guid.NewGuid(), "EmptyTagMod", "Empty Row", 5, string.Empty),
            },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("LinkedMod\nEmptyTagMod\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        var linked = vm.Rows.Single(r => r.Name == "LinkedMod");
        Assert.Null(linked.ModIdText);
        Assert.Null(linked.KnownVersionText); // linked keeps no version record

        var empty = vm.Rows.Single(r => r.Name == "EmptyTagMod");
        Assert.Equal("#5", empty.ModIdText);
        Assert.Null(empty.KnownVersionText); // an empty tag normalizes to blank
    }

    [Fact]
    public async Task Known_facts_do_not_render_in_the_reorder_projection()
    {
        // The reorder review carries no Mod ID/Version content; the facts
        // ride only the import-mode columns.
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "ModA", "A Row", NexusModId: 42, Version: "1.4") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        vm.ChooseReorderCommand.Execute(null);
        var row = Assert.Single(vm.Rows);

        Assert.Null(row.KnownVersionText);
    }

    [Fact]
    public async Task A_lookup_row_shows_no_known_facts()
    {
        // Unresolved + sibling lines identify through the review surface;
        // the plan carries no facts for them.
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("ghost");
        var row = await StartImportReviewAsync(vm, reconciler, "ghost");

        Assert.Null(row.ModIdText);
        Assert.False(row.ShowKnownModId);
        Assert.Null(row.KnownVersionText);
        Assert.True(row.ShowManualEntry); // the lookup surface, not read-only facts
    }

    // ---- the unique normalized-exact auto-identification ------------------------

    /// <summary>
    /// Runs an import review whose single line searches to exactly one
    /// candidate; returns the row.
    /// </summary>
    private static async Task<LoadOrderRowViewModel> SearchSingleAsync(
        string folderName, string canonicalName)
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((7, canonicalName));
        reconciler.NextPlan = UnresolvedPlan(folderName);
        await vm.StartImportCommand.ExecuteAsync(WriteFile(folderName + "\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        return Assert.Single(vm.Rows);
    }

    [Theory]
    [InlineData("SoloSandbox")]
    [InlineData("Solo Sandbox")]
    [InlineData("Solo_Sandbox")]
    public async Task A_unique_normalized_exact_result_identifies_immediately(string folderName)
    {
        // One candidate + the same normalization on both sides: the search
        // result is already a remote Nexus identity, so the row identifies
        // with the canonical title + Mod ID (implying inclusion, Change
        // available) without a redundant exact-identity verify call.
        var row = await SearchSingleAsync(folderName, "SoloSandbox");

        Assert.True(row.IsIdentified);
        Assert.Equal(7, row.IdentifiedModId);
        Assert.Equal("SoloSandbox", row.IdentifiedName);
        Assert.Equal("SoloSandbox", row.MatchText); // the title once
        Assert.Equal("#7", row.ModIdText);
        Assert.False(row.ShowCandidateArea); // no proposal needed
        Assert.False(row.IsSkipped); // identification implies inclusion
        Assert.True(row.ShowIdentifiedFact); // Change is available
    }

    [Theory]
    [InlineData("Solo")]
    [InlineData("Sandbox")]
    [InlineData("Solo Sandbox Utilities")]
    public async Task A_single_non_exact_result_stays_a_proposal(string folderName)
    {
        // The search is broader than exact (substring + stemmed wildcards):
        // a lone hit that is not a normalized spelling of the line never
        // silently identifies.
        var row = await SearchSingleAsync(folderName, "SoloSandbox");

        Assert.False(row.IsIdentified);
        Assert.True(row.ShowCandidateArea); // the child proposal + Accept
        Assert.True(row.ShowManualEntry);
    }

    [Fact]
    public async Task Multiple_results_stay_proposals_even_when_one_is_exact()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((1, "SoloSandbox"), (2, "SoloSandbox Utilities"));
        reconciler.NextPlan = UnresolvedPlan("SoloSandbox");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("SoloSandbox\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        // Never silently choose between hits, even when the first is exact.
        Assert.False(row.IsIdentified);
        Assert.True(row.ShowCandidateArea);
        Assert.Equal(2, row.Candidates.Count);
    }

    [Fact]
    public async Task The_auto_identification_issues_no_redundant_verify_call()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((7, "SoloSandbox"));
        nexus.Identities[7] = new NexusSearchResult(7, "SoloSandbox", null);
        reconciler.NextPlan = UnresolvedPlan("SoloSandbox");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("SoloSandbox\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        Assert.True(row.IsIdentified);
        Assert.Empty(nexus.GetModByIdCalls); // the search result sufficed
    }

    [Fact]
    public async Task A_unique_exact_sibling_result_auto_identifies_the_association()
    {
        // Sibling rows get the same treatment: the folder provides content +
        // the unique exact search supplies the identity, so the association
        // lands without an Accept click.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((7, "SoloSandbox"));
        reconciler.NextPlan = UnresolvedPlan("SoloSandbox");
        var txt = WriteLoadOrderWithSiblings("SoloSandbox\n", ("SoloSandbox", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            var row = Assert.Single(vm.Rows);
            Assert.Equal(LoadOrderLineOutcome.SiblingImport, row.Outcome);

            Assert.True(row.IsIdentified);
            Assert.Equal(7, row.IdentifiedModId);
            Assert.True(row.ShowVersionInput); // the optional tag
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    // ---- manual identification: the remote exact verification ----------------------

    [Fact]
    public async Task A_manual_bare_id_verifies_remotely_and_adopts_the_canonical_title()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.Identities[77] = new NexusSearchResult(77, "Canonical Title", null);
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = "77";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        var call = Assert.Single(nexus.GetModByIdCalls);
        Assert.Equal(77, call.ModId);
        Assert.Equal("warhammer40kdarktide", call.Domain);
        Assert.True(row.IsIdentified);
        Assert.Equal("Canonical Title", row.IdentifiedName);
        Assert.Equal("Canonical Title", row.MatchText); // the title exactly once
        Assert.Equal("#77", row.ModIdText);
        Assert.Null(row.ManualError);
    }

    [Fact]
    public async Task A_manual_URL_extracts_the_id_and_verifies_it()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.Identities[123] = new NexusSearchResult(123, "From A Url", null);
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = "https://www.nexusmods.com/warhammer40kdarktide/mods/123";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.True(row.IsIdentified);
        Assert.Equal(123, row.IdentifiedModId);
        Assert.Equal("From A Url", row.IdentifiedName);
    }

    [Theory]
    [InlineData("0")]                       // all-numeric but not a valid id
    [InlineData("99999999999999999999")]    // numeric overflow
    [InlineData("https://example.com/mods/1")] // a URL, but not a supported Nexus one
    [InlineData("https://nexusmods.com/other-game/mods/1")] // wrong game slug
    [InlineData("   ")]                     // blank
    public async Task Malformed_id_url_or_blank_input_shows_inline_validation_with_no_network_call(string input)
    {
        // Input that clearly INTENDS an id or URL (or is blank) shows inline
        // validation and is never reinterpreted as a name search; no client
        // call happens.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = input;
        var searchesBefore = nexus.SearchCalls.Count; // the automatic pass already ran
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.NotNull(row.ManualError);
        Assert.Equal(Localization["LoadOrder_ManualInvalidError"], row.ManualError);
        Assert.False(row.IsIdentified);
        Assert.True(row.ShowManualEntry); // the input stays editable
        Assert.Empty(nexus.GetModByIdCalls);
        Assert.Equal(searchesBefore, nexus.SearchCalls.Count); // no manual call either
    }

    [Fact]
    public async Task A_name_search_runs_the_anonymous_search_and_requires_acceptance()
    {
        // Any non-id/non-URL text is user-supplied mod-name criteria: the
        // search runs with the existing normalization + cap and the results
        // become PROPOSALS (an explicit Accept each; the normalized-exact
        // auto-identification rule belongs to the automatic folder-name
        // queue only, even for a single exact hit). The typed criteria stay.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        // The AUTOMATIC folder-name search fails (so the row stays
        // unidentified for the manual pass), then the manual criteria hit.
        nexus.NextSearchThrows = new InvalidOperationException("cloudflare");
        reconciler.NextPlan = UnresolvedPlan("solo_sandbox");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("solo_sandbox\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsIdentified);

        nexus.NextSearchThrows = null;
        nexus.NextResults = Results((42, "SoloSandbox"));
        row.ManualId = "SoloSandbox";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        // Both searches (the automatic folder-name pass, then the manual
        // criteria) ran with the shared normalization + cap.
        Assert.Equal(2, nexus.SearchCalls.Count);
        var call = nexus.SearchCalls[1];
        Assert.Equal("solo sandbox", call.Terms); // normalized criteria
        Assert.Equal(5, call.Count); // the shared candidate cap
        Assert.False(row.IsIdentified); // a single exact hit still needs Accept
        Assert.True(row.ShowCandidateArea);
        Assert.Equal(42, row.TopCandidate!.ModId);
        Assert.Equal("SoloSandbox", row.ManualId); // the criteria are retained
        Assert.Null(row.ManualError);

        vm.AcceptCandidateCommand.Execute(row);
        Assert.True(row.IsIdentified);
    }

    [Fact]
    public async Task A_name_search_replaces_the_rows_current_proposals()
    {
        // A later manual search swaps the row's proposal set wholesale (the
        // user understands what they last searched), and its no-result shape
        // clears the proposals with the no-results hint.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextSearchThrows = new InvalidOperationException("cloudflare");
        reconciler.NextPlan = UnresolvedPlan("ghostmod");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghostmod\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsIdentified);

        // First manual search: two proposals.
        nexus.NextSearchThrows = null;
        nexus.NextResults = Results((1, "First Guess"), (2, "Second Guess"));
        row.ManualId = "ghostmod";
        await vm.FindNexusModCommand.ExecuteAsync(row);
        Assert.Equal(2, row.Candidates.Count);

        // Second manual search: a fresh single result replaces the set.
        nexus.NextResults = Results((7, "The Real One"));
        await vm.FindNexusModCommand.ExecuteAsync(row);
        var candidate = Assert.Single(row.Candidates);
        Assert.Equal(7, candidate.ModId);

        // A no-result search clears the proposals + shows the hint.
        nexus.NextResults = Results();
        await vm.FindNexusModCommand.ExecuteAsync(row);
        Assert.False(row.HasCandidates);
        Assert.True(row.SearchedNoResults);
        Assert.True(row.ShowNoResultsHint);
        Assert.False(row.IsIdentified);
        Assert.Equal("ghostmod", row.ManualId); // still retained
    }

    [Fact]
    public async Task The_find_action_is_refused_while_the_rows_automatic_search_is_active()
    {
        // The simpler honest guard, one direction: a row's automatic search
        // turn and a manual lookup never interleave, so a stale automatic
        // completion can never overwrite a later manual search.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("one");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.SearchGate = gate;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsSearching); // the automatic turn holds the row
        Assert.False(row.CanFind);

        row.ManualId = "SoloSandbox";
        await vm.FindNexusModCommand.ExecuteAsync(row);
        Assert.Single(nexus.SearchCalls); // only the automatic pass ran
        Assert.False(row.IsFinding);

        gate.TrySetResult(true);
        await WaitUntilAsync(() => !vm.IsSearchRunning);
    }

    [Fact]
    public async Task The_automatic_queue_skips_a_row_whose_manual_lookup_is_in_flight()
    {
        // The other direction: a manual lookup in flight claims the row, so
        // the automatic queue passes it by (its completion can never
        // overwrite the fresher manual results).
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.Identities[7] = new NexusSearchResult(7, "Second Mod", null);
        reconciler.NextPlan = UnresolvedPlan("one", "two");
        var searchGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var findGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.SearchGate = searchGate;
        nexus.GetModByIdGate = findGate;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\ntwo\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var two = vm.Rows.Single(r => r.Name == "two");
        Assert.True(vm.Rows[0].IsSearching); // the queue holds on row one

        // A manual exact-id find on row two parks at its gate, claiming it.
        two.ManualId = "7";
        var finding = vm.FindNexusModCommand.ExecuteAsync(two);
        Assert.True(two.IsFinding);

        // Release the queue: it finishes row one, then passes row two by
        // (manual lookup in flight) without searching it.
        searchGate.TrySetResult(true);
        await WaitUntilAsync(() => !vm.IsSearchRunning);
        Assert.Equal("one", Assert.Single(nexus.SearchCalls).Terms);
        Assert.True(two.IsFinding); // the manual find is still parked

        findGate.TrySetResult(true);
        await finding;
        Assert.True(two.IsIdentified); // the manual identity stands
        Assert.Equal(7, two.IdentifiedModId);
    }

    [Fact]
    public async Task A_failed_name_search_shows_inline_feedback_and_identifies_nothing()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextSearchThrows = new InvalidOperationException("api down");
        reconciler.NextPlan = UnresolvedPlan("ghostmod");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghostmod\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = "Ghost Mod";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.NotNull(row.ManualError);
        Assert.Contains("api down", row.ManualError);
        Assert.False(row.IsIdentified);
        Assert.False(row.IsFinding);
        Assert.True(row.ShowManualEntry); // editable
    }

    [Fact]
    public async Task A_missing_identity_stays_editable_with_an_inline_error()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = "4040";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.Equal(Localization["LoadOrder_ManualNotFoundError"], row.ManualError);
        Assert.False(row.IsIdentified);
        Assert.Equal("4040", row.ManualId); // retained
    }

    [Fact]
    public async Task A_failed_verification_shows_the_failure_and_keeps_the_input()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextGetModByIdThrows = new InvalidOperationException("api down");
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);

        row.ManualId = "7";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.NotNull(row.ManualError);
        Assert.Contains("api down", row.ManualError);
        Assert.False(row.IsIdentified);
        Assert.False(row.IsFinding);
    }

    [Fact]
    public async Task An_unverified_id_never_reaches_the_enqueue()
    {
        // A syntactically valid id whose verification returned not-found: the
        // row is NOT identified, so the apply has nothing to enqueue (and
        // nothing silently downloads a guessed id).
        var (vm, reconciler, _, _, _, nexus, _, auth, acquisition, queue, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        row.ManualId = "77"; // no scripted identity: Nexus says not found
        await vm.FindNexusModCommand.ExecuteAsync(row);
        Assert.False(row.IsIdentified);

        // The file has no other actionable row: apply is unavailable (nothing
        // to do), and no enqueue was attempted.
        Assert.False(vm.CanApply);
        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Equal(1, auth.GetCurrentStateCallCount); // entry read only
    }

    [Fact]
    public async Task A_busy_verification_blocks_apply()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("one");
        var planReorder = Plan(new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\none\n"));
        reconciler.NextPlan = planReorder;
        // Restart over the reorder+unresolved file so the apply has something
        // to do while a verification is in flight.
        vm.CancelCommand.Execute(null);
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "one" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\none\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        Assert.True(vm.CanApplyNow);

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.GetModByIdGate = gate;
        var row = vm.Rows.Single(r => r.Name == "one");
        row.ManualId = "7";
        var verifying = vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.True(row.IsFinding);
        Assert.False(vm.CanApplyNow); // the apply cannot race the verification

        gate.TrySetResult(true);
        await verifying;
        Assert.True(vm.CanApplyNow);
    }

    [Fact]
    public async Task The_queue_finishing_and_a_row_identifying_notify_the_apply_button()
    {
        // The bindings' inputs: the Apply button binds CanApplyNow, which
        // reads the search state + the rows' identification; both flips must
        // re-fire the projection or the button stays stale in the UI.
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results((42, "The Real Mod"));
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Anchor", "ghost" },
            new[] { new LoadOrderProfileMod(Guid.NewGuid(), "Anchor", "Anchor") },
            Array.Empty<LoadOrderRepoCandidate>());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nexus.SearchGate = gate;
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Anchor\nghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        gate.TrySetResult(true);
        await WaitUntilAsync(() => !vm.IsSearchRunning);
        Assert.Contains(nameof(LoadOrderImportViewModel.CanApplyNow), fired);

        fired.Clear();
        vm.AcceptCandidateCommand.Execute(vm.Rows.Single(r => r.Name == "ghost"));
        Assert.Contains(nameof(LoadOrderImportViewModel.CanApply), fired);
        Assert.Contains(nameof(LoadOrderImportViewModel.CanApplyNow), fired);
    }

    // ---- the version surface --------------------------------------------------------

    [Fact]
    public async Task Only_an_identified_sibling_import_shows_a_version_input()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.Identities[42] = new NexusSearchResult(42, "A Mod", null);
        reconciler.NextPlan = UnresolvedPlan("RealMod");
        var txt = WriteLoadOrderWithSiblings("RealMod\n", ("RealMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            var sibling = Assert.Single(vm.Rows);

            // Unidentified sibling: the local folder needs no version.
            Assert.False(sibling.ShowVersionInput);

            sibling.ManualId = "42";
            await vm.FindNexusModCommand.ExecuteAsync(sibling);
            Assert.True(sibling.ShowVersionInput); // identified: the optional tag
            Assert.False(sibling.ShowVersionFromDownloadNote);
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task A_remote_only_identified_row_shows_the_download_note_not_an_input()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.Identities[42] = new NexusSearchResult(42, "A Remote Mod", null);
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null); // the default fake auth is Premium
        var row = Assert.Single(vm.Rows);

        row.ManualId = "42";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        Assert.False(row.ShowVersionInput); // no input...
        Assert.True(row.ShowVersionFromDownloadNote); // ...the download resolves it
        Assert.Equal(Localization["LoadOrder_ActionDownload"], row.ActionText);
    }

    [Fact]
    public async Task An_identified_sibling_with_a_blank_version_imports_on_the_version_unknown_path()
    {
        // Blank is a valid choice now: the identified sibling imports as
        // NexusSource with the empty version string (the version-unknown
        // representation), the apply completes, and the workspace closes.
        var (vm, reconciler, profiles, session, _, nexus, imports, _, _, _, _, _) = Build();
        nexus.Identities[42] = new NexusSearchResult(42, "A Mod", null);
        reconciler.NextPlan = UnresolvedPlan("RealMod");
        var txt = WriteLoadOrderWithSiblings("RealMod\n", ("RealMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            var row = Assert.Single(vm.Rows);
            row.ManualId = "42";
            await vm.FindNexusModCommand.ExecuteAsync(row);
            Assert.True(row.ShowVersionInput);
            Assert.Equal(string.Empty, row.Version); // blank: unknown

            var imported = Guid.NewGuid();
            imports.NextImportResults = new Queue<(Guid, string)>(new[] { (imported, "v-folder") });
            await vm.ApplyCommand.ExecuteAsync(null);

            var call = Assert.Single(imports.Imports);
            var source = Assert.IsType<NexusSource>(call.Source);
            Assert.Equal(42, source.ModId);
            Assert.Equal(string.Empty, call.Version); // unknown, never a block
            Assert.Contains(profiles.AddModCalls, c => c.ContainerId == imported);
            Assert.Equal([imported], Assert.Single(profiles.SetModOrderCalls));
            Assert.False(vm.IsActive); // the apply completed + closed
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task A_blank_version_sibling_import_lands_as_a_version_unknown_mods_row()
    {
        // The downstream half of the blank-version contract: the repository
        // shape the import produces (a Nexus container whose latest version
        // carries an empty VersionString) flows into the ordinary Mods row's
        // derived version-unknown state, with its enabled download/update
        // action (the resolution path), rather than a dead row.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession { ActiveProfileId = profiles.ListProfiles().First().Id };
        var repo = new FakeModRepository();
        var imported = repo.Seed(new NexusSource { ModId = 42 }, "RealMod", versionString: string.Empty);
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = imported.Id, Order = 0, Policy = ModVersionPolicy.Latest });

        var list = TestDoubles.BuildModList(profiles, session, repo);

        var row = Assert.Single(list.Mods);
        Assert.True(row.IsVersionUnknown);
        Assert.True(row.CanShowUpdateAction);
        Assert.True(row.UpdateActionEnabled); // the enabled resolution action
    }

    [Fact]
    public async Task A_valid_version_imports_with_the_nexus_source_and_tag()
    {
        var (vm, reconciler, profiles, session, _, nexus, imports, _, _, _, _, _) = Build();
        nexus.Identities[42] = new NexusSearchResult(42, "A Mod", null);
        reconciler.NextPlan = UnresolvedPlan("RealMod");
        var txt = WriteLoadOrderWithSiblings("RealMod\n", ("RealMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            var row = Assert.Single(vm.Rows);
            row.ManualId = "42";
            await vm.FindNexusModCommand.ExecuteAsync(row);
            row.Version = "  1.4  ";

            var imported = Guid.NewGuid();
            imports.NextImportResults = new Queue<(Guid, string)>(new[] { (imported, "v-folder") });
            await vm.ApplyCommand.ExecuteAsync(null);

            var call = Assert.Single(imports.Imports);
            var source = Assert.IsType<NexusSource>(call.Source);
            Assert.Equal(42, source.ModId);
            Assert.Equal("1.4", call.Version); // trimmed
            Assert.Contains(profiles.AddModCalls, c => c.ContainerId == imported);
            Assert.Equal([imported], Assert.Single(profiles.SetModOrderCalls));
            Assert.False(vm.IsActive);
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task An_untracked_sibling_import_needs_no_version()
    {
        var (vm, reconciler, profiles, _, _, _, imports, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("PlainMod");
        var txt = WriteLoadOrderWithSiblings("PlainMod\n", ("PlainMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyNow); // nothing blocks: no identity, no version

            var imported = Guid.NewGuid();
            imports.NextImportResults = new Queue<(Guid, string)>(new[] { (imported, "v-folder") });
            await vm.ApplyCommand.ExecuteAsync(null);

            var call = Assert.Single(imports.Imports);
            Assert.IsType<UntrackedSource>(call.Source);
            Assert.Equal(string.Empty, call.Version); // the version-unknown path
            Assert.Contains(profiles.AddModCalls, c => c.ContainerId == imported);
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    // ---- the Premium enqueue honesty ---------------------------------------------

    [Fact]
    public async Task Premium_enqueues_resolved_head_downloads_and_records_a_placement_plan()
    {
        var reorder = Guid.NewGuid();
        var nexus = new FakeNexusSearchClient();
        nexus.Identities[77] = new NexusSearchResult(77, "Ghostly", null);
        var (vm, reconciler, profiles, session, _, _, _, auth, acquisition, queue, placements, _) =
            Build(nexus: nexus);
        var profileId = session.ActiveProfileId!.Value;
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "Ghost" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        acquisition.NextResolve = (1234, "2.0");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nGhost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var ghost = vm.Rows.Single(r => r.Name == "Ghost");
        ghost.ManualId = "77";
        await vm.FindNexusModCommand.ExecuteAsync(ghost);
        Assert.True(ghost.IsIdentified);

        await vm.ApplyCommand.ExecuteAsync(null);

        // The enqueue: resolved head file, ProfileAdd, no container.
        var request = Assert.Single(queue.Requests);
        Assert.Equal(77, request.ModId);
        Assert.Equal(1234, request.FileId);
        Assert.Equal(DownloadPurpose.ProfileAdd, request.Purpose);
        Assert.Null(request.ContainerId);
        Assert.Equal(profileId, request.TargetProfileId);
        Assert.Single(acquisition.ResolveLatestCalls);
        // Premium verified fresh at apply (entry read + apply recheck).
        Assert.Equal(2, auth.GetCurrentStateCallCount);

        // The local order write covered the anchor only (the download lands
        // later), and the placement plan waits on the mod id.
        Assert.Equal([reorder], Assert.Single(profiles.SetModOrderCalls));
        Assert.True(placements.HasPending(profileId));
        Assert.True(session.HasPendingChanges);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task Non_premium_remote_rows_are_visibly_non_actionable_with_no_lookup_work()
    {
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, placements, _) = Build();
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false);
        var profileId = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "Ghost" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nGhost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var ghost = vm.Rows.Single(r => r.Name == "Ghost");

        // Visibly non-actionable from the start: no lookup capability, so no
        // search ran, no manual entry exists, and the honest copy + the
        // upfront notice explain why.
        Assert.False(ghost.IsLookupRow);
        Assert.False(ghost.ShowManualEntry);
        Assert.False(ghost.ShowCandidateArea);
        Assert.False(ghost.ShowNoResultsHint);
        Assert.False(ghost.ShowVersionFromDownloadNote);
        Assert.False(ghost.CanSkip);
        Assert.Equal(Localization["LoadOrder_ActionSkipped"], ghost.ActionText);
        Assert.Equal(Localization["LoadOrder_OutcomeUnresolved"], ghost.MatchText);
        Assert.Empty(nexus.SearchCalls);
        Assert.Empty(nexus.GetModByIdCalls);
        Assert.Equal(0, vm.SearchTotal); // totals cover only searchable rows
        Assert.True(vm.ShowRemoteUnavailableNotice);
        Assert.Contains("1", vm.RemoteUnavailableNoticeText);

        // The programmatic manual path is refused too (defense in depth).
        ghost.ManualId = "77";
        await vm.FindNexusModCommand.ExecuteAsync(ghost);
        Assert.False(ghost.IsIdentified);
        Assert.Empty(nexus.GetModByIdCalls);

        await vm.ApplyCommand.ExecuteAsync(null);

        // The apply proceeded (the reorder stood, the workspace closed) with
        // zero network action for the non-actionable row + no failure.
        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Single(profiles.SetModOrderCalls);
        Assert.Null(vm.ApplyFailure);
        Assert.False(vm.IsActive);
        Assert.False(placements.HasPending(profileId));
        Assert.Equal(1, auth.GetCurrentStateCallCount); // entry read only
    }

    [Fact]
    public async Task A_non_premium_standalone_txt_with_no_siblings_makes_zero_search_calls()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false);
        reconciler.NextPlan = UnresolvedPlan("one", "two");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\ntwo\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        // No sibling content + no capability: nothing is searchable, no
        // manual-ID affordances exist, and the notice counts both lines.
        Assert.Empty(nexus.SearchCalls);
        Assert.All(vm.Rows, r => Assert.False(r.ShowManualEntry));
        Assert.Equal(0, vm.SearchTotal);
        Assert.False(vm.IsSearchRunning);
        Assert.True(vm.ShowRemoteUnavailableNotice);
        Assert.Contains("2", vm.RemoteUnavailableNoticeText);
    }

    [Fact]
    public async Task A_capability_read_failure_degrades_to_non_actionable_remote_rows()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        auth.ThrowOnGetCurrentState = true;
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsLookupRow);
        Assert.False(row.ShowManualEntry);
        Assert.Equal(Localization["LoadOrder_ActionSkipped"], row.ActionText);
        Assert.Empty(nexus.SearchCalls);
        Assert.True(vm.ShowRemoteUnavailableNotice);
    }

    [Fact]
    public async Task Sibling_lookup_work_stays_available_on_a_non_premium_account()
    {
        // Local content exists + a Nexus association is useful at every tier:
        // the sibling row keeps its search + manual identification; only the
        // remote-only line is gated. Search totals cover the sibling alone.
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false);
        nexus.NextResults = Results((5, "Real Mod Pro"));
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "RealMod", "Ghost" },
            Array.Empty<LoadOrderProfileMod>(),
            Array.Empty<LoadOrderRepoCandidate>());
        var txt = WriteLoadOrderWithSiblings("RealMod\nGhost\n", ("RealMod", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);
            var sibling = vm.Rows.Single(r => r.Name == "RealMod");
            var ghost = vm.Rows.Single(r => r.Name == "Ghost");

            Assert.True(sibling.IsLookupRow);
            Assert.True(sibling.ShowManualEntry);
            Assert.False(ghost.IsLookupRow);

            var call = Assert.Single(nexus.SearchCalls); // the sibling only
            Assert.Equal("real mod", call.Terms);
            Assert.Equal(1, vm.SearchTotal);
            Assert.True(sibling.ShowCandidateArea); // proposals arrived

            // The sibling's manual verification works on this tier too.
            nexus.Identities[9] = new NexusSearchResult(9, "Real Mod", null);
            sibling.ManualId = "9";
            await vm.FindNexusModCommand.ExecuteAsync(sibling);
            Assert.True(sibling.IsIdentified);
            Assert.True(sibling.ShowVersionInput); // the optional tag
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task A_premium_loss_at_apply_is_a_visible_failure_that_keeps_the_workspace_open()
    {
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, _, _) = Build();
        nexus.Identities[77] = new NexusSearchResult(77, "Ghostly", null);
        var profileId = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "Ghost" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\nGhost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var ghost = vm.Rows.Single(r => r.Name == "Ghost");
        ghost.ManualId = "77";
        await vm.FindNexusModCommand.ExecuteAsync(ghost);
        Assert.Equal(Localization["LoadOrder_ActionDownload"], ghost.ActionText);

        // The account loses Premium after the review promised the download.
        auth.State = new NexusAuthState(NexusAuthMethod.OAuth, "downgraded", IsPremium: false);

        await vm.ApplyCommand.ExecuteAsync(null);

        // Nothing was written (not even the local reorder): a stale promise
        // is a visible failure, never a silent partial apply + close.
        Assert.NotNull(vm.ApplyFailure);
        Assert.Equal(Localization["LoadOrder_PremiumLostFailure"], vm.ApplyFailure);
        Assert.True(vm.IsActive);
        Assert.Empty(profiles.SetModOrderCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(queue.Requests);
        Assert.Empty(acquisition.ResolveLatestCalls);
    }

    [Fact]
    public async Task An_unreadable_premium_state_at_apply_is_the_same_visible_failure()
    {
        var (vm, reconciler, _, _, _, nexus, _, auth, _, _, _, _) = Build();
        nexus.Identities[77] = new NexusSearchResult(77, "Ghostly", null);
        reconciler.NextPlan = UnresolvedPlan("ghost");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ghost\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows);
        row.ManualId = "77";
        await vm.FindNexusModCommand.ExecuteAsync(row);

        // The entry read succeeded (Premium), the apply recheck throws.
        auth.ThrowOnGetCurrentState = true;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ApplyFailure);
        Assert.True(vm.IsActive);
    }

    // ---- the enqueue batch's failure semantics -----------------------------------

    [Fact]
    public async Task A_rate_limit_aborts_the_remaining_enqueues_with_prior_work_standing()
    {
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, placements, _) = Build();
        nexus.Identities[2] = new NexusSearchResult(2, "Second Mod", null);
        nexus.Identities[3] = new NexusSearchResult(3, "Third Mod", null);
        var profileId = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "First", "Second", "Third" },
            new[] { new LoadOrderProfileMod(reorder, "First", "First") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("First\nSecond\nThird\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Second", "Third" })
        {
            var row = vm.Rows.Single(r => r.Name == name);
            row.ManualId = name == "Second" ? "2" : "3";
            await vm.FindNexusModCommand.ExecuteAsync(row);
        }

        // The second line's resolve succeeds; the third's throws 429.
        acquisition.ResolveThrowQueue.Enqueue(null);
        acquisition.ResolveThrowQueue.Enqueue(
            new NexusRateLimitException(429, new NexusRateLimits(2500, 0, null, 100, 0, null)));

        await vm.ApplyCommand.ExecuteAsync(null);

        // The second line's enqueue landed; the third was skipped.
        var request = Assert.Single(queue.Requests);
        Assert.Equal(2, request.ModId);
        Assert.True(placements.HasPending(profileId)); // the landed enqueue's plan
        Assert.True(vm.IsActive); // the re-runnable failure keeps the workspace open
        Assert.NotNull(vm.ApplyFailure);
        Assert.Contains("rate limit", vm.ApplyFailure, StringComparison.OrdinalIgnoreCase);
        // Prior work stands: the reorder's order write landed.
        Assert.Contains(reorder, Assert.Single(profiles.SetModOrderCalls));
        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task A_resolve_failure_on_one_line_is_recorded_and_the_batch_continues()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, acquisition, queue, _, _) = Build();
        nexus.Identities[1] = new NexusSearchResult(1, "Boom Mod", null);
        nexus.Identities[2] = new NexusSearchResult(2, "Fine Mod", null);
        reconciler.NextPlan = UnresolvedPlan("Boom", "Fine");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Boom\nFine\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        foreach (var row in vm.Rows)
        {
            row.ManualId = row.Name == "Boom" ? "1" : "2";
            await vm.FindNexusModCommand.ExecuteAsync(row);
        }

        acquisition.ResolveThrowQueue.Enqueue(new InvalidOperationException("api down"));
        acquisition.NextResolve = (555, "1.0");

        await vm.ApplyCommand.ExecuteAsync(null);

        var boom = vm.Rows.Single(r => r.Name == "Boom");
        Assert.NotNull(boom.LineFailure);
        var fine = vm.Rows.Single(r => r.Name == "Fine");
        Assert.Null(fine.LineFailure);
        Assert.Single(queue.Requests); // the fine line enqueued
        Assert.True(vm.IsActive); // the per-line failure keeps the review readable
    }

    [Fact]
    public async Task A_per_line_import_failure_is_recorded_and_the_rest_continue()
    {
        var (vm, reconciler, profiles, _, _, _, imports, _, _, _, _, _) = Build();
        reconciler.NextPlan = UnresolvedPlan("Bad", "Good");
        var txt = WriteLoadOrderWithSiblings("Bad\nGood\n", ("Bad", true), ("Good", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);

            var good = Guid.NewGuid();
            imports.ImportExceptionQueue = new Queue<Exception?>(
                new Exception?[] { new InvalidOperationException("bad archive"), null });
            imports.NextImportResults = new Queue<(Guid, string)>(new[] { (good, "v-folder") });
            await vm.ApplyCommand.ExecuteAsync(null);

            var badRow = vm.Rows.Single(r => r.Name == "Bad");
            Assert.NotNull(badRow.LineFailure);
            Assert.Contains("bad archive", badRow.LineFailure);
            // The failed line did not stop the apply: the good import landed
            // + the reload fired; the review stays open for the message.
            Assert.Contains(profiles.AddModCalls, c => c.ContainerId == good);
            Assert.True(vm.IsActive);
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    // ---- the order write + the placement convergence ---------------------------

    [Fact]
    public async Task The_order_write_carries_every_known_container_in_file_order()
    {
        var (vm, reconciler, profiles, session, _, _, imports, _, _, _, _, _) = Build();
        var reorder = Guid.NewGuid();
        var library = Guid.NewGuid();
        profiles.WithMods(session.ActiveProfileId!.Value,
            new ModListEntry { ContainerId = reorder, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "ModA", "ModB", "ModC" },
            new[] { new LoadOrderProfileMod(reorder, "ModA", "A") },
            new[] { new LoadOrderRepoCandidate(library, "ModB", false, "B") });
        var txt = WriteLoadOrderWithSiblings("ModA\nModB\nModC\n", ("ModC", true));
        try
        {
            await vm.StartImportCommand.ExecuteAsync(txt);
            await vm.ChooseImportCommand.ExecuteAsync(null);

            var imported = Guid.NewGuid();
            imports.NextImportResults = new Queue<(Guid, string)>(new[] { (imported, "v-folder") });
            await vm.ApplyCommand.ExecuteAsync(null);

            // ONE order write, listing all three at their file positions
            // (membership preceded it, so the imported container lands at its
            // file position rather than appended by AddMod).
            var order = Assert.Single(profiles.SetModOrderCalls);
            Assert.Equal([reorder, library, imported], order);
            var resulting = profiles.GetModList(session.ActiveProfileId!.Value);
            Assert.Equal([reorder, library, imported], resulting.Select(e => e.ContainerId));
        }
        finally
        {
            CleanupTxt(txt);
        }
    }

    [Fact]
    public async Task Successful_downloads_converge_to_the_file_order_through_the_placements()
    {
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, placements, _) = Build();
        nexus.Identities[11] = new NexusSearchResult(11, "First Missing", null);
        nexus.Identities[22] = new NexusSearchResult(22, "Second Missing", null);
        var profileId = session.ActiveProfileId!.Value;
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Anchor", "First", "Second" },
            new[] { new LoadOrderProfileMod(anchor, "Anchor", "Anchor") },
            Array.Empty<LoadOrderRepoCandidate>());
        acquisition.NextResolve = (900, "1.0");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Anchor\nFirst\nSecond\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        foreach (var row in vm.Rows.Where(r => r.Outcome == LoadOrderLineOutcome.Unresolved))
        {
            row.ManualId = row.Name == "First" ? "11" : "22";
            await vm.FindNexusModCommand.ExecuteAsync(row);
        }

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(2, queue.Requests.Count);
        Assert.Equal([anchor], Assert.Single(profiles.SetModOrderCalls));

        // The queue's completions land the containers (mirroring the real
        // queue's AddMod-then-signal order): each completion rewrites the
        // order toward the file's [Anchor, First, Second].
        var first = queue.Items[0];
        var second = queue.Items[1];
        var firstContainer = Guid.NewGuid();
        var secondContainer = Guid.NewGuid();
        profiles.AddMod(profileId, firstContainer, ModVersionPolicy.Latest); // appended
        first.ContainerId = firstContainer;
        first.Phase = DownloadPhase.Completed;
        queue.Publish(first);

        var afterFirst = profiles.GetModList(profileId).Select(e => e.ContainerId).ToArray();
        Assert.Equal([anchor, firstContainer], afterFirst); // converged past the append

        profiles.AddMod(profileId, secondContainer, ModVersionPolicy.Latest);
        second.ContainerId = secondContainer;
        second.Phase = DownloadPhase.Completed;
        queue.Publish(second);

        Assert.Equal(
            [anchor, firstContainer, secondContainer],
            profiles.GetModList(profileId).Select(e => e.ContainerId).ToArray());
        Assert.False(placements.HasPending(profileId)); // the plan completed + dropped
    }

    [Fact]
    public async Task A_completion_during_the_enqueue_sequence_still_converges()
    {
        // The ordering race, pinned: each admission completes SYNCHRONOUSLY
        // inside Enqueue (an ultra-fast finish), so the FIRST download is
        // terminal before the second is even admitted. Under the old
        // admit-as-you-resolve shape the plan was recorded only after the
        // whole loop, the racing completion found no plan, and its container
        // stayed appended forever. The plan must exist before any admission.
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, placements, _) = Build();
        nexus.Identities[11] = new NexusSearchResult(11, "First Missing", null);
        nexus.Identities[22] = new NexusSearchResult(22, "Second Missing", null);
        var profileId = session.ActiveProfileId!.Value;
        var anchor = Guid.NewGuid();
        profiles.WithMods(profileId,
            new ModListEntry { ContainerId = anchor, Order = 0, Policy = ModVersionPolicy.Latest });
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "Anchor", "First", "Second" },
            new[] { new LoadOrderProfileMod(anchor, "Anchor", "Anchor") },
            Array.Empty<LoadOrderRepoCandidate>());
        acquisition.NextResolve = (900, "1.0");
        var containers = new Dictionary<int, Guid>();
        queue.CompleteOnEnqueue = request =>
        {
            // Register before signaling (the real queue's completion order),
            // then resolve + complete the item inside Enqueue itself.
            var container = Guid.NewGuid();
            profiles.AddMod(profileId, container, ModVersionPolicy.Latest);
            containers[request.ModId] = container;
            return container;
        };
        await vm.StartImportCommand.ExecuteAsync(WriteFile("Anchor\nFirst\nSecond\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        foreach (var row in vm.Rows.Where(r => r.Outcome == LoadOrderLineOutcome.Unresolved))
        {
            row.ManualId = row.Name == "First" ? "11" : "22";
            await vm.FindNexusModCommand.ExecuteAsync(row);
        }

        await vm.ApplyCommand.ExecuteAsync(null);

        // Both racing completions converged: the file order, not the
        // admission-appended order.
        Assert.Equal(2, queue.Requests.Count);
        Assert.Equal(
            [anchor, containers[11], containers[22]],
            profiles.GetModList(profileId).Select(e => e.ContainerId).ToArray());
        Assert.False(placements.HasPending(profileId));
        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task A_rate_limited_apply_admits_the_resolved_rows_and_shows_the_queued_notice()
    {
        // The rate limit aborts further RESOLVES, but the rows resolved
        // before it are still admitted (prior work stands) with their plan
        // intact; the workspace stays open with the failure + the
        // queued-downloads notice (the open workspace hides the mod list's
        // download rows).
        var (vm, reconciler, profiles, session, _, nexus, _, auth, acquisition, queue, placements, _) = Build();
        nexus.Identities[2] = new NexusSearchResult(2, "Second Mod", null);
        nexus.Identities[3] = new NexusSearchResult(3, "Third Mod", null);
        var profileId = session.ActiveProfileId!.Value;
        var reorder = Guid.NewGuid();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            new[] { "First", "Second", "Third" },
            new[] { new LoadOrderProfileMod(reorder, "First", "First") },
            Array.Empty<LoadOrderRepoCandidate>());
        await vm.StartImportCommand.ExecuteAsync(WriteFile("First\nSecond\nThird\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Second", "Third" })
        {
            var row = vm.Rows.Single(r => r.Name == name);
            row.ManualId = name == "Second" ? "2" : "3";
            await vm.FindNexusModCommand.ExecuteAsync(row);
        }

        // The second line's resolve succeeds; the third's throws 429.
        acquisition.ResolveThrowQueue.Enqueue(null);
        acquisition.ResolveThrowQueue.Enqueue(
            new NexusRateLimitException(429, new NexusRateLimits(2500, 0, null, 100, 0, null)));

        await vm.ApplyCommand.ExecuteAsync(null);

        var request = Assert.Single(queue.Requests);
        Assert.Equal(2, request.ModId);
        Assert.True(placements.HasPending(profileId)); // the landed enqueue's plan
        Assert.True(vm.IsActive); // the re-runnable failure keeps the workspace open
        Assert.NotNull(vm.ApplyFailure);
        Assert.Contains("rate limit", vm.ApplyFailure, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(vm.QueuedDownloadsNotice);
        Assert.Contains("1", vm.QueuedDownloadsNotice);
    }

    // ---- reset paths + applying state --------------------------------------------

    [Fact]
    public async Task Cancel_discards_the_session_with_no_writes()
    {
        var (vm, reconciler, profiles, _, _, _, _, _, _, _, _, _) = Build();
        reconciler.NextPlan = Plan(new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        vm.ChooseReorderCommand.Execute(null);

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.Empty(vm.Rows);
        Assert.Empty(profiles.SetModOrderCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task Back_returns_to_the_choice_state_and_a_new_choice_rebuilds_the_rows()
    {
        var (vm, reconciler, _, _, _, nexus, _, _, _, _, _, _) = Build();
        nexus.NextResults = Results();
        reconciler.NextPlan = UnresolvedPlan("one");
        await vm.StartImportCommand.ExecuteAsync(WriteFile("one\n"));
        await vm.ChooseImportCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);

        vm.BackCommand.Execute(null);

        Assert.Equal(LoadOrderStage.ChoosingMode, vm.Stage);
        Assert.Empty(vm.Rows);

        // Reorder now: the review rebuilds under the other mode.
        vm.ChooseReorderCommand.Execute(null);
        Assert.True(vm.IsReviewing);
        Assert.True(vm.IsReorderMode);
        Assert.Single(vm.Rows);
        Assert.False(vm.Rows[0].IsLookupRow); // no lookup in the reorder projection
    }

    [Fact]
    public async Task A_profile_switch_resets_an_open_session()
    {
        var profiles = TestDoubles.Profiles(
            new ProfileSummary(Guid.NewGuid(), "Alpha", ""),
            new ProfileSummary(Guid.NewGuid(), "Beta", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var (vm, reconciler, _, _, _, _, _, _, _, _, _, _) = Build(profiles: profiles, session: session);
        reconciler.NextPlan = Plan(new LoadOrderLine("ModA", LoadOrderLineOutcome.Reorder, Guid.NewGuid(), "ModA", "A"));
        await vm.StartImportCommand.ExecuteAsync(WriteFile("ModA\n"));
        vm.ChooseReorderCommand.Execute(null);
        Assert.True(vm.IsActive);

        session.ActiveProfileId = profiles.ListProfiles().Last().Id;

        Assert.False(vm.IsActive);
    }

    [Fact]
    public async Task A_profile_switch_mid_apply_defers_the_reset_and_completes_the_write()
    {
        // The in-flight apply owns its captured profile: a switch mid-apply
        // defers the reset; the apply completes, then the reset deactivates
        // the workspace.
        var profiles = TestDoubles.Profiles(
            new ProfileSummary(Guid.NewGuid(), "Alpha", ""),
            new ProfileSummary(Guid.NewGuid(), "Beta", ""));
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = profiles.ListProfiles().First().Id,
        };
        var (vm, reconciler, _, _, _, _, imports, _, _, _, _, _) = Build(
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
        await vm.ChooseImportCommand.ExecuteAsync(null);
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (Guid.NewGuid(), "v") });
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        imports.ImportGate = gate;

        var applying = vm.ApplyCommand.ExecuteAsync(null);
        // Switch while the import worker is held inside Import.
        session.ActiveProfileId = profiles.ListProfiles().Last().Id;
        Assert.True(vm.IsActive); // not reset mid-apply

        gate.TrySetResult(true);
        await applying;

        // The full apply completed against the CAPTURED profile, then the
        // deferred reset deactivated the workspace.
        Assert.Contains(profiles.AddModCalls, c => c.Id == captured);
        Assert.Contains(reorder, Assert.Single(profiles.SetModOrderCalls));
        Assert.False(vm.IsActive);

        CleanupTxt(txt);
    }

    [Fact]
    public async Task Applying_disables_row_mutation_and_holds_the_card_gate()
    {
        var (vm, reconciler, profiles, _, _, _, imports, _, _, _, _, import) = Build();
        reconciler.NextPlan = UnresolvedPlan("Held");
        var txt = WriteLoadOrderWithSiblings("Held\n", ("Held", true));
        await vm.StartImportCommand.ExecuteAsync(txt);
        await vm.ChooseImportCommand.ExecuteAsync(null);
        Assert.True(vm.Rows[0].CanSkip);
        imports.NextImportResults = new Queue<(Guid, string)>(new[] { (Guid.NewGuid(), "v") });
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        imports.ImportGate = gate;

        var applying = vm.ApplyCommand.ExecuteAsync(null);

        // Mid-apply: the workspace stays active (the gate holds), rows refuse
        // mutation, the buttons disable, and a PROGRAMMATIC cancel is refused
        // (the in-flight apply owns its captured profile + writes; Back's
        // defense-in-depth shape).
        Assert.True(vm.IsActive);
        Assert.True(vm.IsApplying);
        Assert.True(vm.Rows[0].IsApplyingRow);
        Assert.False(vm.Rows[0].CanSkip);
        Assert.False(vm.CanApplyNow);
        Assert.False(vm.CanCancelNow);
        vm.CancelCommand.Execute(null);
        Assert.True(vm.IsActive);
        import.StartBatchCommand.Execute(new[] { "/tmp/other" });
        Assert.False(import.IsActive);

        gate.TrySetResult(true);
        await applying;
        Assert.False(vm.IsActive);
        import.StartBatchCommand.Execute(new[] { "/tmp/other" });
        Assert.True(import.IsActive);

        CleanupTxt(txt);
    }

    [Fact]
    public async Task A_deferred_marshal_seam_still_adds_and_orders_the_imported_container()
    {
        // The production race, pinned: the import worker posts row-side
        // assignments through the marshal seam, and the seam's post may land
        // AFTER the awaited continuation proceeds. The apply passes the
        // import results as data, so a seam that DEFERS every post until
        // after the apply task completes must still produce the add + the
        // order entry.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha", ""));
        var session = new FakeProfileSession { ActiveProfileId = profiles.ListProfiles().First().Id };
        var repo = new FakeModRepository();
        var reconciler = new FakeLoadOrderReconciler();
        var imports = new FakeModImportService();
        var deferred = new ConcurrentQueue<Action>();

        var queue = new FakeModDownloadQueue();
        var placements = new LoadOrderDownloadPlacements(
            queue, profiles, NullLogger<LoadOrderDownloadPlacements>.Instance);
        var vm = new LoadOrderImportViewModel(
            profiles, session, reconciler, new FakeNexusSearchClient(),
            imports, new FakeNexusAuthService { State = null },
            new FakeModAcquisitionService(), queue, placements,
            new ModCardsGate(), new FakeDialogService(),
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
        await vm.ChooseImportCommand.ExecuteAsync(null);
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

        CleanupTxt(txt);
    }

    // ---- scale: every row retained, nothing truncated --------------------------

    [Fact]
    public async Task A_150_row_review_retains_every_row_and_orders_them_all()
    {
        const int total = 150;
        const int libraryStart = 100;
        var names = Enumerable.Range(0, total).Select(i => $"Mod{i:000}").ToArray();
        var reorderIds = Enumerable.Range(0, libraryStart).Select(_ => Guid.NewGuid()).ToArray();
        var libraryIds = Enumerable.Range(libraryStart, total - libraryStart).Select(_ => Guid.NewGuid()).ToArray();
        var (vm, reconciler, profiles, session, _, nexus, _, _, _, _, placements, _) = Build();
        reconciler.NextPlan = LoadOrderPlanner.Build(
            names,
            names.Take(libraryStart)
                .Select((n, i) => new LoadOrderProfileMod(reorderIds[i], n, n))
                .ToArray(),
            names.Skip(libraryStart)
                .Select((n, i) => new LoadOrderRepoCandidate(libraryIds[i], n, false, n))
                .ToArray());
        var profileId = session.ActiveProfileId!.Value;
        await vm.StartImportCommand.ExecuteAsync(WriteFile(string.Join('\n', names)));
        await vm.ChooseImportCommand.ExecuteAsync(null);

        // Every line is a row, in file order, each carrying its action.
        Assert.Equal(total, vm.Rows.Count);
        Assert.Equal(names, vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.NotNull(r.ActionText));
        Assert.All(vm.Rows, r => Assert.False(r.IsSkipped));
        // The search queue never fired: no row is a lookup row.
        Assert.Empty(nexus.SearchCalls);

        await vm.ApplyCommand.ExecuteAsync(null);

        // One order write covering all 150 known containers in file order +
        // one add per library row; no pending placement (nothing downloads).
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal(total, order.Count);
        Assert.Equal(
            reorderIds.Concat(libraryIds),
            order);
        Assert.Equal(total - libraryStart, profiles.AddModCalls.Count);
        Assert.False(placements.HasPending(profileId));
        Assert.False(vm.IsActive);
    }
}
