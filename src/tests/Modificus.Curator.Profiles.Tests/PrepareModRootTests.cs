using System.Text;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="IProfileService.PrepareModRoot"/> + <c>mods.lst</c> generation
/// contract under the container-based staging model: enabled mods that resolve to
/// a present version folder are symlinked into <c>staged/mods/</c> and written to
/// <c>mods.lst</c> in <see cref="ModListEntry.Order"/>; disabled mods and mods
/// with no resolved version are omitted; UTF-8 no BOM; trailing newline;
/// idempotent (clears + rebuilds <c>staged/</c>); returns the <c>--mod-path</c>
/// (<c>staged/</c>).
/// </summary>
public sealed class PrepareModRootTests
{
    [Fact]
    public void Returns_staged_path_and_writes_mods_lst()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var modPath = fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(fx.StagedDir(profile.Id), modPath);
        Assert.True(Directory.Exists(modPath));
        Assert.True(File.Exists(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_lists_staged_enabled_mods_in_order_one_per_line_with_trailing_newline()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("DMF");
        var b = fx.AddContainerWithVersion("ModB");
        var c = fx.AddContainerWithVersion("ModC");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);
        // Reverse the order so we prove Order is honored, not insertion order.
        fx.Service.SetModOrder(profile.Id, [c.Id, a.Id, b.Id]);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal("ModC\nDMF\nModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_omits_disabled_mods()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("DMF");
        var b = fx.AddContainerWithVersion("DisabledMod");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, b.Id, enabled: false);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal("DMF\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_empty_file_when_no_enabled_mods()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings()); // no mods at all

        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(fx.ModsLst(profile.Id)));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_empty_file_when_all_mods_disabled()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: false);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_utf8_without_bom()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        var bytes = File.ReadAllBytes(fx.ModsLst(profile.Id));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "mods.lst must not carry a UTF-8 BOM.");
        Assert.Equal("DMF\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void PrepareModRoot_is_idempotent_on_repeat_calls()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("DMF");
        var b = fx.AddContainerWithVersion("ModB");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);

        var first = fx.Service.PrepareModRoot(profile.Id);
        var firstContent = File.ReadAllText(fx.ModsLst(profile.Id));
        var second = fx.Service.PrepareModRoot(profile.Id);
        var secondContent = File.ReadAllText(fx.ModsLst(profile.Id));

        Assert.Equal(first, second);
        Assert.Equal(firstContent, secondContent);
        Assert.Equal("DMF\nModB\n", secondContent);
    }

    [Fact]
    public void PrepareModRoot_reflects_latest_state_after_changes()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var a = fx.AddContainerWithVersion("DMF");
        var b = fx.AddContainerWithVersion("ModB");
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        Assert.Equal("DMF\n", File.ReadAllText(fx.ModsLst(profile.Id)));

        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, a.Id, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);

        // DMF now disabled -> omitted; ModB only.
        Assert.Equal("ModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void PrepareModRoot_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.PrepareModRoot(Guid.NewGuid()));
    }
}
