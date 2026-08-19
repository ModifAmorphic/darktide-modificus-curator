using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// The game-dir hosting ladder over <c>&lt;game&gt;/mods</c>, exercised against
/// the real platform staging-link primitive (junction on Windows, symlink on
/// Linux) + the real app-state receipts store: every claim row (absent, ours
/// by marker, ours by profiles-root prefix incl. a dead link, foreign real
/// dir/file/link), silent re-pointing, the consented takeover (rename + README
/// + receipt), and the best-effort owned-link removal.
/// </summary>
public sealed class GameDirModsHostTests
{
    private sealed class HostFixture : IDisposable
    {
        public string TempRoot { get; } =
            Path.Combine(Path.GetTempPath(), "curator-gamedir-" + Guid.NewGuid().ToString("N"));

        public string GameDir { get; }
        public string ProfilesRoot { get; }
        public string StatePath { get; }
        public FakeProfileService Profiles { get; }
        public AppStateStore State { get; }
        public GameDirModsHost Host { get; }

        public string ModsSlot => Path.Combine(GameDir, "mods");

        /// <summary>Creates a staged-root shape (with its mods/ + marker) and
        /// returns the staged root path.</summary>
        public string MakeStagedRoot(string name)
        {
            var stagedRoot = Path.Combine(ProfilesRoot, name, "staged");
            var stagedMods = Path.Combine(stagedRoot, "mods");
            Directory.CreateDirectory(stagedMods);
            WriteMarker(stagedMods);
            return stagedRoot;
        }

        public static void WriteMarker(string dir) =>
            File.WriteAllText(Path.Combine(dir, StagingOwnership.MarkerFileName), "{}");

        public HostFixture()
        {
            GameDir = Path.Combine(TempRoot, "game");
            ProfilesRoot = Path.Combine(TempRoot, "profiles");
            StatePath = Path.Combine(TempRoot, "app-state.json");
            Directory.CreateDirectory(GameDir);
            Directory.CreateDirectory(ProfilesRoot);

            Profiles = new FakeProfileService { ProfilesRoot = ProfilesRoot };
            State = new AppStateStore(StatePath);
            Host = new GameDirModsHost(
                CreatePlatformLink(),
                Profiles,
                State,
                NullLogger<GameDirModsHost>.Instance);
        }

        public void Dispose()
        {
            DeleteTree(TempRoot);
        }

        // Reparse-aware teardown: links are removed as links, never followed
        // into the staged trees they point at (which the same root owns).
        private static void DeleteTree(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                DeleteEntry(entry);
            }
            Directory.Delete(root);
        }

        private static void DeleteEntry(string entry)
        {
            var attrs = File.GetAttributes(entry);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attrs & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    /// <summary>
    /// Resolves the real platform-selective staging-link creator through the
    /// Profiles DI registration, so these tests exercise the same primitive
    /// production wires.
    /// </summary>
    private static StagingLinkCreator CreatePlatformLink()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddProfiles();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<StagingLinkCreator>();
    }

