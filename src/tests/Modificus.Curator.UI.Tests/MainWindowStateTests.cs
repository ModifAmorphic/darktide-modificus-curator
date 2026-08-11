using Avalonia;
using Avalonia.Controls;
using Modificus.Curator.General;
using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Focused unit tests for the pure window-geometry policy seams on
/// <see cref="MainWindow"/>. These cover size normalization + clamping, the
/// meaningful-state policy, reason-aware resize acceptance, the #19431
/// visible-restore correction decision, the close-path trusted-candidate
/// resolution, the persisted-maximized seeding, and the screen-conversion
/// validation. All seams are pure internal statics so these exercise the
/// policy without a live Avalonia Window or Screen.
/// </summary>
public sealed class MainWindowStateTests
{
    private static readonly Size DefaultNormal =
        new(MainWindow.DefaultWidth, MainWindow.DefaultHeight);

    // ---- NormalizeSavedSize ----------------------------------------------

    [Fact]
    public void Null_saved_state_returns_the_fallback_signal()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            saved: null,
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Absent_work_area_returns_the_fallback_signal()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(1280.0, 800.0, false),
            workAreaWidth: null,
            workAreaHeight: null,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Valid_state_within_range_passes_through_unchanged()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(1000.0, 700.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((1000.0, 700.0), (width, height));
    }

    [Fact]
    public void Oversized_state_clamps_to_the_work_area()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(5000.0, 3000.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1040.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((1920.0, 1040.0), (width, height));
    }

    [Fact]
    public void Undersized_state_clamps_to_the_minimum()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(100.0, 100.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((MainWindow.MinWindowWidth, MainWindow.MinWindowHeight), (width, height));
    }

