using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="LoadOrderPlanner"/> (pure) + <see cref="ILoadOrderReconciler"/>
/// (the real resolution glue over the temp-dir fixture): every matching
/// outcome (profile reorder, library add, unresolved, the ambiguity
/// preference), the derived plan projections, and the base-name resolution
/// both sides key on (policy-resolved for profile mods, latest/linked for
/// repo candidates).
/// </summary>
public sealed class LoadOrderPlannerTests
{
    private static LoadOrderProfileMod ProfileMod(string baseName, Guid? id = null, string? display = null) =>
        new(id ?? Guid.NewGuid(), baseName, display ?? baseName);

    private static LoadOrderRepoCandidate Repo(
        string baseName, bool nexus = false, Guid? id = null, string? display = null) =>
        new(id ?? Guid.NewGuid(), baseName, nexus, display ?? baseName);

    // ---- pure planner -------------------------------------------------------

    [Fact]
    public void An_empty_name_list_yields_an_empty_plan()
    {
        var plan = LoadOrderPlanner.Build(
            Array.Empty<string>(), Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>());

        Assert.Empty(plan.Lines);
        Assert.Empty(plan.OrderedContainerIds);
        Assert.Empty(plan.LibraryAdds);
        Assert.Empty(plan.UnmatchedNames);
        Assert.Same(LoadOrderPlan.Empty, LoadOrderPlanner.Build(
            Array.Empty<string>(), Array.Empty<LoadOrderProfileMod>(), Array.Empty<LoadOrderRepoCandidate>()));
    }

    [Fact]
    public void Profile_matches_reorder_in_file_order()
    {
        var a = ProfileMod("ModA");
        var b = ProfileMod("ModB");

        var plan = LoadOrderPlanner.Build(["modb", "MODA"], [a, b], Array.Empty<LoadOrderRepoCandidate>());

        // Case-insensitive ordinal on the base name; file order drives the
        // ordered-ids projection.
        Assert.Equal([b.ContainerId, a.ContainerId], plan.OrderedContainerIds);
        Assert.All(plan.Lines, l => Assert.Equal(LoadOrderLineOutcome.Reorder, l.Outcome));
        Assert.All(plan.Lines, l => Assert.NotNull(l.ContainerId));
        Assert.Empty(plan.LibraryAdds);
        Assert.Empty(plan.UnmatchedNames);
    }

    [Fact]
    public void A_name_matching_both_a_profile_mod_and_a_repo_candidate_prefers_the_profile()
    {
        var profile = ProfileMod("ModA");
        var repo = Repo("ModA");

        var plan = LoadOrderPlanner.Build(["ModA"], [profile], [repo]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.Reorder, line.Outcome);
        Assert.Equal(profile.ContainerId, line.ContainerId);
        Assert.Empty(plan.LibraryAdds);
    }

    [Fact]
    public void Repo_only_matches_are_library_adds()
    {
        var candidate = Repo("ModC", display: "Some Display Name");

        var plan = LoadOrderPlanner.Build(["modc"], Array.Empty<LoadOrderProfileMod>(), [candidate]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.LibraryAdd, line.Outcome);
        Assert.Equal(candidate.ContainerId, line.ContainerId);
        Assert.Equal("Some Display Name", line.DisplayName);
        // Library adds participate in the ordered ids (SetModOrder ignores
        // ids that are not profile members yet; the apply sequencing owns
        // their final placement).
        Assert.Equal([candidate.ContainerId], plan.OrderedContainerIds);
        Assert.Equal(plan.Lines, plan.LibraryAdds);
    }

    [Fact]
    public void Unknown_names_are_reported_unmatched_never_dropped()
    {
        var plan = LoadOrderPlanner.Build(
            ["ModA", "Ghost"], [ProfileMod("ModA")], Array.Empty<LoadOrderRepoCandidate>());

        var unmatched = Assert.Single(plan.UnmatchedNames);
        Assert.Equal("Ghost", unmatched.Name);
        Assert.Null(unmatched.ContainerId);
        Assert.Null(unmatched.DisplayName);
        Assert.Equal(2, plan.Lines.Count);
    }

    [Fact]
    public void Ambiguity_prefers_the_nexus_sourced_candidate()
    {
        var nexus = Repo("ModA", nexus: true);
        var untracked = Repo("ModA");

        var plan = LoadOrderPlanner.Build(["ModA"], Array.Empty<LoadOrderProfileMod>(), [untracked, nexus]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.LibraryAdd, line.Outcome);
        Assert.Equal(nexus.ContainerId, line.ContainerId);
    }

