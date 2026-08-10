namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Tests for the shared <see cref="LaunchSettingsValidator"/>: the structured
/// shape of its result (index / kind / field), the per-kind verdicts, and a
/// parameterized <b>agreement test</b> that feeds the SAME launch-settings
/// inputs through both the service's verdict (does
/// <see cref="IProfileService.UpdateProfile"/> throw for the launch settings?)
/// and the validator's verdict (any errors?) and asserts they agree across
/// valid + every invalid case. The agreement test is the regression guard
/// against the two consumers drifting again.
/// </summary>
public sealed class LaunchSettingsValidatorTests
{
    // ---- structured shape + verdicts ---------------------------------------

    [Fact]
    public void Validate_empty_settings_returns_no_errors()
    {
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings());

        Assert.Empty(errors);
        Assert.True(LaunchSettingsValidator.IsValid(new LaunchSettings()));
    }

    [Fact]
    public void Validate_valid_entries_return_no_errors()
    {
        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("DXVK_HUD", "fps,frametime"),
            },
            GameArguments = new[] { "-windowed", "-borderless" },
        };

        Assert.Empty(LaunchSettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_reports_empty_name_on_the_right_index_and_field()
    {
        var settings = new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("GOOD", "1"),
                new EnvVar("   ", "2"), // whitespace -> empty
            },
        };

        var only = Assert.Single(LaunchSettingsValidator.Validate(settings));
        Assert.Equal(1, only.Index);
        Assert.Equal(LaunchSettingsValidationErrorKind.NameEmpty, only.Kind);
        Assert.Equal(LaunchSettingsErrorField.Name, only.Field);
        Assert.Equal(string.Empty, only.Name);
    }

    [Fact]
    public void Validate_reports_invalid_name_for_equals_or_nul()
    {
        var equalsErrors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("FOO=BAR", "1") },
        });
        var equals = Assert.Single(equalsErrors);
        Assert.Equal(LaunchSettingsValidationErrorKind.NameInvalid, equals.Kind);
        Assert.Equal(LaunchSettingsErrorField.Name, equals.Field);

        var nulErrors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("FOO\0BAR", "1") },
        });
        Assert.Equal(LaunchSettingsValidationErrorKind.NameInvalid, Assert.Single(nulErrors).Kind);
    }

    [Fact]
    public void Validate_reports_value_nul_with_the_value_field()
    {
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("FOO", "a\0b") },
        });

        var only = Assert.Single(errors);
        Assert.Equal(LaunchSettingsValidationErrorKind.ValueNul, only.Kind);
        Assert.Equal(LaunchSettingsErrorField.Value, only.Field);
        Assert.Equal("FOO", only.Name);
    }

    [Fact]
    public void Validate_reports_reserved_name_case_insensitively()
    {
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[] { new EnvVar("appdir", "x") }, // lowercase reserved
        });

        var only = Assert.Single(errors);
        Assert.Equal(LaunchSettingsValidationErrorKind.NameReserved, only.Kind);
        Assert.Equal("appdir", only.Name);
    }

    [Fact]
    public void Validate_reports_a_duplicate_on_every_colliding_entry()
    {
        // Every entry that participates in a case-insensitive collision is
        // flagged, not just the later one, so the UI can flag every row
        // involved. The service, which throws on the first error, surfaces the
        // first colliding entry.
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("PROTON_LOG", "1"),
                new EnvVar("proton_log", "2"),
                new EnvVar("PROTON_LOG", "3"),
            },
        });

        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Equal(LaunchSettingsValidationErrorKind.NameDuplicate, e.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, errors.Select(e => e.Index).ToArray());
    }

    [Fact]
    public void Validate_reports_at_most_one_error_per_entry_in_precedence_order()
    {
        // A name that is BOTH reserved AND a duplicate: reserved wins (it is
        // checked before duplicate), so both entries get NameReserved, not
        // NameDuplicate.
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("APPDIR", "1"),
                new EnvVar("APPDIR", "2"),
            },
        });

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(LaunchSettingsValidationErrorKind.NameReserved, e.Kind));
    }

    [Fact]
    public void Validate_returns_errors_in_entry_order()
    {
        var errors = LaunchSettingsValidator.Validate(new LaunchSettings
        {
            EnvironmentVariables = new[]
            {
                new EnvVar("", "1"),       // 0: empty
                new EnvVar("GOOD", "2"),   // 1: valid
                new EnvVar("BAR", "x\0"),  // 2: value NUL
            },
        });

        Assert.Equal(new[] { 0, 2 }, errors.Select(e => e.Index).ToArray());
    }

    [Fact]
    public void Validate_ignores_game_arguments()
    {
        // Game arguments need no validation; any string (incl. '=' or empty) is a
        // legal argv value, so game args never produce errors.
        var settings = new LaunchSettings
        {
            GameArguments = new[] { "-flag=value", "", "--width=1920" },
        };

        Assert.Empty(LaunchSettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_rejects_null_settings()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchSettingsValidator.Validate(null!));
    }

    [Theory]
    [MemberData(nameof(AgreementCases))]
    public void Validator_and_service_agree_on_every_case(
        LaunchSettings settings, bool expectValid, string description)
    {
        // The validator's verdict (any errors?) must agree with the service's
        // verdict (does UpdateProfile throw an ArgumentException for the launch
        // settings?) across valid + every invalid case. This is the regression
        // guard against the two consumers drifting again. (The description
        // parameter carries the case label for the test runner's display name.)
        _ = description; // label only; surfaced by the theory display name.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var profileId = profile.Id;

        var validatorSaysValid = LaunchSettingsValidator.IsValid(settings);
        var serviceSaysValid = TryApplyLaunchSettings(fx.Service, profileId, settings);

        Assert.Equal(expectValid, validatorSaysValid);
        Assert.Equal(expectValid, serviceSaysValid);
        Assert.Equal(validatorSaysValid, serviceSaysValid);
    }

    /// <summary>
    /// Drives <see cref="IProfileService.UpdateProfile"/> with the profile's
    /// current name + description + the supplied launch settings, and returns
    /// whether it accepted the settings (no <see cref="ArgumentException"/>).
    /// <see cref="ArgumentNullException"/> (null settings) is not a validation
    /// verdict and rethrows; any other exception rethrows too.
    /// </summary>
    private static bool TryApplyLaunchSettings(IProfileService service, Guid profileId, LaunchSettings settings)
    {
        try
        {
            var current = service.GetProfile(profileId);
            service.UpdateProfile(profileId, current.Name, current.Description, settings);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The agreement cases: each entry is <c>(settings, expectValid)</c>.
    /// Covers valid inputs + every invalid kind (empty, =, NUL name, NUL value,
    /// duplicate same/different case, reserved exact/mixed-case) + combinations.
    /// </summary>
    public static IEnumerable<object[]> AgreementCases =>
        new List<object[]>
        {
            // ---- valid ----
            Case(new LaunchSettings(), expectValid: true, "empty settings"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("PROTON_LOG", "1") },
            }, expectValid: true, "one env var"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("PROTON_LOG", "1"),
                    new EnvVar("DXVK_HUD", "fps"),
                },
            }, expectValid: true, "two distinct env vars"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("WITH_SPACES", "a value with spaces"),
                    new EnvVar("EMPTY_VAL", ""),
                },
            }, expectValid: true, "spaces + empty value (legal, stored exactly)"),
            Case(new LaunchSettings
            {
                GameArguments = new[] { "-windowed", "-borderless" },
            }, expectValid: true, "game args only"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("PROTON_LOG", "1"),
                    new EnvVar("dxvk_hud", "fps"), // distinct name, mixed case, not a dup
                },
                GameArguments = new[] { "-flag=value" }, // '=' is fine in a game arg
            }, expectValid: true, "env + game args; mixed-case distinct names"),

            // ---- invalid: name ----
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("", "1") },
            }, expectValid: false, "empty name"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("   ", "1") },
            }, expectValid: false, "whitespace name"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO=BAR", "1") },
            }, expectValid: false, "name with ="),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO\0BAR", "1") },
            }, expectValid: false, "name with NUL"),

            // ---- invalid: value ----
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("FOO", "a\0b") },
            }, expectValid: false, "value with NUL"),

            // ---- invalid: duplicate ----
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("FOO", "1"),
                    new EnvVar("FOO", "2"),
                },
            }, expectValid: false, "duplicate same case"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("PROTON_LOG", "1"),
                    new EnvVar("proton_log", "2"),
                },
            }, expectValid: false, "duplicate different case"),

            // ---- invalid: reserved ----
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("APPDIR", "x") },
            }, expectValid: false, "reserved name exact"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[] { new EnvVar("steam_compat_data_path", "x") },
            }, expectValid: false, "reserved name mixed case"),

            // ---- invalid: combinations ----
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("GOOD", "1"),
                    new EnvVar("", "2"), // empty name in position 1
                },
            }, expectValid: false, "one valid + one empty-name"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("APPDIR", "1"),  // reserved (also a duplicate)
                    new EnvVar("APPDIR", "2"),
                },
            }, expectValid: false, "reserved + duplicate collision"),
            Case(new LaunchSettings
            {
                EnvironmentVariables = new[]
                {
                    new EnvVar("", "1"),        // empty name
                    new EnvVar("A=b", "2"),     // invalid name
                    new EnvVar("GOOD", "x\0"),  // value NUL
                },
            }, expectValid: false, "multiple distinct errors"),
        };

    /// <summary>Wraps a case for <see cref="AgreementCases"/> with a description.</summary>
    private static object[] Case(LaunchSettings settings, bool expectValid, string description) =>
        new object[] { settings, expectValid, description };
}
