using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural + contrast tests for the full-client launch overlay: the
/// in-window scrim + centered indeterminate progress card shown while
/// <see cref="ShellViewModel.IsLaunchAttemptInProgress"/> is true. Source
/// assertions read MainWindow.axaml / App.axaml / Strings.resx directly (the
/// established <see cref="ShellStylingTests"/> approach): the XAML loads as
/// XML after undeclared Avalonia markup prefixes are declared, so structure
/// (root Grid layering, bindings, hit-testability, control inventory) is
/// asserted semantically rather than by substring position. The runtime
/// attempt-state lifecycle is covered separately by
/// <see cref="ShellLaunchAttemptTests"/>; XAML compilation (the UI project
/// build) proves the markup itself is valid.
/// </summary>
public sealed class LaunchOverlayTests
{
    // ---- overlay palette (mirrors the CuratorLaunchOverlay* resources) -----
    private const string CardFace = "#30383F";
    private const string CardBorder = "#D86638";
    private const string Title = "#F4EEE9";
    private const string Message = "#C6CDD2";
    private const string Bar = "#D86638";
    private const string Track = "#242B31";

    private static readonly XDocument MainWindowXaml = LoadMainWindowXaml();
    private static readonly string AppXamlText =
        File.ReadAllText(RequireSourceFile("src/ui/App.axaml"));
    private static readonly string ResxText =
        File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx"));

    // x:Name is colon-qualified (xmlns:x); Avalonia attached properties such
    // as AutomationProperties.Name use dot notation and carry no namespace.
    private static readonly XNamespace Xns = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- structure helpers --------------------------------------------------

    /// <summary>The root Grid (the Window's single content child): the shell
    /// SplitView with the launch overlay layered above it.</summary>
    private static XElement RootGrid() =>
        MainWindowXaml.Root!.Elements().Single(e => e.Name.LocalName == "Grid");

    private static XElement SplitView() =>
        RootGrid().Elements().Single(e => e.Name.LocalName == "SplitView");

    private static XElement Overlay() =>
        RootGrid().Elements().Single(e => e.Name.LocalName == "Panel"
            && (string?)e.Attribute(Xns + "Name") == "LaunchAttemptOverlay");

    /// <summary>
    /// Loads MainWindow.axaml as XML (comments stripped so only functional
    /// markup parses). Avalonia's dotted attached properties
    /// (ToolTip.Tip, AutomationProperties.Name, ...) are plain dotted
    /// attribute names, so the document is well-formed XML as-is.
    /// </summary>
    private static XDocument LoadMainWindowXaml()
    {
        var text = File.ReadAllText(RequireSourceFile("src/ui/Views/MainWindow.axaml"));
        text = Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return XDocument.Parse(text);
    }

    private static string? A(XElement e, string name) => (string?)e.Attribute(name);

    // ---- binding + layering -------------------------------------------------

