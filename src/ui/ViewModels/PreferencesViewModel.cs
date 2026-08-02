using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Preferences dialog: theme picker, font-scale
/// percent slider, language dropdown. Each change is applied immediately through
/// <see cref="IPreferencesService"/>.<c>ApplyAndPersist</c> (the single
/// authority), so the running app + the persisted config reflect each change as
/// it is made; "Done" just closes the dialog. The dialog has no Cancel concept:
/// preferences are not staged-and-committed, they are immediate (a theme flip
/// is a one-step reversible action, not a destructive one).
/// </summary>
/// <remarks>
/// <para><b>Initial state</b> comes from a one-off snapshot read from
/// <see cref="IConfigLoader"/> at construction (no cached singleton); the
/// snapshot reflects whatever the running app last persisted, so the picker
/// matches what the user actually sees.</para>
/// <para><b>Theme:</b> a <see cref="ThemeMode"/>-backed picker; maps 1:1 to
/// <c>Avalonia.Styling.ThemeVariant</c>.</para>
/// <para><b>Font scale:</b> a percent (<see cref="FontScalePercent"/>), 80 to
/// 150 in 5% steps. Persisted as a double (<see cref="FontScalePercent"/> /
/// 100.0) on <see cref="PreferencesConfig.FontScale"/>.</para>
/// <para><b>Language:</b> the list of <see cref="LanguageOption"/>s. English
/// ships; the option + culture switching are in place, real translations come
/// later as translated resx files (and extend this list, no code change to the
/// apply path). Switching language updates the running UI through the
/// <see cref="LocalizationService"/> (dynamic, no restart).</para>
/// <para><b>ShowRelayConsole:</b> whether to show the Mod Relay console window
/// on launch. Persisted immediately but read at launch time (no live-apply);
/// <c>false</c> by default (the window is hidden).</para>
/// </remarks>
public partial class PreferencesViewModel : ObservableObject
{
    /// <summary>
    /// The slider floor / ceiling in percent + step. Exposed as constants so
    /// the view can bind Min/Max/TickFrequency from one source of truth.
    /// </summary>
    public const int FontScaleMinPercent = 80;
    public const int FontScaleMaxPercent = 150;
    public const int FontScaleStepPercent = 5;

    private static readonly ThemeMode DefaultTheme = ThemeMode.System;

    private readonly IPreferencesService _preferences;

    /// <summary>Whether a property change should fire <see cref="ApplyAndPersist"/>.</summary>
    /// <remarks>
    /// False during the initial restore (constructing the VM restores the current
    /// preferences into the bound controls without re-applying them; they already
    /// match the running app, so re-applying would be a noisy no-op).
    /// </remarks>
    private bool _suppressApply;

