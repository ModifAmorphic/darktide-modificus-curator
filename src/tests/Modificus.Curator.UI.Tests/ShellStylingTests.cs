using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Contrast + structural tests for the two shell styling surfaces in issue
/// #181: the app self-update status-strip pill (theme-safe, independent of the
/// platform accent) and the global Launch Darktide action (the iron-and-rust
/// branded primary action). The contrast math is the small test-only
/// <see cref="WcagContrast"/> helper; the palette constants mirror the app-owned
/// resources in App.axaml (a drift guard below ties the two together). Source
/// assertions read the XAML/csproj/asset files directly so a regression that
/// re-introduces a platform accent or drops a state is caught at build time.
/// These are repository source tests: <see cref="RequireSourceFile"/> walks up
/// to the repo root and fails clearly if the layout regressed (no silent skip).
/// </summary>
public sealed class ShellStylingTests
{
    // ---- Launch palette (theme-independent branded primary action) ----------
    // Mirrors the CuratorLaunch* resources in App.axaml.
    private const string LaunchFace = "#30383F";
    private const string LaunchFaceHover = "#3A444C";
    private const string LaunchFacePressed = "#242B31";
    private const string LaunchText = "#F4EEE9";   // constant across enabled states
    private const string LaunchRust = "#D86638";
    private const string LaunchRustHover = "#E47742";
    private const string LaunchRustPressed = "#C95A32";
    private const string LaunchFocus = "#57E8F6";

    // ---- Update-notice palette (Light) -------------------------------------
    private const string NoticeLightBg = "#E5F8FA";
    private const string NoticeLightHover = "#D2F0F3";
    private const string NoticeLightPressed = "#BDE5E9";
    private const string NoticeLightFg = "#004B53";
    private const string NoticeLightBorder = "#007C89";
    private const string NoticeLightFocus = "#004B53";

    // ---- Update-notice palette (Dark) --------------------------------------
    private const string NoticeDarkBg = "#0C343B";
    private const string NoticeDarkHover = "#124650";
    private const string NoticeDarkPressed = "#185864";
    private const string NoticeDarkFg = "#D7FAFD";
    private const string NoticeDarkBorder = "#42D3DF";
    private const string NoticeDarkFocus = "#72E4EC";

    // ---- Fluent 12.1 region background ------------------------------------
    // The status strip / window surface the pill floats on. SystemRegionBrush
    // resolves from SystemRegionColor in the pinned Fluent BaseColorsPalette.xaml
    // (tag 12.1.0): #FFFFFF in the Light dictionary, #000000 in the Dark
    // dictionary. The outer pill boundary is the app-owned border against this.
    private const string FluentLightRegion = "#FFFFFF";
    private const string FluentDarkRegion = "#000000";

    // =====================================================================
    // Launch: text/background contrast (>= 4.5:1, every enabled state)
    // =====================================================================

    [Theory]
    [InlineData(LaunchText, LaunchFace, "normal")]
    [InlineData(LaunchText, LaunchFaceHover, "hover")]
    [InlineData(LaunchText, LaunchFacePressed, "pressed")]
    public void Launch_text_meets_contrast_on_each_enabled_face(
        string text, string face, string state)
    {
        Assert.True(
            WcagContrast.Ratio(text, face) >= WcagContrast.TextThreshold,
            $"Launch text on {state} face must be >= {WcagContrast.TextThreshold}:1, " +
            $"got {WcagContrast.Ratio(text, face):F2}:1.");
    }

    // =====================================================================
    // Launch: rust boundary edge (>= 3:1, every enabled state)
    // =====================================================================

    [Theory]
    [InlineData(LaunchRust, LaunchFace, "normal")]
    [InlineData(LaunchRustHover, LaunchFaceHover, "hover")]
    [InlineData(LaunchRustPressed, LaunchFacePressed, "pressed")]
    public void Launch_rust_edge_meets_non_text_contrast_on_each_state(
        string rust, string face, string state)
    {
        Assert.True(
            WcagContrast.Ratio(rust, face) >= WcagContrast.NonTextThreshold,
            $"Launch rust edge on {state} face must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(rust, face):F2}:1.");
    }

