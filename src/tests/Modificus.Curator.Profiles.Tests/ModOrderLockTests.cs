using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Profile-scoped fixed-position load-order locks (<see cref="ModListEntry.OrderLocked"/>):
/// persistence + backward compatibility, <see cref="IProfileService.SetModOrderLocked"/>,
/// the <see cref="IProfileService.SetModOrder"/> lock projection (locked entries keep their
/// exact zero-based index; unlocked rows fill the open slots in the caller's desired
/// relative order), <see cref="IProfileService.AddMod"/> (ordinary adds append unlocked +
/// compact; the DMF fresh-add prepend is covered by <see cref="DmfAddTests"/>), and
/// <see cref="IProfileService.RemoveMod"/> (compacts survivors and re-baselines surviving
/// locks). Existing profile files load unlocked.
/// </summary>
public sealed class ModOrderLockTests
{
    // ---- persistence + backward compatibility --------------------------------

    [Fact]
    public void OrderLocked_round_trips_true_and_false()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var locked = fx.AddContainerWithVersion("Locked");
        var open = fx.AddContainerWithVersion("Open");
        fx.Service.AddMod(profile.Id, locked.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, open.Id, ModVersionPolicy.Latest);
        fx.Service.SetModOrderLocked(profile.Id, locked.Id, orderLocked: true);

        var reloaded = fx.Service.GetProfile(profile.Id);
        Assert.True(Assert.Single(reloaded.Mods, m => m.ContainerId == locked.Id).OrderLocked);
        Assert.False(Assert.Single(reloaded.Mods, m => m.ContainerId == open.Id).OrderLocked);

