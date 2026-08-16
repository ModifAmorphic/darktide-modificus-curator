using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Modificus.Curator.General;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The main window: the app shell (SplitView navigation rail + hosted
/// destination content + global status strip). Its <c>DataContext</c> is set by
/// the composition root to the resolved <see cref="ViewModels.ShellViewModel"/>.
/// </summary>
/// <remarks>
/// <para><b>Dynamic open-pane width:</b> the SplitView's XAML
/// <c>OpenPaneLength=200</c> is the design-time/startup fallback. Once the
/// window is open, <see cref="UpdateOpenPaneLength"/> measures the live
/// localized pane labels with the representative <c>NavMeasureLabel</c>'s
/// actual typography (FontFamily, FontStyle, FontWeight, FontStretch, FontSize,
/// LetterSpacing) at the current culture, and grows
/// <c>NavSplitView.OpenPaneLength</c> to fit the widest label, bounded to
/// [200, 360]. The pure arithmetic lives in <see cref="ComputeOpenPaneLength"/>
/// so unit tests can exercise it without a live Window.</para>
/// <para><b>Persisted window geometry.</b> The last unmaximized (Normal) client
/// size in DIP and whether the last meaningful state was Maximized are read
/// from <see cref="IMainWindowStatePersistence.MainWindowState"/> on the production path,
/// normalized + clamped via <see cref="WindowGeometryTracker.SeedPersisted"/>
/// and applied as <c>Width</c>/<c>Height</c> before first Show so the platform
/// has the right restore size. When the persisted maximized flag is set, the
/// window maximizes once in <see cref="OnOpened"/> (after Show) for Win32/X11
/// consistency, so a later unmaximize restores to the saved Normal size.
/// Resize observation is deferred + reason-aware: the window feeds every
/// observation to the <see cref="WindowGeometryTracker"/> (fed with Size,
/// ResizeReason, and WindowState; it owns the #19431 stale-Layout correction,
/// the meaningful-state policy, and the trusted-Normal tracking, and is
/// unit-tested headless). State persists once, through
/// <see cref="OnClosing"/>, matching the title-bar close path. No window
/// position is stored.</para>
/// </remarks>
public partial class MainWindow : Window
{
    internal const double PaneOpenMin = 200.0;
    internal const double PaneOpenMax = 360.0;
    internal const double PaneIconColumn = 48.0;
    internal const double PaneLabelMargin = 12.0;
    internal const double PaneTrailingBreathingRoom = 16.0;

    internal const double DefaultWidth = 960.0;    // matches XAML Width
    internal const double DefaultHeight = 640.0;   // matches XAML Height
    internal const double MinWindowWidth = 720.0;  // matches XAML MinWidth
    internal const double MinWindowHeight = 480.0; // matches XAML MinHeight

    /// <summary>The resx keys for the pane labels (the five destinations plus the
    /// pane-bottom Exit), in pane order. Exit is included so a translated
    /// "Exit" longer than the destinations still fits the measured pane width.
    /// Read from the live LocalizationService so measurement tracks what the
    /// user sees.</summary>
    private static readonly string[] NavLabelKeys =
    {
        "Profiles_Title",
        "ModList_Header",
        "Integrations_Title",
        "Preferences_Title",
        "Settings_Title",
        "Nav_Exit",
    };

    private LocalizationService? _localization;
    private bool _measuring; // reentrancy guard for OpenPaneLength layout feedback

    // Persisted-window-geometry wiring. The tracker owns the state machine
    // (observation tagging, deferred applies, the #19431 correction decision,
    // the meaningful-state policy, the trusted-Normal size); this window owns
    // the Window operations (apply size, maximize, persist) + the single
    // persist guard. All UI-thread affine.
    private readonly IMainWindowStatePersistence? _stateStore;
    private readonly WindowGeometryTracker _geometry;
    private bool _persisted;                     // OnClosing persists at most once

    /// <summary>
    /// Parameterless constructor: the valid XAML runtime/designer path. Loads
    /// XAML and safe in-memory defaults but does not create or locate a store,
    /// so load/save is cleanly skipped on this path (no service locator, no
    /// fake store). Production construction goes through the internal
    /// <see cref="MainWindow(IMainWindowStatePersistence)"/> overload.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _geometry = new WindowGeometryTracker(
            new Size(DefaultWidth, DefaultHeight),
            action => Dispatcher.UIThread.Post(action));
        _geometry.CorrectionRequested += OnGeometryCorrectionRequested;
    }

    /// <summary>
    /// Production constructor: chains the parameterless path, then attaches the
    /// injected store and applies the persisted geometry. Used by the
    /// composition-root factory so the store is supplied before the window is
    /// returned/shown.
    /// </summary>
    internal MainWindow(IMainWindowStatePersistence stateStore) : this()
    {
        _stateStore = stateStore;
        ApplyPersistedWindowState();
    }

    internal static double ComputeOpenPaneLength(double widestLabelWidth) =>
        Math.Clamp(
            Math.Ceiling(PaneIconColumn + PaneLabelMargin + widestLabelWidth + PaneTrailingBreathingRoom),
            PaneOpenMin,
            PaneOpenMax);

    /// <summary>
    /// Pure conversion + validation of a screen's physical working area to DIP.
    /// Returns false (with zeroed outs) for non-finite/non-positive scaling or
    /// resulting non-finite/non-positive DIP dimensions; the caller then falls
    /// back to the XAML defaults rather than trusting a corrupt conversion.
    /// Pure so the invalid-screen policy is testable without a live Screen.
    /// </summary>
    internal static bool TryConvertWorkAreaDip(
        double scaling,
        double pixelWidth,
        double pixelHeight,
        out double widthDip,
        out double heightDip)
    {
        widthDip = 0;
        heightDip = 0;
        if (!double.IsFinite(scaling) || scaling <= 0)
        {
            return false;
        }
        if (!double.IsFinite(pixelWidth) || pixelWidth <= 0 || !double.IsFinite(pixelHeight) || pixelHeight <= 0)
        {
            return false;
        }
        var w = pixelWidth / scaling;
        var h = pixelHeight / scaling;
        if (!double.IsFinite(w) || w <= 0 || !double.IsFinite(h) || h <= 0)
        {
            return false;
        }
        widthDip = w;
        heightDip = h;
        return true;
    }

    private void ApplyPersistedWindowState()
    {
        if (_stateStore is null)
        {
            return;
        }

        AppWindowState? saved;
        try
        {
            saved = _stateStore.MainWindowState;
        }
        catch
        {
            saved = null;
        }

        // The tracker normalizes + clamps the saved size against the work area
        // (DIP) + the XAML minimums and routes the persisted maximized fact
        // into the meaningful flag + the one-shot first-open maximize. An
        // invalid/absent saved size keeps the XAML defaults (already seeded in
        // the tracker's construction) as the last Normal so the close path
        // persists a sensible seed.
        if (_geometry.SeedPersisted(saved, TryGetWorkAreaDip(), MinWindowWidth, MinWindowHeight))
        {
            Width = _geometry.LastNormalSize.Width;
            Height = _geometry.LastNormalSize.Height;
        }
    }

    /// <summary>
    /// Resolves the primary screen's working area in DIP, or <c>null</c> when
    /// screen data is unavailable or invalid (non-finite/non-positive scaling
    /// or working-area dimensions). Delegates the conversion + validation to
    /// the pure <see cref="TryConvertWorkAreaDip"/> helper.
    /// </summary>
    private Size? TryGetWorkAreaDip()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen is null)
            {
                return null;
            }
            if (!TryConvertWorkAreaDip(
                    screen.Scaling,
                    screen.WorkingArea.Width,
                    screen.WorkingArea.Height,
                    out var width,
                    out var height))
            {
                return null;
            }
            return new Size(width, height);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _geometry.NotifyOpened();

        // Resolve the live LocalizationService (App swaps the XAML placeholder
        // for the real DI singleton at startup). The subscription is idempotent.
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

        UpdateOpenPaneLength();

        if (_geometry.ConsumeMaximizeOnFirstOpen())
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.NewValue is WindowState ws)
        {
            _geometry.ObserveWindowState(ws);
        }
        else if (change.Property == FontSizeProperty)
        {
            UpdateOpenPaneLength();
        }
    }

    // Every resize observation feeds the geometry tracker, which tags it
    // (trusted vs Layout; whether the window had opened) and posts one
    // deferred apply; the settled window state (fed through
    // OnPropertyChanged, and re-read at apply time below) decides what the
    // observation meant. Layout is never authoritative for the tracked Normal
    // size (#19431): the tracker may answer with a CorrectionRequested reapply
    // of the trusted size through ClientSize.
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        _geometry.ObserveResize(e.ClientSize, e.Reason);
        // The tracker's deferred apply runs on the dispatcher; when it does,
        // it needs the settled state as of that moment.
        _geometry.ObserveWindowState(WindowState);
    }

    /// <summary>
    /// The tracker decided the visible size conflicts materially with the
    /// trusted Normal size (#19431): reapply it through ClientSize. The
    /// tracker's correction guard covers the re-entrant observations this set
    /// raises.
    /// </summary>
    private void OnGeometryCorrectionRequested(object? sender, Size size) => ClientSize = size;

    // Persists the window geometry once, through the normal close path. Calls
    // base so a Closing subscriber can still cancel; if it does, nothing is
    // written. Otherwise the tracker consumes any pending trusted candidate
    // when the settled state is Normal (never the raw ClientSize, which may be
    // the stale #19431 value), and the window writes one atomic
    // AppWindowState from the tracker's tracked size + meaningful flag.
    // Closing while Maximized/Minimized keeps the tracked last-Normal size +
    // meaningful flag.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _geometry.PrepareClose(WindowState);

        if (_stateStore is not null && !_persisted)
        {
            _persisted = true;
            try
            {
                var size = _geometry.LastNormalSize;
                _stateStore.MainWindowState = new AppWindowState(
                    size.Width,
                    size.Height,
                    _geometry.LastMeaningfulMaximized);
            }
            catch
            {
                // Swallow: window-state persistence is non-critical.
            }
        }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

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
        if (e.PropertyName == nameof(LocalizationService.Culture)
            || e.PropertyName == "Item[]")
        {
            UpdateOpenPaneLength();
        }
    }

    private void UpdateOpenPaneLength()
    {
        if (_measuring || NavSplitView is null || NavMeasureLabel is null || _localization is null)
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
                if (layout.WidthIncludingTrailingWhitespace > widest)
                {
                    widest = layout.WidthIncludingTrailingWhitespace;
                }
            }

            NavSplitView.OpenPaneLength = ComputeOpenPaneLength(widest);
        }
        catch
        {
            // Fall back to the XAML OpenPaneLength=200 if measurement throws.
        }
        finally
        {
            _measuring = false;
        }
    }
}
