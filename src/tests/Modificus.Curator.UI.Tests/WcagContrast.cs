namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Test-only WCAG 2.2 contrast math. Parses a <c>#RRGGBB</c> color, linearizes
/// each sRGB channel, and returns the contrast ratio between two colors.
/// Intentionally not a production abstraction: it exists only so the shell
/// styling tests can assert contrast thresholds directly against the app-owned
/// palette. See
/// <see href="https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html"/>.
/// </summary>
internal static class WcagContrast
{
    /// <summary>The WCAG threshold for normal-size text (Success Criterion 1.4.3).</summary>
    public const double TextThreshold = 4.5;

    /// <summary>
    /// The WCAG threshold for non-text contrast (control boundaries, focus
    /// indicators) per Success Criterion 1.4.11.
    /// </summary>
    public const double NonTextThreshold = 3.0;

    public static double Ratio(string a, string b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Linear(int channel)
    {
        // WCAG 2.2 sRGB linearization: the channel is divided by 12.92 at or
        // below the 0.04045 cutoff, otherwise raised through the gamma transfer.
        // https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (int r, int g, int b) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6)
            throw new FormatException($"Expected #RRGGBB, got '{hex}'.");
        return (
            Convert.ToInt32(h[0..2], 16),
            Convert.ToInt32(h[2..4], 16),
            Convert.ToInt32(h[4..6], 16));
    }
}
