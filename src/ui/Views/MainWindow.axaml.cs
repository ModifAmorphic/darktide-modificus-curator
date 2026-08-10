using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The main window: the app shell (SplitView navigation rail + hosted
/// destination content + global status strip). Its <c>DataContext</c> is set by
/// the composition root (<see cref="App.OnFrameworkInitializationCompleted"/>)
/// to the resolved <see cref="ViewModels.ShellViewModel"/>.
/// </summary>
/// <remarks>
/// <para><b>Dynamic open-pane width:</b> the SplitView's XAML
/// <c>OpenPaneLength=200</c> is the design-time/startup fallback. Once the
/// window is open, <see cref="UpdateOpenPaneLength"/> measures the five live
/// localized nav-rail labels with the representative <c>NavMeasureLabel</c>'s
/// actual typography (FontFamily, FontStyle, FontWeight, FontStretch, FontSize,
/// LetterSpacing) at the current culture, and grows
/// <c>NavSplitView.OpenPaneLength</c> to fit the widest label, bounded to
/// [200, 360]. The pure arithmetic lives in <see cref="ComputeOpenPaneLength"/>
/// so unit tests can exercise it without a live Window. Measurement re-runs on
/// inherited <c>Window.FontSize</c> changes (PreferencesService applies the
/// user's font scale by overwriting the AppFontSize DynamicResource) and on
/// LocalizationService Culture / <c>Item[]</c> changes (a culture flip re-
/// resolves every label). Beyond 360px the per-label
/// <c>TextTrimming=CharacterEllipsis</c> kicks in as a graceful fallback; the
/// full label remains available via the tooltip and automation name.</para>
/// <para><b>UI-thread only.</b> Font-size + culture changes originate on the UI
/// path; <see cref="UpdateOpenPaneLength"/> runs on the calling thread, no
/// <c>ConfigureAwait(false)</c> involved (UI-layer convention).</para>
/// </remarks>
public partial class MainWindow : Window
{
    // Nav-rail pane sizing constants, in device-independent pixels. The expanded
    // CompactInline pane grows from the design-time 200px fallback to fit the
    // widest localized label at the current font scale, bounded so future
    // translations never clip and the pane never eats too much of the content
    // area.

    /// <summary>The design-time/startup fallback AND the lower bound for the
    /// measured width. Anything measuring below this stays at 200px (the original
    /// visual rhythm the operator approved).</summary>
    internal const double PaneOpenMin = 200.0;

    /// <summary>The upper bound for the open pane. Beyond this, labels
    /// ellipsize (<c>TextTrimming=CharacterEllipsis</c>) and the full text
    /// remains available via the tooltip / automation name.</summary>
    internal const double PaneOpenMax = 360.0;

    /// <summary>The icon column width inside a nav tile (the always-visible
    /// 48px square the Material icon centers in). Matches the SplitView's
    /// CompactPaneLength and each tile's first column.</summary>
    internal const double PaneIconColumn = 48.0;

    /// <summary>The left margin on each nav-label TextBlock (between the icon
    /// column and the label), mirrored from the XAML.</summary>
    internal const double PaneLabelMargin = 12.0;

    /// <summary>The trailing breathing room added after the widest label so
    /// labels do not run flush against the pane edge.</summary>
    internal const double PaneTrailingBreathingRoom = 16.0;

    /// <summary>
    /// The resx keys for the five nav-rail labels, in nav-rail order. Measured
    /// at the current culture to find the widest one. Read from the live
    /// LocalizationService (the same source the XAML labels bind through) so
    /// measurement tracks what the user sees.
    /// </summary>
    private static readonly string[] NavLabelKeys =
    {
        "Profiles_Title",
        "ModList_Header",
        "Integrations_Title",
        "Preferences_Title",
        "Settings_Title",
    };

    private LocalizationService? _localization;

    // Reentrancy guard: setting OpenPaneLength can trigger layout that
    // re-enters a property-change path; this bool prevents a recursive
    // measurement loop without touching the property itself.
    private bool _measuring;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pure pane-length arithmetic: rounds the measured widest-label width up
    /// to a whole pixel, adds the icon column + label margin + trailing
    /// breathing room, and clamps to [<see cref="PaneOpenMin"/>,
    /// <see cref="PaneOpenMax"/>]. Pure so unit tests can exercise it without
    /// creating a live Window.
    /// </summary>
    /// <param name="widestLabelWidth">The widest unwrapped measured label width
    /// (<c>TextLayout.WidthIncludingTrailingWhitespace</c>) at the current font
    /// scale + culture.</param>
    /// <returns>The bounded open pane length, in device-independent
    /// pixels.</returns>
    internal static double ComputeOpenPaneLength(double widestLabelWidth) =>
        Math.Clamp(
            Math.Ceiling(PaneIconColumn + PaneLabelMargin + widestLabelWidth + PaneTrailingBreathingRoom),
            PaneOpenMin,
            PaneOpenMax);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Resolve the live LocalizationService (App swaps the XAML placeholder
        // for the real DI singleton at startup; if it is unavailable, fall back
        // to the XAML OpenPaneLength=200 without throwing). Opened can fire
        // more than once without a matching Close (e.g. minimize/restore on
        // some platforms), so the subscription is idempotent: only attach when
        // not already attached to this instance, and detach the previous
        // instance first if a different one is ever observed (defensive against
        // a hypothetical resource swap).
        if (Application.Current?.Resources["Loc"] is LocalizationService loc)
        {
            if (!ReferenceEquals(_localization, loc))
            {
                if (_localization is not null)
                {
                    _localization.PropertyChanged -= OnLocalizationChanged;
                }
                _localization = loc;
                loc.PropertyChanged += OnLocalizationChanged;
            }
        }

        // First measurement on every Opened, even when the subscription was
        // already in place: the labels may have changed (culture flip while the
        // window was closed) and a re-open should always reflect the live state.
        UpdateOpenPaneLength();
    }

    /// <summary>
    /// Re-measures when an inherited property that affects label width changes.
    /// <see cref="Window.FontSize"/> cascades from the AppFontSize
    /// DynamicResource (PreferencesService applies the user's font scale by
    /// overwriting it), so each inherited FontSize change warrants a fresh
    /// measurement. All other property changes fall through to base.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
        {
            UpdateOpenPaneLength();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_localization is not null)
        {
            _localization.PropertyChanged -= OnLocalizationChanged;
            _localization = null;
        }
        base.OnClosed(e);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Culture flips raise "Item[]" (the indexer wildcard) so every label
        // re-resolves; a direct Culture assignment raises the named property.
        // Both warrant a re-measure.
        if (e.PropertyName == nameof(LocalizationService.Culture)
            || e.PropertyName == "Item[]")
        {
            UpdateOpenPaneLength();
        }
    }

    /// <summary>
    /// Measures the five live localized nav labels with the NavMeasureLabel's
    /// actual typography + sets NavSplitView.OpenPaneLength to the bounded
    /// result. Falls back silently to the XAML OpenPaneLength=200 when the
    /// SplitView, the measure label, or the live LocalizationService is missing
    /// (e.g. design-time paths) or when measurement throws at runtime.
    /// </summary>
    private void UpdateOpenPaneLength()
    {
        if (_measuring)
        {
            return;
        }
        if (NavSplitView is null || NavMeasureLabel is null || _localization is null)
        {
            return;
        }

        _measuring = true;
        try
        {
            var typeface = new Typeface(
                NavMeasureLabel.FontFamily,
                NavMeasureLabel.FontStyle,
                NavMeasureLabel.FontWeight,
                NavMeasureLabel.FontStretch);

            double widest = 0;
            foreach (var key in NavLabelKeys)
            {
                var text = _localization[key];
                using var layout = new TextLayout(
                    text,
                    typeface,
                    NavMeasureLabel.FontSize,
                    foreground: null,
                    letterSpacing: NavMeasureLabel.LetterSpacing);
                // WidthIncludingTrailingWhitespace covers trailing space-padded
                // glyphs (none today, but defensive against future resx tweaks);
                // NoWrap + infinite MaxWidth are the constructor defaults so the
                // label measures unwrapped.
                if (layout.WidthIncludingTrailingWhitespace > widest)
                {
                    widest = layout.WidthIncludingTrailingWhitespace;
                }
            }

            NavSplitView.OpenPaneLength = ComputeOpenPaneLength(widest);
        }
        catch
        {
            // Defensive: if measurement throws (bad font, missing resource at
            // runtime, etc.), fall back to the XAML OpenPaneLength=200 rather
            // than crashing the window.
        }
        finally
        {
            _measuring = false;
        }
    }
}