    public PreferencesViewModel(
        IPreferencesService preferences,
        IConfigLoader configLoader,
        LocalizationService localization)
    {
        _preferences = preferences;

        ThemeOptions = new ThemeOption[]
        {
            new(ThemeMode.System, "Preferences_ThemeSystem", localization),
            new(ThemeMode.Dark, "Preferences_ThemeDark", localization),
            new(ThemeMode.Light, "Preferences_ThemeLight", localization),
        };
        LanguageOptions = new[]
        {
            new LanguageOption("en", "Preferences_LanguageEnglish", localization),
        };

        // Restore the current preferences into the bound controls without
        // re-applying (they already match the running app). One live snapshot:
        // reflects whatever was last persisted.
        _suppressApply = true;
        try
        {
            var configured = configLoader.Load().Preferences;
            SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Mode == configured.Theme)
                ?? ThemeOptions.First(o => o.Mode == DefaultTheme);
            FontScalePercent = ClampFontScale(configured.FontScale);
            SelectedLanguage = LanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Name, configured.Language, StringComparison.OrdinalIgnoreCase))
                ?? LanguageOptions[0];
            ShowRelayConsole = configured.ShowRelayConsole;
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>The theme picker options.</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    /// <summary>The language picker options.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>
    /// The selected theme. Changing it applies the theme + persists immediately.
    /// </summary>
    [ObservableProperty]
    private ThemeOption _selectedTheme = null!;

    /// <summary>
    /// The font-scale percent (80 to 150 in 5% steps). Persisted as
    /// <see cref="PreferencesConfig.FontScale"/> = <see cref="FontScalePercent"/> / 100.
    /// </summary>
    [ObservableProperty]
    private int _fontScalePercent = 100;

    /// <summary>
    /// The selected language. Changing it switches the UI culture dynamically
    /// (no restart) + persists.
    /// </summary>
    [ObservableProperty]
    private LanguageOption _selectedLanguage = null!;

    /// <summary>
    /// Whether to show the Mod Relay console window when launching the game.
    /// Persisted immediately (no live-apply: it is read at launch time). <c>false</c>
    /// by default (the window is hidden).
    /// </summary>
    [ObservableProperty]
    private bool _showRelayConsole;

    partial void OnSelectedThemeChanged(ThemeOption value) => ApplyAndPersist();
    partial void OnFontScalePercentChanged(int value) => ApplyAndPersist();
    partial void OnSelectedLanguageChanged(LanguageOption value) => ApplyAndPersist();
    partial void OnShowRelayConsoleChanged(bool value) => ApplyAndPersist();

    /// <summary>
    /// Pushes the current selection through <see cref="IPreferencesService"/>:
    /// applies theme + font scale + language to the running app and persists the
    /// new values to the config file's <see cref="PreferencesConfig"/> section.
    /// Suppressed during the initial restore.
    /// </summary>
    private void ApplyAndPersist()
    {
        if (_suppressApply)
        {
            return;
        }

        _preferences.ApplyAndPersist(
            SelectedTheme.Mode,
            FontScalePercent / 100.0,
            SelectedLanguage.Name,
            ShowRelayConsole);
    }

    /// <summary>
    /// Maps the persisted double onto a valid slider percent. Non-finite /
    /// non-positive values fall back to 100 (no scaling).
    /// </summary>
    private static int ClampFontScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return 100;
        }

        var percent = (int)Math.Round(scale * 100);
        return Math.Clamp(percent, FontScaleMinPercent, FontScaleMaxPercent);
    }
}

/// <summary>
/// One row of the theme picker: the underlying <see cref="ThemeMode"/> plus the
/// resource key for its display label. <see cref="Label"/> resolves the key
/// through the <see cref="LocalizationService"/> and refreshes on a culture
/// change (so the picker text follows a language switch live).
/// </summary>
public sealed class ThemeOption : ObservableObject
{
    private readonly LocalizationService _localization;

    public ThemeOption(ThemeMode mode, string labelKey, LocalizationService localization)
    {
        Mode = mode;
        LabelKey = labelKey;
        _localization = localization;
        _localization.PropertyChanged += OnCultureChanged;
    }

    public ThemeMode Mode { get; }
    public string LabelKey { get; }

    /// <summary>The localized display label (refreshes on a culture change).</summary>
    public string Label => _localization[LabelKey];

    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalizationService.Culture) or "Item[]")
        {
            OnPropertyChanged(nameof(Label));
        }
    }
}

/// <summary>
/// One row of the language picker: the culture name (e.g. <c>en</c>) plus the
/// resource key for its display label. Adding a translated resx later extends
/// the VM's <c>LanguageOptions</c> list, no code change to the apply path.
/// <see cref="Label"/> resolves the key through the <see cref="LocalizationService"/>
/// and refreshes on a culture change.
/// </summary>
public sealed class LanguageOption : ObservableObject
{
    private readonly LocalizationService _localization;

    public LanguageOption(string name, string labelKey, LocalizationService localization)
    {
        Name = name;
        LabelKey = labelKey;
        _localization = localization;
        _localization.PropertyChanged += OnCultureChanged;
    }

    public string Name { get; }
    public string LabelKey { get; }

    /// <summary>The localized display label (refreshes on a culture change).</summary>
    public string Label => _localization[LabelKey];

    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalizationService.Culture) or "Item[]")
        {
            OnPropertyChanged(nameof(Label));
        }
    }
}