    [Fact]
    public void Overlay_binds_to_the_launch_attempt_state_and_disables_the_shell()
    {
        // The root Grid hosts exactly the shell + the overlay, and the overlay
        // is the final child (renders on top of the shell).
        var children = RootGrid().Elements().ToList();
        Assert.Equal(2, children.Count);
        Assert.Equal("SplitView", children[0].Name.LocalName);
        Assert.Equal("Panel", children[1].Name.LocalName);
        Assert.Same(children[1], Overlay());

        // The shell disables while an attempt is in progress (keyboard + any
        // residual pointer activation are blocked at the shell root).
        Assert.Equal("{Binding !IsLaunchAttemptInProgress}", A(SplitView(), "IsEnabled"));
        // The overlay shows for exactly the same state (no second state machine).
        Assert.Equal("{Binding IsLaunchAttemptInProgress}", A(Overlay(), "IsVisible"));

        // Binding validity: the path resolves on the window's DataContext type.
        var prop = typeof(ShellViewModel).GetProperty("IsLaunchAttemptInProgress");
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void Overlay_is_top_layered_hit_testable_and_carries_a_dimming_scrim()
    {
        var overlay = Overlay();
        // Explicit z-order on top of final-child layering (defense in depth).
        Assert.Equal("10", A(overlay, "ZIndex"));
        // The overlay itself must intercept input (the second input barrier
        // behind disabling the shell).
        Assert.Equal("True", A(overlay, "IsHitTestVisible"));
        // A semi-opaque scrim brush as the background both dims the client
        // area and makes the full surface hit-testable (a null background
        // would let pointer events fall through to the shell).
        var bg = A(overlay, "Background");
        Assert.NotNull(bg);
        Assert.Contains("CuratorLaunchOverlayScrimBrush", bg);
    }

    [Fact]
    public void Overlay_card_carries_localized_title_message_and_indeterminate_bar()
    {
        var texts = Overlay().Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => (string?)e.Attribute("Text"))
            .ToList();

        Assert.Contains(texts, t => t!.Contains("[Launch_OverlayTitle]"));
        Assert.Contains(texts, t => t!.Contains("[Launch_OverlayMessage]"));

        // The stock indeterminate ProgressBar (the Fluent ControlTheme's own
        // animation; no custom spinner control or class).
        var bar = Assert.Single(
            Overlay().Descendants(), e => e.Name.LocalName == "ProgressBar");
        Assert.Equal("True", A(bar, "IsIndeterminate"));
        Assert.Null(A(bar, "Classes"));
        // App-owned bar + track brushes (never the platform accent).
        Assert.Contains("CuratorLaunchOverlayBarBrush", A(bar, "Foreground"));
        Assert.Contains("CuratorLaunchOverlayTrackBrush", A(bar, "Background"));

        // The localized values exist verbatim, with a real ellipsis.
        Assert.Contains("<value>Launching Darktide</value>", ResxText);
        Assert.Contains("<value>Preparing your modded game…</value>", ResxText);
    }

    [Fact]
    public void Overlay_has_no_interactive_controls_or_commands()
    {
        // No Cancel affordance or any other interactive control: the overlay
        // subtree holds only layout/presentation elements, and nothing binds a
        // command or click handler.
        var interactive = new HashSet<string>
        {
            "Button", "HyperlinkButton", "ToggleButton", "RepeatButton",
            "TextBox", "CheckBox", "ComboBox", "ListBox", "Slider", "RadioButton",
            "SplitButton", "DropDownButton",
        };
        Assert.DoesNotContain(
            Overlay().DescendantsAndSelf(),
            e => interactive.Contains(e.Name.LocalName));
        Assert.DoesNotContain(
            Overlay().DescendantsAndSelf().Attributes(),
            a => a.Name.LocalName is "Command" or "Click" or "CommandParameter");
    }

    [Fact]
    public void Overlay_carries_declarative_accessibility_metadata()
    {
        var overlay = Overlay();
        // The overlay's accessible name is the localized title, declared as a
        // polite live region so its appearance is announced; no imperative
        // focus behavior (no focus trap, no code-behind accessibility logic).
        var name = A(overlay, "AutomationProperties.Name");
        Assert.NotNull(name);
        Assert.Contains("[Launch_OverlayTitle]", name);
        Assert.Equal("Polite", A(overlay, "AutomationProperties.LiveSetting"));

        // The card names itself with the localized message.
        var card = Overlay().Elements().Single(e => e.Name.LocalName == "Border");
        var cardName = A(card, "AutomationProperties.Name");
        Assert.NotNull(cardName);
        Assert.Contains("[Launch_OverlayMessage]", cardName);
    }

