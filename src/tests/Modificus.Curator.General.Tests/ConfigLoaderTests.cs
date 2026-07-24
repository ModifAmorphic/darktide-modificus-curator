using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Establishes the config-loader pattern: JSON overrides are bound onto full
/// defaults, and a missing file yields a fully-usable defaulted config.
/// </summary>
public sealed class ConfigLoaderTests
{
    [Fact]
    public void Load_returns_defaults_when_json_file_is_missing()
    {
        // A path that does not exist; optional:true keeps the loader from throwing.
        var loader = new ConfigLoader(Path.Combine(Path.GetTempPath(), "curator-missing-" + Guid.NewGuid() + ".json"));

        var config = loader.Load();

        Assert.NotNull(config.Logging);
        Assert.Equal("Information", config.Logging.Level);
        Assert.False(string.IsNullOrEmpty(config.Logging.LogFile));
        Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
        Assert.False(string.IsNullOrEmpty(config.ModsFolder));
        Assert.False(string.IsNullOrEmpty(config.RelayDir));
        Assert.EndsWith("profiles", config.ProfilesBaseFolder);
    }

    [Fact]
    public void Logging_RetainedLogFileCount_defaults_to_5_when_absent()
    {
        var loader = new ConfigLoader(Path.Combine(Path.GetTempPath(), "curator-missing-" + Guid.NewGuid() + ".json"));

        Assert.Equal(5, loader.Load().Logging.RetainedLogFileCount);
    }

