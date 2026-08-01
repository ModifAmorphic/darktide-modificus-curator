using Avalonia;
using Avalonia.Styling;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.UI.Localization;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Preferences;

/// <summary>
/// The single authority for applying user-facing preferences (theme, font scale,
/// language) to the running app and persisting them to <see cref="CuratorConfig"/>.
/// All three concerns (theme variant, global font scale, UI culture) live behind
/// one method so the values stay consistent: nothing else should touch
/// <c>RequestedThemeVariant</c>, the font-scale resource, or
/// <see cref="LocalizationService.Culture"/> directly.
/// </summary>
public interface IPreferencesService
{
    /// <summary>
    /// Applies <paramref name="theme"/> / <paramref name="fontScale"/> /
    /// <paramref name="language"/> to the running app (theme variant, global
    /// font scale, UI culture), then persists all four preferences (including
    /// <paramref name="showRelayConsole"/>, which has no live-apply step) to the
    /// config file via a read-modify-save through <see cref="IConfigLoader"/>
    /// (load the live snapshot, overwrite the <see cref="PreferencesConfig"/>
    /// section, save). Safe to call at startup (the values may match the loaded
    /// config, which is a no-op apply).
    /// </summary>
    void ApplyAndPersist(ThemeMode theme, double fontScale, string language, bool showRelayConsole);
}

/// <summary>
/// Default <see cref="IPreferencesService"/>. Applies the theme via
/// <see cref="Application.RequestedThemeVariant"/>, the font scale via
/// application-level <c>AppFontSize</c> + <c>AppStatusFontSize</c> resources
/// that a Window style / the status TextBlock bind to (cascading to all controls
/// through inheritance + DynamicResource), and the language via
/// <see cref="LocalizationService"/>.<see cref="LocalizationService.SetCulture"/>.
/// </summary>
public sealed class PreferencesService : IPreferencesService
{
    /// <summary>
    /// The base (unscaled) UI font size in pixels. The applied value is
    /// <see cref="BaseFontSize"/> * <c>FontScale</c>; the resource key
    /// <c>AppFontSize</c> is read by the Window style in App.axaml.
    /// </summary>
    public const double BaseFontSize = 14.0;

    /// <summary>
    /// The base (unscaled) status-strip font size in pixels. The applied value
    /// is <see cref="BaseStatusFontSize"/> * <c>FontScale</c>; the resource key
    /// <c>AppStatusFontSize</c> is read by the status TextBlock in MainWindow.
    /// Smaller than <see cref="BaseFontSize"/> because the status strip is a
    /// secondary, low-emphasis line.
    /// </summary>
    public const double BaseStatusFontSize = 12.0;

    private const string AppFontSizeResourceKey = "AppFontSize";
    private const string AppStatusFontSizeResourceKey = "AppStatusFontSize";

    private readonly IConfigLoader _configLoader;
    private readonly LocalizationService _localization;
    private readonly ILogger<PreferencesService> _logger;

    public PreferencesService(
        IConfigLoader configLoader,
        LocalizationService localization,
        ILogger<PreferencesService> logger)
    {
        _configLoader = configLoader;
        _localization = localization;
        _logger = logger;
    }

    /// <inheritdoc />
    public void ApplyAndPersist(ThemeMode theme, double fontScale, string language, bool showRelayConsole)
    {
        // 1. Apply to the running app. (showRelayConsole has no live-apply step:
        //    it is read at launch time by the Relay launcher.)
        ApplyTheme(theme);
        ApplyFontScale(fontScale);
        ApplyLanguage(language);

        // 2. Persist via read-modify-save: load the live snapshot, overwrite the
        //    Preferences section, save. There is no cached singleton; each
        //    Load() is a fresh snapshot, so this carries the user's change onto
        //    the current disk state without clobbering sibling sections written
        //    by another flow (the Settings window, etc.). Save is best-
        //    effort (ConfigLoader.Save swallows write errors), so a persistence
        //    failure never crashes the dialog mid-interaction.
        var config = _configLoader.Load();
        config.Preferences.Theme = theme;
        config.Preferences.FontScale = fontScale;
        config.Preferences.Language = language;
        config.Preferences.ShowRelayConsole = showRelayConsole;
        _configLoader.Save(config);

        _logger.LogInformation(
            "Preferences applied + persisted: theme={Theme}; fontScale={FontScale:F2}; language={Language}; showRelayConsole={ShowRelayConsole}",
            theme,
            fontScale,
            language,
            showRelayConsole);
    }

    /// <summary>
    /// Sets <see cref="Application.RequestedThemeVariant"/> from
    /// <see cref="ThemeMode"/>. <see cref="ThemeMode.System"/> maps to
    /// <see cref="ThemeVariant.Default"/> (follow the OS).
    /// </summary>
    private static void ApplyTheme(ThemeMode theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = theme switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>
    /// Publishes the scaled font sizes as the <c>AppFontSize</c> +
    /// <c>AppStatusFontSize</c> application resources. The Window style in
    /// App.axaml binds <c>Window.FontSize</c> to <c>AppFontSize</c>
    /// (DynamicResource), so all open windows (and their inheriting children)
    /// re-resolve when the resource changes; MainWindow's status TextBlock binds
    /// to <c>AppStatusFontSize</c>. Both use the same scale so the status strip
    /// grows with the body. Non-finite or non-positive scales fall back to 1.0
    /// (graceful: a bad value does not collapse the UI).
    /// </summary>
    private static void ApplyFontScale(double fontScale)
    {
        if (Application.Current is null)
        {
            return;
        }

        var scale = double.IsFinite(fontScale) && fontScale > 0 ? fontScale : 1.0;
        Application.Current.Resources[AppFontSizeResourceKey] = BaseFontSize * scale;
        Application.Current.Resources[AppStatusFontSizeResourceKey] = BaseStatusFontSize * scale;
    }

    /// <summary>
    /// Switches the UI culture through <see cref="LocalizationService"/>, which
    /// raises the change event so every <c>{Binding [Key]}</c> refreshes
    /// (dynamic-language path).
    /// </summary>
    private void ApplyLanguage(string language) => _localization.SetCulture(language);
}