    [Fact]
    public void Remaining_ambiguity_is_unmatched_no_silent_pick()
    {
        // Two untracked candidates with the same base name...
        var plan = LoadOrderPlanner.Build(
            ["ModA"], Array.Empty<LoadOrderProfileMod>(), [Repo("ModA"), Repo("ModA")]);
        Assert.Equal(LoadOrderLineOutcome.Unresolved, Assert.Single(plan.Lines).Outcome);
        Assert.Empty(plan.OrderedContainerIds);

        // ...and two Nexus candidates: the preference cannot break either tie.
        plan = LoadOrderPlanner.Build(
            ["ModA"], Array.Empty<LoadOrderProfileMod>(), [Repo("ModA", nexus: true), Repo("ModA", nexus: true)]);
        Assert.Equal(LoadOrderLineOutcome.Unresolved, Assert.Single(plan.Lines).Outcome);
    }

    [Fact]
    public void Unlisted_profile_mods_are_not_appended_by_the_planner()
    {
        // SetModOrder's own semantics append installed-but-unlisted mods in
        // relative order; the planner supplies only the listed prefix.
        var listed = ProfileMod("ModA");
        var unlisted = ProfileMod("ModB");

        var plan = LoadOrderPlanner.Build(["ModA"], [listed, unlisted], Array.Empty<LoadOrderRepoCandidate>());

        Assert.Equal([listed.ContainerId], plan.OrderedContainerIds);
    }

    [Fact]
    public void Lines_preserve_file_order_across_outcomes()
    {
        var add = Repo("ModB");
        var reorder = ProfileMod("ModC");

        var plan = LoadOrderPlanner.Build(["ModB", "Ghost", "ModC"], [reorder], [add]);

        Assert.Equal(
            [LoadOrderLineOutcome.LibraryAdd, LoadOrderLineOutcome.Unresolved, LoadOrderLineOutcome.Reorder],
            plan.Lines.Select(l => l.Outcome));
        Assert.Equal([add.ContainerId, reorder.ContainerId], plan.OrderedContainerIds);
    }

    [Fact]
    public void Duplicate_case_variants_matching_one_container_order_first_occurrence()
    {
        // Two file names differing only by case match the same base name;
        // the ordered projection carries the container twice (SetModOrder
        // ignores repeats beyond the first) and the table shows both lines.
        var mod = ProfileMod("moda");

        var plan = LoadOrderPlanner.Build(["ModA", "moda"], [mod], Array.Empty<LoadOrderRepoCandidate>());

        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal([mod.ContainerId, mod.ContainerId], plan.OrderedContainerIds);
    }

    // ---- the reconciler (real fixture) ----------------------------------------

    [Fact]
    public void Reconciler_keys_profile_mods_on_their_policy_resolved_base_name()
    {
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion("Warp Unbound Timer");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var plan = fx.Reconciler.Reconcile(profile.Id, ["warp unbound timer"]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.Reorder, line.Outcome);
        Assert.Equal(container.Id, line.ContainerId);
        Assert.Equal("Warp Unbound Timer", line.DisplayName);
    }

    // ---- the reconciler's read-only identity facts (the plan carries what
    // Curator already knows: the Nexus id + the version this operation will
    // use, so the review can show them without re-querying by container id).

    [Fact]
    public void Reconciler_facts_an_active_latest_nexus_entry_with_the_resolved_latest()
    {
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion(
            "LatestMod", source: new NexusSource { ModId = 42 }, versionString: "1.0");
        fx.AddVersion(container.Id, "2.0"); // becomes the resolved latest
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var line = Assert.Single(fx.Reconciler.Reconcile(profile.Id, ["latestmod"]).Lines);

        Assert.Equal(42, line.NexusModId);
        Assert.Equal("2.0", line.Version);
    }

    [Fact]
    public void Reconciler_facts_an_active_pinned_nexus_entry_with_the_pinned_version()
    {
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion(
            "PinnedMod", source: new NexusSource { ModId = 7 }, versionString: "1.0");
        fx.AddVersion(container.Id, "2.0"); // latest, but pinned past
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var pinned = new PinnedPolicy(container.Versions.Single(v => v.VersionString == "1.0").Folder);
        fx.Service.AddMod(profile.Id, container.Id, pinned);

        var line = Assert.Single(fx.Reconciler.Reconcile(profile.Id, ["pinnedmod"]).Lines);

        Assert.Equal(7, line.NexusModId);
        Assert.Equal("1.0", line.Version); // the pin, not the latest
    }

    [Fact]
    public void Reconciler_facts_a_library_add_with_its_resolved_latest()
    {
        using var fx = new ProfileServiceFixture();
        var inProfile = fx.AddContainerWithVersion("ModInProfile");
        var library = fx.AddContainerWithVersion(
            "ModInLibrary", source: new NexusSource { ModId = 99 }, versionString: "1.0");
        fx.AddVersion(library.Id, "3.0");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, inProfile.Id, ModVersionPolicy.Latest);

