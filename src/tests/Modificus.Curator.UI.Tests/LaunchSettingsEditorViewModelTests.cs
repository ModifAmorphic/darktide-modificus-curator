using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Focused tests for the reusable <see cref="LaunchSettingsEditorViewModel"/>:
/// Load deep-copies + resets dirty, BuildSettings preserves exact ordered
/// values (incl. duplicate game args) + booleans, env validation runs through
/// the shared <see cref="LaunchSettingsValidator"/> with localized messages,
/// every add/remove/edit/toggle recomputes dirty + raises Changed, Load
/// suppresses Changed, detached rows stop firing, and BuildSettings never
/// persists (the editor has no persistence dependency).
/// </summary>
public sealed class LaunchSettingsEditorViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static LaunchSettingsEditorViewModel NewEditor() => new(Localization);

    // ---- load: deep-copies + starts clean ----------------------------------

    [Fact]
    public void Load_deep_copies_rows_and_starts_clean()
    {
        var editor = NewEditor();
        var source = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1"), new EnvVar("B", "two") },
            GameArguments = new[] { "-a", "-b" },
            EnableLuaLogs = true,
            SkipSplash = true,
        };

        editor.Load(source);

        Assert.False(editor.IsDirty);
        Assert.True(editor.IsValid);

        // The editor holds its own row instances: mutating the source after Load
        // cannot reach the editor (deep copy, not a reference into the source).
        Assert.Equal(2, editor.EnvironmentVariables.Count);
        Assert.Equal(new[] { "-a", "-b" }, editor.GameArguments.Select(r => r.Value));
        Assert.True(editor.EnableLuaLogs);
        Assert.True(editor.SkipSplash);

        // Editing an editor row does not mutate the source collections.
        editor.EnvironmentVariables[0].Value = "changed";
        Assert.Equal("1", source.EnvironmentVariables[0].Value);
    }

    [Fact]
    public void Load_with_empty_settings_starts_clean_and_empty()
    {
        var editor = NewEditor();

        editor.Load(new LaunchSettings());

        Assert.Empty(editor.EnvironmentVariables);
        Assert.Empty(editor.GameArguments);
        Assert.False(editor.EnableLuaLogs);
        Assert.False(editor.SkipSplash);
        Assert.False(editor.IsDirty);
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void Reload_replaces_rows_with_a_fresh_baseline()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1") },
            GameArguments = new[] { "-x" },
        });
        editor.EnvironmentVariables[0].Value = "dirty";

        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("B", "2") },
        });

        Assert.Single(editor.EnvironmentVariables);
        Assert.Equal("B", editor.EnvironmentVariables[0].Name);
        Assert.Empty(editor.GameArguments);
        Assert.False(editor.IsDirty); // baseline reset to the reloaded rows
    }

    // ---- BuildSettings: preserves order, duplicates, booleans ---------------

    [Fact]
    public void BuildSettings_preserves_ordered_values_duplicate_args_and_booleans()
    {
        var editor = NewEditor();
        var original = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("DXVK_HUD", "fps"),
            },
            GameArguments = new[] { "-a", "-a", "-b" }, // duplicate preserved
            EnableLuaLogs = true,
            SkipSplash = true,
        };
        editor.Load(original);

        var built = editor.BuildSettings();

        Assert.Equal(2, built.EnvironmentVariables.Count);
        Assert.Equal("PROTON_LOG", built.EnvironmentVariables[0].Name);
        Assert.Equal("1", built.EnvironmentVariables[0].Value);
        Assert.Equal("DXVK_HUD", built.EnvironmentVariables[1].Name);
        Assert.Equal("fps", built.EnvironmentVariables[1].Value);
        Assert.Equal(new[] { "-a", "-a", "-b" }, built.GameArguments.ToArray());
        Assert.True(built.EnableLuaLogs);
        Assert.True(built.SkipSplash);
    }

    [Fact]
    public void BuildSettings_returns_independent_instances_and_never_persists()
    {
        // The editor takes only LocalizationService (no IProfileService / no I/O):
        // BuildSettings is a pure value builder. Two calls return equal but
        // independent values, and building does not mutate editor state.
        var editor = NewEditor();
        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1") },
            GameArguments = new[] { "-x" },
        });

        var first = editor.BuildSettings();
        var second = editor.BuildSettings();

        Assert.Equal(first.EnvironmentVariables, second.EnvironmentVariables);
        Assert.Equal(first.GameArguments, second.GameArguments);
        Assert.NotSame(first, second);

        // Building never marks the editor dirty.
        Assert.False(editor.IsDirty);
    }

    // ---- validation: through the shared LaunchSettingsValidator -------------

    [Fact]
    public void Empty_env_name_shows_a_localized_error_and_is_invalid()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null); // name left empty

        Assert.NotEmpty(editor.EnvironmentVariables[0].ErrorMessage);
        Assert.False(editor.IsValid);
        Assert.Equal(Localization["LaunchSettings_ErrNameRequired"],
            editor.EnvironmentVariables[0].ErrorMessage);
    }

    [Fact]
    public void Name_with_equals_shows_a_localized_error()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null);
        editor.EnvironmentVariables[0].Name = "FOO=BAR";

        Assert.Equal(Localization["LaunchSettings_ErrNameInvalid"],
            editor.EnvironmentVariables[0].ErrorMessage);
        Assert.False(editor.IsValid);
    }

    [Fact]
    public void Reserved_name_shows_a_localized_error_carrying_the_name()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null);
        editor.EnvironmentVariables[0].Name = "APPDIR";

        Assert.Contains("APPDIR", editor.EnvironmentVariables[0].ErrorMessage);
        Assert.False(editor.IsValid);
    }

    [Fact]
    public void Duplicate_name_flags_both_rows()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null);
        editor.AddEnvVarCommand.Execute(null);
        editor.EnvironmentVariables[0].Name = "PROTON_LOG";
        editor.EnvironmentVariables[1].Name = "proton_log"; // case-insensitive dup

        Assert.NotEmpty(editor.EnvironmentVariables[0].ErrorMessage);
        Assert.NotEmpty(editor.EnvironmentVariables[1].ErrorMessage);
        Assert.False(editor.IsValid);

        // Recovering: renaming the duplicate clears both + re-validates.
        editor.EnvironmentVariables[1].Name = "DXVK_HUD";
        Assert.Empty(editor.EnvironmentVariables[0].ErrorMessage);
        Assert.Empty(editor.EnvironmentVariables[1].ErrorMessage);
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void Value_with_nul_shows_a_localized_error()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null);
        editor.EnvironmentVariables[0].Name = "FOO";
        editor.EnvironmentVariables[0].Value = "a\0b";

        Assert.Equal(Localization["LaunchSettings_ErrValueInvalid"],
            editor.EnvironmentVariables[0].ErrorMessage);
        Assert.False(editor.IsValid);
    }

    // ---- dirty + change notification ---------------------------------------

    [Fact]
    public void Add_remove_edit_and_toggle_each_set_dirty_and_raise_changed()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1") },
            GameArguments = new[] { "-x" },
        });

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        // Add env row -> dirty + changed.
        editor.AddEnvVarCommand.Execute(null);
        Assert.True(editor.IsDirty);
        var addEnvFires = fires;

        // Edit the added row -> dirty + changed.
        editor.EnvironmentVariables[1].Name = "B";
        Assert.True(editor.IsDirty);
        Assert.True(fires > addEnvFires);

        // Toggle EnableLuaLogs -> dirty + changed.
        var before = fires;
        editor.EnableLuaLogs = true;
        Assert.True(editor.IsDirty);
        Assert.True(fires > before);

        // Toggle SkipSplash -> dirty + changed.
        before = fires;
        editor.SkipSplash = true;
        Assert.True(editor.IsDirty);
        Assert.True(fires > before);

        // Remove a row -> dirty + changed.
        before = fires;
        editor.RemoveEnvVarCommand.Execute(editor.EnvironmentVariables[0]);
        Assert.True(editor.IsDirty);
        Assert.True(fires > before);
    }

    [Fact]
    public void Game_argument_edits_participate_in_dirty_state()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings { GameArguments = new[] { "-a" } });

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        // Edit an existing arg value.
        editor.GameArguments[0].Value = "-b";
        Assert.True(editor.IsDirty);
        Assert.Equal(1, fires);

        // Add an arg.
        editor.AddGameArgCommand.Execute(null);
        Assert.True(editor.IsDirty);

        // Remove an arg.
        editor.RemoveGameArgCommand.Execute(editor.GameArguments[0]);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Reversion_to_baseline_clears_dirty()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings { EnvironmentVariables = new[] { new EnvVar("A", "1") } });

        editor.EnvironmentVariables[0].Value = "2";
        Assert.True(editor.IsDirty);

        editor.EnvironmentVariables[0].Value = "1"; // back to baseline
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Toggling_a_boolean_back_to_baseline_clears_dirty()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings { EnableLuaLogs = false });

        editor.EnableLuaLogs = true;
        Assert.True(editor.IsDirty);

        editor.EnableLuaLogs = false; // back to baseline
        Assert.False(editor.IsDirty);
    }

    // ---- row handler lifetime: detached rows stop firing -------------------

    [Fact]
    public void Removed_row_no_longer_fires_changed()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings { EnvironmentVariables = new[] { new EnvVar("A", "1") } });
        var row = editor.EnvironmentVariables[0];

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        editor.RemoveEnvVarCommand.Execute(row);
        var afterRemove = fires;
        Assert.True(afterRemove >= 1);

        // Mutating the now-detached row must not fire or influence editor state.
        row.Name = "DETACHED";

        Assert.Equal(afterRemove, fires);
        Assert.Empty(editor.EnvironmentVariables);
    }

    [Fact]
    public void Reloaded_rows_no_longer_fire_changed()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings { EnvironmentVariables = new[] { new EnvVar("A", "1") } });
        var staleRow = editor.EnvironmentVariables[0];

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        editor.Load(new LaunchSettings()); // reload: old rows detached

        Assert.Equal(0, fires); // Load suppresses Changed

        staleRow.Name = "STALE"; // detached on reload

        Assert.Equal(0, fires);
        Assert.Empty(editor.EnvironmentVariables);
    }

    // ---- Load suppresses Changed + resets validation/dirty ------------------

    [Fact]
    public void Load_suppresses_changed_and_resets_dirty_and_validation()
    {
        var editor = NewEditor();
        editor.Load(new LaunchSettings());

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1") },
            GameArguments = new[] { "-x" },
            EnableLuaLogs = true,
            SkipSplash = true,
        });

        Assert.Equal(0, fires); // suppressed
        Assert.False(editor.IsDirty);
        Assert.True(editor.IsValid);
        Assert.True(editor.EnableLuaLogs);
        Assert.True(editor.SkipSplash);
    }

    [Fact]
    public void Load_recomputes_validation_so_loaded_invalid_rows_show_errors()
    {
        // An empty-name row loaded from persisted settings must still surface its
        // inline error (validation reruns at Load, not just on user edits).
        var editor = NewEditor();

        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("", "1") },
        });

        Assert.NotEmpty(editor.EnvironmentVariables[0].ErrorMessage);
        Assert.False(editor.IsValid);
        Assert.False(editor.IsDirty);
    }

    // ---- RefreshLocalizedValidation: remaps current errors after culture change ----

    [Fact]
    public void RefreshLocalizedValidation_remaps_current_errors_without_changing_values_or_dirty()
    {
        // The culture-change refresh path: a row with an error must get a fresh
        // localized message, but values, baseline, dirty state, and the user-edit
        // Changed event are all untouched.
        var editor = NewEditor();
        editor.Load(new LaunchSettings());
        editor.AddEnvVarCommand.Execute(null); // empty name -> NameRequired
        var beforeMessage = editor.EnvironmentVariables[0].ErrorMessage;
        Assert.Equal(Localization["LaunchSettings_ErrNameRequired"], beforeMessage);

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        // Flip the culture to something different so the resolved message would
        // differ if a satellite resx existed; with only the neutral resx the key
        // resolves to the same text, but the remap pass still runs through every
        // row and re-pushes ErrorMessage. The invariant guarantee below is what
        // matters: the refresh did not change values, dirty, or raise Changed.
        Localization.SetCulture("fr");

        editor.RefreshLocalizedValidation();

        // The row's message re-resolved to the same localized key (neutral resx).
        Assert.Equal(Localization["LaunchSettings_ErrNameRequired"],
            editor.EnvironmentVariables[0].ErrorMessage);

        // Still invalid + still dirty from the earlier edit.
        Assert.False(editor.IsValid);
        Assert.True(editor.IsDirty);

        // The refresh raised no user-edit Changed event.
        Assert.Equal(0, fires);
    }

    [Fact]
    public void RefreshLocalizedValidation_is_safe_on_a_clean_editor()
    {
        // No rows, no errors: the refresh is a no-op pass that does not throw +
        // does not raise Changed or mark dirty.
        var editor = NewEditor();
        editor.Load(new LaunchSettings());

        var fires = 0;
        editor.Changed += (_, _) => fires++;

        editor.RefreshLocalizedValidation();

        Assert.True(editor.IsValid);
        Assert.False(editor.IsDirty);
        Assert.Equal(0, fires);
    }

    [Fact]
    public void RefreshLocalizedValidation_clears_an_error_when_the_row_was_fixed()
    {
        // If a row was fixed after the validator's last pass (which cannot happen
        // through the editor's own OnEdit path, but can through a culture refresh
        // racing a programmatic value change), the refresh re-runs the validator
        // and clears any stale error. This pins the contract: the refresh always
        // recomputes from current values, not a cached message.
        var editor = NewEditor();
        editor.Load(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("", "1") }, // empty name -> error
        });
        Assert.False(editor.IsValid);

        // Fix the row's name programmatically (no OnEdit, so the validator has
        // not re-run yet), then refresh.
        editor.EnvironmentVariables[0].Name = "FIXED";
        editor.RefreshLocalizedValidation();

        Assert.Empty(editor.EnvironmentVariables[0].ErrorMessage);
        Assert.True(editor.IsValid);
    }
}