    // =====================================================================
    // Launch: keyboard focus indicator (>= 3:1, every enabled face, since
    // :focus-visible can coexist with hover/pressed and wins the border by
    // ordering)
    // =====================================================================

    [Theory]
    [InlineData(LaunchFocus, LaunchFace, "normal")]
    [InlineData(LaunchFocus, LaunchFaceHover, "hover")]
    [InlineData(LaunchFocus, LaunchFacePressed, "pressed")]
    public void Launch_focus_indicator_meets_non_text_contrast(
        string focus, string face, string state)
    {
        Assert.True(
            WcagContrast.Ratio(focus, face) >= WcagContrast.NonTextThreshold,
            $"Launch focus indicator on {state} face must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(focus, face):F2}:1.");
    }

    // =====================================================================
    // Update notice: text/background contrast (>= 4.5:1, every enabled state,
    // both themes)
    // =====================================================================

    [Theory]
    [InlineData(NoticeLightFg, NoticeLightBg, "Light", "normal")]
    [InlineData(NoticeLightFg, NoticeLightHover, "Light", "hover")]
    [InlineData(NoticeLightFg, NoticeLightPressed, "Light", "pressed")]
    [InlineData(NoticeDarkFg, NoticeDarkBg, "Dark", "normal")]
    [InlineData(NoticeDarkFg, NoticeDarkHover, "Dark", "hover")]
    [InlineData(NoticeDarkFg, NoticeDarkPressed, "Dark", "pressed")]
    public void Notice_text_meets_contrast_in_each_theme_and_state(
        string fg, string bg, string theme, string state)
    {
        Assert.True(
            WcagContrast.Ratio(fg, bg) >= WcagContrast.TextThreshold,
            $"{theme} notice text on {state} bg must be >= {WcagContrast.TextThreshold}:1, " +
            $"got {WcagContrast.Ratio(fg, bg):F2}:1.");
    }

    // =====================================================================
    // Update notice: outer pill boundary (>= 3:1). The pill border against the
    // Fluent region surface it floats on (SystemRegionBrush: #FFFFFF Light,
    // #000000 Dark). This is the rendered outer adjacency (WCAG 1.4.11).
    // =====================================================================

    [Theory]
    [InlineData(NoticeLightBorder, FluentLightRegion, "Light border vs Fluent region")]
    [InlineData(NoticeDarkBorder, FluentDarkRegion, "Dark border vs Fluent region")]
    public void Notice_outer_border_meets_non_text_contrast_vs_region(
        string border, string region, string label)
    {
        Assert.True(
            WcagContrast.Ratio(border, region) >= WcagContrast.NonTextThreshold,
            $"Notice outer {label} must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(border, region):F2}:1.");
    }

    // =====================================================================
    // Update notice: inner pill boundary (>= 3:1). The border against the pill's
    // own background (the inner adjacency). The hover/pressed shades are NOT
    // tested here: they apply only to the inner link/dismiss presenters, not the
    // outer pill border.
    // =====================================================================

    [Theory]
    [InlineData(NoticeLightBorder, NoticeLightBg, "Light border vs pill bg")]
    [InlineData(NoticeDarkBorder, NoticeDarkBg, "Dark border vs pill bg")]
    public void Notice_inner_border_meets_non_text_contrast_vs_pill_background(
        string border, string bg, string label)
    {
        Assert.True(
            WcagContrast.Ratio(border, bg) >= WcagContrast.NonTextThreshold,
            $"Notice inner {label} must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(border, bg):F2}:1.");
    }

    // =====================================================================
    // Update notice: focus indicator (>= 3:1) against every inner background it
    // can sit on, both themes. :focus-visible can coexist with pointerover /
    // pressed and its style wins the border by ordering, so the focus ring must
    // read against the normal, hover, and pressed shades alike.
    // =====================================================================

