using Avalonia;
using Avalonia.Controls;
using Modificus.Curator.General;
using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The <see cref="WindowGeometryTracker"/> contract: the pure policy seams
/// (size normalization + clamping, the meaningful-state policy, reason-aware
/// resize acceptance, the #19431 visible-restore correction decision, the
/// close-path trusted-candidate resolution, the persisted-maximized seeding)
/// plus the state-machine behaviors that previously lived untestable inside
/// the Window subclass (deferred + coalesced applies, Layout never
/// authoritative, the end-to-end #19431 correction with no recursion, the
/// close path). The tracker is fed (Size, ResizeReason, WindowState)
/// observations through an injectable post seam, so no live Window is needed.
/// </summary>
public sealed class WindowGeometryTrackerTests
{
    private static readonly Size DefaultNormal = new(960.0, 640.0);

    private static double MinWidth => MainWindow.MinWindowWidth;

    private static double MinHeight => MainWindow.MinWindowHeight;

    // ---- NormalizeSavedSize ----------------------------------------------

    [Fact]
    public void Null_saved_state_returns_the_fallback_signal()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            saved: null,
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MinWidth,
            MinHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Absent_work_area_returns_the_fallback_signal()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(1280.0, 800.0, false),
            workAreaWidth: null,
            workAreaHeight: null,
            MinWidth,
            MinHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Valid_state_within_range_passes_through_unchanged()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(1000.0, 700.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MinWidth,
            MinHeight);

