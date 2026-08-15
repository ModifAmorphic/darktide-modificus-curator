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
/// size in DIP and whether the last meaningful state was Maximized are read from
/// <see cref="IMainWindowStatePersistence.MainWindowState"/> on the production path, clamped
/// via the pure <see cref="NormalizeSavedSize"/> helper to the XAML minimum and
/// (when available) the primary screen's working area in DIP, and applied as
/// <c>Width</c>/<c>Height</c> before first Show so the platform has the right
/// restore size. When the persisted maximized flag is set, the window maximizes
/// once in <see cref="OnOpened"/> (after Show) for Win32/X11 consistency, so a
/// later unmaximize restores to the saved Normal size. The persisted maximized
/// flag seeds <see cref="_lastMeaningfulMaximized"/> immediately. Resize
/// observation is deferred + reason-aware (see <see cref="OnResized"/>); state
/// persists once, through <see cref="OnClosing"/>, matching the title-bar close
/// path. No window position is stored.</para>
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

    /// <summary>The material-conflict threshold (DIP) for the #19431 correction.
    /// Fractional differences at or below this are treated as rounding, not the
    /// stale-size bug, so correction cannot loop on sub-pixel noise.</summary>
    internal const double CorrectionTolerance = 1.0;

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

    // Persisted-window-geometry tracking. All UI-thread affine.
    private readonly IMainWindowStatePersistence? _stateStore;
    private Size _lastNormalSize;            // freshest trusted Normal client size, DIP
    private bool _lastMeaningfulMaximized;   // Normal=false, Maximized=true, Minimized/FullScreen=unchanged
    private bool _maximizeOnFirstOpen;       // persisted maximized flag routes a one-shot OnOpened maximize
    private bool _hasOpened;                 // set in OnOpened; tags Layout observations for #19431
    private bool _isClosing;                 // set in OnClosing once not cancelled
    private bool _persisted;                 // OnClosing persists at most once
    private Size? _trustedCandidate;         // latest trusted resize observation awaiting apply
    private Size? _layoutCandidate;          // latest Layout resize observation (never authoritative)
    private bool _layoutSawOpen;             // a Layout observation arrived after OnOpened
    private bool _resizePosted;              // one deferred apply is already queued
    private bool _applyingCorrection;        // guards ClientSize reapply from recursing

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
        _lastNormalSize = new Size(DefaultWidth, DefaultHeight);
        _lastMeaningfulMaximized = false;
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
    /// Pure size-normalization for a persisted window geometry. Rejects an
    /// absent/invalid saved size and absent/non-finite/non-positive work-area
    /// dimensions by returning (0, 0) (the caller then keeps the XAML fallback
    /// size). Otherwise clamps to [<paramref name="minWidth"/>, work-area-width]
    /// and [<paramref name="minHeight"/>, work-area-height], flooring the upper
    /// bound at the minimum so a work area smaller than the XAML minimum still
    /// yields the minimum. Pure so unit tests can exercise it without a live
    /// Window or Screen.
    /// </summary>
    internal static (double Width, double Height) NormalizeSavedSize(
        AppWindowState? saved,
        double? workAreaWidth,
        double? workAreaHeight,
        double minWidth,
        double minHeight)
    {
        if (!IsValidDimension(workAreaWidth) || !IsValidDimension(workAreaHeight))
        {
            return (0.0, 0.0);
        }
        if (saved is null || !IsValidDimension(saved.Width) || !IsValidDimension(saved.Height))
        {
            return (0.0, 0.0);
        }

        var aw = workAreaWidth.GetValueOrDefault();
        var ah = workAreaHeight.GetValueOrDefault();
        var maxW = Math.Max(minWidth, aw);
        var maxH = Math.Max(minHeight, ah);
        return (Math.Clamp(saved.Width, minWidth, maxW), Math.Clamp(saved.Height, minHeight, maxH));

        static bool IsValidDimension(double? v) =>
            v is double d && double.IsFinite(d) && d > 0;
    }

    /// <summary>
    /// Pure meaningful-window-state policy. <c>Normal</c> clears the flag,
    /// <c>Maximized</c> sets it, and <c>Minimized</c> + <c>FullScreen</c> leave
    /// the preceding flag unchanged (a minimize or a fullscreen toggle never
    /// replaces the last meaningful Normal/Maximized state).
    /// </summary>
    internal static bool NextMeaningfulMaximized(WindowState current, bool previous) => current switch
    {
        WindowState.Normal => false,
        WindowState.Maximized => true,
        _ => previous,
    };

    /// <summary>
    /// Whether a resize reason is an authoritative observation of the real
    /// client size. <see cref="WindowResizeReason.Layout"/> is not: per Avalonia
    /// issue #19431 (https://github.com/AvaloniaUI/Avalonia/issues/19431), a
    /// post-Maximized Layout resize can carry the stale maximized ClientSize,
    /// so it never updates <see cref="_lastNormalSize"/> and is only checked for
    /// the visible-restore correction. <c>User</c>, <c>Unspecified</c>,
    /// <c>Application</c>, and <c>DpiChange</c> are trusted observations of the
    /// actual client size (the correct Normal size in the #19431 sequence
    /// arrives as <c>Unspecified</c>; the correction's own <c>Application</c>
    /// reapply harmlessly reaffirms the same value).
    /// </summary>
    internal static bool IsTrustedResizeReason(WindowResizeReason reason) =>
        reason != WindowResizeReason.Layout;

    /// <summary>
    /// Resolves the trusted Normal size from a pending trusted candidate.
    /// Returns the candidate when it is valid and the settled state is Normal;
    /// otherwise returns the current tracked size. Used both by the deferred
    /// resize apply and by the close path. A Layout observation and the raw
    /// <see cref="WindowBase.ClientSize"/> are never passed here: Layout is
    /// never authoritative (#19431), and at close the raw ClientSize may be the
    /// stale maximized value.
    /// </summary>
    internal static Size ResolveTrustedNormal(Size? trustedCandidate, WindowState state, Size current) =>
        trustedCandidate is { Width: > 0, Height: > 0 } && state == WindowState.Normal
            ? trustedCandidate.Value
            : current;

    /// <summary>
    /// The #19431 correction test: whether a post-open Layout observation that
    /// materially conflicts with the resolved trusted Normal size should trigger
    /// a reapply of the trusted size through <see cref="WindowBase.ClientSize"/>.
    /// Only fires when the settled state is Normal. Layout is never
    /// authoritative; this only corrects the visible size back to the trusted
    /// value, never persists a new size from Layout.
    /// </summary>
    internal static bool ShouldCorrectFromLayout(
        Size? layoutCandidate,
        bool layoutSawOpen,
        WindowState state,
        Size resolvedNormal) =>
        layoutSawOpen
        && state == WindowState.Normal
        && layoutCandidate is { Width: > 0, Height: > 0 } ls
        && (Math.Abs(ls.Width - resolvedNormal.Width) > CorrectionTolerance
            || Math.Abs(ls.Height - resolvedNormal.Height) > CorrectionTolerance);

    /// <summary>
    /// Pure seeding policy: the persisted maximized flag routes two independent
    /// in-memory states at construction, the meaningful-maximized flag and the
    /// one-shot first-open maximize. Both derive from the same persisted fact so
    /// a Maximized close always reopens Maximized even if OnOpened /
    /// OnPropertyChanged ordering ever varies.
    /// </summary>
    internal static bool PersistedSeedsMaximized(AppWindowState? saved) =>
        saved is { IsMaximized: true };

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

        var workArea = TryGetWorkAreaDip();
        var (width, height) = NormalizeSavedSize(
            saved,
            workArea?.Width,
            workArea?.Height,
            MinWindowWidth,
            MinWindowHeight);

        if (width > 0 && height > 0)
        {
            Width = width;
            Height = height;
            _lastNormalSize = new Size(width, height);
        }
        else
        {
            // Invalid/absent saved size: keep the XAML defaults (already seeded
            // by the parameterless ctor) as the last Normal so the close path
            // persists a sensible seed.
            _lastNormalSize = new Size(DefaultWidth, DefaultHeight);
        }

        // Seed the meaningful flag from the persisted fact immediately rather
        // than relying on OnOpened/OnPropertyChanged, and route the one-shot
        // first-open maximize off the same fact.
        var seedMaximized = PersistedSeedsMaximized(saved);
        _lastMeaningfulMaximized = seedMaximized;
        _maximizeOnFirstOpen = seedMaximized;
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
        _hasOpened = true;

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

        if (_maximizeOnFirstOpen)
        {
            _maximizeOnFirstOpen = false;
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.NewValue is WindowState ws)
        {
            _lastMeaningfulMaximized = NextMeaningfulMaximized(ws, _lastMeaningfulMaximized);
        }
        else if (change.Property == FontSizeProperty)
        {
            UpdateOpenPaneLength();
        }
    }

    // Resize observation is deferred + reason-aware because the platform's
    // settled-state ordering is not reliable: Win32 reports the maximized resize
    // BEFORE its managed WindowState change, while X11 generally reports state
    // first. OnResized tags each observation (trusted vs Layout; whether the
    // window had opened) and posts ONE apply; at apply time the settled state
    // has propagated. Layout is never authoritative for _lastNormalSize
    // (#19431): a post-open Layout resize that materially conflicts with the
    // trusted Normal size triggers a narrow visible-restore through ClientSize.
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (_isClosing)
        {
            return;
        }
        var size = e.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (IsTrustedResizeReason(e.Reason))
        {
            _trustedCandidate = size;
        }
        else
        {
            // Layout: never authoritative for _lastNormalSize, but it is the
            // #19431 stale-size vector, so retain it for conflict detection.
            // Tag at observation time so an initial-show Layout queued before
            // OnOpened cannot trigger correction from a later apply.
            _layoutCandidate = size;
            if (_hasOpened)
            {
                _layoutSawOpen = true;
            }
        }

        if (!_resizePosted)
        {
            _resizePosted = true;
            Dispatcher.UIThread.Post(ApplySettledResize);
        }
    }

    private void ApplySettledResize()
    {
        _resizePosted = false;
        if (_isClosing)
        {
            _trustedCandidate = null;
            _layoutCandidate = null;
            _layoutSawOpen = false;
            return;
        }

        var trusted = _trustedCandidate;
        var layout = _layoutCandidate;
        var layoutSawOpen = _layoutSawOpen;
        _trustedCandidate = null;
        _layoutCandidate = null;
        _layoutSawOpen = false;

        var state = WindowState;

        // A trusted observation wins and becomes _lastNormalSize BEFORE the
        // correction check, so a trusted + stale-Layout burst resolves to the
        // trusted value.
        _lastNormalSize = ResolveTrustedNormal(trusted, state, _lastNormalSize);

        if (!_applyingCorrection
            && ShouldCorrectFromLayout(layout, layoutSawOpen, state, _lastNormalSize))
        {
            _applyingCorrection = true;
            try
            {
                ClientSize = _lastNormalSize;
            }
            finally
            {
                _applyingCorrection = false;
            }
        }
    }

    // Persists the window geometry once, through the normal close path. Calls
    // base so a Closing subscriber can still cancel; if it does, nothing is
    // written. Otherwise marks the window closing (queued applies no-op),
    // consumes any pending trusted candidate when the settled state is Normal
    // (never the raw ClientSize, which may be the stale #19431 value), and
    // writes one atomic AppWindowState. Closing while Maximized/Minimized keeps
    // the tracked last-Normal size + meaningful flag.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _isClosing = true;

        if (WindowState == WindowState.Normal)
        {
            _lastNormalSize = ResolveTrustedNormal(_trustedCandidate, WindowState.Normal, _lastNormalSize);
        }
        _trustedCandidate = null;
        _layoutCandidate = null;
        _layoutSawOpen = false;

        if (_stateStore is not null && !_persisted)
        {
            _persisted = true;
            try
            {
                _stateStore.MainWindowState = new AppWindowState(
                    _lastNormalSize.Width,
                    _lastNormalSize.Height,
                    _lastMeaningfulMaximized);
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