    [Theory]
    [InlineData(NoticeLightFocus, NoticeLightBg, "Light focus vs normal")]
    [InlineData(NoticeLightFocus, NoticeLightHover, "Light focus vs hover")]
    [InlineData(NoticeLightFocus, NoticeLightPressed, "Light focus vs pressed")]
    [InlineData(NoticeDarkFocus, NoticeDarkBg, "Dark focus vs normal")]
    [InlineData(NoticeDarkFocus, NoticeDarkHover, "Dark focus vs hover")]
    [InlineData(NoticeDarkFocus, NoticeDarkPressed, "Dark focus vs pressed")]
    public void Notice_focus_indicator_meets_non_text_contrast(
        string focus, string bg, string label)
    {
        Assert.True(
            WcagContrast.Ratio(focus, bg) >= WcagContrast.NonTextThreshold,
            $"Notice {label} must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(focus, bg):F2}:1.");
    }

    // =====================================================================
    // Structural: the notice + launch styles are theme-accent-independent
    // and cover every required state. Reads the source files directly.
    // =====================================================================

    [Fact]
    public void MainWindow_does_not_reference_uncontrolled_platform_accent()
    {
        // The shell markup must be free of the two resources that made the notice
        // disappear under a low-contrast SteamOS accent (the only prior uses
        // were the update pill). Guards against a regression. XAML comments are
        // stripped first so explanatory prose is not mistaken for usage.
        var xaml = RequireSourceFile("src/ui/Views/MainWindow.axaml");
        var text = WithoutXmlComments(File.ReadAllText(xaml));
        Assert.DoesNotContain("SystemAccentColor", text);
        Assert.DoesNotContain("SystemControlForegroundAccentBrush", text);
    }

    [Fact]
    public void Update_notice_link_styles_cover_every_state()
    {
        var text = File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml"));
        // The scoped link class pins foreground/background on the Fluent
        // ContentPresenter so the theme's own accent-reapplying setters cannot
        // win (class-qualified selectors outrank the ControlTheme's).
        Assert.Contains("HyperlinkButton.updateNoticeLink", text);
        Assert.Contains("HyperlinkButton.updateNoticeLink:pointerover", text);
        Assert.Contains("HyperlinkButton.updateNoticeLink:pressed", text);
        Assert.Contains("HyperlinkButton.updateNoticeLink:disabled", text);
        Assert.Contains("HyperlinkButton.updateNoticeLink:focus-visible", text);
        // The pill + link draw only from app-owned resources.
        Assert.Contains("CuratorUpdateNoticeBackgroundBrush", text);
        Assert.Contains("CuratorUpdateNoticeForegroundBrush", text);
    }

    [Fact]
    public void Update_notice_dismiss_styles_cover_every_state()
    {
        var text = File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml"));
        Assert.Contains("Button.updateNoticeDismiss", text);
        Assert.Contains("Button.updateNoticeDismiss:pointerover", text);
        Assert.Contains("Button.updateNoticeDismiss:pressed", text);
        Assert.Contains("Button.updateNoticeDismiss:disabled", text);
        Assert.Contains("Button.updateNoticeDismiss:focus-visible", text);
    }

    [Fact]
    public void Launch_style_is_branded_and_state_complete()
    {
        var text = File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml"));
        // The branded class + every required state.
        Assert.Contains("Button.launchAction", text);
        Assert.Contains("Button.launchAction:pointerover", text);
        Assert.Contains("Button.launchAction:pressed", text);
        Assert.Contains("Button.launchAction:disabled", text);
        Assert.Contains("Button.launchAction:focus-visible", text);
        // Quantico Bold display font, referenced by its internal family so a
        // missing family does not silently fall back. Bold weight, 1-DIP letter
        // spacing, and strong hinting + baseline alignment keep it crisp at
        // button size and fractional scaling.
        Assert.Contains("Quantico-Bold.ttf#Quantico", text);
        Assert.Contains("FontWeight=\"Bold\"", text);
        Assert.Contains("LetterSpacing=\"1\"", text);
        Assert.Contains("TextOptions.TextHintingMode=\"Strong\"", text);
        Assert.Contains("TextOptions.BaselinePixelAlignment=\"Aligned\"", text);
        // Minimum 44 DIP touch target.
        Assert.Contains("MinHeight", text);
        Assert.Contains("\"44\"", text);
        // A drawn play-icon Path (no Unicode glyph): the Material play_arrow data.
        Assert.Contains("M8 5v14l11-7z", text);
        // App-owned face + rust resources (never the platform accent).
        Assert.Contains("CuratorLaunchFaceBrush", text);
        Assert.Contains("CuratorLaunchRustBrush", text);
        Assert.Contains("CuratorLaunchFocusBrush", text);
    }

