using Microsoft.Extensions.DependencyInjection;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Per-profile launch settings: <see cref="IProfileService.GetLaunchSettings"/>
/// + the atomic <see cref="IProfileService.UpdateProfile"/> write (the single
/// editable-profile trust boundary), the data model (backward-compat
/// normalization, round-trip, order/duplicate preservation), and the full
/// validation surface (invalid names, NUL, duplicates, reserved names) plus the
/// guarantee that a launch-settings update preserves the rest of the profile
/// aggregate.
/// </summary>
public sealed class LaunchSettingsTests
{
    // ---- defaults + backward compat ----------------------------------------

    [Fact]
    public void CreateProfile_defaults_to_empty_non_null_launch_settings()
    {
        using var fx = new ProfileServiceFixture();

        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.NotNull(profile.LaunchSettings);
        Assert.Empty(profile.LaunchSettings.EnvironmentVariables);
        Assert.Empty(profile.LaunchSettings.GameArguments);
    }

    [Fact]
    public void GetLaunchSettings_for_a_new_profile_returns_empty_settings()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        var settings = fx.Service.GetLaunchSettings(profile.Id);

        Assert.NotNull(settings);
        Assert.Empty(settings.EnvironmentVariables);
        Assert.Empty(settings.GameArguments);
    }

    [Fact]
    public void Old_json_without_launch_settings_loads_as_empty()
    {
        // A pre-launch-settings profile.json (no LaunchSettings property)
        // deserializes to empty, non-null settings.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var rawJson = $$"""{"Id":"{{profile.Id}}","Name":"P","CreatedAt":"{{profile.CreatedAt:O}}","Mods":[]}""";
        File.WriteAllText(fx.ProfileJson(profile.Id), rawJson, new System.Text.UTF8Encoding(false));

        var settings = fx.Service.GetLaunchSettings(profile.Id);

        Assert.NotNull(settings);
        Assert.Empty(settings.EnvironmentVariables);
        Assert.Empty(settings.GameArguments);
    }

    [Fact]
    public void Explicit_null_launch_settings_normalizes_to_empty()
    {
        // A hand-edit writing LaunchSettings:null deserializes to null and is
        // coerced to empty on read (mirrors the Mods ??= Empty normalization).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var id = profile.Id.ToString();
        File.WriteAllText(fx.ProfileJson(profile.Id),
            $$"""{"Id":"{{id}}","Name":"P","CreatedAt":"{{profile.CreatedAt:O}}","Mods":[],"LaunchSettings":null}""",
            new System.Text.UTF8Encoding(false));

        var settings = fx.Service.GetLaunchSettings(profile.Id);

        Assert.NotNull(settings);
        Assert.Empty(settings.EnvironmentVariables);
        Assert.Empty(settings.GameArguments);
    }

    [Fact]
    public void Old_json_without_enable_lua_logs_loads_as_false()
    {
        // A pre-EnableLuaLogs profile.json (LaunchSettings present but without
        // the EnableLuaLogs property) deserializes EnableLuaLogs to its bool
        // default (false), matching the backward-compat behavior of the rest of
        // the record.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var id = profile.Id.ToString();
        File.WriteAllText(fx.ProfileJson(profile.Id),
            $$$"""{"Id":"{{{id}}}","Name":"P","CreatedAt":"{{{profile.CreatedAt:O}}}","Mods":[],"LaunchSettings":{"EnvironmentVariables":[{"Name":"PROTON_LOG","Value":"1"}],"GameArguments":[]}}""",
            new System.Text.UTF8Encoding(false));

        var settings = fx.Service.GetLaunchSettings(profile.Id);

        Assert.False(settings.EnableLuaLogs);
        // The rest of the settings still round-trip.
        Assert.Single(settings.EnvironmentVariables);
    }

    [Fact]
    public void Old_json_without_skip_splash_loads_as_false()
    {
        // A pre-SkipSplash profile.json (LaunchSettings present but without the
        // SkipSplash property) deserializes SkipSplash to its bool default
        // (false), matching the backward-compat behavior of the rest of the
        // record.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var id = profile.Id.ToString();
        File.WriteAllText(fx.ProfileJson(profile.Id),
            $$$"""{"Id":"{{{id}}}","Name":"P","CreatedAt":"{{{profile.CreatedAt:O}}}","Mods":[],"LaunchSettings":{"EnvironmentVariables":[{"Name":"PROTON_LOG","Value":"1"}],"GameArguments":[]}}""",
            new System.Text.UTF8Encoding(false));

        var settings = fx.Service.GetLaunchSettings(profile.Id);

        Assert.False(settings.SkipSplash);
        // The rest of the settings still round-trip.
        Assert.Single(settings.EnvironmentVariables);
    }

    // ---- round-trip + order/duplicates -------------------------------------

    [Fact]
    public void UpdateProfile_round_trips_launch_settings_across_a_fresh_service_instance()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("GAMEMODERUN_OUTPUT", ""),
            },
            GameArguments = new[] { "-windowed", "-borderless" },
            EnableLuaLogs = true,
            SkipSplash = true,
        };

        ApplyLaunchSettings(fx.Service, profile.Id, settings);

        // A second service instance reads the same disk state -- proves the
        // settings genuinely persist, not just in-memory.
        var reloadConfig = CuratorConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        reloadConfig.ModsFolder = fx.ModsFolder;
        using var reloadFx = new ReloadFixture(reloadConfig);
        var loaded = reloadFx.Service.GetLaunchSettings(profile.Id);

        Assert.Equal(2, loaded.EnvironmentVariables.Count);
        Assert.Equal("PROTON_LOG", loaded.EnvironmentVariables[0].Name);
        Assert.Equal("1", loaded.EnvironmentVariables[0].Value);
        Assert.Equal("GAMEMODERUN_OUTPUT", loaded.EnvironmentVariables[1].Name);
        Assert.Equal(string.Empty, loaded.EnvironmentVariables[1].Value);
        Assert.Equal(new[] { "-windowed", "-borderless" }, loaded.GameArguments);
        Assert.True(loaded.EnableLuaLogs);
        Assert.True(loaded.SkipSplash);
    }

    [Fact]
    public void Game_argument_order_and_duplicates_surive_persistence()
    {
        // An ordered list of game args, including a deliberate duplicate: each
        // entry is a distinct argv value, and the stored order + duplicates must
        // round-trip unchanged.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var args = new[] { "-one", "-two", "-one", "-three" };

        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings { GameArguments = args });

        var loaded = fx.Service.GetLaunchSettings(profile.Id);
        Assert.Equal(args, loaded.GameArguments);
    }

    [Fact]
    public void Environment_variable_order_is_preserved()
    {
        // The list shape (not a dictionary) keeps insertion order across
        // persistence; the JSON serializes the list in order.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("ZETA", "z"),
                new EnvVar("ALPHA", "a"),
                new EnvVar("MIKE", "m"),
            },
        });

        var loaded = fx.Service.GetLaunchSettings(profile.Id);
        Assert.Equal(new[] { "ZETA", "ALPHA", "MIKE" },
            loaded.EnvironmentVariables.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Values_with_spaces_and_empty_values_are_stored_exactly()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("WITH_SPACES", "a value with spaces"),
                new EnvVar("EMPTY", ""),
            },
            GameArguments = new[] { "an arg with spaces", "" },
        });

        var loaded = fx.Service.GetLaunchSettings(profile.Id);
        Assert.Equal("a value with spaces", loaded.EnvironmentVariables[0].Value);
        Assert.Equal(string.Empty, loaded.EnvironmentVariables[1].Value);
        Assert.Equal("an arg with spaces", loaded.GameArguments[0]);
        Assert.Equal(string.Empty, loaded.GameArguments[1]);
    }

    [Fact]
    public void UpdateProfile_replacing_launch_settings_with_empty_clears_prior_settings()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("FOO", "1") },
            GameArguments = new[] { "-x" },
        });

        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings());

        var loaded = fx.Service.GetLaunchSettings(profile.Id);
        Assert.Empty(loaded.EnvironmentVariables);
        Assert.Empty(loaded.GameArguments);
    }

    // ---- preserves the rest of the aggregate -------------------------------

    [Fact]
    public void UpdateProfile_launch_settings_preserves_id_created_at_and_mods()
    {
        // Editing launch settings through UpdateProfile preserves id, creation
        // time, and the mod list (the name + description carried through are the
        // profile's current values). The atomic single-write contract is owned
        // by UpdateProfile; see ProfileMetadataTests for the full preservation
        // matrix.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("Original", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        var before = fx.Service.GetProfile(profile.Id);
        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("PROTON_LOG", "1") },
        };

        ApplyLaunchSettings(fx.Service, profile.Id, settings);
        var after = fx.Service.GetProfile(profile.Id);

        Assert.Equal(profile.Id, after.Id);
        Assert.Equal("Original", after.Name);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        var mod = Assert.Single(after.Mods);
        Assert.Equal(container.Id, mod.ContainerId);
        // And the settings persisted.
        Assert.Single(after.LaunchSettings.EnvironmentVariables);
    }

    // ---- unknown profile ---------------------------------------------------

    [Fact]
    public void GetLaunchSettings_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.GetLaunchSettings(Guid.NewGuid()));
    }

    [Fact]
    public void UpdateProfile_launch_settings_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.UpdateProfile(Guid.NewGuid(), "P", string.Empty, new LaunchSettings()));
    }

    [Fact]
    public void UpdateProfile_launch_settings_rejects_null_settings()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.UpdateProfile(profile.Id, "P", string.Empty, null!));
    }

    // ---- validation: per-entry name ----------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateProfile_launch_settings_rejects_an_empty_or_whitespace_name(string name)
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        var ex = Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar(name, "1") },
            }));
        Assert.Contains("empty name", ex.Message);
        // And nothing persisted.
        Assert.Empty(fx.Service.GetLaunchSettings(profile.Id).EnvironmentVariables);
    }

    [Fact]
    public void UpdateProfile_launch_settings_rejects_a_name_containing_equals()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO=BAR", "1") },
            }));
    }

    [Fact]
    public void UpdateProfile_launch_settings_rejects_a_name_containing_nul()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO\0BAR", "1") },
            }));
    }

    // ---- validation: per-entry value ---------------------------------------

    [Fact]
    public void UpdateProfile_launch_settings_rejects_a_value_containing_nul()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO", "a\0b") },
            }));
    }

    // ---- validation: duplicates --------------------------------------------

    [Fact]
    public void UpdateProfile_launch_settings_rejects_duplicate_names_case_insensitively()
    {
        // Profile portability between Windows (case-insensitive env) and Linux
        // (case-sensitive env): duplicate detection is case-insensitive so a
        // profile saved on one OS doesn't carry a confusing collision on the
        // other.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("PROTON_LOG", "1"),
                    new EnvVar("proton_log", "2"),
                },
            }));
    }

    [Fact]
    public void UpdateProfile_launch_settings_accepts_the_same_name_only_once_when_identical_case()
    {
        // A single entry is fine (the duplicate check is across entries, not a
        // blanket ban on a name).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("PROTON_LOG", "1") },
        });

        Assert.Single(fx.Service.GetLaunchSettings(profile.Id).EnvironmentVariables);
    }

    // ---- validation: reserved names ----------------------------------------

    [Fact]
    public void ReservedEnvironmentNames_is_exactly_the_documented_set()
    {
        // 14 names: 7 Curator-owned OS/launch env + 7 Relay config env.
        var expected = new[]
        {
            "STEAM_COMPAT_DATA_PATH",
            "STEAM_COMPAT_CLIENT_INSTALL_PATH",
            "APPDIR",
            "APPIMAGE",
            "ARGV0",
            "OWD",
            "BAMF_DESKTOP_FILE_HINT",
            "MODIFICUS_GAME_BINARY",
            "MODIFICUS_MOD_PATH",
            "RELAY_LOG_FILE",
            "RELAY_LOG_LEVEL",
            "MODIFICUS_STEAM_APP_ID",
            "RELAY_LUA_LOGS",
            "RELAY_SKIP_SPLASH",
        };

        Assert.Equal(14, LaunchSettings.ReservedEnvironmentNames.Count);
        foreach (var name in expected)
        {
            Assert.Contains(name, LaunchSettings.ReservedEnvironmentNames);
        }
    }

    [Fact]
    public void ReservedEnvironmentNames_is_case_insensitive()
    {
        Assert.Contains("steam_compat_data_path", LaunchSettings.ReservedEnvironmentNames);
        Assert.Contains("appdir", LaunchSettings.ReservedEnvironmentNames);
    }

    [Theory]
    [InlineData("STEAM_COMPAT_DATA_PATH")]
    [InlineData("STEAM_COMPAT_CLIENT_INSTALL_PATH")]
    [InlineData("APPDIR")]
    [InlineData("APPIMAGE")]
    [InlineData("ARGV0")]
    [InlineData("OWD")]
    [InlineData("BAMF_DESKTOP_FILE_HINT")]
    [InlineData("MODIFICUS_GAME_BINARY")]
    [InlineData("MODIFICUS_MOD_PATH")]
    [InlineData("RELAY_LOG_FILE")]
    [InlineData("RELAY_LOG_LEVEL")]
    [InlineData("MODIFICUS_STEAM_APP_ID")]
    [InlineData("RELAY_LUA_LOGS")]
    [InlineData("RELAY_SKIP_SPLASH")]
    public void UpdateProfile_launch_settings_rejects_each_reserved_name(string reserved)
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar(reserved, "x") },
            }));
    }

    [Fact]
    public void UpdateProfile_launch_settings_rejects_a_reserved_name_case_insensitively()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("appdir", "x") },
            }));
    }

    [Fact]
    public void UpdateProfile_launch_settings_reports_the_offending_name_in_the_message()
    {
        // The ArgumentException message names the offending field so the UI can
        // surface a clear error (and a log reader can diagnose).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        var ex = Assert.Throws<ArgumentException>(() =>
            ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("APPDIR", "x") },
            }));
        Assert.Contains("APPDIR", ex.Message);
    }

    // ---- game args are not validated ---------------------------------------

    [Fact]
    public void UpdateProfile_launch_settings_accepts_any_game_argument_string()
    {
        // Game args are not validated: any string is a legal argv value, and
        // Relay owns the final quoting. A value that would be illegal as an env
        // name (=) is fine as a game arg.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        ApplyLaunchSettings(fx.Service, profile.Id, new LaunchSettings
        {
            GameArguments = new[] { "-flag=value", "--width=1920", "" },
        });

        Assert.Equal(3, fx.Service.GetLaunchSettings(profile.Id).GameArguments.Count);
    }

    // ---- test-only helper --------------------------------------------------

    /// <summary>
    /// Test-only helper: applies <paramref name="settings"/> as the profile's
    /// launch settings through the atomic <see cref="IProfileService.UpdateProfile"/>
    /// write, preserving the profile's current name + description. The hosted
    /// Profiles page edits launch settings this way (UpdateProfile is the single
    /// editable-profile trust boundary; there is no focused launch-settings
    /// write).
    /// </summary>
    private static void ApplyLaunchSettings(IProfileService profiles, Guid id, LaunchSettings settings)
    {
        var current = profiles.GetProfile(id);
        profiles.UpdateProfile(id, current.Name, current.Description, settings);
    }

    /// <summary>Resolves a second <see cref="IProfileService"/> against a given config.</summary>
    private sealed class ReloadFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public IProfileService Service { get; }

        public ReloadFixture(CuratorConfig config)
        {
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging()
                .AddProfiles()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IProfileService>();
        }

        public void Dispose() => _provider.Dispose();
    }
}
