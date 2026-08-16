using Avalonia;
using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The window-geometry seams that remain on <see cref="MainWindow"/> itself:
/// the XAML-matched constants + the pure screen working-area conversion
/// (the window owns the Screen access; the geometry state machine lives on
/// <see cref="WindowGeometryTracker"/> and is tested in
/// <see cref="WindowGeometryTrackerTests"/>).
/// </summary>
public sealed class MainWindowStateTests
{
    // ---- Constants ---------------------------------------------------------

    [Fact]
    public void Constants_are_the_documented_values()
    {
        Assert.Equal(960.0, MainWindow.DefaultWidth);
        Assert.Equal(640.0, MainWindow.DefaultHeight);
        Assert.Equal(720.0, MainWindow.MinWindowWidth);
        Assert.Equal(480.0, MainWindow.MinWindowHeight);
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