    private static string ResolveTarget(string linkPath) =>
        new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: false)!.FullName;

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.Ordinal);

    // ---- ladder: absent ------------------------------------------------------

    [Fact]
    public void Absent_slot_creates_the_link_silently()
    {
        using var fx = new HostFixture();
        var stagedRoot = fx.MakeStagedRoot("alpha");

        var result = fx.Host.EnsureHosting(fx.GameDir, stagedRoot);

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
        Assert.Null(result.ConflictPath);
        Assert.True(Directory.Exists(fx.ModsSlot));
        Assert.True(SamePath(stagedRoot + Path.DirectorySeparatorChar + "mods", ResolveTarget(fx.ModsSlot)));
    }

    // ---- ladder: ours --------------------------------------------------------

    [Fact]
    public void Owned_link_at_the_right_target_is_left_in_place()
    {
        using var fx = new HostFixture();
        var stagedRoot = fx.MakeStagedRoot("alpha");
        fx.Host.EnsureHosting(fx.GameDir, stagedRoot);

        var result = fx.Host.EnsureHosting(fx.GameDir, stagedRoot);

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
        Assert.True(SamePath(stagedRoot + Path.DirectorySeparatorChar + "mods", ResolveTarget(fx.ModsSlot)));
    }

    [Fact]
    public void Owned_link_pointing_elsewhere_is_repointed_and_the_old_target_survives()
    {
        // Profile switch then launch: the link silently re-points; the staged
        // tree it used to serve is never touched through the link.
        using var fx = new HostFixture();
        var alpha = fx.MakeStagedRoot("alpha");
        var beta = fx.MakeStagedRoot("beta");
        fx.Host.EnsureHosting(fx.GameDir, alpha);
        var alphaContent = Path.Combine(alpha, "mods", "somemod");
        Directory.CreateDirectory(alphaContent);
        File.WriteAllText(Path.Combine(alphaContent, "keep.txt"), "data");

        var result = fx.Host.EnsureHosting(fx.GameDir, beta);

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
        Assert.True(SamePath(beta + Path.DirectorySeparatorChar + "mods", ResolveTarget(fx.ModsSlot)));
        // The old staged tree survives the re-point untouched.
        Assert.True(File.Exists(Path.Combine(alphaContent, "keep.txt")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(alphaContent, "keep.txt")));
    }

    [Fact]
    public void Owned_link_is_proven_by_the_marker_even_outside_the_profiles_root()
    {
        // The marker inside the link's target claims the link wherever the
        // target lives (the prefix rule is an additional proof, not the only
        // one).
        using var fx = new HostFixture();
        var externalTarget = Path.Combine(fx.TempRoot, "elsewhere", "mods");
        Directory.CreateDirectory(externalTarget);
        HostFixture.WriteMarker(externalTarget);
        CreatePlatformLink()(fx.ModsSlot, externalTarget);

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
    }

    [Fact]
    public void Dead_link_whose_target_is_under_the_profiles_root_is_silently_recreated()
    {
        // A dead link after a data move: the stored target lies under the
        // profiles root, so it stays Curator's + gets re-created without
        // ceremony.
        using var fx = new HostFixture();
        var deadTarget = Path.Combine(fx.ProfilesRoot, "gone-profile", "staged", "mods");
        CreatePlatformLink()(fx.ModsSlot, deadTarget); // dangling by construction
        Assert.False(Directory.Exists(fx.ModsSlot));

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
        Assert.True(Directory.Exists(fx.ModsSlot));
    }

    // ---- ladder: foreign -----------------------------------------------------

    [Fact]
    public void Real_directory_is_foreign_untouched_and_reported()
    {
        using var fx = new HostFixture();
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Conflict, result.Outcome);
        Assert.Equal(fx.ModsSlot, result.ConflictPath);
        // Never claimed or mutated: the user's content is exactly as it was.
        Assert.True(Directory.Exists(fx.ModsSlot));
        Assert.False(File.Exists(Path.Combine(fx.ModsSlot, StagingOwnership.MarkerFileName)));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(fx.ModsSlot, "usermod")));
    }

    [Fact]
    public void Real_file_is_foreign_and_reported()
    {
        using var fx = new HostFixture();
        File.WriteAllText(fx.ModsSlot, "not a folder");

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Conflict, result.Outcome);
        Assert.Equal(fx.ModsSlot, result.ConflictPath);
        Assert.True(File.Exists(fx.ModsSlot));
        Assert.Equal("not a folder", File.ReadAllText(fx.ModsSlot));
    }

    [Fact]
    public void User_made_link_without_the_marker_outside_the_profiles_root_is_foreign()
    {
        // Reparse-ness alone proves nothing: a user's own link to their own
        // folder is never claimed or deleted.
        using var fx = new HostFixture();
        var userTarget = Path.Combine(fx.TempRoot, "user-space", "mods");
        Directory.CreateDirectory(userTarget);
        CreatePlatformLink()(fx.ModsSlot, userTarget);

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Conflict, result.Outcome);
        Assert.Equal(fx.ModsSlot, result.ConflictPath);
        Assert.True(Directory.Exists(fx.ModsSlot));
        Assert.True(SamePath(userTarget, ResolveTarget(fx.ModsSlot)));
        Assert.True(Directory.Exists(userTarget));
    }

    [Fact]
    public void Dead_link_outside_curators_space_is_foreign()
    {
        using var fx = new HostFixture();
        CreatePlatformLink()(fx.ModsSlot, Path.Combine(fx.TempRoot, "user-space", "gone"));

        var result = fx.Host.EnsureHosting(fx.GameDir, fx.MakeStagedRoot("alpha"));

        Assert.Equal(GameDirHostingOutcome.Conflict, result.Outcome);
        Assert.Equal(fx.ModsSlot, result.ConflictPath);
    }

    // ---- takeover ------------------------------------------------------------

    [Fact]
    public void TakeOver_renames_a_real_folder_with_a_readme_and_records_a_receipt()
    {
        using var fx = new HostFixture();
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var returned = fx.Host.TakeOver(fx.GameDir);

        Assert.False(Directory.Exists(fx.ModsSlot)); // the slot is free for the link
        var renamed = Directory.GetDirectories(fx.GameDir, "mods_*").Single();
        Assert.Matches(@"mods_\d{8}-\d{4}$", Path.GetFileName(renamed));
        // The return value is the renamed entry's full path (the rename
        // notice the shell shows carries it).
        Assert.Equal(renamed, returned);
        // Nothing deleted: the user's content moved aside intact.
        Assert.Equal("user data", File.ReadAllText(Path.Combine(renamed, "usermod")));
        Assert.True(File.Exists(Path.Combine(renamed, GameDirModsHost.TakeOverReadmeFileName)));

        // The receipt is persisted through the real store.
        var receipts = new AppStateStore(fx.StatePath).RenamedModsFolders;
        var receipt = Assert.Single(receipts!);
        Assert.Equal(fx.ModsSlot, receipt.OriginalPath);
        Assert.Equal(renamed, receipt.RenamedPath);
        Assert.InRange(receipt.RenamedAtUtc, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void TakeOver_records_the_receipt_when_the_readme_write_fails()
    {
        // The receipt is the audit trail for a mutation that already happened:
        // a README failure must neither fail the takeover nor lose the receipt.
        // A directory occupying the README path makes the write fail
        // deterministically on both platforms (IOException on Unix,
        // UnauthorizedAccessException on Windows).
        using var fx = new HostFixture();
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");
        Directory.CreateDirectory(Path.Combine(fx.ModsSlot, GameDirModsHost.TakeOverReadmeFileName));

        var returned = fx.Host.TakeOver(fx.GameDir); // must not throw

        var renamed = Directory.GetDirectories(fx.GameDir, "mods_*").Single();
        Assert.Equal(renamed, returned);
        Assert.True(Directory.Exists(Path.Combine(renamed, GameDirModsHost.TakeOverReadmeFileName)));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(renamed, "usermod")));

        // The receipt landed through the real store despite the README failure.
        var receipts = new AppStateStore(fx.StatePath).RenamedModsFolders;
        var receipt = Assert.Single(receipts!);
        Assert.Equal(fx.ModsSlot, receipt.OriginalPath);
        Assert.Equal(renamed, receipt.RenamedPath);
    }

    [Fact]
    public void TakeOver_appends_to_existing_receipts_without_clobbering()
    {
        using var fx = new HostFixture();
        var earlier = new RenamedModsFolder("/old/game/mods", "/old/game/mods_20250101-0000", DateTimeOffset.UtcNow);
        fx.State.RenamedModsFolders = new[] { earlier };
        Directory.CreateDirectory(fx.ModsSlot);

        var returned = fx.Host.TakeOver(fx.GameDir);

        Assert.NotNull(returned);
        var receipts = new AppStateStore(fx.StatePath).RenamedModsFolders!;
        Assert.Equal(2, receipts.Count);
        Assert.Equal(earlier, receipts[0]);
        Assert.Equal(fx.ModsSlot, receipts[1].OriginalPath);
        Assert.Equal(returned, receipts[1].RenamedPath);
    }

    [Fact]
    public void TakeOver_bumps_the_suffix_on_a_name_collision()
    {
        using var fx = new HostFixture();
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");

        var first = fx.Host.TakeOver(fx.GameDir);
        // Recreate a second foreign entry + pre-occupy the plain-stamp sibling
        // shape by taking over again within the same minute boundary: the
        // candidate name must not collide with the first rename.
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod2"), "more data");
        var second = fx.Host.TakeOver(fx.GameDir);

        var renamed = Directory.GetDirectories(fx.GameDir, "mods_*")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, renamed.Count);
        Assert.NotEqual(renamed[0], renamed[1]);
        Assert.Equal(renamed[0], first);
        Assert.Equal(renamed[1], second);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(renamed[0], "usermod")));
        Assert.Equal("more data", File.ReadAllText(Path.Combine(renamed[1], "usermod2")));
    }

    [Fact]
    public void TakeOver_renames_a_file_without_a_readme()
    {
        using var fx = new HostFixture();
        File.WriteAllText(fx.ModsSlot, "not a folder");

        var returned = fx.Host.TakeOver(fx.GameDir);

        var renamedFile = Directory.GetFiles(fx.GameDir, "mods_*").Single();
        Assert.Equal(renamedFile, returned);
        Assert.Equal("not a folder", File.ReadAllText(renamedFile));
        Assert.False(File.Exists(Path.Combine(renamedFile, GameDirModsHost.TakeOverReadmeFileName)));
        Assert.NotNull(new AppStateStore(fx.StatePath).RenamedModsFolders);
    }

    [Fact]
    public void TakeOver_is_a_no_op_for_an_absent_slot_or_an_owned_link()
    {
        using var fx = new HostFixture();
        // Absent: nothing to move aside, nothing renamed.
        Assert.Null(fx.Host.TakeOver(fx.GameDir));
        Assert.False(Directory.Exists(fx.ModsSlot));
        Assert.Null(new AppStateStore(fx.StatePath).RenamedModsFolders);

        // Owned: the link is Curator's; a takeover must not rename it.
        var stagedRoot = fx.MakeStagedRoot("alpha");
        fx.Host.EnsureHosting(fx.GameDir, stagedRoot);
        Assert.Null(fx.Host.TakeOver(fx.GameDir));
        Assert.True(Directory.Exists(fx.ModsSlot));
        Assert.True(SamePath(stagedRoot + Path.DirectorySeparatorChar + "mods", ResolveTarget(fx.ModsSlot)));
        Assert.Null(new AppStateStore(fx.StatePath).RenamedModsFolders);
    }

    [Fact]
    public void TakeOver_then_EnsureHosting_hosts_through_the_ladder()
    {
        // The consent flow end-to-end: takeover frees the slot, the retry
        // hosts normally.
        using var fx = new HostFixture();
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");
        var stagedRoot = fx.MakeStagedRoot("alpha");
        Assert.Equal(GameDirHostingOutcome.Conflict, fx.Host.EnsureHosting(fx.GameDir, stagedRoot).Outcome);

        fx.Host.TakeOver(fx.GameDir);
        var result = fx.Host.EnsureHosting(fx.GameDir, stagedRoot);

        Assert.Equal(GameDirHostingOutcome.Hosted, result.Outcome);
    }

    // ---- removal (external mode) ---------------------------------------------

    [Fact]
    public void RemoveOwnedLink_removes_a_curator_owned_link()
    {
        using var fx = new HostFixture();
        var stagedRoot = fx.MakeStagedRoot("alpha");
        fx.Host.EnsureHosting(fx.GameDir, stagedRoot);

        fx.Host.RemoveOwnedLink(fx.GameDir);

        Assert.False(Directory.Exists(fx.ModsSlot));
        // The staged tree survives: only the link was removed.
        Assert.True(Directory.Exists(Path.Combine(stagedRoot, "mods")));
    }

    [Fact]
    public void RemoveOwnedLink_never_touches_a_foreign_entry_or_an_absent_slot()
    {
        using var fx = new HostFixture();
        // Absent: no-op.
        fx.Host.RemoveOwnedLink(fx.GameDir);
        Assert.False(Directory.Exists(fx.ModsSlot));

        // Foreign real dir: never touched.
        Directory.CreateDirectory(fx.ModsSlot);
        File.WriteAllText(Path.Combine(fx.ModsSlot, "usermod"), "user data");
        fx.Host.RemoveOwnedLink(fx.GameDir);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(fx.ModsSlot, "usermod")));
    }

    [Fact]
    public void RemoveOwnedLink_is_best_effort_on_io_failure()
    {
        // A removal failure must not propagate: the external-mode launch it
        // serves cannot be blocked by cleanup.
        using var fx = new HostFixture();
        var throwing = new GameDirModsHost(
            (linkPath, targetPath) => throw new IOException("no links today"),
            fx.Profiles,
            fx.State,
            NullLogger<GameDirModsHost>.Instance);
        Directory.CreateDirectory(fx.ModsSlot); // any entry so the ladder runs

        throwing.RemoveOwnedLink(fx.GameDir); // must not throw

        Assert.True(Directory.Exists(fx.ModsSlot));
    }
}
