using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Focused unit tests for the pure pane-length arithmetic on
/// <see cref="MainWindow"/>. The arithmetic is factored into a pure internal
/// helper (<see cref="MainWindow.ComputeOpenPaneLength"/>) so these tests can
/// exercise it without creating a live Avalonia Window (which would require a
/// headless UI-test framework, intentionally absent here per the project's
/// verification strategy). Live glyph measurement + the XAML update path are
/// covered by XAML compilation + operator visual testing.
/// </summary>
public sealed class MainWindowPaneLengthTests
{
    // The formula under test, mirrored from MainWindow.axaml.cs:
    //   ceil(48 icon + 12 margin + widestLabel + 16 trailing), clamped to [200, 360].

    [Fact]
    public void Below_minimum_returns_the_minimum()
    {
        // The shortest current localized label ("Nexus", 5 chars at the default
        // font scale) measures well under what 48 + 12 + width + 16 needs to
        // exceed 200, so the result clamps up to the design-time fallback.
        var result = MainWindow.ComputeOpenPaneLength(widestLabelWidth: 30);

        Assert.Equal(MainWindow.PaneOpenMin, result);
        Assert.Equal(200.0, result);
    }

    [Fact]
    public void A_realistic_wider_label_grows_above_the_minimum()
    {
        // A realistic wide label at an enlarged font scale (e.g. a long
        // translated word) grows the pane above 200 by exactly the
        // ceiling-rounded arithmetic. With widestLabelWidth = 130:
        //   ceil(48 + 12 + 130 + 16) = ceil(206) = 206.
        var result = MainWindow.ComputeOpenPaneLength(widestLabelWidth: 130);

        Assert.Equal(206, result);
        Assert.InRange(result, MainWindow.PaneOpenMin, MainWindow.PaneOpenMax);
    }

    [Fact]
    public void Fractional_label_width_rounds_up_to_a_whole_pixel()
    {
        // TextLayout widths are sub-pixel; the helper rounds up so a 130.4px
        // label still has a whole-pixel pane length that fits it.
        var result = MainWindow.ComputeOpenPaneLength(widestLabelWidth: 130.4);

        Assert.Equal(207, result);
    }

    [Fact]
    public void An_extreme_label_caps_at_the_maximum()
    {
        // Beyond 360px the per-label TextTrimming=CharacterEllipsis kicks in;
        // the pane itself never exceeds the cap regardless of label width.
        var result = MainWindow.ComputeOpenPaneLength(widestLabelWidth: 400);

        Assert.Equal(MainWindow.PaneOpenMax, result);
        Assert.Equal(360.0, result);
    }

    [Fact]
    public void Constants_are_the_documented_values()
    {
        // Pin the documented constants so a future tweak is deliberate.
        Assert.Equal(200.0, MainWindow.PaneOpenMin);
        Assert.Equal(360.0, MainWindow.PaneOpenMax);
        Assert.Equal(48.0, MainWindow.PaneIconColumn);
        Assert.Equal(12.0, MainWindow.PaneLabelMargin);
        Assert.Equal(16.0, MainWindow.PaneTrailingBreathingRoom);
    }
}