        var lines = fx.Reconciler.Reconcile(profile.Id, ["modinlibrary"]).Lines;
        var line = Assert.Single(lines);

        Assert.Equal(LoadOrderLineOutcome.LibraryAdd, line.Outcome);
        Assert.Equal(99, line.NexusModId);
        Assert.Equal("3.0", line.Version); // AddMod applies Latest
    }

    [Fact]
    public void Reconciler_facts_an_untracked_match_with_a_version_but_no_id()
    {
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion("LocalMod", versionString: "1.5");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var line = Assert.Single(fx.Reconciler.Reconcile(profile.Id, ["localmod"]).Lines);

        Assert.Null(line.NexusModId); // untracked: no Nexus identity
        Assert.Equal("1.5", line.Version); // the known resolved tag shows
    }

    [Fact]
    public void Reconciler_facts_a_linked_match_with_neither_id_nor_version()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var linkedId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, linkedId, ModVersionPolicy.Latest);

        var line = Assert.Single(fx.Reconciler.Reconcile(profile.Id, ["linkedmod"]).Lines);

        Assert.Null(line.NexusModId);
        Assert.Null(line.Version); // a linked container keeps no version record
    }

    [Fact]
    public void Reconciler_facts_an_empty_latest_tag_as_an_honestly_empty_version()
    {
        // The version-unknown shape: the resolved latest exists but carries
        // an empty tag; the fact is an empty string (rendered blank), never
        // fabricated.
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion(
            "UnknownTag", source: new NexusSource { ModId = 5 }, versionString: string.Empty);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var line = Assert.Single(fx.Reconciler.Reconcile(profile.Id, ["unknowntag"]).Lines);

        Assert.Equal(5, line.NexusModId);
        Assert.NotNull(line.Version);
        Assert.Equal(string.Empty, line.Version);
    }

    [Fact]
    public void Reconciler_resolves_repo_candidates_by_their_latest_base_name()
    {
        using var fx = new ProfileServiceFixture();
        var inProfile = fx.AddContainerWithVersion("ModInProfile");
        var library = fx.AddContainerWithVersion(
            "ModInLibrary", source: new NexusSource { ModId = 42 });
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, inProfile.Id, ModVersionPolicy.Latest);

        var plan = fx.Reconciler.Reconcile(profile.Id, ["ModInProfile", "modinlibrary"]);

        Assert.Equal(
            [LoadOrderLineOutcome.Reorder, LoadOrderLineOutcome.LibraryAdd],
            plan.Lines.Select(l => l.Outcome));
        Assert.Equal(library.Id, plan.Lines[1].ContainerId);
    }

    [Fact]
    public void Reconciler_keys_linked_containers_on_the_external_folder_name()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var linkedId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, linkedId, ModVersionPolicy.Latest);

        var plan = fx.Reconciler.Reconcile(profile.Id, ["linkedmod"]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.Reorder, line.Outcome);
        Assert.Equal(linkedId, line.ContainerId);
    }

    [Fact]
    public void Reconciler_reports_names_of_unresolvable_entries_as_unmatched()
    {
        // A profile entry whose container vanished resolves no base name; the
        // file line naming it reports unmatched instead of silently matching
        // nothing.
        using var fx = new ProfileServiceFixture();
        var container = fx.AddContainerWithVersion("GhostMod");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Repo.RemoveVersion(
            container.Id, container.Versions.Single().Folder); // leaves the entry unresolvable
        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>());

        var plan = fx.Reconciler.Reconcile(profile.Id, ["GhostMod"]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.Unresolved, line.Outcome);
        Assert.Null(line.ContainerId);
    }

    [Fact]
    public void Reconciler_resolves_a_real_ambiguity_with_the_nexus_preference()
    {
        using var fx = new ProfileServiceFixture();
        // Two repo containers whose version folders carry the SAME base
        // directory name (the fixture's name-derived helper cannot express
        // this, so the base folders are authored directly).
        Guid AddWithBaseName(bool nexus)
        {
            var container = fx.Repo.CreateContainer(
                nexus ? new NexusSource { ModId = 7 } : new UntrackedSource(),
                (nexus ? "Nexus" : "Local") + " SameBase container");
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                var baseDir = Path.Combine(dir, "SameBase");
                Directory.CreateDirectory(baseDir);
                File.WriteAllText(Path.Combine(baseDir, "SameBase.mod"), "SameBase");
            });
            return container.Id;
        }

        var untracked = AddWithBaseName(nexus: false);
        var nexus = AddWithBaseName(nexus: true);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        var plan = fx.Reconciler.Reconcile(profile.Id, ["SameBase"]);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(LoadOrderLineOutcome.LibraryAdd, line.Outcome);
        Assert.Equal(nexus, line.ContainerId);
        Assert.NotEqual(untracked, nexus);
    }
}