    [Fact]
    public void Launch_uses_uppercase_display_label_with_accessible_name_intact()
    {
        // The visible label is the uppercase display form; the accessible name +
        // tooltip stay the ordinary Launch_Button (verified by resource presence
        // so the two stay distinct).
        var text = File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx"));
        Assert.Contains("Launch_ButtonDisplay", text);
        Assert.Contains("LAUNCH DARKTIDE", text);
        // The accessible-name resource is retained.
        Assert.Contains("name=\"Launch_Button\"", text);
        Assert.Contains("Launch Darktide", text);
    }

    // =====================================================================
    // Font asset + license: present, unmodified, embedded, and shipped to
    // build + publish output.
    // =====================================================================

    [Fact]
    public void Quantico_bold_font_is_present_unmodified_and_embedded()
    {
        // The exact TTF from Google Fonts (ofl/quantico/Quantico-Bold.ttf).
        // Verifying the SHA-256 guards against an altered/substituted file.
        const string expectedSha256 =
            "e3a88a18c85bfa8c08577abb07e3d490e9264fc09c3c690c6de382c8628901ff";

        var fontPath = RequireSourceFile("src/ui/Assets/fonts/Quantico-Bold.ttf");
        using var stream = File.OpenRead(fontPath);
        var hash = BitConverter.ToString(SHA256.HashData(stream)).Replace("-", "").ToLowerInvariant();
        Assert.Equal(expectedSha256, hash);

        // Embedded as an AvaloniaResource (referenced by the avares URI), not
        // just present on disk.
        var projText = File.ReadAllText(RequireSourceFile("src/ui/Modificus.Curator.UI.csproj"));
        Assert.Contains("<AvaloniaResource Include=\"Assets/fonts/Quantico-Bold.ttf\" />", projText);
    }

    [Fact]
    public void Quantico_license_and_copyright_ship_alongside()
    {
        var licensePath = RequireSourceFile("src/ui/Assets/fonts/OFL.txt");
        var text = File.ReadAllText(licensePath);
        Assert.Contains("SIL OPEN FONT LICENSE", text);
        Assert.Contains("Matthew Desmond", text);
        Assert.Contains("Reserved Font Name Quantico", text);
    }

