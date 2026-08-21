using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// <see cref="AppStateStore"/>: round-trip + first-run + corrupt-file safety
/// for all three persisted fields (the active-profile id, the last-update-check
/// timestamp, and the manual-refresh throttle window). Establishes the
/// persistence contracts the shell VM (active id), the update-check runner
/// (last-check gate + manual window), rely on, and pins the no-clobber
/// guarantee: assigning one field preserves the others.
/// </summary>
public sealed class AppStateStoreTests
{
    // ---- OnboardingCompleted (the first-run Welcome onboarding flag) ------

    [Fact]
    public void OnboardingCompleted_is_false_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.False(store.OnboardingCompleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OnboardingCompleted_round_trips_true()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);

            store.OnboardingCompleted = true;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.True(new AppStateStore(path).OnboardingCompleted);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_OnboardingCompleted_preserves_the_other_fields()
    {
        // The no-clobber guarantee now covers five fields. Setting
        // OnboardingCompleted must not wipe the others (the whole cached model
        // is rewritten).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[] { stamp };
        var known = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
        {
            [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", stamp, null) },
        };
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;
            store.ManualRefreshTimestamps = window;
            store.KnownUpdates = known;

            store.OnboardingCompleted = true; // must NOT wipe the other four

            Assert.True(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);
            Assert.Equal(known, store.KnownUpdates);

            // And on disk: a fresh instance sees all five.
            var reloaded = new AppStateStore(path);
            Assert.True(reloaded.OnboardingCompleted);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_another_field_preserves_OnboardingCompleted()
    {
        var path = TempPath();
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.OnboardingCompleted = true;

            store.ActiveProfileId = id; // must NOT wipe onboarding

            Assert.True(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_OnboardingCompleted_loads_false_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes OnboardingCompleted as false (the
        // System.Text.Json default for an absent bool member). Existing fields
        // still read.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.False(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Corrupt_file_loads_OnboardingCompleted_false_without_throwing()
    {
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.False(store.OnboardingCompleted);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- ActiveProfileId ---------------------------------------------------

    [Fact]
    public void ActiveProfileId_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.ActiveProfileId);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Set_persists_and_get_round_trips_the_value()
    {
        var path = TempPath();
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);

            store.ActiveProfileId = id;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.Equal(id, new AppStateStore(path).ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Set_null_clears_the_recorded_value()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = Guid.NewGuid();
            store.ActiveProfileId = null;

            Assert.Null(new AppStateStore(path).ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Get_returns_null_for_corrupt_file_without_throwing()
    {
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.Null(store.ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Get_returns_null_when_parent_directory_is_missing()
    {
        // First-run case: neither the directory nor the file exist yet.
        var path = System.IO.Path.Combine(Path.GetTempPath(), "curator-state-missing-" + Guid.NewGuid(), "app-state.json");

        Assert.Null(new AppStateStore(path).ActiveProfileId);
    }

    // ---- LastUpdateCheckUtc (Task 2: persisted update-check gate) ---------

    [Fact]
    public void LastUpdateCheckUtc_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.LastUpdateCheckUtc);
    }

    [Fact]
    public void LastUpdateCheckUtc_persists_and_round_trips_the_value()
    {
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);

            store.LastUpdateCheckUtc = stamp;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.Equal(stamp, new AppStateStore(path).LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_ActiveProfileId_preserves_a_persisted_LastUpdateCheckUtc()
    {
        // The critical no-clobber guarantee (Task 2): the store holds an
        // in-memory cached model and writes it whole, so assigning one property
        // must not reset the other to its default. Without the cache, saving
        // ActiveProfileId would write a fresh model with LastUpdateCheckUtc
        // null and wipe this stamp.
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.LastUpdateCheckUtc = stamp;

            store.ActiveProfileId = id; // must NOT wipe the timestamp

            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(id, store.ActiveProfileId);
            // And the on-disk file holds both: a fresh instance sees both too.
            var reloaded = new AppStateStore(path);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_LastUpdateCheckUtc_preserves_a_persisted_ActiveProfileId()
    {
        // The mirror of the above: assigning the timestamp must not wipe the id.
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;

            store.LastUpdateCheckUtc = stamp; // must NOT wipe the id

            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_LastUpdateCheckUtc_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes LastUpdateCheckUtc as null (System.Text.Json
        // default for an absent nullable member). The runner floors null to
        // DateTimeOffset.MinValue, so the opening startup check fires normally.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            // Hand-write an old-shape file: only ActiveProfileId.
            var id = Guid.NewGuid();
            File.WriteAllText(path, "{\"activeProfileId\":\"" + id + "\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.LastUpdateCheckUtc);
            Assert.Equal(id, store.ActiveProfileId); // existing field still reads
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- ManualRefreshTimestamps (the manual throttle's persisted window) ---

    [Fact]
    public void ManualRefreshTimestamps_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.ManualRefreshTimestamps);
    }

    [Fact]
    public void ManualRefreshTimestamps_persists_and_round_trips_the_list()
    {
        var path = TempPath();
        var stamps = new[]
        {
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 3, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 3, 6, 0, TimeSpan.Zero),
        };
        try
        {
            var store = new AppStateStore(path);

            store.ManualRefreshTimestamps = stamps;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted list.
            var reloaded = new AppStateStore(path).ManualRefreshTimestamps;
            Assert.NotNull(reloaded);
            Assert.Equal(stamps, reloaded);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_null_clears_ManualRefreshTimestamps()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.ManualRefreshTimestamps = new[]
            {
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            };
            store.ManualRefreshTimestamps = null;

            Assert.Null(new AppStateStore(path).ManualRefreshTimestamps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_ManualRefreshTimestamps_preserves_the_other_two_fields()
    {
        // The no-clobber guarantee now covers three fields. Set all three, then
        // mutate ManualRefreshTimestamps and confirm ActiveProfileId +
        // LastUpdateCheckUtc survive; then mutate each of those and confirm
        // ManualRefreshTimestamps survives too (the cached whole-model write is
        // symmetric).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[]
        {
            new DateTimeOffset(2025, 1, 2, 3, 5, 0, TimeSpan.Zero),
        };
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;

            // Mutate the new field: the other two must survive (on the instance
            // and on disk for a fresh instance).
            store.ManualRefreshTimestamps = window;
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);

            var reloaded = new AppStateStore(path);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.Equal(window, reloaded.ManualRefreshTimestamps);

            // Mutate ActiveProfileId: the other two (incl. the new field) survive.
            var id2 = Guid.NewGuid();
            store.ActiveProfileId = id2;
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);

            // Mutate LastUpdateCheckUtc: the other two survive.
            var stamp2 = stamp.AddHours(1);
            store.LastUpdateCheckUtc = stamp2;
            Assert.Equal(id2, store.ActiveProfileId);
            Assert.Equal(window, store.ManualRefreshTimestamps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_ManualRefreshTimestamps_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes ManualRefreshTimestamps as null
        // (System.Text.Json default for an absent nullable member). The runner
        // treats null as an empty queue (no throttle history), so a manual
        // refresh fires freely after upgrade.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            // Hand-write an old-shape file: only the two prior fields.
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.ManualRefreshTimestamps);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Corrupt_file_seeds_all_fields_as_defaults_without_throwing()
    {
        // The first-run-safe contract extends to every field: a corrupt file
        // must not throw, and all fields read their defaults (OnboardingCompleted
        // false, the rest null).
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.False(store.OnboardingCompleted);
            Assert.Null(store.ActiveProfileId);
            Assert.Null(store.LastUpdateCheckUtc);
            Assert.Null(store.ManualRefreshTimestamps);
            Assert.Null(store.LastNexusMetadataBackfillUtc);
            Assert.Null(store.MainWindowState);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Default_state_path_is_under_app_data()
    {
        var path = AppStateStore.DefaultStatePath();

        // Windows nests the data root under an org/app hierarchy
        // (ModifAmorphic\Modificus Curator); Linux keeps the flat
        // Modificus Curator segment. The state file sits directly under it.
        var expectedSegment = OperatingSystem.IsWindows()
            ? System.IO.Path.Combine("ModifAmorphic", "Modificus Curator")
            : "Modificus Curator";
        Assert.EndsWith(System.IO.Path.Combine(expectedSegment, "app-state.json"), path);
    }

    // ---- KnownUpdates (the persisted known-update snapshots) ---------------

    [Fact]
    public void KnownUpdates_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.KnownUpdates);
    }

    [Fact]
    public void KnownUpdates_persists_and_round_trips_a_profile_scoped_map()
    {
        var path = TempPath();
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var container = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);

            store.KnownUpdates = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
            {
                [profileA] = new[]
                {
                    new KnownUpdateSnapshot(profileA, container, 8, "1.0", stamp, stamp),
                },
                [profileB] = Array.Empty<KnownUpdateSnapshot>(),
            };

            // A fresh instance over the same file reads the persisted map.
            var reloaded = new AppStateStore(path).KnownUpdates;
            Assert.NotNull(reloaded);
            Assert.True(reloaded.ContainsKey(profileA));
            Assert.True(reloaded.ContainsKey(profileB));
            var entry = Assert.Single(reloaded[profileA]);
            Assert.Equal(container, entry.ContainerId);
            Assert.Equal(8, entry.ModId);
            Assert.Equal("1.0", entry.CurrentVersion);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_KnownUpdates_preserves_the_other_three_fields()
    {
        // The no-clobber guarantee now covers four fields. Setting KnownUpdates
        // must not wipe the others (the whole cached model is rewritten).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[] { stamp };
        var known = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
        {
            [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", stamp, null) },
        };
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;
            store.ManualRefreshTimestamps = window;

            store.KnownUpdates = known; // must NOT wipe the other three

            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);
            Assert.Equal(known, store.KnownUpdates);

            // And on disk: a fresh instance sees all four.
            var reloaded = new AppStateStore(path);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.NotNull(reloaded.KnownUpdates);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_KnownUpdates_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes KnownUpdates as null (System.Text.Json
        // default for an absent nullable member). Existing fields still read.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.KnownUpdates);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_KnownUpdates_to_null_clears_it()
    {
        var path = TempPath();
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.KnownUpdates = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
            {
                [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", DateTimeOffset.UtcNow, null) },
            };
            store.KnownUpdates = null;

            Assert.Null(new AppStateStore(path).KnownUpdates);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- LastNexusMetadataBackfillUtc (the metadata-backfill gate) ----------

    [Fact]
    public void LastNexusMetadataBackfillUtc_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.LastNexusMetadataBackfillUtc);
    }

    [Fact]
    public void LastNexusMetadataBackfillUtc_persists_and_round_trips_the_value()
    {
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);

            store.LastNexusMetadataBackfillUtc = stamp;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.Equal(stamp, new AppStateStore(path).LastNexusMetadataBackfillUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_LastNexusMetadataBackfillUtc_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes it as null (System.Text.Json default for an
        // absent nullable member). Existing fields still read.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.LastNexusMetadataBackfillUtc);
            Assert.Equal(id, store.ActiveProfileId);
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_LastNexusMetadataBackfillUtc_preserves_every_sibling_field()
    {
        // The no-clobber guarantee now covers six fields. Setting the backfill
        // stamp must not wipe the others (the whole cached model is rewritten).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[] { stamp };
        var known = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
        {
            [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", stamp, null) },
        };
        var backfill = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);
            store.OnboardingCompleted = true;
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;
            store.ManualRefreshTimestamps = window;
            store.KnownUpdates = known;

            store.LastNexusMetadataBackfillUtc = backfill; // must NOT wipe the others

            Assert.True(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);
            Assert.Equal(known, store.KnownUpdates);
            Assert.Equal(backfill, store.LastNexusMetadataBackfillUtc);

            // And on disk: a fresh instance sees all six.
            var reloaded = new AppStateStore(path);
            Assert.True(reloaded.OnboardingCompleted);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.Equal(backfill, reloaded.LastNexusMetadataBackfillUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_another_field_preserves_LastNexusMetadataBackfillUtc()
    {
        // Mirror: assigning a sibling field must not wipe the backfill stamp.
        var path = TempPath();
        var backfill = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.LastNexusMetadataBackfillUtc = backfill;

            store.ActiveProfileId = id; // must NOT wipe the stamp

            Assert.Equal(backfill, store.LastNexusMetadataBackfillUtc);
            Assert.Equal(id, store.ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- MainWindowState (the persisted main-window geometry) --------------

    [Fact]
    public void MainWindowState_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.MainWindowState);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void MainWindowState_round_trips_width_height_and_maximized()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);

            store.MainWindowState = new AppWindowState(1280.0, 800.0, true);

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted record.
            var reloaded = new AppStateStore(path).MainWindowState;
            Assert.NotNull(reloaded);
            Assert.Equal(1280.0, reloaded.Width);
            Assert.Equal(800.0, reloaded.Height);
            Assert.True(reloaded.IsMaximized);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_null_clears_MainWindowState()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.MainWindowState = new AppWindowState(1280, 800, false);
            store.MainWindowState = null;

            Assert.Null(new AppStateStore(path).MainWindowState);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_MainWindowState_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes MainWindowState as null (System.Text.Json
        // default for an absent nullable member). Existing fields still read.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.MainWindowState);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Corrupt_file_loads_MainWindowState_null_without_throwing()
    {
        // The first-run-safe contract extends to MainWindowState: a corrupt
        // file must not throw, and the field reads its default (null).
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.Null(store.MainWindowState);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_MainWindowState_preserves_every_sibling_field()
    {
        // The no-clobber guarantee now covers seven fields. Setting
        // MainWindowState must not wipe the others (the whole cached model is
        // rewritten) and writes atomically (the three components land together).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[] { stamp };
        var known = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
        {
            [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", stamp, null) },
        };
        var backfill = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);
            store.OnboardingCompleted = true;
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;
            store.ManualRefreshTimestamps = window;
            store.KnownUpdates = known;
            store.LastNexusMetadataBackfillUtc = backfill;

            store.MainWindowState = new AppWindowState(1100.0, 700.0, true); // must NOT wipe the others

            Assert.True(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);
            Assert.Equal(known, store.KnownUpdates);
            Assert.Equal(backfill, store.LastNexusMetadataBackfillUtc);
            Assert.Equal(1100.0, store.MainWindowState!.Width);
            Assert.Equal(700.0, store.MainWindowState.Height);
            Assert.True(store.MainWindowState.IsMaximized);

            // And on disk: a fresh instance sees all seven.
            var reloaded = new AppStateStore(path);
            Assert.True(reloaded.OnboardingCompleted);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.Equal(backfill, reloaded.LastNexusMetadataBackfillUtc);
            Assert.NotNull(reloaded.MainWindowState);
            Assert.True(reloaded.MainWindowState!.IsMaximized);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_another_field_preserves_MainWindowState()
    {
        // Mirror: assigning a sibling field must not wipe the window geometry.
        var path = TempPath();
        var window = new AppWindowState(1100.0, 700.0, false);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.MainWindowState = window;

            store.ActiveProfileId = id; // must NOT wipe the geometry

            Assert.Equal(window, store.MainWindowState);
            Assert.Equal(id, store.ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MainWindowState_writes_atomic_width_height_maximized_together()
    {
        // The atomic single-record write is the design contract: a width write
        // never lands without its height + flag. The simplest proof is that the
        // on-disk JSON for the record carries all three keys after one write.
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.MainWindowState = new AppWindowState(1024.0, 768.0, true);

            var json = File.ReadAllText(path);
            Assert.Contains("\"width\"", json);
            Assert.Contains("\"height\"", json);
            Assert.Contains("\"isMaximized\"", json);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- RenamedModsFolders (the game-dir takeover receipts) ---------------

    [Fact]
    public void RenamedModsFolders_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.RenamedModsFolders);
    }

    [Fact]
    public void RenamedModsFolders_persists_and_round_trips_the_list()
    {
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 7, 1, 12, 30, 0, TimeSpan.Zero);
        var receipts = new[]
        {
            new RenamedModsFolder("/game/mods", "/game/mods_20250701-1230", stamp),
        };
        try
        {
            var store = new AppStateStore(path);

            store.RenamedModsFolders = receipts;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted list.
            var reloaded = new AppStateStore(path).RenamedModsFolders;
            Assert.NotNull(reloaded);
            var entry = Assert.Single(reloaded);
            Assert.Equal("/game/mods", entry.OriginalPath);
            Assert.Equal("/game/mods_20250701-1230", entry.RenamedPath);
            Assert.Equal(stamp, entry.RenamedAtUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_null_clears_RenamedModsFolders()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.RenamedModsFolders = new[]
            {
                new RenamedModsFolder("/game/mods", "/game/mods_20250701-1230", DateTimeOffset.UtcNow),
            };
            store.RenamedModsFolders = null;

            Assert.Null(new AppStateStore(path).RenamedModsFolders);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_RenamedModsFolders_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes RenamedModsFolders as null (System.Text.Json
        // default for an absent nullable member). Existing fields still read.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.RenamedModsFolders);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_RenamedModsFolders_preserves_every_sibling_field()
    {
        // The no-clobber guarantee now covers eight fields. Setting the
        // receipts list must not wipe the others (the whole cached model is
        // rewritten).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[] { stamp };
        var known = new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>
        {
            [id] = new[] { new KnownUpdateSnapshot(id, Guid.NewGuid(), 8, "1.0", stamp, null) },
        };
        var backfill = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var receipts = new[]
        {
            new RenamedModsFolder("/game/mods", "/game/mods_20250701-1230", stamp),
        };
        try
        {
            var store = new AppStateStore(path);
            store.OnboardingCompleted = true;
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;
            store.ManualRefreshTimestamps = window;
            store.KnownUpdates = known;
            store.LastNexusMetadataBackfillUtc = backfill;
            store.MainWindowState = new AppWindowState(1100.0, 700.0, false);

            store.RenamedModsFolders = receipts; // must NOT wipe the others

            Assert.True(store.OnboardingCompleted);
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);
            Assert.Equal(known, store.KnownUpdates);
            Assert.Equal(backfill, store.LastNexusMetadataBackfillUtc);
            Assert.NotNull(store.MainWindowState);
            Assert.Equal(receipts, store.RenamedModsFolders);

            // And on disk: a fresh instance sees all eight.
            var reloaded = new AppStateStore(path);
            Assert.True(reloaded.OnboardingCompleted);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.Equal(backfill, reloaded.LastNexusMetadataBackfillUtc);
            Assert.NotNull(reloaded.MainWindowState);
            Assert.Equal(receipts, reloaded.RenamedModsFolders);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string TempPath() =>
        System.IO.Path.Combine(Path.GetTempPath(), "curator-state-" + Guid.NewGuid(), "app-state.json");

    private static void Cleanup(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
