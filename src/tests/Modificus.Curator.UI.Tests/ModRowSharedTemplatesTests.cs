using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the shared mod-row building blocks: the drag grip,
/// the badge cluster, and the action strip each have exactly ONE definition
/// (a DataTemplate resource in ModListView), hosted by both the Compact and
/// the Detailed row roots through <c>ContentControl.ContentTemplate</c>. The
/// assertions follow the <see cref="GamingModeGatingXamlTests"/> source-text
/// pattern (the XAML loads as XML after comments are stripped), so a regression
/// that reintroduces a duplicated row-control block (or drops a host + its
/// per-density styling) is a red test rather than a silent copy-drift.
/// </summary>
public sealed class ModRowSharedTemplatesTests
{
    private static XDocument LoadStrippedXaml(string relativeFromRepo)
    {
        var text = File.ReadAllText(RequireSourceFile(relativeFromRepo));
        text = Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return XDocument.Parse(text);
    }

    private static string? A(XElement e, string name) => (string?)e.Attribute(name);

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    [Fact]
    public void Every_shared_row_control_exists_exactly_once()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The interactive row controls exist once each: the action strip, the
        // badge cluster, and the grip are single shared DataTemplates, not
        // duplicated per row root.
        Assert.Single(Elements(xaml.Root!, "CheckBox"), cb => A(cb, "Click") == "Enabled_Click");
        Assert.Single(Elements(xaml.Root!, "ComboBox"), c => A(c, "SelectionChanged") == "Policy_Changed");
        Assert.Single(Elements(xaml.Root!, "ComboBox"), c => A(c, "SelectionChanged") == "PinnedVersion_Changed");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Click") == "Update_Click");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Click") == "MoveUp_Click");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Click") == "MoveDown_Click");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Click") == "ToggleOrderLock_Click");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Click") == "Remove_Click");
        Assert.Single(Elements(xaml.Root!, "Border"), b => A(b, "PointerPressed") == "Grip_PointerPressed");
        Assert.Single(Elements(xaml.Root!, "HyperlinkButton"), b => A(b, "Click") == "OpenFolder_Click");
    }

    [Fact]
    public void Both_row_roots_host_the_shared_templates()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // Each shared template is referenced by exactly two hosts: one inside
        // the Compact root (Grid.compactRow) and one inside the Detailed card.
        var hosts = Elements(xaml.Root!, "ContentControl")
            .Where(c => A(c, "ContentTemplate") is { } template
                && template.Contains("ModRow"))
            .ToList();

        Assert.Equal(2, hosts.Count(c => A(c, "ContentTemplate")!.Contains("ModRowGripTemplate")));
        Assert.Equal(2, hosts.Count(c => A(c, "ContentTemplate")!.Contains("ModRowBadgesTemplate")));
        Assert.Equal(2, hosts.Count(c => A(c, "ContentTemplate")!.Contains("ModRowActionStripTemplate")));

        // The Enabled checkbox label is density-driven from row state (the
        // shared strip binds EnabledLabel rather than a fixed content), so the
        // Detailed label + the Compact contentless checkbox come from one
        // definition.
        var checkbox = Assert.Single(
            Elements(xaml.Root!, "CheckBox"),
            cb => A(cb, "Click") == "Enabled_Click");
        Assert.Equal("{Binding EnabledLabel}", A(checkbox, "Content"));
        Assert.Equal("{Binding EnabledLabel}", A(checkbox, "AutomationProperties.Name"));
    }

    [Fact]
    public void The_compact_row_keeps_its_single_line_spacing_through_scoped_styles()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The Compact root carries the scoping class, and the page styles
        // supply the strip's compact margins (12 / 12 / 8 / 8 / 4 / 4 / 4,
        // exactly the former per-column margins) + zero item spacing. Without
        // these styles the compact strip would fall back to the Detailed
        // spacing, a silent visual change.
        var compactRoot = Assert.Single(
            Elements(xaml.Root!, "Grid"),
            g => A(g, "Classes")?.Contains("compactRow") == true);
        Assert.Equal("Auto,*,Auto,Auto", A(compactRoot, "ColumnDefinitions"));

        var styles = Elements(xaml.Root!, "Style")
            .Select(s => A(s, "Selector") ?? string.Empty)
            .ToList();
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > CheckBox", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > StackPanel", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > Panel.updateCell", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > Button.moveUp", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > Button.moveDown", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > Button.orderLock", styles);
        Assert.Contains("Grid.compactRow WrapPanel.actionStrip > Button.remove", styles);
    }

    [Fact]
    public void The_680dip_breakpoint_still_moves_the_strip_and_thumbnail()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The container query survives the extraction: the named card stays a
        // width container, and the query overrides the thumbnail size/row-span
        // plus the action strip's grid placement (now carried by the hosting
        // ContentControl.detailedActions rather than a WrapPanel child).
        var card = Assert.Single(
            Elements(xaml.Root!, "Border"),
            b => A(b, "Container.Name") == "detailedModRow");
        Assert.Equal("Width", A(card, "Container.Sizing"));

        var query = Assert.Single(Elements(xaml.Root!, "ContainerQuery"));
        Assert.Equal("max-width:680", A(query, "Query"));

        var selectors = query.Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Select(s => A(s, "Selector") ?? string.Empty)
            .ToList();
        Assert.Contains("ContentControl.detailedActions", selectors);
        Assert.Contains("Border.detailedThumb", selectors);
        Assert.Contains("Path.detailedPlaceholder", selectors);
    }

    // ---- required source lookup (the GamingModeGatingXamlTests pattern) ----

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
