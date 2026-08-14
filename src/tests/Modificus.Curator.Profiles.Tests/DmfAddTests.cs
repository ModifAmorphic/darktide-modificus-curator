using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// The DMF (Darktide Mod Framework) fresh-add rule on
/// <see cref="IProfileService.AddMod"/>: a fresh DMF add lands at rank 0 +
/// order-locked (shifting survivors down one rank with all metadata intact),
/// regardless of the acquisition path that led to the add. Recognition is the
/// deliberately small rule: Nexus mod 8 by source, or the canonical lower-case
/// <c>dmf</c> base folder containing <c>dmf.mod</c>. Everything else keeps the
/// ordinary append-at-end unlocked behavior; re-adds of an existing entry stay
/// strict no-ops.
/// </summary>
public sealed class DmfAddTests
{
    private static NexusSource DmfSource() => new() { ModId = 8 };

    /// <summary>
    /// Creates a container + version whose base folder is exactly
    /// <paramref name="baseFolderName"/> holding <paramref name="descriptorName"/>
    /// (a hand-shaped on-disk structure; AddVersion's populate callback does no
    /// import validation, so a lookalike shape is expressible).
    /// </summary>
    private static ModContainer AddContainerWithBaseFolder(
        ProfileServiceFixture fx, string name, string baseFolderName, string descriptorName)
    {
        var container = fx.Repo.CreateContainer(new UntrackedSource(), name);
        return fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            var baseDir = Path.Combine(dir, baseFolderName);
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, descriptorName), name);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), name);
        });
    }

    // ---- Nexus mod 8 recognition -------------------------------------------

    [Fact]
    public void Nexus_mod8_on_an_empty_profile_becomes_order0_and_locked()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());

        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(dmf.Id, entry.ContainerId);
        Assert.Equal(0, entry.Order);
        Assert.True(entry.OrderLocked);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void Nexus_mod8_added_after_ordinary_mods_is_prepended_dense_and_locked()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        // Survivor metadata the prepend must preserve: disabled + locked B,
        // enabled + unlocked A.
        fx.Service.SetModEnabled(profile.Id, b.Id, enabled: false);
        fx.Service.SetModOrderLocked(profile.Id, b.Id, orderLocked: true);

        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(dmf.Id, 0, true, true), (a.Id, 1, false, true), (b.Id, 2, true, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked, m.Enabled)).ToArray());
    }

    // ---- canonical dmf/dmf.mod content recognition --------------------------

    [Fact]
    public void Canonical_dmf_content_recognized_for_an_untracked_container()
    {
        // The content-based fallback: a non-Nexus container whose policy-
        // selected content resolves to the canonical lower-case dmf base
        // folder containing dmf.mod.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var first = fx.AddContainerWithVersion("Ordinary");
        var dmf = AddContainerWithBaseFolder(fx, "Darktide Mod Framework", "dmf", "dmf.mod");
        fx.Service.AddMod(profile.Id, first.Id, ModVersionPolicy.Latest);

        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(dmf.Id, 0, true), (first.Id, 1, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked)).ToArray());
    }

    [Fact]
    public void Canonical_dmf_content_recognized_for_a_linked_folder()
    {
        // A linked external folder named dmf containing dmf.mod stages as the
        // canonical base folder, so the same rule applies through the linked
        // resolver.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var first = fx.AddContainerWithVersion("Ordinary");
        var external = fx.MakeExternalModFolder("dmf");
        var linkedId = fx.Imports.LinkFolder(external);

        fx.Service.AddMod(profile.Id, first.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, linkedId, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(linkedId, 0, true), (first.Id, 1, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked)).ToArray());
    }

    // ---- lookalikes stay ordinary -------------------------------------------

    [Fact]
    public void Lookalike_named_DMF_without_canonical_content_is_ordinary()
    {
        // A container named DMF whose base folder is "DMF" (not the canonical
        // lower-case dmf) is NOT recognized by the content rule.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var first = fx.AddContainerWithVersion("Ordinary");
        var lookalike = fx.AddContainerWithVersion("DMF");

        fx.Service.AddMod(profile.Id, first.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, lookalike.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(first.Id, 0, false), (lookalike.Id, 1, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked)).ToArray());
    }

    [Fact]
    public void Lookalike_dmf_folder_without_dmf_mod_descriptor_is_ordinary()
    {
        // The canonical base folder with a NON-matching descriptor fails the
        // rule: both halves (folder + descriptor) are required.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var lookalike = AddContainerWithBaseFolder(fx, "Fake Framework", "dmf", "framework.mod");

        fx.Service.AddMod(profile.Id, lookalike.Id, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(0, entry.Order);
        Assert.False(entry.OrderLocked);
    }

    [Fact]
    public void Other_nexus_mod_with_ordinary_content_is_ordinary()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var other = fx.AddContainerWithVersion("WeaponTweaks", source: new NexusSource { ModId = 999 });

        fx.Service.AddMod(profile.Id, other.Id, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(0, entry.Order);
        Assert.False(entry.OrderLocked);
    }

    // ---- ordinary adds + unknown container ids -------------------------------

    [Fact]
    public void Non_dmf_add_appends_at_the_end_unlocked()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");

        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(a.Id, 0, false), (b.Id, 1, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked)).ToArray());
        Assert.All(mods, m => Assert.True(m.Enabled));
    }

    [Fact]
    public void Unknown_container_id_appends_ordinarily()
    {
        // Preserved allowance: an id the repository does not know cannot be
        // recognized as DMF, so it follows ordinary append behavior.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var stranger = Guid.NewGuid();

        fx.Service.AddMod(profile.Id, stranger, ModVersionPolicy.Latest);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(stranger, entry.ContainerId);
        Assert.Equal(0, entry.Order);
        Assert.False(entry.OrderLocked);
    }

    // ---- idempotency + remove/re-add -----------------------------------------

    [Fact]
    public void Readding_dmf_after_unlock_and_reorder_is_a_strict_no_op()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());
        var other = fx.AddContainerWithVersion("Other");
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, other.Id, ModVersionPolicy.Latest);

        // The user rearranges DMF: unlock, move it after Other, disable it.
        fx.Service.SetModOrderLocked(profile.Id, dmf.Id, orderLocked: false);
        fx.Service.SetModOrder(profile.Id, [other.Id, dmf.Id]);
        fx.Service.SetModEnabled(profile.Id, dmf.Id, enabled: false);
        var before = fx.Service.GetModList(profile.Id);

        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest); // no-op

        var after = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            before.Select(m => (m.ContainerId, m.Order, m.OrderLocked, m.Enabled, m.Policy)),
            after.Select(m => (m.ContainerId, m.Order, m.OrderLocked, m.Enabled, m.Policy)));
    }

    [Fact]
    public void Removing_then_readding_dmf_reapplies_first_and_locked()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());
        var a = fx.AddContainerWithVersion("A");
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);

        fx.Service.RemoveMod(profile.Id, dmf.Id);
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(dmf.Id, 0, true), (a.Id, 1, false)],
            mods.Select(m => (m.ContainerId, m.Order, m.OrderLocked)).ToArray());
    }

    // ---- interplay with the lock projection -----------------------------------

    [Fact]
    public void Prepended_dmf_lock_holds_rank0_across_a_later_reorder()
    {
        // The structural consequence: the fresh DMF lock participates in the
        // SetModOrder projection like any other lock, so ordinary reorders
        // cannot displace DMF from rank 0 until the user unlocks it.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());
        var a = fx.AddContainerWithVersion("A");
        var b = fx.AddContainerWithVersion("B");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);

        fx.Service.SetModOrder(profile.Id, [b.Id, a.Id, dmf.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(dmf.Id, 0), (b.Id, 1), (a.Id, 2)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
    }

    [Fact]
    public void Unlocked_prepended_dmf_is_ordinary_from_then_on()
    {
        // The lock is a fresh-add default, not a protected state: once the user
        // unlocks DMF, a later reorder is free to move it.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var dmf = fx.AddContainerWithVersion("DMF", source: DmfSource());
        var a = fx.AddContainerWithVersion("A");
        fx.Service.AddMod(profile.Id, dmf.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);

        fx.Service.SetModOrderLocked(profile.Id, dmf.Id, orderLocked: false);
        fx.Service.SetModOrder(profile.Id, [a.Id, dmf.Id]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal(
            [(a.Id, 0), (dmf.Id, 1)],
            mods.Select(m => (m.ContainerId, m.Order)).ToArray());
    }
}