    [Theory]
    [InlineData(double.NaN, 600.0)]
    [InlineData(800.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 600.0)]
    [InlineData(800.0, double.NegativeInfinity)]
    [InlineData(0.0, 600.0)]
    [InlineData(800.0, 0.0)]
    [InlineData(-10.0, 600.0)]
    [InlineData(800.0, -10.0)]
    public void Invalid_saved_dimensions_return_the_fallback_signal(double w, double h)
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(w, h, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Theory]
    [InlineData(double.NaN, 1080.0)]
    [InlineData(1920.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 1080.0)]
    [InlineData(1920.0, double.NegativeInfinity)]
    [InlineData(0.0, 1080.0)]
    [InlineData(1920.0, 0.0)]
    [InlineData(-10.0, 1080.0)]
    [InlineData(1920.0, -10.0)]
    public void Invalid_work_area_dimensions_return_the_fallback_signal(double aw, double ah)
    {
        // Non-finite, zero, or negative work-area dims are corrupt -> fallback,
        // even with a valid persisted size.
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(1000.0, 700.0, false),
            workAreaWidth: aw,
            workAreaHeight: ah,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Work_area_smaller_than_minimum_still_clamps_to_minimum()
    {
        var (width, height) = MainWindow.NormalizeSavedSize(
            new AppWindowState(500.0, 400.0, false),
            workAreaWidth: 300.0,
            workAreaHeight: 200.0,
            MainWindow.MinWindowWidth,
            MainWindow.MinWindowHeight);

        Assert.Equal((MainWindow.MinWindowWidth, MainWindow.MinWindowHeight), (width, height));
    }

    [Fact]
    public void Constants_are_the_documented_values()
    {
        Assert.Equal(960.0, MainWindow.DefaultWidth);
        Assert.Equal(640.0, MainWindow.DefaultHeight);
        Assert.Equal(720.0, MainWindow.MinWindowWidth);
        Assert.Equal(480.0, MainWindow.MinWindowHeight);
        Assert.Equal(1.0, MainWindow.CorrectionTolerance);
    }

    // ---- NextMeaningfulMaximized -----------------------------------------

    [Fact]
    public void Normal_clears_the_flag()
    {
        Assert.False(MainWindow.NextMeaningfulMaximized(WindowState.Normal, previous: true));
        Assert.False(MainWindow.NextMeaningfulMaximized(WindowState.Normal, previous: false));
    }

    [Fact]
    public void Maximized_sets_the_flag()
    {
        Assert.True(MainWindow.NextMeaningfulMaximized(WindowState.Maximized, previous: false));
        Assert.True(MainWindow.NextMeaningfulMaximized(WindowState.Maximized, previous: true));
    }

    [Fact]
    public void Minimized_leaves_the_preceding_flag_unchanged()
    {
        Assert.False(MainWindow.NextMeaningfulMaximized(WindowState.Minimized, previous: false));
        Assert.True(MainWindow.NextMeaningfulMaximized(WindowState.Minimized, previous: true));
    }

    [Fact]
    public void FullScreen_leaves_the_preceding_flag_unchanged()
    {
        Assert.False(MainWindow.NextMeaningfulMaximized(WindowState.FullScreen, previous: false));
        Assert.True(MainWindow.NextMeaningfulMaximized(WindowState.FullScreen, previous: true));
    }

    [Fact]
    public void Normal_then_minimized_resolves_to_normal()
    {
        var flag = false;
        flag = MainWindow.NextMeaningfulMaximized(WindowState.Normal, flag);
        flag = MainWindow.NextMeaningfulMaximized(WindowState.Minimized, flag);
        Assert.False(flag);
    }

    [Fact]
    public void Maximized_then_minimized_resolves_to_maximized()
    {
        var flag = false;
        flag = MainWindow.NextMeaningfulMaximized(WindowState.Maximized, flag);
        flag = MainWindow.NextMeaningfulMaximized(WindowState.Minimized, flag);
        Assert.True(flag);
    }

    // ---- IsTrustedResizeReason -------------------------------------------

    [Fact]
    public void User_and_Unspecified_are_trusted()
    {
        // The correct Normal size in the #19431 Maximized->Normal sequence
        // arrives as Unspecified; User is the drag-the-frame case.
        Assert.True(MainWindow.IsTrustedResizeReason(WindowResizeReason.User));
        Assert.True(MainWindow.IsTrustedResizeReason(WindowResizeReason.Unspecified));
    }

    [Fact]
    public void Layout_is_never_trusted_as_normal_size_authority()
    {
        // #19431: a post-Maximized Layout resize can carry the stale maximized
        // ClientSize, so it never updates _lastNormalSize.
        Assert.False(MainWindow.IsTrustedResizeReason(WindowResizeReason.Layout));
    }

    [Fact]
    public void Application_and_DpiChange_are_trusted_observations_of_real_size()
    {
        // Application fires when code sets Width/Height/ClientSize (the
        // correction's own ClientSize reapply reaffirms the same value);
        // DpiChange rescales the window and the post-change DIP size is what
        // the user sees. Both are real observations of the client size.
        Assert.True(MainWindow.IsTrustedResizeReason(WindowResizeReason.Application));
        Assert.True(MainWindow.IsTrustedResizeReason(WindowResizeReason.DpiChange));
    }

    // ---- ResolveTrustedNormal (deferred apply + close candidate) ---------

    [Fact]
    public void Trusted_candidate_becomes_normal_size_when_settled_normal()
    {
        var candidate = new Size(1100, 750);

        var resolved = MainWindow.ResolveTrustedNormal(candidate, WindowState.Normal, DefaultNormal);

        Assert.Equal(candidate, resolved);
    }

    [Fact]
    public void Trusted_candidate_is_ignored_when_settled_maximized()
    {
        // The maximized client size is not a Normal restore target.
        var resolved = MainWindow.ResolveTrustedNormal(
            new Size(1920, 1040), WindowState.Maximized, DefaultNormal);

        Assert.Equal(DefaultNormal, resolved);
    }

    [Fact]
    public void No_trusted_candidate_keeps_the_current_normal_size()
    {
        var resolved = MainWindow.ResolveTrustedNormal(
            trustedCandidate: null, WindowState.Normal, DefaultNormal);

        Assert.Equal(DefaultNormal, resolved);
    }

    [Fact]
    public void Invalid_trusted_candidate_keeps_the_current_normal_size()
    {
        Assert.Equal(DefaultNormal,
            MainWindow.ResolveTrustedNormal(new Size(0, 700), WindowState.Normal, DefaultNormal));
        Assert.Equal(DefaultNormal,
            MainWindow.ResolveTrustedNormal(new Size(700, 0), WindowState.Normal, DefaultNormal));
    }

    // ---- ShouldCorrectFromLayout (#19431 correction decision) -----------

    [Fact]
    public void Post_open_conflicting_layout_while_normal_requests_correction()
    {
        // #19431: after Maximized->Normal, a stale Layout resize carries the
        // maximized size and materially conflicts with the trusted Normal size.
        var staleMaximized = new Size(1920, 1040);
        var trustedNormal = new Size(960, 640);

        Assert.True(MainWindow.ShouldCorrectFromLayout(
            staleMaximized, layoutSawOpen: true, WindowState.Normal, trustedNormal));
    }

    [Fact]
    public void Pre_open_layout_does_not_request_correction()
    {
        // Initial-show Layout was tagged before OnOpened, so even if the
        // deferred apply runs after open, it must not correct.
        var conflicting = new Size(1920, 1040);

        Assert.False(MainWindow.ShouldCorrectFromLayout(
            conflicting, layoutSawOpen: false, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Maximized_layout_does_not_request_correction()
    {
        // Correction only applies to the visible Normal restore; a Layout
        // observed while Maximized is irrelevant.
        Assert.False(MainWindow.ShouldCorrectFromLayout(
            new Size(1920, 1040), layoutSawOpen: true, WindowState.Maximized, DefaultNormal));
    }

    [Fact]
    public void Near_equal_fractional_layout_does_not_request_correction()
    {
        // Sub-tolerance differences are rounding noise, not the stale-size bug,
        // so correction cannot loop on fractional jitter.
        Assert.False(MainWindow.ShouldCorrectFromLayout(
            new Size(960.4, 640.6), layoutSawOpen: true, WindowState.Normal, DefaultNormal));
        Assert.False(MainWindow.ShouldCorrectFromLayout(
            new Size(960.0 + MainWindow.CorrectionTolerance, 640.0),
            layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Absent_layout_does_not_request_correction()
    {
        Assert.False(MainWindow.ShouldCorrectFromLayout(
            layoutCandidate: null, layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Invalid_layout_does_not_request_correction()
    {
        Assert.False(MainWindow.ShouldCorrectFromLayout(
            new Size(0, 1040), layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    // ---- The #19431 sequence resolves to the trusted normal size --------

    [Fact]
    public void Trusted_unspecified_then_stale_layout_resolves_to_trusted_and_requests_correction()
    {
        // The #19431 burst: a correct Unspecified Normal resize followed by a
        // stale Layout carrying the maximized ClientSize. The trusted candidate
        // must win and become _lastNormalSize before the correction check, so
        // the correction targets the trusted Normal size (not the stale value,
        // not any prior Normal size).
        var trustedNormal = new Size(1024, 768);
        var staleMaximized = new Size(1920, 1040);

        // 1) Trusted Unspecified is resolved first.
        var resolved = MainWindow.ResolveTrustedNormal(
            trustedNormal, WindowState.Normal, DefaultNormal);
        Assert.Equal(trustedNormal, resolved);

        // 2) Layout is then checked against the resolved trusted value.
        Assert.True(MainWindow.ShouldCorrectFromLayout(
            staleMaximized, layoutSawOpen: true, WindowState.Normal, resolved));
    }

    [Fact]
    public void Stale_layout_then_trusted_unspecified_also_resolves_to_trusted()
    {
        // Order independence: even if the stale Layout arrives in an earlier
        // burst than the trusted Unspecified, _lastNormalSize holds the trusted
        // value and the Layout never overwrites it.
        var trustedNormal = new Size(1024, 768);
        var staleMaximized = new Size(1920, 1040);

        // Burst with only Layout (no trusted): current Normal is retained.
        var afterLayout = MainWindow.ResolveTrustedNormal(
            trustedCandidate: null, WindowState.Normal, trustedNormal);
        Assert.Equal(trustedNormal, afterLayout);

        // The stale Layout conflicts with the retained trusted Normal.
        Assert.True(MainWindow.ShouldCorrectFromLayout(
            staleMaximized, layoutSawOpen: true, WindowState.Normal, afterLayout));
    }

    // ---- Close-path candidate resolution --------------------------------

    [Fact]
    public void Close_while_normal_consumes_a_pending_trusted_candidate()
    {
        var pending = new Size(1200, 800);

        var resolved = MainWindow.ResolveTrustedNormal(
            pending, WindowState.Normal, DefaultNormal);

        Assert.Equal(pending, resolved);
    }

    [Fact]
    public void Close_while_normal_without_pending_candidate_keeps_tracked_size()
    {
        // Never falls back to raw ClientSize (which may be the stale #19431
        // value); keeps the last trusted size.
        var resolved = MainWindow.ResolveTrustedNormal(
            trustedCandidate: null, WindowState.Normal, DefaultNormal);

        Assert.Equal(DefaultNormal, resolved);
    }

    [Fact]
    public void Close_while_maximized_keeps_the_prior_normal_size()
    {
        // The pending candidate (and raw ClientSize) are ignored when not
        // Normal; the tracked last-Normal size is what an unmaximize restores.
        var resolved = MainWindow.ResolveTrustedNormal(
            new Size(1920, 1040), WindowState.Maximized, DefaultNormal);

        Assert.Equal(DefaultNormal, resolved);
    }

    [Fact]
    public void Close_while_minimized_keeps_the_prior_normal_size()
    {
        var resolved = MainWindow.ResolveTrustedNormal(
            new Size(0, 0), WindowState.Minimized, DefaultNormal);

        Assert.Equal(DefaultNormal, resolved);
    }

    // ---- Persisted maximized seeding ------------------------------------

    [Fact]
    public void Persisted_maximized_seeds_the_maximized_behavior()
    {
        // The persisted flag routes both the meaningful-state seed and the
        // one-shot first-open maximize.
        Assert.True(MainWindow.PersistedSeedsMaximized(new AppWindowState(960, 640, true)));
    }

    [Fact]
    public void Persisted_normal_does_not_seed_the_maximized_behavior()
    {
        Assert.False(MainWindow.PersistedSeedsMaximized(new AppWindowState(960, 640, false)));
    }

    [Fact]
    public void Absent_persisted_state_does_not_seed_the_maximized_behavior()
    {
        Assert.False(MainWindow.PersistedSeedsMaximized(null));
    }

    // ---- TryConvertWorkAreaDip (screen validation) ----------------------

    [Fact]
    public void Valid_scaling_and_pixels_convert_to_dip()
    {
        var ok = MainWindow.TryConvertWorkAreaDip(
            scaling: 1.5, pixelWidth: 2880, pixelHeight: 1620,
            out var w, out var h);

        Assert.True(ok);
        Assert.Equal(1920.0, w);
        Assert.Equal(1080.0, h);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Invalid_scaling_returns_false(double scaling)
    {
        var ok = MainWindow.TryConvertWorkAreaDip(
            scaling, pixelWidth: 1920, pixelHeight: 1080, out var w, out var h);

        Assert.False(ok);
        Assert.Equal((0.0, 0.0), (w, h));
    }

    [Theory]
    [InlineData(double.NaN, 1080.0)]
    [InlineData(1920.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 1080.0)]
    [InlineData(1920.0, double.NegativeInfinity)]
    [InlineData(0.0, 1080.0)]
    [InlineData(1920.0, 0.0)]
    [InlineData(-10.0, 1080.0)]
    [InlineData(1920.0, -10.0)]
    public void Invalid_pixel_dimensions_return_false(double pw, double ph)
    {
        var ok = MainWindow.TryConvertWorkAreaDip(
            scaling: 1.0, pixelWidth: pw, pixelHeight: ph, out var w, out var h);

        Assert.False(ok);
        Assert.Equal((0.0, 0.0), (w, h));
    }
}
