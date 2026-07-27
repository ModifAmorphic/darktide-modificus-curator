using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Launch-settings modal VM behaviors: existing settings load into rows, add /
/// remove works for both sections, inline localized validation tracks the
/// current state (empty / <c>=</c> / NUL / duplicate / reserved), Save persists
/// once via <c>SetLaunchSettings</c> and closes only on success (SaveResult),
/// and an invalid input keeps the modal open. All against the pure VM (no
/// window) using the in-memory <see cref="FakeProfileService"/>.
/// </summary>
public sealed class LaunchSettingsViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static LaunchSettingsViewModel Build(
        FakeProfileService? profiles = null,
        Guid? profileId = null)
    {
        profiles ??= TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profileId ?? profiles.ListProfiles().First().Id;
        return new LaunchSettingsViewModel(id, profiles, Localization);
    }

    // ---- load ---------------------------------------------------------------

    [Fact]
    public void Construction_loads_no_rows_for_a_profile_with_empty_settings()
    {
        var vm = Build();

        Assert.Empty(vm.EnvironmentVariables);
        Assert.Empty(vm.GameArguments);
    }

    [Fact]
    public void Construction_loads_existing_settings_into_rows()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.SetLaunchSettings(id, new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("DXVK_HUD", "fps"),
            },
            GameArguments = new[] { "-windowed", "-borderless" },
        });

        var vm = Build(profiles, id);

        Assert.Equal(2, vm.EnvironmentVariables.Count);
        Assert.Equal("PROTON_LOG", vm.EnvironmentVariables[0].Name);
        Assert.Equal("1", vm.EnvironmentVariables[0].Value);
        Assert.Equal("DXVK_HUD", vm.EnvironmentVariables[1].Name);
        Assert.Equal("fps", vm.EnvironmentVariables[1].Value);
        Assert.Equal(new[] { "-windowed", "-borderless" },
            vm.GameArguments.Select(r => r.Value).ToArray());
    }

    // ---- add / remove rows --------------------------------------------------

    [Fact]
    public void AddEnvVar_appends_an_empty_row()
    {
        var vm = Build();

        vm.AddEnvVarCommand.Execute(null);

        var row = Assert.Single(vm.EnvironmentVariables);
        Assert.Equal(string.Empty, row.Name);
        Assert.Equal(string.Empty, row.Value);
    }

    [Fact]
    public void RemoveEnvVar_removes_the_row()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.SetLaunchSettings(id, new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1"), new EnvVar("B", "2") },
        });
        var vm = Build(profiles, id);
        var first = vm.EnvironmentVariables[0];

        vm.RemoveEnvVarCommand.Execute(first);

        Assert.Single(vm.EnvironmentVariables);
        Assert.Equal("B", vm.EnvironmentVariables[0].Name);
    }

    [Fact]
    public void AddGameArg_appends_an_empty_row()
    {
        var vm = Build();

        vm.AddGameArgCommand.Execute(null);

        var row = Assert.Single(vm.GameArguments);
        Assert.Equal(string.Empty, row.Value);
    }

    [Fact]
    public void RemoveGameArg_removes_the_row()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.SetLaunchSettings(id, new LaunchSettings
        {
            GameArguments = new[] { "-a", "-b" },
        });
        var vm = Build(profiles, id);
        var first = vm.GameArguments[0];

        vm.RemoveGameArgCommand.Execute(first);

        Assert.Single(vm.GameArguments);
        Assert.Equal("-b", vm.GameArguments[0].Value);
    }

    // ---- save: persists once + closes only on success -----------------------

    [Fact]
    public void Save_persists_once_via_SetLaunchSettings_and_sets_SaveResult()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";
        vm.EnvironmentVariables[0].Value = "1";
        vm.AddGameArgCommand.Execute(null);
        vm.GameArguments[0].Value = "-windowed";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.SaveResult);
        var (savedId, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.Equal(id, savedId);
        var env = Assert.Single(saved.EnvironmentVariables);
        Assert.Equal("PROTON_LOG", env.Name);
        Assert.Equal("1", env.Value);
        Assert.Equal("-windowed", Assert.Single(saved.GameArguments));
    }

    [Fact]
    public void Save_result_is_false_until_a_successful_save()
    {
        var vm = Build();

        Assert.False(vm.SaveResult);
    }

    [Fact]
    public void Save_surfaces_a_generic_error_when_the_service_rejects_the_settings()
    {
        // Defense-in-depth: the inline pass should have caught any violation
        // (the Save button is disabled while invalid), so the service rejecting
        // a valid-looking input means the two validators diverged. The surfaced
        // message must be a generic, localized "save failed" -- never the
        // rule-specific ErrNameInvalid (which would be actively misleading for,
        // say, a reserved-name or duplicate cause) and never the raw service
        // message (non-localized English). The modal stays open (SaveResult
        // false) and the call reached the service.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.SetLaunchSettingsThrows = new ArgumentException("reserved name APPDIR");
        var vm = Build(profiles, id);
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";
        vm.EnvironmentVariables[0].Value = "1";
        Assert.True(vm.CanSave); // inline-valid, so Save is enabled

        vm.SaveCommand.Execute(null);

        Assert.False(vm.SaveResult); // modal stays open
        Assert.Single(profiles.SetLaunchSettingsCalls); // it tried + the service threw
        Assert.False(string.IsNullOrEmpty(vm.SaveError)); // a message surfaced
        Assert.NotEqual(Localization["LaunchSettings_ErrNameInvalid"], vm.SaveError);
        Assert.Equal(Localization["LaunchSettings_ErrSaveFailed"], vm.SaveError);
    }

    [Fact]
    public void An_empty_profile_saves_empty_settings()
    {
        // A profile with no rows is a valid (empty) save: clears any prior
        // settings + closes.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        // Seed directly through the store (bypasses the recorded call list) so
        // the assertion counts only the VM's save.
        profiles.LaunchSettingsByProfile[id] = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("OLD", "1") },
        };
        var vm = Build(profiles, id);
        // Remove the loaded row + save empty.
        vm.RemoveEnvVarCommand.Execute(vm.EnvironmentVariables[0]);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.SaveResult);
        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.Empty(saved.EnvironmentVariables);
    }

    [Fact]
    public void Game_arg_with_spaces_round_trips_through_save()
    {
        // Values with spaces are stored exactly; Relay owns the final quoting.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);
        vm.AddGameArgCommand.Execute(null);
        vm.GameArguments[0].Value = "an arg with spaces";

        vm.SaveCommand.Execute(null);

        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.Equal("an arg with spaces", Assert.Single(saved.GameArguments));
    }

    // ---- enable-lua-logs toggle: load + persist ----------------------------

    [Fact]
    public void Construction_loads_enable_lua_logs_from_the_profile()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.LaunchSettingsByProfile[id] = new LaunchSettings { EnableLuaLogs = true };

        var vm = Build(profiles, id);

        Assert.True(vm.EnableLuaLogs);
    }

    [Fact]
    public void Flipping_enable_lua_logs_is_persisted_on_save()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);
        vm.EnableLuaLogs = true;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.SaveResult);
        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.True(saved.EnableLuaLogs);
    }

    [Fact]
    public void Enable_lua_logs_defaults_to_false_and_persists_false()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);

        Assert.False(vm.EnableLuaLogs);

        vm.SaveCommand.Execute(null);

        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.False(saved.EnableLuaLogs);
    }

    // ---- skip-splash toggle: load + persist --------------------------------

    [Fact]
    public void Construction_loads_skip_splash_from_the_profile()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        profiles.LaunchSettingsByProfile[id] = new LaunchSettings { SkipSplash = true };

        var vm = Build(profiles, id);

        Assert.True(vm.SkipSplash);
    }

    [Fact]
    public void Flipping_skip_splash_is_persisted_on_save()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);
        vm.SkipSplash = true;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.SaveResult);
        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.True(saved.SkipSplash);
    }

    [Fact]
    public void Skip_splash_defaults_to_false_and_persists_false()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);

        Assert.False(vm.SkipSplash);

        vm.SaveCommand.Execute(null);

        var (_, saved) = Assert.Single(profiles.SetLaunchSettingsCalls);
        Assert.False(saved.SkipSplash);
    }

    // ---- validation: inline localized errors keep the modal open ------------

    [Fact]
    public void CanSave_is_true_for_valid_rows()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";
        vm.EnvironmentVariables[0].Value = "1";

        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void An_empty_env_name_shows_a_localized_error_and_blocks_save()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var vm = Build(profiles);
        vm.AddEnvVarCommand.Execute(null);
        // Name left empty.

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Save does not persist on an invalid state.
        vm.SaveCommand.Execute(null);
        Assert.False(vm.SaveResult);
        Assert.Empty(profiles.SetLaunchSettingsCalls);
    }

    [Fact]
    public void A_name_with_equals_shows_a_localized_error_and_blocks_save()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "FOO=BAR";

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void A_name_with_nul_shows_a_localized_error_and_blocks_save()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "FOO\0BAR";

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void A_value_with_nul_shows_a_localized_error_and_blocks_save()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "FOO";
        vm.EnvironmentVariables[0].Value = "a\0b";

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void A_duplicate_name_shows_a_localized_error_on_both_rows_and_blocks_save()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";
        vm.EnvironmentVariables[1].Name = "proton_log"; // case-insensitive dup

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.NotEmpty(vm.EnvironmentVariables[1].ErrorMessage);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void A_reserved_name_shows_a_localized_error_and_blocks_save()
    {
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "STEAM_COMPAT_DATA_PATH";

        Assert.NotEmpty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Clearing_a_duplicate_recovers_to_valid_live()
    {
        // Live validation: fixing the duplicate clears the error + re-enables save.
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";
        vm.EnvironmentVariables[1].Name = "proton_log";
        Assert.False(vm.CanSave);

        vm.EnvironmentVariables[1].Name = "DXVK_HUD";

        Assert.Empty(vm.EnvironmentVariables[0].ErrorMessage);
        Assert.Empty(vm.EnvironmentVariables[1].ErrorMessage);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Reserved_name_error_message_carries_the_offending_name()
    {
        // The localized reserved-name error is parameterized with the name.
        var vm = Build();
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "APPDIR";

        Assert.Contains("APPDIR", vm.EnvironmentVariables[0].ErrorMessage);
    }

    // ---- cancel makes no change ---------------------------------------------

    [Fact]
    public void Cancel_does_not_persist_and_keeps_SaveResult_false()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        var vm = Build(profiles, id);
        vm.AddEnvVarCommand.Execute(null);
        vm.EnvironmentVariables[0].Name = "PROTON_LOG";

        vm.CancelCommand.Execute(null);

        Assert.False(vm.SaveResult);
        Assert.Empty(profiles.SetLaunchSettingsCalls);
    }

    [Fact]
    public void No_save_call_is_made_when_only_loading_existing_settings()
    {
        // Construction loads but never persists. The fake records SetLaunchSettings
        // calls; none should appear just from constructing the VM.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "P"));
        var id = profiles.ListProfiles().First().Id;
        // Seed directly through the store (bypasses the recorded call list).
        profiles.LaunchSettingsByProfile[id] = new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("A", "1") },
        };

        var vm = Build(profiles, id);

        Assert.Empty(profiles.SetLaunchSettingsCalls);
        Assert.Single(vm.EnvironmentVariables);
    }
}
