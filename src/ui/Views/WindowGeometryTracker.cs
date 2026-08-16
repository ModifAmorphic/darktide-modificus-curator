using Avalonia;
using Avalonia.Controls;
using Modificus.Curator.General;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The main window's geometry state machine, extracted from
/// <see cref="MainWindow"/> so it is unit-testable headless: the deferred +
/// coalesced, reason-aware resize observation, the meaningful-state policy,
/// the persisted-state seeding, and the Avalonia #19431 stale-Layout
/// correction decision. The window feeds it observations
/// (<see cref="ObserveResize"/>, <see cref="ObserveWindowState"/>,
/// <see cref="NotifyOpened"/>) and queries it for actions (the persisted
/// seed's size + one-shot maximize flag, the close-time snapshot, and the
/// <see cref="CorrectionRequested"/> reapply signal); the tracker never
/// touches a Window.
/// </summary>
/// <remarks>
/// <para><b>Deferred, reason-aware observation:</b> the platform's
/// settled-state ordering is not reliable (Win32 reports the maximized resize
/// BEFORE its managed WindowState change; X11 generally reports state first),
/// so each resize observation is tagged (trusted vs Layout; whether the window
/// had opened) and ONE deferred apply is posted through the injected
/// <paramref name="post"/> seam; at apply time the settled state is passed
/// with <see cref="ObserveWindowState"/> and decides what the observation
/// meant.</para>
/// <para><b>Layout is never authoritative</b> (Avalonia #19431): a
/// post-Maximized Layout resize can carry the stale maximized ClientSize, so
/// it never updates the tracked Normal size; it is only checked for the
/// visible-restore correction, which re-applies the trusted size through the
/// window's ClientSize (surfaced as <see cref="CorrectionRequested"/>) when a
/// post-open Layout observation materially conflicts. The correction guard
/// lives here so the synchronous re-entrant observations the window emits
/// while applying cannot loop.</para>
/// </remarks>
internal sealed class WindowGeometryTracker
{
    /// <summary>
    /// The material-conflict threshold (DIP) for the #19431 correction.
    /// Fractional differences at or below this are treated as rounding, not
    /// the stale-size bug, so correction cannot loop on sub-pixel noise.
    /// </summary>
    internal const double CorrectionTolerance = 1.0;

    private readonly Action<Action> _post;

    private Size _lastNormalSize;
    private bool _lastMeaningfulMaximized;
    private bool _maximizeOnFirstOpen;
    private bool _hasOpened;
    private bool _isClosing;
    private Size? _trustedCandidate;
    private Size? _layoutCandidate;
    private bool _layoutSawOpen;
    private bool _resizePosted;
    private bool _applyingCorrection;
    private WindowState _settledState = WindowState.Normal;

    /// <param name="defaultNormalSize">The fallback Normal size (the XAML
    /// defaults) seeded before any persisted state is applied.</param>
    /// <param name="post">The deferred-apply seam: production posts one apply
    /// to the UI thread's dispatcher; tests capture or run it inline.</param>
    public WindowGeometryTracker(Size defaultNormalSize, Action<Action>? post = null)
    {
        _lastNormalSize = defaultNormalSize;
        _post = post ?? (static action => action());
    }

    /// <summary>The freshest trusted Normal client size (DIP). Updated by
    /// deferred applies + the close path; never by a Layout
    /// observation.</summary>
    public Size LastNormalSize => _lastNormalSize;

    /// <summary>Whether the last meaningful window state was Maximized
    /// (Minimized / FullScreen never replace the preceding
    /// Normal/Maximized verdict).</summary>
    public bool LastMeaningfulMaximized => _lastMeaningfulMaximized;

    /// <summary>
    /// Whether the persisted state routes a one-shot maximize on first open
    /// (seeded from the persisted maximized flag; the window consumes it in
    /// its opened handler).
    /// </summary>
    public bool MaximizeOnFirstOpen => _maximizeOnFirstOpen;

    /// <summary>
    /// Consumes the one-shot first-open maximize: returns whether the window
    /// should maximize now (the first call after a maximized seed) and clears
    /// the flag so later opens never re-maximize.
    /// </summary>
    public bool ConsumeMaximizeOnFirstOpen()
    {
        if (!_maximizeOnFirstOpen)
        {
            return false;
        }

        _maximizeOnFirstOpen = false;
        return true;
    }

    /// <summary>
    /// Raised (at most once per conflicting apply, re-entrancy-guarded) when
    /// the #19431 correction should re-apply the tracked trusted Normal size
    /// through the window's <c>ClientSize</c>. Carries the size to apply.
    /// </summary>
    public event EventHandler<Size>? CorrectionRequested;

    /// <summary>
    /// Pure size-normalization for a persisted window geometry. Rejects an
    /// absent/invalid saved size and absent/non-finite/non-positive work-area
    /// dimensions by returning (0, 0) (the caller then keeps its fallback
    /// size). Otherwise clamps to [min, work-area], flooring the upper bound
    /// at the minimum so a work area smaller than the minimum still yields the
    /// minimum.
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
    /// client size. <see cref="WindowResizeReason.Layout"/> is not: per
    /// Avalonia issue #19431, a post-Maximized Layout resize can carry the
    /// stale maximized ClientSize, so it never updates the tracked Normal size
    /// and is only checked for the visible-restore correction. <c>User</c>,
    /// <c>Unspecified</c>, <c>Application</c>, and <c>DpiChange</c> are trusted
    /// observations of the actual client size (the correct Normal size in the
    /// #19431 sequence arrives as <c>Unspecified</c>; the correction's own
    /// <c>Application</c> reapply harmlessly reaffirms the same value).
    /// </summary>
    internal static bool IsTrustedResizeReason(WindowResizeReason reason) =>
        reason != WindowResizeReason.Layout;

