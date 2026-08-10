using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Mod-list management on a known profile: <see cref="IProfileService.AddMod"/>,
/// <see cref="IProfileService.RemoveMod"/>, <see cref="IProfileService.SetModOrder"/>,
/// <see cref="IProfileService.SetModEnabled"/>, <see cref="IProfileService.GetModList"/>,
/// and <see cref="IProfileService.GetBaseNameCollision"/> (the import-time
/// base-name collision hard-block the add flow consults before importing).
/// Container-keyed (the new shape): every per-mod mutation takes the
/// <see cref="ModListEntry.ContainerId"/>.
/// </summary>
public sealed class ModListTests
{
    [Fact]
    public void AddMod_appends_enabled_entry_and_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");

        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        var entry = Assert.Single(mods);
        Assert.Equal(container.Id, entry.ContainerId);
        Assert.True(entry.Enabled);
        Assert.Equal(0, entry.Order);
    }

    [Fact]
    public void AddMod_assigns_increasing_order_to_subsequent_adds()
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
        Assert.Equal([(a.Id, 0), (b.Id, 1), (c.Id, 2)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
    }

    [Fact]
    public void AddMod_is_idempotent_for_existing_container()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest); // no-op

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(container.Id, entry.ContainerId);
    }

    [Fact]
    public void AddMod_rejects_empty_container_id()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            fx.Service.AddMod(profile.Id, Guid.Empty, ModVersionPolicy.Latest));
        Assert.Empty(fx.Service.GetModList(profile.Id));
    }

    [Fact]
    public void AddMod_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.AddMod(Guid.NewGuid(), Guid.NewGuid(), ModVersionPolicy.Latest));
    }

    [Fact]
    public void AddMod_with_explicit_Pinned_policy_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        var vId = container.Versions[0].Folder;
        var pinned = new PinnedPolicy(vId);

        fx.Service.AddMod(profile.Id, container.Id, pinned);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        var pinnedLoaded = Assert.IsType<PinnedPolicy>(entry.Policy);
        Assert.Equal(vId, pinnedLoaded.VersionId);
    }

    [Fact]
    public void AddMod_is_idempotent_keeps_existing_policy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        var vId = container.Versions[0].Folder;
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy(vId));

        // Re-add with a different policy: idempotent, keeps the original.
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.IsType<PinnedPolicy>(entry.Policy);
        Assert.Equal(vId, Assert.IsType<PinnedPolicy>(entry.Policy).VersionId);
    }

    [Fact]
    public void SetModPolicy_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.SetModPolicy(profile.Id, Guid.NewGuid(), ModVersionPolicy.Latest));
    }

    [Fact]
    public void SetModPolicy_with_present_versionId_succeeds_and_persists()
    {
        // The happy path: a PinnedPolicy whose VersionId references a version
        // present in the container is accepted, persisted, and round-trips.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0"); // becomes isLatest
        var v1 = fx.Repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "1.0");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.SetModPolicy(profile.Id, container.Id, new PinnedPolicy(v1.Folder));

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        var pinned = Assert.IsType<PinnedPolicy>(entry.Policy);
        Assert.Equal(v1.Folder, pinned.VersionId);
    }

    [Fact]
    public void SetModPolicy_with_orphan_versionId_throws_ArgumentException()
    {
        // Defense-in-depth: a programmatic call with a versionId that does not
        // resolve to a version in the container must not silently create a
        // phantom pin (one that would skip+warn at every stage). The UI
        // dropdown can't produce such an id, so this guards the programmatic
        // path only.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var ex = Assert.Throws<ArgumentException>(() =>
            fx.Service.SetModPolicy(profile.Id, container.Id, new PinnedPolicy("no-such-version-id")));
        Assert.Contains("No version with id", ex.Message);
    }

    [Fact]
    public void SetModPolicy_with_Pinned_on_missing_container_throws_ArgumentException()
    {
        // A container that no longer exists can't satisfy any Pinned policy; the
        // validation rejects it before persisting a phantom pin.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        var vId = container.Versions[0].Folder;
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        // Drop the container from the repo (simulates a prune / hand-delete).
        fx.Repo.RemoveVersion(container.Id, vId);

        // The version is gone; SetModPolicy must reject the orphan pin.
        Assert.Throws<ArgumentException>(() =>
            fx.Service.SetModPolicy(profile.Id, container.Id, new PinnedPolicy(vId)));
    }

    [Fact]
    public void ReadProfileFile_drops_legacy_Name_based_entries_and_treats_as_empty()
    {
        // Fresh-start tolerance: a legacy profile.json carries mod entries
        // with a Name field instead of ContainerId. Those entries deserialize with
        // ContainerId == Guid.Empty and are dropped on read (logged). The profile
        // is otherwise intact (name + created-at preserved).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        // Raw JSON hand-edit: an entry carrying the old Name-based shape (no
        // ContainerId), with a $kind-discriminated Policy. Written verbatim
        // because the C# anonymous-object route cannot express "$kind" as a
        // property name.
        var rawJson = $$"""
{
  "Id": "{{profile.Id}}",
  "Name": "P",
  "CreatedAt": "{{profile.CreatedAt:O}}",
  "Mods": [
    { "Name": "DMF", "Enabled": true, "Order": 0, "Policy": { "$kind": "latest" } }
  ]
}
""";
        File.WriteAllText(fx.ProfileJson(profile.Id), rawJson, new System.Text.UTF8Encoding(false));

        var loaded = fx.Service.GetProfile(profile.Id);

        Assert.Empty(loaded.Mods);
    }

    [Fact]
    public void ReadProfileFile_coerces_a_null_policy_to_Latest()
    {
        // A hand-edit (or future schema regression) could write a null Policy.
        // The service coerces it to Latest so downstream enumeration never NREs.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        // Hand-edit: wipe the Policy field on the entry (set to null).
        var handEdited = new
        {
            Id = profile.Id,
            Name = "P",
            CreatedAt = profile.CreatedAt,
            Mods = new[]
            {
                new { ContainerId = container.Id, Enabled = true, Order = 0, Policy = (object?)null },
            }
        };
        File.WriteAllText(fx.ProfileJson(profile.Id),
            System.Text.Json.JsonSerializer.Serialize(handEdited),
            new System.Text.UTF8Encoding(false));

        var entry = Assert.Single(fx.Service.GetProfile(profile.Id).Mods);
        Assert.IsType<LatestPolicy>(entry.Policy);
    }

    [Fact]
    public void ReadProfileFile_drops_legacy_pinned_entries_with_a_Version_tag()
    {
        // Fresh-start tolerance: a pre-versionId profile.json carries pinned
        // entries as { "$kind":"pinned", "Version":"1.2.3" }. Under the new shape
        // the "Version" property is unrecognized + skipped, leaving the
        // deserialized PinnedPolicy's VersionId empty. An empty VersionId is a
        // phantom pin (no version resolves); the service drops it + logs so the
        // entry is re-added and re-pinned through the dropdown. The profile is
        // otherwise intact. (Raw JSON because the anonymous-object route cannot
        // express "$kind" / "Version" as property names.)
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        var rawJson = $$"""
{
  "Id": "{{profile.Id}}",
  "Name": "P",
  "CreatedAt": "{{profile.CreatedAt:O}}",
  "Mods": [
    { "ContainerId": "{{container.Id}}", "Enabled": true, "Order": 0,
      "Policy": { "$kind": "pinned", "Version": "1.2.3" } }
  ]
}
""";
        File.WriteAllText(fx.ProfileJson(profile.Id), rawJson, new System.Text.UTF8Encoding(false));

        var loaded = fx.Service.GetProfile(profile.Id);

        // The legacy pinned entry is dropped (empty VersionId); the profile is
        // otherwise intact and re-adding through the import flow works.
        Assert.Empty(loaded.Mods);
    }

    [Fact]
    public void RemoveMod_drops_entry_and_leaves_repository_copy_intact()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.RemoveMod(profile.Id, container.Id);

        Assert.Empty(fx.Service.GetModList(profile.Id));
        // Repository copy survives (other profiles may still reference it; the
        // startup prune reclaims it when none does).
        Assert.NotNull(fx.Repo.Get(container.Id));
    }

    [Fact]
    public void RemoveMod_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.RemoveMod(profile.Id, Guid.NewGuid()));
    }

    [Fact]
    public void SetModEnabled_toggles_state_and_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: false);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.False(entry.Enabled);

        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: true);
        Assert.True(Assert.Single(fx.Service.GetModList(profile.Id)).Enabled);
    }

    [Fact]
    public void SetModEnabled_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.SetModEnabled(profile.Id, Guid.NewGuid(), true));
    }

    [Fact]
    public void SetModOrder_reorders_mods_by_container_id_sequence()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest); // order 0
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest); // order 1
        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest); // order 2

        fx.Service.SetModOrder(profile.Id, [c.Id, a.Id, b.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([(c.Id, 0), (a.Id, 1), (b.Id, 2)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
    }

    [Fact]
    public void SetModOrder_appends_unmentioned_mods_in_their_relative_order()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var c = fx.AddContainerWithVersion("C");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);

        // Only A is mentioned; B + C keep their order, after A.
        fx.Service.SetModOrder(profile.Id, [a.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([(a.Id, 0), (b.Id, 1), (c.Id, 2)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
    }

    [Fact]
    public void SetModOrder_ignores_ids_not_in_the_profile()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);

        fx.Service.SetModOrder(profile.Id, [a.Id, Guid.NewGuid(), Guid.NewGuid()]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Single(mods);
        Assert.Equal(a.Id, mods[0].ContainerId);
    }

    [Fact]
    public void SetModOrder_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.SetModOrder(Guid.NewGuid(), [Guid.NewGuid()]));
    }

    // ---- GetBaseNameCollision (import-time hard-block) ---------------------
    //
    // GetBaseNameCollision resolves each profile mod's base folder name (via the
    // same resolution as staging) + returns the first that matches the candidate,
    // excluding a would-be re-add container. Used by the add flow to REFUSE a mod
    // whose base folder name matches an existing profile mod (the loader can't
    // tell two same-folder mods apart).

    [Fact]
    public void GetBaseNameCollision_returns_null_when_no_mod_shares_the_base_name()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF"); // base folder 'DMF'
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var hit = fx.Service.GetBaseNameCollision(profile.Id, "OtherMod", excludeContainerId: null);

        Assert.Null(hit);
    }

    [Fact]
    public void GetBaseNameCollision_returns_the_colliding_entry_for_a_shared_base_name()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var existing = fx.AddContainerWithVersion("DMF"); // base folder 'DMF'
        fx.Service.AddMod(profile.Id, existing.Id, ModVersionPolicy.Latest);

        var hit = fx.Service.GetBaseNameCollision(profile.Id, "DMF", excludeContainerId: null);

        Assert.NotNull(hit);
        Assert.Equal(existing.Id, hit!.ContainerId);
    }

    [Fact]
    public void GetBaseNameCollision_excludes_the_given_container_id_the_re_add_case()
    {
        // A re-add resolves to the same container; the add flow excludes it, so
        // the collision check returns null even though the base name matches.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var hit = fx.Service.GetBaseNameCollision(profile.Id, "DMF", excludeContainerId: dmf.Id);

        Assert.Null(hit);
    }

    [Fact]
    public void GetBaseNameCollision_considers_disabled_mods()
    {
        // A disabled colliding mod could be enabled later, so it counts: the
        // check considers ALL profile mods, not just enabled ones.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var disabled = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, disabled.Id, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, disabled.Id, enabled: false);

        var hit = fx.Service.GetBaseNameCollision(profile.Id, "DMF", excludeContainerId: null);

        Assert.NotNull(hit);
        Assert.Equal(disabled.Id, hit!.ContainerId);
    }

    [Fact]
    public void GetBaseNameCollision_skips_a_mod_whose_base_name_cannot_be_resolved()
    {
        // A mod whose version folder is corrupted (zero/multiple subdirs) can't
        // yield a base name; it is skipped silently (can't collide). Only a
        // resolvable mod with the matching base name is returned.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var real = fx.AddContainerWithVersion("RealMod"); // base folder 'RealMod'
        var bad = fx.Repo.CreateContainer(new UntrackedSource(), "BadMod");
        fx.Repo.AddVersion(bad.Id, "1.0", dir =>
        {
            // A loose file at the version root, no base subdir.
            File.WriteAllText(Path.Combine(dir, "loose.txt"), "x");
        });
        fx.Service.AddMod(profile.Id, real.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, bad.Id, ModVersionPolicy.Latest);

        var hitReal = fx.Service.GetBaseNameCollision(profile.Id, "RealMod", excludeContainerId: null);
        var hitBad = fx.Service.GetBaseNameCollision(profile.Id, "BadMod", excludeContainerId: null);

        Assert.NotNull(hitReal);
        Assert.Equal(real.Id, hitReal!.ContainerId);
        Assert.Null(hitBad); // 'BadMod' has no resolvable base folder name
    }

    [Fact]
    public void GetBaseNameCollision_returns_the_first_match_when_several_share_the_base_name()
    {
        // Two distinct containers seeded with the same base name (a hand-edit
        // only; the import block forbids this in normal use): the first in
        // profile order is returned (either is enough to refuse the import).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var first = fx.AddContainerWithVersion("DMF");
        var second = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, first.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, second.Id, ModVersionPolicy.Latest);

        var hit = fx.Service.GetBaseNameCollision(profile.Id, "DMF", excludeContainerId: null);

        Assert.NotNull(hit);
        Assert.Equal(first.Id, hit!.ContainerId);
    }

    [Fact]
    public void GetBaseNameCollision_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.GetBaseNameCollision(Guid.NewGuid(), "DMF", excludeContainerId: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetBaseNameCollision_rejects_an_empty_or_whitespace_base_name(string baseName)
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            fx.Service.GetBaseNameCollision(profile.Id, baseName, excludeContainerId: null));
    }
}