    [Fact]
    public void Launch_button_surface_is_unchanged()
    {
        // The overlay is the additional feedback; the Launch button keeps its
        // command, play icon, uppercase display label, tooltip, and accessible
        // name exactly as before.
        var launch = MainWindowXaml.Root!.Descendants()
            .Single(e => e.Name.LocalName == "Button"
                && ((string?)e.Attribute("Classes") ?? string.Empty).Contains("launchAction"));
        Assert.Equal("{Binding LaunchCommand}", A(launch, "Command"));
        Assert.Contains(launch.Descendants()
            .Select(d => (string?)d.Attribute("Data")),
            d => d is not null && d.Contains("M8 5v14l11-7z"));
        Assert.Contains("[Launch_Button]", A(launch, "ToolTip.Tip"));
        Assert.Contains("[Launch_Button]", A(launch, "AutomationProperties.Name"));
        Assert.Contains(launch.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => (string?)e.Attribute("Text")),
            t => t!.Contains("[Launch_ButtonDisplay]"));
    }

    [Fact]
    public void Native_window_chrome_is_not_suppressed()
    {
        // The overlay is in-window client-area content: the Window keeps its
        // system decorations (no ExtendClientAreaToDecorationsHint /
        // WindowDecorations / SystemDecorations changes; only the modal
        // ProgressDialog customizes its chrome).
        var window = MainWindowXaml.Root!;
        Assert.Null(A(window, "ExtendClientAreaToDecorationsHint"));
        Assert.Null(A(window, "WindowDecorations"));
        Assert.Null(A(window, "SystemDecorations"));
    }

    // ---- contrast (both themes share the theme-independent card) -----------

    [Theory]
    [InlineData(Title, CardFace, "title")]
    [InlineData(Message, CardFace, "message")]
    public void Overlay_text_meets_contrast_on_the_card_face(string fg, string bg, string label)
    {
        Assert.True(
            WcagContrast.Ratio(fg, bg) >= WcagContrast.TextThreshold,
            $"Overlay {label} on the card face must be >= {WcagContrast.TextThreshold}:1, " +
            $"got {WcagContrast.Ratio(fg, bg):F2}:1.");
    }

    [Theory]
    [InlineData(CardBorder, CardFace, "card border vs card face")]
    [InlineData(Bar, Track, "progress bar vs track")]
    [InlineData(Bar, CardFace, "progress bar vs card face")]
    public void Overlay_non_text_surfaces_meet_contrast(string fg, string bg, string label)
    {
        Assert.True(
            WcagContrast.Ratio(fg, bg) >= WcagContrast.NonTextThreshold,
            $"Overlay {label} must be >= {WcagContrast.NonTextThreshold}:1, " +
            $"got {WcagContrast.Ratio(fg, bg):F2}:1.");
    }

    [Fact]
    public void App_axaml_defines_the_overlay_palette_asserted_above()
    {
        // Drift guard: the constants above match the app-owned resources.
        // The card brushes are theme-independent; the scrim differs per theme.
        foreach (var hex in new[] { CardFace, CardBorder, Title, Message, Bar, Track })
        {
            Assert.Contains(hex, AppXamlText);
        }
        Assert.Contains("CuratorLaunchOverlayScrimBrush", AppXamlText);
        Assert.Contains("#8C000000", AppXamlText); // Light scrim (55% black)
        Assert.Contains("#D9000000", AppXamlText); // Dark scrim (85% black)
        // The overlay markup references every app-owned resource by name.
        var overlayText = Overlay().ToString();
        foreach (var key in new[]
        {
            "CuratorLaunchOverlayScrimBrush", "CuratorLaunchOverlayCardFaceBrush",
            "CuratorLaunchOverlayCardBorderBrush", "CuratorLaunchOverlayTitleBrush",
            "CuratorLaunchOverlayMessageBrush", "CuratorLaunchOverlayBarBrush",
            "CuratorLaunchOverlayTrackBrush",
        })
        {
            Assert.Contains(key, overlayText);
        }
    }

    [Fact]
    public void Overlay_strings_avoid_an_em_dash()
    {
        foreach (var line in ResxText.Split('\n'))
        {
            if (line.Contains("Launch_Overlay"))
            {
                Assert.DoesNotContain("\u2014", line);
            }
        }
    }

    // ---- required source lookup (the ShellStylingTests pattern) ------------

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
}
