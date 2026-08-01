using System.Globalization;
using Modificus.Curator.Config;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="PreferencesService"/>: applying a triple read-modify-saves it
/// through the config loader (load the live snapshot, overwrite the Preferences
/// section, save), and switches the <see cref="LocalizationService"/> culture.
/// The theme + font-scale apply paths target Avalonia's <c>Application.Current</c>,
/// which is <c>null</c> outside a running app; both are guarded no-ops in that
/// case (the test asserts what is testable here; the visual apply is exercised
/// by the operator's visual review).
/// </summary>
public sealed class PreferencesServiceTests
{
    private static readonly LocalizationService Localization = new();

    private static (PreferencesService svc, FakeConfigLoader loader, CuratorConfig config) Build()
    {
        // The fake's Load() returns the same mutable Config object, so the
        // read-modify-save in ApplyAndPersist reads it, mutates the Preferences
        // section, and Save captures it. A real ConfigLoader on a temp path
        // would also work (and proves the disk round-trip; see ConfigLoaderTests).
        var config = CuratorConfig.CreateDefault();
        var loader = new FakeConfigLoader { Config = config };
        var svc = new PreferencesService(
            loader,
            Localization,
            NullLogger<PreferencesService>.Instance);
        return (svc, loader, config);
    }

    [Fact]
    public void ApplyAndPersist_switches_the_localization_culture()
    {
        var (svc, _, _) = Build();

        svc.ApplyAndPersist(ThemeMode.Dark, 1.0, "fr", showRelayConsole: false);

        Assert.Equal("fr", Localization.Culture.Name);
    }

    [Fact]
    public void ApplyAndPersist_blank_language_resolves_to_invariant()
    {
        var (svc, _, _) = Build();

        svc.ApplyAndPersist(ThemeMode.System, 1.0, "", showRelayConsole: false);

        Assert.Equal(CultureInfo.InvariantCulture, Localization.Culture);
    }

    [Fact]
    public void ApplyAndPersist_unknown_language_does_not_throw()
    {
        // CultureInfo.GetCultureInfo behavior for unknown names varies by
        // platform (Linux ICU may synthesize a culture instead of throwing);
        // the contract is the service never crashes on a bad language and the
        // indexer keeps resolving to the neutral resx.
        var (svc, _, _) = Build();

        var ex = Record.Exception(() => svc.ApplyAndPersist(ThemeMode.System, 1.0, "xx-XX", showRelayConsole: false));

        Assert.Null(ex);
    }

    [Fact]
    public void ApplyAndPersist_persists_the_values_via_read_modify_save()
    {
        // ApplyAndPersist reads the live snapshot, overwrites the Preferences
        // section, and saves. The fake captures the saved config; its
        // Preferences mirror the applied quartet (proving the read-modify-save
        // carried the values onto the snapshot without clobbering siblings).
        var (svc, loader, _) = Build();

        svc.ApplyAndPersist(ThemeMode.Light, 1.25, "fr", showRelayConsole: true);

        Assert.Equal(1, loader.SaveCalls);
        Assert.NotNull(loader.LastSaved);
        Assert.Equal(ThemeMode.Light, loader.LastSaved!.Preferences.Theme);
        Assert.Equal(1.25, loader.LastSaved.Preferences.FontScale);
        Assert.Equal("fr", loader.LastSaved.Preferences.Language);
        Assert.True(loader.LastSaved.Preferences.ShowRelayConsole);
    }

    [Fact]
    public void ApplyAndPersist_does_not_clobber_sibling_sections_on_save()
    {
        // Read-modify-save defense: a sibling section set on the live snapshot
        // (e.g. RelayDir, written by the upcoming Settings window)
        // must survive the Preferences save, because ApplyAndPersist loads the
        // current snapshot rather than mutating a stale cached singleton.
        var config = CuratorConfig.CreateDefault();
        config.RelayDir = "/custom/runtime";
        var loader = new FakeConfigLoader { Config = config };
        var svc = new PreferencesService(loader, Localization, NullLogger<PreferencesService>.Instance);

        svc.ApplyAndPersist(ThemeMode.Dark, 1.0, "en", showRelayConsole: false);

        Assert.Equal("/custom/runtime", loader.LastSaved!.RelayDir);
    }

    [Fact]
    public void ApplyAndPersist_does_not_throw_when_application_current_is_null()
    {
        // Unit tests run without an Avalonia Application; the theme + font-scale
        // apply paths must guard against that (the persistence path still runs).
        var (svc, loader, _) = Build();

        var ex = Record.Exception(() => svc.ApplyAndPersist(ThemeMode.Dark, 1.5, "en", showRelayConsole: false));

        Assert.Null(ex);
        Assert.Equal(1, loader.SaveCalls);
    }

    [Fact]
    public void Base_font_size_constants_match_the_design_intent()
    {
        // The body + status font sizes are the unscaled anchors the
        // PreferencesService multiplies by the user's font scale. They back the
        // AppFontSize / AppStatusFontSize resources; pinning them guards the
        // status-strip (12) staying a step below the body (14).
        Assert.Equal(14.0, PreferencesService.BaseFontSize);
        Assert.Equal(12.0, PreferencesService.BaseStatusFontSize);
    }
}