    /// <summary>
    /// Resolves the trusted Normal size from a pending trusted candidate:
    /// the candidate when it is valid and the settled state is Normal,
    /// otherwise the current tracked size. Used by the deferred apply and the
    /// close path. A Layout observation and a raw ClientSize are never passed
    /// here.
    /// </summary>
    internal static Size ResolveTrustedNormal(Size? trustedCandidate, WindowState state, Size current) =>
        trustedCandidate is { Width: > 0, Height: > 0 } && state == WindowState.Normal
            ? trustedCandidate.Value
            : current;

    /// <summary>
    /// The #19431 correction test: whether a post-open Layout observation that
    /// materially conflicts with the resolved trusted Normal size should
    /// trigger a reapply through the window's ClientSize. Only fires when the
    /// settled state is Normal. Layout is never authoritative; this only
    /// corrects the visible size back to the trusted value, never persists a
    /// new size from Layout.
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
    /// Pure seeding policy: the persisted maximized flag routes two
    /// independent in-memory states, the meaningful-maximized flag and the
    /// one-shot first-open maximize. Both derive from the same persisted fact
    /// so a Maximized close always reopens Maximized even if the open /
    /// state-observation ordering ever varies.
    /// </summary>
    internal static bool PersistedSeedsMaximized(AppWindowState? saved) =>
        saved is { IsMaximized: true };

    /// <summary>
    /// Seeds the tracker from the persisted state (the window's close-path
    /// write): normalizes the saved size against the work area (DIP) + the
    /// window minimums and, when valid, adopts it as the tracked Normal size.
    /// Returns whether a valid size was applied (the caller then sets its
    /// Width/Height; on false it keeps its XAML fallback).
    /// </summary>
    public bool SeedPersisted(AppWindowState? saved, Size? workAreaDip, double minWidth, double minHeight)
    {
        var (width, height) = NormalizeSavedSize(
            saved, workAreaDip?.Width, workAreaDip?.Height, minWidth, minHeight);

        if (width > 0 && height > 0)
        {
            _lastNormalSize = new Size(width, height);
        }

        var seedMaximized = PersistedSeedsMaximized(saved);
        _lastMeaningfulMaximized = seedMaximized;
        _maximizeOnFirstOpen = seedMaximized;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// The window opened: subsequent Layout observations are tagged as
    /// post-open (eligible for the #19431 correction; an initial-show Layout
    /// queued before the open never is).
    /// </summary>
    public void NotifyOpened() => _hasOpened = true;

    /// <summary>
    /// The window's state changed (or the settled state is being read at apply
    /// time): updates the meaningful-state flag. Minimized / FullScreen leave
    /// the preceding verdict unchanged.
    /// </summary>
    public void ObserveWindowState(WindowState state)
    {
        _settledState = state;
        _lastMeaningfulMaximized = NextMeaningfulMaximized(state, _lastMeaningfulMaximized);
    }

    /// <summary>
    /// A resize observation (the window's OnResized). Classifies by reason
    /// (trusted vs Layout, the #19431 stale-size vector), tags Layout
    /// observations as post-open when they arrive after the open, and posts
    /// ONE deferred apply (coalesced: later observations before the apply just
    /// replace the candidates). Ignored once closing has begun or for
    /// non-positive sizes.
    /// </summary>
    public void ObserveResize(Size clientSize, WindowResizeReason reason)
    {
        if (_isClosing)
        {
            return;
        }

        if (clientSize.Width <= 0 || clientSize.Height <= 0)
        {
            return;
        }

        if (IsTrustedResizeReason(reason))
        {
            _trustedCandidate = clientSize;
        }
        else
        {
            // Layout: never authoritative for the tracked Normal size, but it
            // is the #19431 stale-size vector, so retain it for conflict
            // detection. Tag at observation time so an initial-show Layout
            // queued before the open cannot trigger correction from a later
            // apply.
            _layoutCandidate = clientSize;
            if (_hasOpened)
            {
                _layoutSawOpen = true;
            }
        }

        if (!_resizePosted)
        {
            _resizePosted = true;
            _post(ApplySettledResize);
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

        // A trusted observation wins and becomes the tracked Normal size
        // BEFORE the correction check, so a trusted + stale-Layout burst
        // resolves to the trusted value.
        _lastNormalSize = ResolveTrustedNormal(trusted, _settledState, _lastNormalSize);

        if (!_applyingCorrection
            && ShouldCorrectFromLayout(layout, layoutSawOpen, _settledState, _lastNormalSize))
        {
            // The guard covers the window's synchronous re-entrant resize
            // observations while it applies ClientSize (the correction's own
            // Application-reason reapply is trusted + reaffirms the same
            // value).
            _applyingCorrection = true;
            try
            {
                CorrectionRequested?.Invoke(this, _lastNormalSize);
            }
            finally
            {
                _applyingCorrection = false;
            }
        }
    }

    /// <summary>
    /// The window is closing for real (not cancelled): marks the tracker
    /// closing (queued applies no-op) and consumes any pending trusted
    /// candidate when the settled state is Normal (never a raw ClientSize,
    /// which may be the stale #19431 value). After this the caller reads
    /// <see cref="LastNormalSize"/> + <see cref="LastMeaningfulMaximized"/>
    /// for its single persist write.
    /// </summary>
    public void PrepareClose(WindowState closingState)
    {
        _isClosing = true;
        _settledState = closingState;

        if (closingState == WindowState.Normal)
        {
            _lastNormalSize = ResolveTrustedNormal(_trustedCandidate, WindowState.Normal, _lastNormalSize);
        }

        _trustedCandidate = null;
        _layoutCandidate = null;
        _layoutSawOpen = false;
    }
}
