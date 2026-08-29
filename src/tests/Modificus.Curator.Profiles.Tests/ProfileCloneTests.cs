using Microsoft.Extensions.DependencyInjection;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="IProfileCloner"/> coverage against the real filesystem-backed
/// service: the exact persisted copy (every mod-entry field, every policy
/// kind, every launch setting, across a fresh service instance), the empty
/// staged scaffold, post-clone independence in both directions, the full
/// generated-name table, and the unknown-source + no-<see cref="IProfileService.ProfileCreated"/>
/// contracts.
/// </summary>
public sealed class ProfileCloneTests
{
    /// <summary>
    /// A source profile exercising every copied field: two containers with
    /// distinct policies (Latest + Pinned to a superseded version), a disabled
    /// mod, a load-order lock, a non-trivial order, and launch settings with
    /// ordered env vars + game args + both toggles.
    /// </summary>
    private sealed record RichSource(Profile Profile, Guid ModA, Guid ModB);

    private static RichSource CreateRichSource(ProfileServiceFixture fx)
    {
        var modA = fx.AddContainerWithVersion("ModA", "1.0.0");
        var modB = fx.AddContainerWithVersion("ModB", "1.0.0");
        // A second version becomes ModB's latest; the pin below targets the
        // superseded 1.0.0 folder id.
        var modBUpdated = fx.AddVersion(modB.Id, "2.0.0");
        var pinnedVersionId = modBUpdated.Versions.Single(v => v.VersionString == "1.0.0").Folder;

        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("CURATOR_FIRST", "one"), new EnvVar("CURATOR_SECOND", "two") },
            GameArguments = new[] { "--width", "1280", "--height", "720" },
            EnableLuaLogs = true,
            SkipSplash = true,
        };

        var source = fx.Service.CreateProfile("Source", "the source description", settings);
        fx.Service.AddMod(source.Id, modA.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(source.Id, modB.Id, new PinnedPolicy(pinnedVersionId));
        // ModB first, ModA second + disabled, ModB order-locked: order,
        // enabled, lock, and both policy kinds all carry distinct values.
        fx.Service.SetModOrder(source.Id, new[] { modB.Id, modA.Id });
        fx.Service.SetModEnabled(source.Id, modA.Id, enabled: false);
        fx.Service.SetModOrderLocked(source.Id, modB.Id, orderLocked: true);

        return new RichSource(source, modA.Id, modB.Id);
    }

    [Fact]
    public void Clone_copies_every_persisted_field_with_a_new_id_and_creation_time()
    {
        using var fx = new ProfileServiceFixture();
        var rich = CreateRichSource(fx);
        var source = fx.Service.GetProfile(rich.Profile.Id);

        var before = DateTimeOffset.UtcNow;
        var clone = fx.Cloner.CloneProfile(source.Id);
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(source.Id, clone.Id);
        Assert.NotEqual(Guid.Empty, clone.Id);
        Assert.Equal("Source (Copy 1)", clone.Name);
        // The creation timestamp was assigned by the clone call itself.
        Assert.True(clone.CreatedAt >= before && clone.CreatedAt <= after,
            $"clone CreatedAt {clone.CreatedAt} outside [{before}, {after}]");

        Assert.Equal(source.Description, clone.Description);
        Assert.Equal(source.LaunchSettings.EnvironmentVariables, clone.LaunchSettings.EnvironmentVariables);
        Assert.Equal(source.LaunchSettings.GameArguments, clone.LaunchSettings.GameArguments);
        Assert.Equal(source.LaunchSettings.EnableLuaLogs, clone.LaunchSettings.EnableLuaLogs);
        Assert.Equal(source.LaunchSettings.SkipSplash, clone.LaunchSettings.SkipSplash);

        Assert.Equal(source.Mods.Count, clone.Mods.Count);
        for (var i = 0; i < source.Mods.Count; i++)
        {
            Assert.Equal(source.Mods[i].ContainerId, clone.Mods[i].ContainerId);
            Assert.Equal(source.Mods[i].Enabled, clone.Mods[i].Enabled);
            Assert.Equal(source.Mods[i].Order, clone.Mods[i].Order);
            Assert.Equal(source.Mods[i].OrderLocked, clone.Mods[i].OrderLocked);
            Assert.Equal(source.Mods[i].Policy, clone.Mods[i].Policy);
        }

        // The clone genuinely persists: a fresh service instance over the same
        // disk reads back the exact copied aggregate, and the source is
        // untouched by the copy.
        var reloadConfig = CuratorConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        using var reloadFx = new ReloadFixture(reloadConfig);

        var loadedClone = reloadFx.Service.GetProfile(clone.Id);
        Assert.Equal(clone.Id, loadedClone.Id);
        Assert.Equal("Source (Copy 1)", loadedClone.Name);
        Assert.Equal(clone.CreatedAt, loadedClone.CreatedAt);
        Assert.Equal(source.Mods, loadedClone.Mods);
        // LaunchSettings is a record whose list properties compare by
        // reference, so the fields are compared explicitly (the lists are
        // distinct instances after the JSON round-trip).
        Assert.Equal(source.LaunchSettings.EnvironmentVariables, loadedClone.LaunchSettings.EnvironmentVariables);
        Assert.Equal(source.LaunchSettings.GameArguments, loadedClone.LaunchSettings.GameArguments);
        Assert.Equal(source.LaunchSettings.EnableLuaLogs, loadedClone.LaunchSettings.EnableLuaLogs);
        Assert.Equal(source.LaunchSettings.SkipSplash, loadedClone.LaunchSettings.SkipSplash);

        var loadedSource = reloadFx.Service.GetProfile(source.Id);
        Assert.Equal("Source", loadedSource.Name);
        Assert.Equal(source.CreatedAt, loadedSource.CreatedAt);
        Assert.Equal(source.Mods, loadedSource.Mods);
    }

    [Fact]
    public void Clone_receives_an_empty_staged_dir_even_when_the_source_has_a_staged_tree()
    {
        using var fx = new ProfileServiceFixture();
        var rich = CreateRichSource(fx);
        var staged = fx.Service.PrepareModRoot(rich.Profile.Id);
        // Sanity: the source staged a real tree (the enabled pinned ModB).
        Assert.True(Directory.EnumerateFileSystemEntries(staged).Any());

        var clone = fx.Cloner.CloneProfile(rich.Profile.Id);

        var cloneStaged = fx.StagedDir(clone.Id);
        Assert.True(Directory.Exists(cloneStaged));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cloneStaged));
    }

    [Fact]
    public void Clone_is_independent_of_its_source_in_both_directions()
    {
        using var fx = new ProfileServiceFixture();
        var rich = CreateRichSource(fx);
        var clone = fx.Cloner.CloneProfile(rich.Profile.Id);

        // Mutate the clone across every writable surface.
        fx.Service.UpdateProfile(clone.Id, "Renamed clone", "new desc", new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("CLONE_ONLY", "1") },
            SkipSplash = true,
        });
        fx.Service.SetModEnabled(clone.Id, rich.ModA, enabled: true);
        fx.Service.SetModOrderLocked(clone.Id, rich.ModB, orderLocked: false);
        fx.Service.SetModPolicy(clone.Id, rich.ModB, ModVersionPolicy.Latest);
        fx.Service.RemoveMod(clone.Id, rich.ModA);

        var reSource = fx.Service.GetProfile(rich.Profile.Id);
        Assert.Equal("Source", reSource.Name);
        Assert.Equal("the source description", reSource.Description);
        // The source keeps the rich launch settings the clone was copied from.
        Assert.Equal(
            new[] { new EnvVar("CURATOR_FIRST", "one"), new EnvVar("CURATOR_SECOND", "two") },
            reSource.LaunchSettings.EnvironmentVariables);
        Assert.True(reSource.LaunchSettings.SkipSplash);
        Assert.Equal(2, reSource.Mods.Count);
        var sourceB = reSource.Mods.Single(m => m.ContainerId == rich.ModB);
        Assert.True(sourceB.OrderLocked);
        Assert.Equal(0, sourceB.Order);
        Assert.IsType<PinnedPolicy>(sourceB.Policy);
        var sourceA = reSource.Mods.Single(m => m.ContainerId == rich.ModA);
        Assert.False(sourceA.Enabled);

        // Mutate the source; the clone keeps its own state.
        fx.Service.UpdateProfile(rich.Profile.Id, "Renamed source", "source desc 2", new LaunchSettings());

        var reClone = fx.Service.GetProfile(clone.Id);
        Assert.Equal("Renamed clone", reClone.Name);
        Assert.Equal("new desc", reClone.Description);
        Assert.Equal("CLONE_ONLY", reClone.LaunchSettings.EnvironmentVariables.Single().Name);
        Assert.True(reClone.LaunchSettings.SkipSplash);
        var cloneB = reClone.Mods.Single();
        Assert.Equal(rich.ModB, cloneB.ContainerId);
        Assert.Equal(ModVersionPolicy.Latest, cloneB.Policy);
    }

    public static TheoryData<string[], string, string> CloneNameRows => new()
    {
        // Existing profiles                                           Selected             Expected
        { new[] { "Testing" },                                         "Testing",            "Testing (Copy 1)" },
        { new[] { "Testing", "Testing (Copy 1)" },                     "Testing",            "Testing (Copy 2)" },
        { new[] { "Testing", "Testing (Copy 1)", "Testing (Copy 2)" }, "Testing (Copy 1)",   "Testing (Copy 3)" },
        { new[] { "Testing", "Testing (Copy 1)", "Testing (Copy 3)" }, "Testing",            "Testing (Copy 4)" },
        { new[] { "Testing", "testing (copy 1)" },                     "Testing",            "Testing (Copy 2)" },
        { new[] { "Testing (Copy X)" },                                "Testing (Copy X)",   "Testing (Copy X) (Copy 1)" },
        // Boundary: long.MaxValue advances to long.MaxValue + 1 (numbers are
        // arbitrary precision; the increment can never wrap negative).
        { new[] { "Testing", "Testing (Copy 9223372036854775807)" },   "Testing",            "Testing (Copy 9223372036854775808)" },
    };

    [Theory]
    [MemberData(nameof(CloneNameRows))]
    public void Clone_generates_names_per_the_copy_family_contract(
        string[] existing, string selected, string expected)
    {
        using var fx = new ProfileServiceFixture();
        foreach (var name in existing)
        {
            fx.Service.CreateProfile(name, string.Empty, new LaunchSettings());
        }

        var source = fx.Service.ListProfiles().Single(p => p.Name == selected);

        var clone = fx.Cloner.CloneProfile(source.Id);

        Assert.Equal(expected, clone.Name);
    }

    [Fact]
    public void Clone_unknown_source_throws_KeyNotFoundException_and_creates_nothing()
    {
        using var fx = new ProfileServiceFixture();
        fx.Service.CreateProfile("Only", string.Empty, new LaunchSettings());

        Assert.Throws<KeyNotFoundException>(() => fx.Cloner.CloneProfile(Guid.NewGuid()));

        Assert.Single(fx.Service.ListProfiles());
    }

    [Fact]
    public void Clone_does_not_raise_ProfileCreated()
    {
        using var fx = new ProfileServiceFixture();
        var raised = 0;
        fx.Service.ProfileCreated += (_, _) => raised++;

        var source = fx.Service.CreateProfile("Source", string.Empty, new LaunchSettings());
        Assert.Equal(1, raised);

        fx.Cloner.CloneProfile(source.Id);

        Assert.Equal(1, raised);
        Assert.Equal(2, fx.Service.ListProfiles().Count);
    }

    /// <summary>
    /// A second service instance over the same disk tree, proving persistence
    /// rather than an in-memory copy (the <c>ProfileCrudTests</c> pattern).
    /// </summary>
    private sealed class ReloadFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public IProfileService Service { get; }
        public IProfileCloner Cloner { get; }

        public ReloadFixture(CuratorConfig config)
        {
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging()
                .AddProfiles()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IProfileService>();
            Cloner = _provider.GetRequiredService<IProfileCloner>();
        }

        public void Dispose() => _provider.Dispose();
    }
}
