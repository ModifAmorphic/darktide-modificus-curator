using Microsoft.Extensions.DependencyInjection;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Profile description + atomic create/update contract
/// (<see cref="IProfileService.CreateProfile(string, string, LaunchSettings)"/> +
/// <see cref="IProfileService.UpdateProfile"/>): round-trip, normalization,
/// validation, and atomicity. Each test gets a fresh temp
/// <c>ProfilesBaseFolder</c> via <see cref="ProfileServiceFixture"/>.
/// </summary>
public sealed class ProfileMetadataTests
{
    // ---- create round-trip -------------------------------------------------

    [Fact]
    public void CreateProfile_with_description_and_launch_settings_round_trips()
    {
        using var fx = new ProfileServiceFixture();
        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("MOD", "1") },
            GameArguments = new[] { "--windowed" },
            EnableLuaLogs = true,
            SkipSplash = true,
        };

        var created = fx.Service.CreateProfile("Vanilla+", "A tuned loadout", settings);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Vanilla+", created.Name);
        Assert.Equal("A tuned loadout", created.Description);
        AssertLaunchSettingsEqual(settings, created.LaunchSettings);

        // Persists across a fresh instance (proves it round-trips through JSON,
        // not just in-memory).
        using var reloadFx = Reload(fx);
        var loaded = reloadFx.Service.GetProfile(created.Id);
        Assert.Equal("Vanilla+", loaded.Name);
        Assert.Equal("A tuned loadout", loaded.Description);
        AssertLaunchSettingsEqual(settings, loaded.LaunchSettings);
    }

    // ---- backward-compatible read normalization ----------------------------

    [Fact]
    public void ReadProfileFile_normalizes_missing_description_to_empty()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var id = created.Id.ToString();

        // A pre-description profile.json (no Description property).
        File.WriteAllText(fx.ProfileJson(created.Id),
            $$"""{"Id":"{{id}}","Name":"P","CreatedAt":"2024-01-01T00:00:00Z","Mods":[]}""");

        var loaded = fx.Service.GetProfile(created.Id);
        Assert.Equal(string.Empty, loaded.Description);
    }

    [Fact]
    public void ReadProfileFile_normalizes_explicit_null_description_to_empty()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var id = created.Id.ToString();

        File.WriteAllText(fx.ProfileJson(created.Id),
            $$"""{"Id":"{{id}}","Name":"P","CreatedAt":"2024-01-01T00:00:00Z","Description":null,"Mods":[]}""");

        var loaded = fx.Service.GetProfile(created.Id);
        Assert.Equal(string.Empty, loaded.Description);
    }

    // ---- UpdateProfile atomicity + preservation ----------------------------

    [Fact]
    public void UpdateProfile_persists_all_three_fields_and_preserves_identity_and_mods()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("Original", "old desc", new LaunchSettings());

        var containerA = fx.AddContainerWithVersion("ModA");
        var containerB = fx.AddContainerWithVersion("ModB", "2.0.0");
        fx.Service.AddMod(created.Id, containerA.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(created.Id, containerB.Id,
            new PinnedPolicy(containerB.Versions.First().Folder));
        // Disable B + reorder so the preserved state is non-trivial.
        fx.Service.SetModEnabled(created.Id, containerB.Id, false);
        fx.Service.SetModOrder(created.Id, new[] { containerB.Id, containerA.Id });

        var before = fx.Service.GetProfile(created.Id);
        var originalCreatedAt = before.CreatedAt;
        var originalMods = before.Mods;
        var newSettings = new LaunchSettings { GameArguments = new[] { "--foo" }, EnableLuaLogs = true };

        fx.Service.UpdateProfile(created.Id, "Renamed", "new desc", newSettings);

        using var reloadFx = Reload(fx);
        var loaded = reloadFx.Service.GetProfile(created.Id);

        // Edited fields changed.
        Assert.Equal("Renamed", loaded.Name);
        Assert.Equal("new desc", loaded.Description);
        AssertLaunchSettingsEqual(newSettings, loaded.LaunchSettings);

        // Identity + creation time preserved.
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(originalCreatedAt, loaded.CreatedAt);

        // Mods, order, enabled state, and policies preserved exactly.
        Assert.Equal(originalMods.Count, loaded.Mods.Count);
        for (var i = 0; i < originalMods.Count; i++)
        {
            Assert.Equal(originalMods[i].ContainerId, loaded.Mods[i].ContainerId);
            Assert.Equal(originalMods[i].Order, loaded.Mods[i].Order);
            Assert.Equal(originalMods[i].Enabled, loaded.Mods[i].Enabled);
            Assert.Equal(originalMods[i].Policy, loaded.Mods[i].Policy);
        }
    }

    [Fact]
    public void UpdateProfile_unknown_id_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.UpdateProfile(Guid.NewGuid(), "X", "d", new LaunchSettings()));
    }

    // ---- name normalization + rejection ------------------------------------

    [Fact]
    public void CreateProfile_trims_name_and_description_before_persistence()
    {
        using var fx = new ProfileServiceFixture();

        var created = fx.Service.CreateProfile("  Spaced  ", "  padded desc  ", new LaunchSettings());

        Assert.Equal("Spaced", created.Name);
        Assert.Equal("padded desc", created.Description);

        using var reloadFx = Reload(fx);
        var loaded = reloadFx.Service.GetProfile(created.Id);
        Assert.Equal("Spaced", loaded.Name);
        Assert.Equal("padded desc", loaded.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateProfile_rejects_blank_name(string name)
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile(name, "desc", new LaunchSettings()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateProfile_rejects_blank_name(string name)
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", "d", new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            fx.Service.UpdateProfile(created.Id, name, "d", new LaunchSettings()));
    }

    // ---- description validation --------------------------------------------

    [Fact]
    public void CreateProfile_accepts_empty_description_after_trim()
    {
        using var fx = new ProfileServiceFixture();

        var created = fx.Service.CreateProfile("P", "   ", new LaunchSettings());

        Assert.Equal(string.Empty, created.Description);
    }

    [Fact]
    public void CreateProfile_accepts_description_at_exactly_max_length_after_trim()
    {
        using var fx = new ProfileServiceFixture();
        var desc = new string('a', Profile.DescriptionMaxLength);

        var created = fx.Service.CreateProfile("P", "  " + desc + "  ", new LaunchSettings());

        // Trimmed to exactly the limit.
        Assert.Equal(desc, created.Description);
        Assert.Equal(Profile.DescriptionMaxLength, created.Description.Length);
    }

    [Fact]
    public void CreateProfile_rejects_description_over_max_length_after_trim()
    {
        using var fx = new ProfileServiceFixture();
        var desc = new string('a', Profile.DescriptionMaxLength + 1);

        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile("P", desc, new LaunchSettings()));
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("line1\r\nline2")]
    public void CreateProfile_rejects_multiline_description(string desc)
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile("P", desc, new LaunchSettings()));
    }

    [Fact]
    public void CreateProfile_rejects_null_description()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.CreateProfile("P", null!, new LaunchSettings()));
    }

    [Fact]
    public void UpdateProfile_rejects_null_description()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", "d", new LaunchSettings());

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.UpdateProfile(created.Id, "P", null!, new LaunchSettings()));
    }

    // ---- launch settings validation ----------------------------------------

    [Fact]
    public void CreateProfile_rejects_null_launch_settings()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.CreateProfile("P", "d", null!));
    }

    [Fact]
    public void UpdateProfile_rejects_null_launch_settings()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", "d", new LaunchSettings());

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.UpdateProfile(created.Id, "P", "d", null!));
    }

    [Fact]
    public void CreateProfile_rejects_invalid_launch_settings_via_shared_validator()
    {
        using var fx = new ProfileServiceFixture();
        // A reserved env name is rejected by LaunchSettingsValidator.
        var invalid = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("STEAM_COMPAT_DATA_PATH", "x") },
        };

        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile("P", "d", invalid));
    }

    [Fact]
    public void UpdateProfile_rejects_invalid_launch_settings_via_shared_validator()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P", "d", new LaunchSettings());
        var invalid = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("RELAY_LUA_LOGS", "1") },
        };

        Assert.Throws<ArgumentException>(() =>
            fx.Service.UpdateProfile(created.Id, "P", "d", invalid));
    }

    // ---- atomicity: no partial write ---------------------------------------

    [Fact]
    public void UpdateProfile_invalid_input_leaves_existing_file_unchanged()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("Keep", "orig", new LaunchSettings());
        var jsonBefore = File.ReadAllText(fx.ProfileJson(created.Id));

        // Several invalid inputs: each must throw and leave the file identical.
        Assert.Throws<ArgumentException>(() =>
            fx.Service.UpdateProfile(created.Id, "   ", "d", new LaunchSettings()));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.UpdateProfile(created.Id, "Keep",
                new string('x', Profile.DescriptionMaxLength + 1), new LaunchSettings()));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.UpdateProfile(created.Id, "Keep", "d",
                new LaunchSettings { EnvironmentVariables = new[] { new EnvVar("", "v") } }));

        var jsonAfter = File.ReadAllText(fx.ProfileJson(created.Id));
        Assert.Equal(jsonBefore, jsonAfter);

        // And the in-memory read agrees.
        var loaded = fx.Service.GetProfile(created.Id);
        Assert.Equal("Keep", loaded.Name);
        Assert.Equal("orig", loaded.Description);
    }

    [Fact]
    public void CreateProfile_invalid_input_creates_no_profile_directory()
    {
        using var fx = new ProfileServiceFixture();
        int CountProfileDirs() =>
            Directory.Exists(fx.BaseFolder) ? Directory.EnumerateDirectories(fx.BaseFolder).Count() : 0;
        var dirsBefore = CountProfileDirs();

        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile("   ", "d", new LaunchSettings()));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.CreateProfile("ok", "multi\nline", new LaunchSettings()));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.CreateProfile("ok", "d", null!));

        Assert.Equal(dirsBefore, CountProfileDirs());
    }

    // ---- ListProfiles + ProfileCreated project description -----------------

    [Fact]
    public void ListProfiles_includes_description_and_remains_sorted_by_name()
    {
        using var fx = new ProfileServiceFixture();
        fx.Service.CreateProfile("Bravo", "b-desc", new LaunchSettings());
        fx.Service.CreateProfile("Alpha", "a-desc", new LaunchSettings());

        var summaries = fx.Service.ListProfiles();

        Assert.Equal(new[] { "Alpha", "Bravo" }, summaries.Select(s => s.Name).ToArray());
        Assert.Equal("a-desc", summaries[0].Description);
        Assert.Equal("b-desc", summaries[1].Description);
    }

    [Fact]
    public void ProfileCreated_carries_summary_with_persisted_description()
    {
        using var fx = new ProfileServiceFixture();
        ProfileSummary? raised = null;
        fx.Service.ProfileCreated += (_, s) => raised = s;

        var created = fx.Service.CreateProfile("WithDesc", "carried", new LaunchSettings());

        Assert.NotNull(raised);
        Assert.Equal(created.Id, raised!.Id);
        Assert.Equal("WithDesc", raised.Name);
        Assert.Equal("carried", raised.Description);
    }

    /// <summary>
    /// Asserts two <see cref="LaunchSettings"/> are content-equal field by field.
    /// Needed because <see cref="LaunchSettings"/> is a record whose collection
    /// members compare by reference, and JSON round-trip rebuilds them as
    /// <see cref="List{T}"/> rather than arrays (so record equality fails on
    /// otherwise-identical settings).
    /// </summary>
    private static void AssertLaunchSettingsEqual(LaunchSettings expected, LaunchSettings actual)
    {
        Assert.Equal(expected.EnvironmentVariables.Count, actual.EnvironmentVariables.Count);
        for (var i = 0; i < expected.EnvironmentVariables.Count; i++)
        {
            Assert.Equal(expected.EnvironmentVariables[i], actual.EnvironmentVariables[i]);
        }
        Assert.Equal(expected.GameArguments, actual.GameArguments);
        Assert.Equal(expected.EnableLuaLogs, actual.EnableLuaLogs);
        Assert.Equal(expected.SkipSplash, actual.SkipSplash);
    }

    /// <summary>
    /// Resolves a second <see cref="IProfileService"/> against the same disk
    /// root, proving changes genuinely persist (not just in-memory).
    /// </summary>
    private static ReloadFixture Reload(ProfileServiceFixture fx)
    {
        var config = CuratorConfig.CreateDefault();
        config.ProfilesBaseFolder = fx.BaseFolder;
        config.ModsFolder = fx.ModsFolder;
        return new ReloadFixture(config);
    }

    private sealed class ReloadFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public IProfileService Service { get; }

        public ReloadFixture(CuratorConfig config)
        {
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging()
                .AddMods()
                .AddProfiles()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IProfileService>();
        }

        public void Dispose() => _provider.Dispose();
    }
}