    [Fact]
    public void Logging_RetainedLogFileCount_round_trips_when_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "RetainedLogFileCount": 3 }
            }
            """);

        try
        {
            var loader = new ConfigLoader(configPath);

            // JSON override binds onto the default.
            Assert.Equal(3, loader.Load().Logging.RetainedLogFileCount);

            // And survives a Save/Load round-trip.
            var config = loader.Load();
            loader.Save(config);
            Assert.Equal(3, loader.Load().Logging.RetainedLogFileCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_applies_json_overrides_onto_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "Level": "Debug" },
              "RelayDir": "/custom/runtime"
            }
            """);

        try
        {
            var config = new ConfigLoader(configPath).Load();

            Assert.Equal("Debug", config.Logging.Level);
            Assert.Equal("/custom/runtime", config.RelayDir);
            // Untouched fields keep their defaults.
            Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_defaults_when_parent_directory_is_missing()
    {
        // First-run case: neither the config directory nor file exist yet.
        // SetBasePath would throw if the loader didn't guard against it.
        var path = Path.Combine(Path.GetTempPath(), "curator-missing-dir-" + Guid.NewGuid(), "config.json");

        var config = new ConfigLoader(path).Load();

        Assert.Equal("Information", config.Logging.Level);
        Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
    }

    [Fact]
    public void Default_config_path_is_under_app_data()
    {
        var path = ConfigLoader.DefaultConfigPath();

        // Windows nests the data root under an org/app hierarchy
        // (ModifAmorphic\Modificus Curator); Linux keeps the flat
        // Modificus Curator segment. The config file sits directly under it.
        var expectedSegment = OperatingSystem.IsWindows()
            ? Path.Combine("ModifAmorphic", "Modificus Curator")
            : "Modificus Curator";
        Assert.EndsWith(Path.Combine(expectedSegment, "config.json"), path);
    }

    // ---- Preferences section -----------------------------------------------

    [Fact]
    public void Load_yields_default_preferences_when_section_is_absent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """{ "Logging": { "Level": "Debug" } }""");

        try
        {
            var prefs = new ConfigLoader(configPath).Load().Preferences;

            Assert.Equal(ThemeMode.System, prefs.Theme);
            Assert.Equal(1.0, prefs.FontScale);
            Assert.Equal("en", prefs.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_applies_json_overrides_onto_default_preferences()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Preferences": {
                "Theme": "Dark",
                "FontScale": 1.25,
                "Language": "fr"
              }
            }
            """);

        try
        {
            var prefs = new ConfigLoader(configPath).Load().Preferences;

            Assert.Equal(ThemeMode.Dark, prefs.Theme);
            Assert.Equal(1.25, prefs.FontScale);
            Assert.Equal("fr", prefs.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_round_trips_preferences_through_a_subsequent_Load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = CuratorConfig.CreateDefault();
            config.Preferences.Theme = ThemeMode.Dark;
            config.Preferences.FontScale = 1.5;
            config.Preferences.Language = "fr";

            loader.Save(config);
            var reloaded = loader.Load();

            Assert.Equal(ThemeMode.Dark, reloaded.Preferences.Theme);
            Assert.Equal(1.5, reloaded.Preferences.FontScale);
            Assert.Equal("fr", reloaded.Preferences.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_creates_the_parent_directory_when_missing()
    {
        // First-run case: neither the config dir nor the file exist yet. Save
        // must create the parent dir + write the file (so the next Load sees it).
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);

            loader.Save(CuratorConfig.CreateDefault());

            Assert.True(File.Exists(configPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_preserves_the_other_sections_alongside_preferences()
    {
        // Save writes the whole CuratorConfig back; verify it does not drop a
        // previously-set field from a sibling section.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "Level": "Warning" },
              "RelayDir": "/custom/runtime"
            }
            """);

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = loader.Load();
            config.Preferences.Theme = ThemeMode.Light;

            loader.Save(config);
            var reloaded = loader.Load();

            Assert.Equal("Warning", reloaded.Logging.Level);
            Assert.Equal("/custom/runtime", reloaded.RelayDir);
            Assert.Equal(ThemeMode.Light, reloaded.Preferences.Theme);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_persists_the_theme_enum_as_a_human_readable_string()
    {
        // The JSON-enum converter writes names (not numbers), so the persisted
        // file is human-readable + stable across enum renumbering.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = CuratorConfig.CreateDefault();
            config.Preferences.Theme = ThemeMode.Dark;

            loader.Save(config);

            var json = File.ReadAllText(configPath);
            Assert.Contains("\"theme\": \"dark\"", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- live read (no startup cache) --------------------------------------

    [Fact]
    public void Load_re_reads_the_file_on_each_call_no_stale_cache()
    {
        // Proves the live-read contract: the same ConfigLoader instance does not
        // cache its first Load(). A subsequent disk write (by the Preferences
        // flow, the upcoming Settings window, or a hand-edit) is visible to the
        // next Load() on the same instance, so every consumer that reads per-op
        // sees the current disk state.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);

            // Seed the file with one value.
            var first = CuratorConfig.CreateDefault();
            first.Preferences.Language = "en";
            loader.Save(first);

            // First Load sees the seeded value.
            Assert.Equal("en", loader.Load().Preferences.Language);

            // Overwrite the file directly (simulating an external writer: the
            // Settings window, a hand-edit, another process).
            File.WriteAllText(configPath, """
                {
                  "Preferences": { "Language": "fr" }
                }
                """);

            // Second Load on the SAME loader instance sees the new value: no
            // startup cache, no stale snapshot.
            Assert.Equal("fr", loader.Load().Preferences.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_picks_up_a_runtime_folder_change_with_no_new_instance()
    {
        // Proves the live-read contract for path-valued fields: a consumer that
        // resolves ProfilesBaseFolder / ModsFolder / RelayDir per-op
        // from the loader sees a runtime change without restarting. This is the
        // property the Settings window relies on.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var first = CuratorConfig.CreateDefault();
            first.ModsFolder = "/first/mods";
            loader.Save(first);
            Assert.Equal("/first/mods", loader.Load().ModsFolder);

            // A runtime config write flips the folder.
            var second = loader.Load();
            second.ModsFolder = "/new/mods";
            loader.Save(second);

            // The next per-op read sees the new folder.
            Assert.Equal("/new/mods", loader.Load().ModsFolder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Discovery section -------------------------------------------------

    [Fact]
    public void Load_yields_default_discovery_when_section_is_absent()
    {
        // Absent Discovery section: all four override fields are null (the
        // "auto-discover everything" default).
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """{ "Logging": { "Level": "Debug" } }""");

        try
        {
            var discovery = new ConfigLoader(configPath).Load().Discovery;

            Assert.Null(discovery.UserSteamInstallPath);
            Assert.Null(discovery.UserDarktideGameBinaryPath);
            Assert.Null(discovery.UserCompatdataPath);
            Assert.Null(discovery.UserProtonBinaryPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_applies_json_overrides_onto_default_discovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Discovery": {
                "UserSteamInstallPath": "/custom/steam",
                "UserDarktideGameBinaryPath": "/custom/darktide.exe",
                "UserCompatdataPath": "/custom/compatdata",
                "UserProtonBinaryPath": "/custom/proton"
              }
            }
            """);

        try
        {
            var discovery = new ConfigLoader(configPath).Load().Discovery;

            Assert.Equal("/custom/steam", discovery.UserSteamInstallPath);
            Assert.Equal("/custom/darktide.exe", discovery.UserDarktideGameBinaryPath);
            Assert.Equal("/custom/compatdata", discovery.UserCompatdataPath);
            Assert.Equal("/custom/proton", discovery.UserProtonBinaryPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_round_trips_discovery_through_a_subsequent_Load()
    {
        // A partially-populated Discovery section round-trips: set fields
        // persist; null fields stay null (no defaulting to empty strings, which
        // would change the overlay semantics).
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = CuratorConfig.CreateDefault();
            config.Discovery.UserSteamInstallPath = "/persisted/steam";
            config.Discovery.UserProtonBinaryPath = "/persisted/proton";
            // Leave the other two null.

            loader.Save(config);
            var reloaded = loader.Load().Discovery;

            Assert.Equal("/persisted/steam", reloaded.UserSteamInstallPath);
            Assert.Null(reloaded.UserDarktideGameBinaryPath);
            Assert.Null(reloaded.UserCompatdataPath);
            Assert.Equal("/persisted/proton", reloaded.UserProtonBinaryPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_preserves_the_other_sections_alongside_discovery()
    {
        // Save writes the whole CuratorConfig; verify it does not drop a sibling
        // field when the Discovery section is populated.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "Level": "Warning" },
              "RelayDir": "/custom/runtime"
            }
            """);

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = loader.Load();
            config.Discovery.UserSteamInstallPath = "/override/steam";

            loader.Save(config);
            var reloaded = loader.Load();

            Assert.Equal("Warning", reloaded.Logging.Level);
            Assert.Equal("/custom/runtime", reloaded.RelayDir);
            Assert.Equal("/override/steam", reloaded.Discovery.UserSteamInstallPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AppUpdates_SourceOverride_defaults_to_null_when_absent()
    {
        // SourceOverride is the optional Velopack update-source override (a local
        // directory path or URL); null (the default) means the production
        // GithubSource. A missing section must leave it null.
        var loader = new ConfigLoader(Path.Combine(Path.GetTempPath(), "curator-missing-" + Guid.NewGuid() + ".json"));

        var appUpdates = loader.Load().AppUpdates;

        Assert.Null(appUpdates.SourceOverride);
        // CheckOnStartup keeps its default too, so the section is fully usable.
        Assert.True(appUpdates.CheckOnStartup);
    }

    [Fact]
    public void Save_round_trips_app_updates_SourceOverride_through_a_subsequent_Load()
    {
        // SourceOverride must bind from JSON and survive a Save/Load round-trip
        // (the local-testing / self-hosted-feed knob; set in config.json under
        // AppUpdates). A string field with no special converter auto-serializes.
        var dir = Path.Combine(Path.GetTempPath(), "curator-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "AppUpdates": { "SourceOverride": "/local/releases" }
            }
            """);

        try
        {
            var loader = new ConfigLoader(configPath);

            // JSON override binds onto the default.
            Assert.Equal("/local/releases", loader.Load().AppUpdates.SourceOverride);

            // And survives a Save/Load round-trip.
            var config = loader.Load();
            loader.Save(config);
            Assert.Equal("/local/releases", loader.Load().AppUpdates.SourceOverride);

            // Clearing it writes back null.
            config.AppUpdates.SourceOverride = null;
            loader.Save(config);
            Assert.Null(loader.Load().AppUpdates.SourceOverride);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
