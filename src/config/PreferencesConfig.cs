namespace Modificus.Curator.Config;

/// <summary>
/// User-facing global preferences (the "Preferences" dialog): the UI theme,
/// the font-scale multiplier, the display language, and whether to show the
/// Mod Relay console window on launch. Bound from the <c>Preferences</c>
/// section of <see cref="CuratorConfig"/> by the config loader in
/// <c>Modificus.Curator.General</c>, and persisted back through
/// <c>ConfigLoader.Save</c> when the user changes a value in the dialog.
/// Every field carries a default so an absent section yields a usable object
/// (first-run safe).
/// </summary>
/// <remarks>
/// <para><b>Theme:</b> Dark / Light / System (System follows the OS theme and
/// is the default).</para>
/// <para><b>FontScale:</b> a continuous multiplier applied to the base UI font,
/// so a slider can scale the whole UI (0.8 to 1.5 typical). 1.0 = no scaling.</para>
/// <para><b>Language:</b> a culture name (e.g. <c>en</c>, <c>fr</c>). English
/// ships; the selector + culture switching are in place, real translations are
/// content added later via translated resx files. Empty / <c>"en"</c> = neutral.</para>
/// <para><b>ShowRelayConsole:</b> whether to show the Mod Relay console window
/// when launching the game. <c>false</c> by default (the window is hidden);
/// Relay's output is captured in its log file regardless. Read at launch time
/// by the Relay launcher, not applied live to the running app.</para>
/// </remarks>
public sealed class PreferencesConfig
{
    /// <summary>
    /// The UI theme variant. <see cref="ThemeMode.System"/> follows the OS.
    /// Defaults to <see cref="ThemeMode.System"/>.
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// The UI font-scale multiplier (1.0 = no scaling). The Preferences dialog
    /// exposes this as a percent slider; the persisted value is the raw double
    /// (e.g. 1.25 for 125%).
    /// </summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>
    /// The display language as a culture name (e.g. <c>en</c>, <c>fr</c>).
    /// Empty or <c>en</c> resolves to the neutral English resources. Switching
    /// this at runtime updates the live UI through the LocalizationService.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Whether to show the Mod Relay console window when launching the game.
    /// <c>false</c> by default (the window is hidden). Relay's output is captured
    /// in its log file regardless, so the console is redundant. Read at launch
    /// time by the Relay launcher; not applied live to the running app.
    /// </summary>
    public bool ShowRelayConsole { get; set; }

    /// <summary>
    /// The mod-list row density: <see cref="ModRowDensity.Compact"/> (the dense
    /// one-line row) or <see cref="ModRowDensity.Detailed"/> (multi-line with
    /// summary + thumbnail). Defaults to <see cref="ModRowDensity.Compact"/>.
    /// Owned by the Mods toolbar's density coordinator, not by
    /// <see cref="IPreferencesService.ApplyAndPersist"/>; the coordinator does
    /// its own focused read-modify-save so the density field does not widen that
    /// method's parameter list. Absent or undefined numeric values normalize to
    /// Compact when read.
    /// </summary>
    public ModRowDensity ModRowDensity { get; set; } = ModRowDensity.Compact;
}

/// <summary>
/// The UI theme variant, matching <c>Avalonia.Styling.ThemeVariant</c>.
/// <see cref="System"/> follows the OS theme (Avalonia's
/// <c>ThemeVariant.Default</c>).
/// </summary>
public enum ThemeMode
{
    /// <summary>Follow the OS theme (Avalonia's ThemeVariant.Default).</summary>
    System = 0,

    /// <summary>The dark theme (Avalonia's ThemeVariant.Dark).</summary>
    Dark = 1,

    /// <summary>The light theme (Avalonia's ThemeVariant.Light).</summary>
    Light = 2,
}

/// <summary>
/// The mod-list row density. <see cref="Compact"/> is the dense one-line row
/// (the default); <see cref="Detailed"/> adds the summary + thumbnail rows. An
/// absent or undefined numeric value normalizes to <see cref="Compact"/>.
/// </summary>
public enum ModRowDensity
{
    /// <summary>The dense one-line row (the default).</summary>
    Compact = 0,

    /// <summary>The multi-line row with summary + thumbnail.</summary>
    Detailed = 1,
}