        // Toggle off + round-trip again.
        fx.Service.SetModOrderLocked(profile.Id, locked.Id, orderLocked: false);
        reloaded = fx.Service.GetProfile(profile.Id);
        Assert.False(Assert.Single(reloaded.Mods, m => m.ContainerId == locked.Id).OrderLocked);
    }

    [Fact]
    public void Older_profile_json_without_OrderLocked_loads_false()
    {
        // Backward compatibility: a profile.json written before OrderLocked
        // existed deserializes every entry with OrderLocked = false (the bool
        // default for a missing property), so existing profiles load unlocked.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var rawJson = $$"""
{
  "Id": "{{profile.Id}}",
  "Name": "P",
  "CreatedAt": "{{profile.CreatedAt:O}}",
  "Mods": [
    { "ContainerId": "{{a.Id}}", "Enabled": true, "Order": 0,
      "Policy": { "$kind": "latest" } }
  ]
}
""";
        File.WriteAllText(fx.ProfileJson(profile.Id), rawJson, new System.Text.UTF8Encoding(false));

        var entry = Assert.Single(fx.Service.GetProfile(profile.Id).Mods);

        Assert.False(entry.OrderLocked);
    }

    // ---- SetModOrderLocked ----------------------------------------------------

    [Fact]
    public void SetModOrderLocked_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.SetModOrderLocked(profile.Id, Guid.NewGuid(), orderLocked: true));
    }

    [Fact]
    public void SetModOrderLocked_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.SetModOrderLocked(Guid.NewGuid(), Guid.NewGuid(), orderLocked: true));
    }

    [Fact]
    public void SetModOrderLocked_preserves_order_enabled_and_policy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0"); // becomes isLatest
        var v1 = fx.Repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "1.0");
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy(v1.Folder));
        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: false);
        var before = Assert.Single(fx.Service.GetModList(profile.Id));

        fx.Service.SetModOrderLocked(profile.Id, container.Id, orderLocked: true);

        var after = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.True(after.OrderLocked);
        Assert.Equal(before.Order, after.Order);
        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Equal(before.Policy, after.Policy);
    }

    // ---- SetModOrder lock projection -----------------------------------------
    //
    // Locked entries (OrderLocked = true) keep their exact zero-based load-order
    // index across any SetModOrder call; the caller's requested ordering is
    // projected onto the unlocked slots only.

    [Fact]
    public void SetModOrder_retains_locked_positions_and_crosses_unlocked()
    {
        // [L0, A1, L2, B3], request [B, L0, L2, A] => [L0, B, L2, A].
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l0 = fx.AddContainerWithVersion("L0");
        var a = fx.AddContainerWithVersion("A");
        var l2 = fx.AddContainerWithVersion("L2");
        var b = fx.AddContainerWithVersion("B");
        fx.Service.AddMod(profile.Id, l0.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);  // order 1
        fx.Service.AddMod(profile.Id, l2.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);  // order 3
        fx.Service.SetModOrderLocked(profile.Id, l0.Id, orderLocked: true);
        fx.Service.SetModOrderLocked(profile.Id, l2.Id, orderLocked: true);

        fx.Service.SetModOrder(profile.Id, [b.Id, l0.Id, l2.Id, a.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([l0.Id, b.Id, l2.Id, a.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([(l0.Id, 0), (b.Id, 1), (l2.Id, 2), (a.Id, 3)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
        Assert.True(mods[0].OrderLocked);
        Assert.False(mods[1].OrderLocked);
        Assert.True(mods[2].OrderLocked);
        Assert.False(mods[3].OrderLocked);
    }

    [Fact]
    public void SetModOrder_locked_first_remains_first_under_any_request()
    {
        // [L0, A1, B2]; any reorder keeps L at index 0.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l = fx.AddContainerWithVersion("L");
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        fx.Service.AddMod(profile.Id, l.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest); // order 1
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.SetModOrderLocked(profile.Id, l.Id, orderLocked: true);

        // Request to move B + A ahead of L: L stays at index 0.
        fx.Service.SetModOrder(profile.Id, [b.Id, a.Id, l.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([l.Id, b.Id, a.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.True(mods[0].OrderLocked);
    }

    [Fact]
    public void SetModOrder_all_locked_is_a_no_op_reorder()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var x = fx.AddContainerWithVersion("X");
        var y = fx.AddContainerWithVersion("Y");
        var z = fx.AddContainerWithVersion("Z");
        fx.Service.AddMod(profile.Id, x.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, y.Id, ModVersionPolicy.Latest); // order 1
        fx.Service.AddMod(profile.Id, z.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.SetModOrderLocked(profile.Id, x.Id, orderLocked: true);
        fx.Service.SetModOrderLocked(profile.Id, y.Id, orderLocked: true);
        fx.Service.SetModOrderLocked(profile.Id, z.Id, orderLocked: true);

        fx.Service.SetModOrder(profile.Id, [z.Id, x.Id, y.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        // Every slot reserved: canonical order is preserved exactly.
        Assert.Equal([x.Id, y.Id, z.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
    }

    [Fact]
    public void SetModOrder_partial_request_keeps_unmentioned_after_listed_unlocked()
    {
        // [L0, A1, B2, C3], L0 locked, request [C]: L stays at 0; the listed
        // unlocked C comes next; unmentioned A + B follow in relative order.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l = fx.AddContainerWithVersion("L");
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        fx.Service.AddMod(profile.Id, l.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest); // order 1
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest); // order 3
        fx.Service.SetModOrderLocked(profile.Id, l.Id, orderLocked: true);

        fx.Service.SetModOrder(profile.Id, [c.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        // L reserved at 0; unlocked desired = [C, A, B] (C listed first, A + B
        // unmentioned appended in relative order) fills slots 1, 2, 3.
        Assert.Equal([l.Id, c.Id, a.Id, b.Id],
            mods.Select(m => m.ContainerId).ToArray());
    }

    [Fact]
    public void SetModOrder_ignores_unknown_and_empty_ids_around_locks()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l = fx.AddContainerWithVersion("L");
        var a = fx.AddContainerWithVersion("A");
        fx.Service.AddMod(profile.Id, l.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest); // order 1
        fx.Service.SetModOrderLocked(profile.Id, l.Id, orderLocked: true);

        // Unknown + Guid.Empty ids are ignored; A is the only unlocked entry.
        fx.Service.SetModOrder(profile.Id, [Guid.NewGuid(), Guid.Empty, a.Id, l.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([l.Id, a.Id],
            mods.Select(m => m.ContainerId).ToArray());
    }

    [Fact]
    public void SetModOrder_first_duplicate_in_request_wins_around_locks()
    {
        // Two distinct locked rows; a duplicated unlocked id in the request
        // resolves first-occurrence-wins, projected onto unlocked slots.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l0 = fx.AddContainerWithVersion("L0");
        var a = fx.AddContainerWithVersion("A");
        var l2 = fx.AddContainerWithVersion("L2");
        fx.Service.AddMod(profile.Id, l0.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);  // order 1
        fx.Service.AddMod(profile.Id, l2.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.SetModOrderLocked(profile.Id, l0.Id, orderLocked: true);
        fx.Service.SetModOrderLocked(profile.Id, l2.Id, orderLocked: true);

        // Duplicate A in the request: first occurrence wins. L0 + L2 keep indices.
        fx.Service.SetModOrder(profile.Id, [a.Id, a.Id, l0.Id, l2.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([l0.Id, a.Id, l2.Id],
            mods.Select(m => m.ContainerId).ToArray());
    }

    [Fact]
    public void SetModOrder_with_no_locks_preserves_prior_behavior()
    {
        // No-lock regression: identical to the pre-lock SetModOrder semantics.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);

        fx.Service.SetModOrder(profile.Id, [c.Id, a.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        // C + A listed (in that order); B unmentioned appended.
        Assert.Equal([c.Id, a.Id, b.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
        Assert.All(mods, m => Assert.False(m.OrderLocked));
    }

    [Fact]
    public void SetModOrder_makes_order_dense_and_preserves_state()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l = fx.AddContainerWithVersion("L");
        var a = fx.AddContainerWithVersion("A", "1.0");
        fx.AddVersion(a.Id, "2.0"); // becomes isLatest
        var v1 = fx.Repo.Get(a.Id)!.Versions.Single(v => v.VersionString == "1.0");
        fx.Service.AddMod(profile.Id, l.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, a.Id, new PinnedPolicy(v1.Folder));
        fx.Service.SetModEnabled(profile.Id, a.Id, enabled: false);
        fx.Service.SetModOrderLocked(profile.Id, l.Id, orderLocked: true);

        fx.Service.SetModOrder(profile.Id, [a.Id, l.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([0, 1], mods.Select(m => m.Order).ToArray());
        var lEntry = Assert.Single(mods, m => m.ContainerId == l.Id);
        var aEntry = Assert.Single(mods, m => m.ContainerId == a.Id);
        Assert.True(lEntry.OrderLocked);           // lock preserved
        Assert.False(aEntry.OrderLocked);
        Assert.False(aEntry.Enabled);              // enabled preserved
        Assert.Equal(v1.Folder, Assert.IsType<PinnedPolicy>(aEntry.Policy).VersionId); // policy preserved
    }

    // ---- AddMod + RemoveMod baseline interactions ----------------------------

    [Fact]
    public void AddMod_appends_unlocked_and_compacts_order()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);

        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([a.Id, b.Id, c.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
        // New entries are appended unlocked.
        Assert.False(mods[^1].OrderLocked);
    }

    [Fact]
    public void AddMod_compacts_non_dense_survivor_orders()
    {
        // A hand-edited profile.json with non-dense orders is compacted dense on
        // the next AddMod, establishing a fresh baseline.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        var handEdited = new
        {
            Id = profile.Id,
            Name = "P",
            CreatedAt = profile.CreatedAt,
            Mods = new[]
            {
                new { ContainerId = a.Id, Enabled = true, Order = 0, Policy = (object?)null },
                new { ContainerId = b.Id, Enabled = true, Order = 5, Policy = (object?)null },
            }
        };
        File.WriteAllText(fx.ProfileJson(profile.Id),
            System.Text.Json.JsonSerializer.Serialize(handEdited),
            new System.Text.UTF8Encoding(false));

        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
    }

    [Fact]
    public void AddMod_idempotent_re_add_preserves_lock()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.SetModOrderLocked(profile.Id, container.Id, orderLocked: true);

        // Re-add: strict no-op, lock preserved.
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.True(entry.OrderLocked);
    }

    [Fact]
    public void RemoveMod_compacts_survivors_and_rebaselines_locks()
    {
        // Remove A from [L0, A1, L2, B3] => [L0, L2, B] with orders 0,1,2; the
        // surviving L (was index 2) is now locked at index 1.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l0 = fx.AddContainerWithVersion("L0");
        var a = fx.AddContainerWithVersion("A");
        var l2 = fx.AddContainerWithVersion("L2");
        var b = fx.AddContainerWithVersion("B");
        fx.Service.AddMod(profile.Id, l0.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);  // order 1
        fx.Service.AddMod(profile.Id, l2.Id, ModVersionPolicy.Latest); // order 2
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);  // order 3
        fx.Service.SetModOrderLocked(profile.Id, l0.Id, orderLocked: true);
        fx.Service.SetModOrderLocked(profile.Id, l2.Id, orderLocked: true);

        fx.Service.RemoveMod(profile.Id, a.Id);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([l0.Id, l2.Id, b.Id],
            mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([(l0.Id, 0), (l2.Id, 1), (b.Id, 2)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
        Assert.True(mods[0].OrderLocked);
        Assert.True(mods[1].OrderLocked);   // surviving L re-baselined to index 1
        Assert.False(mods[2].OrderLocked);
    }

    [Fact]
    public void RemoveMod_allows_removing_a_locked_row()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var l = fx.AddContainerWithVersion("L");
        var a = fx.AddContainerWithVersion("A");
        fx.Service.AddMod(profile.Id, l.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.SetModOrderLocked(profile.Id, l.Id, orderLocked: true);

        fx.Service.RemoveMod(profile.Id, l.Id);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(a.Id, entry.ContainerId);
        Assert.Equal(0, entry.Order);
        Assert.False(entry.OrderLocked);
    }
}