        Assert.Equal((1000.0, 700.0), (width, height));
    }

    [Fact]
    public void Oversized_state_clamps_to_the_work_area()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(5000.0, 3000.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1040.0,
            MinWidth,
            MinHeight);

        Assert.Equal((1920.0, 1040.0), (width, height));
    }

    [Fact]
    public void Undersized_state_clamps_to_the_minimum()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(100.0, 100.0, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MinWidth,
            MinHeight);

        Assert.Equal((MinWidth, MinHeight), (width, height));
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
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(w, h, false),
            workAreaWidth: 1920.0,
            workAreaHeight: 1080.0,
            MinWidth,
            MinHeight);

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
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(1000.0, 700.0, false),
            workAreaWidth: aw,
            workAreaHeight: ah,
            MinWidth,
            MinHeight);

        Assert.Equal((0.0, 0.0), (width, height));
    }

    [Fact]
    public void Work_area_smaller_than_minimum_still_clamps_to_minimum()
    {
        var (width, height) = WindowGeometryTracker.NormalizeSavedSize(
            new AppWindowState(500.0, 400.0, false),
            workAreaWidth: 300.0,
            workAreaHeight: 200.0,
            MinWidth,
            MinHeight);

        Assert.Equal((MinWidth, MinHeight), (width, height));
    }

    // ---- NextMeaningfulMaximized -----------------------------------------

    [Fact]
    public void Normal_clears_the_flag()
    {
        Assert.False(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Normal, previous: true));
        Assert.False(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Normal, previous: false));
    }

    [Fact]
    public void Maximized_sets_the_flag()
    {
        Assert.True(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Maximized, previous: false));
        Assert.True(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Maximized, previous: true));
    }

    [Fact]
    public void Minimized_leaves_the_preceding_flag_unchanged()
    {
        Assert.False(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Minimized, previous: false));
        Assert.True(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.Minimized, previous: true));
    }

    [Fact]
    public void FullScreen_leaves_the_preceding_flag_unchanged()
    {
        Assert.False(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.FullScreen, previous: false));
        Assert.True(WindowGeometryTracker.NextMeaningfulMaximized(WindowState.FullScreen, previous: true));
    }

    // ---- IsTrustedResizeReason -------------------------------------------

    [Fact]
    public void User_and_Unspecified_are_trusted()
    {
        // The correct Normal size in the #19431 Maximized->Normal sequence
        // arrives as Unspecified; User is the drag-the-frame case.
        Assert.True(WindowGeometryTracker.IsTrustedResizeReason(WindowResizeReason.User));
        Assert.True(WindowGeometryTracker.IsTrustedResizeReason(WindowResizeReason.Unspecified));
    }

    [Fact]
    public void Layout_is_never_trusted_as_normal_size_authority()
    {
        // #19431: a post-Maximized Layout resize can carry the stale maximized
        // ClientSize, so it never updates the tracked Normal size.
        Assert.False(WindowGeometryTracker.IsTrustedResizeReason(WindowResizeReason.Layout));
    }

    [Fact]
    public void Application_and_DpiChange_are_trusted_observations_of_real_size()
    {
        // Application fires when code sets Width/Height/ClientSize (the
        // correction's own ClientSize reapply reaffirms the same value);
        // DpiChange rescales the window and the post-change DIP size is what
        // the user sees. Both are real observations of the client size.
        Assert.True(WindowGeometryTracker.IsTrustedResizeReason(WindowResizeReason.Application));
        Assert.True(WindowGeometryTracker.IsTrustedResizeReason(WindowResizeReason.DpiChange));
    }

    // ---- ShouldCorrectFromLayout (#19431 correction decision) -----------

    [Fact]
    public void Post_open_conflicting_layout_while_normal_requests_correction()
    {
        var staleMaximized = new Size(1920, 1040);
        var trustedNormal = new Size(960, 640);

        Assert.True(WindowGeometryTracker.ShouldCorrectFromLayout(
            staleMaximized, layoutSawOpen: true, WindowState.Normal, trustedNormal));
    }

    [Fact]
    public void Pre_open_layout_does_not_request_correction()
    {
        var conflicting = new Size(1920, 1040);

        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            conflicting, layoutSawOpen: false, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Maximized_layout_does_not_request_correction()
    {
        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            new Size(1920, 1040), layoutSawOpen: true, WindowState.Maximized, DefaultNormal));
    }

    [Fact]
    public void Near_equal_fractional_layout_does_not_request_correction()
    {
        // Sub-tolerance differences are rounding noise, not the stale-size bug,
        // so correction cannot loop on fractional jitter.
        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            new Size(960.4, 640.6), layoutSawOpen: true, WindowState.Normal, DefaultNormal));
        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            new Size(960.0 + WindowGeometryTracker.CorrectionTolerance, 640.0),
            layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Absent_layout_does_not_request_correction()
    {
        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            layoutCandidate: null, layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    [Fact]
    public void Invalid_layout_does_not_request_correction()
    {
        Assert.False(WindowGeometryTracker.ShouldCorrectFromLayout(
            new Size(0, 1040), layoutSawOpen: true, WindowState.Normal, DefaultNormal));
    }

    // ---- Persisted seeding ----------------------------------------------

    [Fact]
    public void Persisted_maximized_seeds_the_maximized_behavior()
    {
        // The persisted flag routes both the meaningful-state seed and the
        // one-shot first-open maximize.
        Assert.True(WindowGeometryTracker.PersistedSeedsMaximized(new AppWindowState(960, 640, true)));
    }

    [Fact]
    public void Persisted_normal_does_not_seed_the_maximized_behavior()
    {
        Assert.False(WindowGeometryTracker.PersistedSeedsMaximized(new AppWindowState(960, 640, false)));
    }

    [Fact]
    public void Absent_persisted_state_does_not_seed_the_maximized_behavior()
    {
        Assert.False(WindowGeometryTracker.PersistedSeedsMaximized(null));
    }

    // ---- the state machine (fed observations, no live Window) -----------

    /// <summary>
    /// A tracker whose deferred apply is captured instead of run, so a test
    /// controls exactly when the posted apply lands (mirroring the production
    /// dispatcher post).
    /// </summary>
    private sealed class DeferredTracker
    {
        public List<Action> Posted { get; } = new();

        public WindowGeometryTracker Build() => new(DefaultNormal, action => Posted.Add(action));

        public void RunPosted()
        {
            var posted = Posted.ToArray();
            Posted.Clear();
            foreach (var action in posted)
            {
                action();
            }
        }
    }

    [Fact]
    public void A_trusted_observation_settled_normal_becomes_the_tracked_size()
    {
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();
        var resized = new Size(1100, 750);

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(resized, WindowResizeReason.Unspecified);
        deferred.RunPosted();

        Assert.Equal(resized, tracker.LastNormalSize);
    }

    [Fact]
    public void A_trusted_observation_settled_maximized_does_not_become_the_normal_size()
    {
        // The maximized client size is not a Normal restore target; the Win32
        // ordering (resize before the managed state change) is exactly why the
        // apply defers until the settled state is known.
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(new Size(1920, 1040), WindowResizeReason.Unspecified);
        tracker.ObserveWindowState(WindowState.Maximized);
        deferred.RunPosted();

        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
    }

    [Fact]
    public void A_layout_observation_never_updates_the_tracked_normal_size()
    {
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();

        tracker.NotifyOpened();
        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(new Size(1920, 1040), WindowResizeReason.Layout);
        deferred.RunPosted();

        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
    }

    [Fact]
    public void Observations_coalesce_into_one_apply_with_the_latest_candidate()
    {
        // Two trusted observations before the posted apply runs: one apply
        // only, resolving the freshest candidate.
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(new Size(1000, 700), WindowResizeReason.User);
        tracker.ObserveResize(new Size(1200, 800), WindowResizeReason.User);

        Assert.Single(deferred.Posted);

        deferred.RunPosted();
        Assert.Equal(new Size(1200, 800), tracker.LastNormalSize);
        Assert.Empty(deferred.Posted);
    }

    [Fact]
    public void The_19431_burst_requests_a_correction_carrying_the_trusted_size()
    {
        // The #19431 sequence: after Maximized -> Normal, the correct Normal
        // resize arrives as Unspecified and the stale maximized ClientSize
        // arrives as Layout. The trusted candidate wins, and the conflicting
        // post-open Layout triggers a correction carrying the TRUSTED size.
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();
        var trustedNormal = new Size(1024, 768);
        var staleMaximized = new Size(1920, 1040);
        Size? corrected = null;
        tracker.CorrectionRequested += (_, size) => corrected = size;

        tracker.NotifyOpened();
        tracker.ObserveWindowState(WindowState.Maximized);
        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(trustedNormal, WindowResizeReason.Unspecified);
        tracker.ObserveResize(staleMaximized, WindowResizeReason.Layout);
        deferred.RunPosted();

        Assert.Equal(trustedNormal, tracker.LastNormalSize);
        Assert.Equal(trustedNormal, corrected);
    }

    [Fact]
    public void The_correction_does_not_recurse()
    {
        // The window's ClientSize reapply raises a synchronous Application
        // resize; feeding it back during the correction must not request a
        // second correction.
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();
        var corrections = 0;
        tracker.CorrectionRequested += (_, size) =>
        {
            corrections++;
            // The re-entrant observation the window emits while applying.
            tracker.ObserveResize(size, WindowResizeReason.Application);
            // Its apply is posted (not inline), so run it after the correction.
            foreach (var action in deferred.Posted.ToArray())
            {
                action();
            }
        };

        tracker.NotifyOpened();
        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(new Size(1024, 768), WindowResizeReason.Unspecified);
        tracker.ObserveResize(new Size(1920, 1040), WindowResizeReason.Layout);
        deferred.RunPosted();

        Assert.Equal(1, corrections);
        Assert.Equal(new Size(1024, 768), tracker.LastNormalSize);
    }

    [Fact]
    public void An_initial_show_layout_before_open_never_corrects()
    {
        // A Layout observation tagged before the open cannot trigger the
        // correction from a later apply, even once the window has opened.
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();
        var corrections = 0;
        tracker.CorrectionRequested += (_, _) => corrections++;

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(new Size(1920, 1040), WindowResizeReason.Layout);
        tracker.NotifyOpened();
        deferred.RunPosted();

        Assert.Equal(0, corrections);
        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
    }

    [Fact]
    public void Observations_after_the_close_are_ignored()
    {
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();

        tracker.PrepareClose(WindowState.Normal);
        tracker.ObserveResize(new Size(1500, 900), WindowResizeReason.User);
        deferred.RunPosted();

        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
    }

    [Fact]
    public void PrepareClose_while_normal_consumes_a_pending_trusted_candidate()
    {
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();
        var pending = new Size(1200, 800);

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveResize(pending, WindowResizeReason.User);
        // Close before the posted apply runs: the pending candidate is
        // consumed by the close path instead.
        tracker.PrepareClose(WindowState.Normal);
        deferred.RunPosted();

        Assert.Equal(pending, tracker.LastNormalSize);
    }

    [Fact]
    public void PrepareClose_while_maximized_keeps_the_prior_normal_size()
    {
        var deferred = new DeferredTracker();
        var tracker = deferred.Build();

        tracker.ObserveWindowState(WindowState.Normal);
        tracker.ObserveWindowState(WindowState.Maximized);
        tracker.ObserveResize(new Size(1920, 1040), WindowResizeReason.Unspecified);
        tracker.PrepareClose(WindowState.Maximized);

        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
        Assert.True(tracker.LastMeaningfulMaximized);
    }

    [Fact]
    public void Minimized_then_close_keeps_the_meaningful_state()
    {
        var tracker = new WindowGeometryTracker(DefaultNormal);

        tracker.ObserveWindowState(WindowState.Maximized);
        tracker.ObserveWindowState(WindowState.Minimized);
        tracker.PrepareClose(WindowState.Minimized);

        Assert.True(tracker.LastMeaningfulMaximized);
    }

    [Fact]
    public void SeedPersisted_applies_a_valid_size_and_routes_the_one_shot_maximize()
    {
        var tracker = new WindowGeometryTracker(DefaultNormal);

        var applied = tracker.SeedPersisted(
            new AppWindowState(1100, 750, true),
            workAreaDip: new Size(1920, 1040),
            MinWidth,
            MinHeight);

        Assert.True(applied);
        Assert.Equal(new Size(1100, 750), tracker.LastNormalSize);
        Assert.True(tracker.LastMeaningfulMaximized);
        // One-shot: consumed once, never again.
        Assert.True(tracker.ConsumeMaximizeOnFirstOpen());
        Assert.False(tracker.ConsumeMaximizeOnFirstOpen());
    }

    [Fact]
    public void SeedPersisted_with_invalid_state_keeps_the_default_normal()
    {
        var tracker = new WindowGeometryTracker(DefaultNormal);

        var applied = tracker.SeedPersisted(null, workAreaDip: new Size(1920, 1040), MinWidth, MinHeight);

        Assert.False(applied);
        Assert.Equal(DefaultNormal, tracker.LastNormalSize);
        Assert.False(tracker.LastMeaningfulMaximized);
        Assert.False(tracker.ConsumeMaximizeOnFirstOpen());
    }

    [Fact]
    public void SeedPersisted_clamps_an_oversized_state_to_the_work_area()
    {
        var tracker = new WindowGeometryTracker(DefaultNormal);

        tracker.SeedPersisted(
            new AppWindowState(5000, 3000, false),
            workAreaDip: new Size(1920, 1040),
            MinWidth,
            MinHeight);

        Assert.Equal(new Size(1920, 1040), tracker.LastNormalSize);
    }
}