    [Fact]
    public void Quantico_license_is_copied_to_build_and_publish_output()
    {
        // The SIL OFL 1.1 license must ship in both build and publish output
        // (the font is bundled in the assembly; the human-readable license ships
        // as a sibling file). Asserts the copy metadata on the OFL.txt item, not
        // merely source presence. (Verified end to end by a real publish.)
        var doc = XDocument.Load(RequireSourceFile("src/ui/Modificus.Curator.UI.csproj"));
        var none = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "None" &&
            (string?)e.Attribute("Include") == "Assets/fonts/OFL.txt");
        Assert.True(none is not null,
            "OFL.txt must be declared as a None item in the UI csproj.");
        Assert.Equal("PreserveNewest", (string?)none!.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)none.Attribute("CopyToPublishDirectory"));
    }

    [Fact]
    public void Drawn_icon_paths_use_working_button_ancestor_binding()
    {
        // Operator verification proved the $parent[ContentPresenter].Foreground
        // ancestor does not resolve for a Path placed as button content, leaving
        // the icon invisible; $parent[Button].Foreground does. Both drawn icons
        // (the Launch play arrow and the update-notice dismiss close) must use the
        // working Button binding. Only the icon Fill binding is asserted absent;
        // TextBlock/ContentPresenter styling is not touched by this check.
        var stripped = WithoutXmlComments(
            File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml")));

        var buttonBindings = System.Text.RegularExpressions.Regex.Matches(
            stripped,
            System.Text.RegularExpressions.Regex.Escape("$parent[Button].Foreground")).Count;
        Assert.True(buttonBindings >= 2,
            $"Both drawn icons must bind Fill to $parent[Button].Foreground; found {buttonBindings}.");

        // The obsolete, non-resolving icon binding must not appear in functional
        // markup (it may appear in comments, which are stripped above).
        Assert.DoesNotContain("$parent[ContentPresenter].Foreground", stripped);

        // The dismiss icon (bound to the button foreground) stays app-owned: the
        // scoped Button.updateNoticeDismiss style pins the Button-level Foreground
        // to the app-owned notice brush (the ContentPresenter setters alone would
        // not reach the Path).
        Assert.Contains("Selector=\"Button.updateNoticeDismiss\"", stripped);
        Assert.Contains("CuratorUpdateNoticeForegroundBrush", stripped);
    }

    [Fact]
    public void Russo_one_asset_and_references_are_fully_removed()
    {
        // Quantico Bold replaced Russo One. The TTF must be gone and no Russo
        // reference may linger in source, resources, the project file, or the
        // app-owned resource dictionary.
        var fontPath = Path.Combine(
            RepoRoot(), "src", "ui", "Assets", "fonts", "RussoOne-Regular.ttf");
        Assert.False(File.Exists(fontPath),
            $"Russo One TTF must be removed; found {fontPath}.");

        var projText = File.ReadAllText(RequireSourceFile("src/ui/Modificus.Curator.UI.csproj"));
        Assert.DoesNotContain("Russo", projText);

        var xamlText = File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml"));
        Assert.DoesNotContain("Russo", xamlText);

        var resxText = File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx"));
        Assert.DoesNotContain("Russo", resxText);

        var appText = File.ReadAllText(RequireSourceFile("src/ui/App.axaml"));
        Assert.DoesNotContain("Russo", appText);
    }

    // =====================================================================
    // Drift guard: the palette constants in these tests match the app-owned
    // resources in App.axaml, so the two cannot diverge silently.
    // =====================================================================

    [Fact]
    public void App_axaml_defines_the_palette_asserted_above()
    {
        var text = File.ReadAllText(RequireSourceFile("src/ui/App.axaml"));
        // Launch (theme-independent).
        Assert.Contains("#30383F", text);
        Assert.Contains("#3A444C", text);
        Assert.Contains("#242B31", text);
        Assert.Contains("#F4EEE9", text);
        Assert.Contains("#D86638", text);
        Assert.Contains("#E47742", text);
        Assert.Contains("#C95A32", text);
        Assert.Contains("#57E8F6", text);
        // Notice Light.
        Assert.Contains("#E5F8FA", text);
        Assert.Contains("#D2F0F3", text);
        Assert.Contains("#BDE5E9", text);
        Assert.Contains("#004B53", text);
        Assert.Contains("#007C89", text);
        // Notice Dark.
        Assert.Contains("#0C343B", text);
        Assert.Contains("#124650", text);
        Assert.Contains("#185864", text);
        Assert.Contains("#D7FAFD", text);
        Assert.Contains("#42D3DF", text);
        Assert.Contains("#72E4EC", text);
    }

    // =====================================================================
    // Required source lookup: walk up from the test bin dir to the repo root
    // (the directory holding src/modificus-curator.sln), robust for local and
    // CI layouts. Fails clearly when the root or the file is absent rather than
    // silently skipping (these are repository source tests).
    // =====================================================================

    private static string RequireSourceFile(string relativeFromRepo)
    {
        var path = Path.Combine(
            RepoRoot(),
            relativeFromRepo.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path),
            $"Required source file missing: {path}. " +
            "A layout regression moved or removed it.");
        return path;
    }

    /// <summary>
    /// Walks up from the test bin dir to the repo root (the directory holding
    /// <c>src/modificus-curator.sln</c>), robust for local and CI layouts. Fails
    /// clearly when the root is absent (these are repository source tests).
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "modificus-curator.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        Assert.Fail(
            "Could not locate the repository root (src/modificus-curator.sln) " +
            "from the test output directory. These are repository source tests " +
            "and must run from a build inside the repo.");
        return null!; // unreachable; the assertion above throws.
    }

    /// <summary>
    /// Strips <c>&lt;!-- ... --&gt;</c> XAML comments (including multiline) so
    /// source assertions target functional markup, not explanatory prose.
    /// </summary>
    private static string WithoutXmlComments(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
}
