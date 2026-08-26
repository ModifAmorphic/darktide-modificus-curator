using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the load-order card's entry + hosting: the sticky
/// fifth Add mode on the split button's flyout, the card hosted below the
/// import-workflow card, and the reserved resolver columns in the review
/// table's layout (the header + row grids share identical column
/// definitions so activating the id/version cells never reshuffles the
/// table). Source assertions read the .axaml files directly (the
/// <see cref="GamingModeGatingXamlTests"/> approach); the VM-level behavior
/// (activation, mutual exclusion, apply) is covered by
/// <see cref="LoadOrderImportViewModelTests"/>; XAML compilation (the UI
/// project build) proves the markup itself is valid.
/// </summary>
public sealed class LoadOrderXamlTests
{
    private static XDocument LoadXaml(string relativeFromRepo)
    {
        var text = File.ReadAllText(RequireSourceFile(relativeFromRepo));
        // Comments can contain raw illustrations of attribute shapes; strip
        // them so only functional markup parses.
        text = Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return XDocument.Parse(text);
    }

    private static string? A(XElement e, string name) => (string?)e.Attribute(name);

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    [Fact]
    public void The_fifth_flyout_item_starts_the_load_order_mode()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        var item = Assert.Single(
            Elements(xaml.Root!, "MenuItem"),
            m => A(m, "Click") == "ImportLoadOrder_Click");
        Assert.Equal(
            "{ReflectionBinding [ModList_AddLoadOrder], Source={StaticResource Loc}}",
            A(item, "Header"));

        // The mode exists + the face label tracks it.
        Assert.Contains(typeof(ModAddMode).GetEnumNames(), n => n == nameof(ModAddMode.LoadOrder));
        Assert.NotNull(typeof(ModListViewModel).GetProperty(nameof(ModListViewModel.AddModeLabel)));
    }

    [Fact]
    public void The_load_order_card_is_hosted_below_the_import_card()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        // Both card hosts are siblings in the page grid; the load-order card
        // sits one row below the import card and never stacks with it (the
        // shared card gate makes them mutually exclusive).
        var importCard = Assert.Single(
            Elements(xaml.Root!, "Panel"),
            p => A(p, "IsVisible") == "{Binding ImportWorkflow.IsBatchActive}");
        var loadOrderCard = Assert.Single(
            Elements(xaml.Root!, "Panel"),
            p => A(p, "IsVisible") == "{Binding LoadOrder.IsActive}");
        Assert.Single(loadOrderCard.Descendants(), e => e.Name.LocalName == "LoadOrderImportView");
        Assert.Equal(
            int.Parse(A(importCard, "Grid.Row")!) + 1,
            int.Parse(A(loadOrderCard, "Grid.Row")!));
    }

    [Fact]
    public void The_review_table_header_and_rows_share_one_column_layout()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // The reserved resolver columns (mod id + version) exist in BOTH
        // definitions at fixed widths, so activating their cells later adds
        // content without reshuffling the table. The duplicated definition
        // strings are pinned identical: drift between the header + the row
        // template is a red test, not silent misalignment.
        // The header grid carries the column headers; distinguish it from the
        // row grid (same definitions) by its header bindings.
        var headerGrid = Elements(xaml.Root!, "Grid")
            .Single(g => (A(g, "ColumnDefinitions") ?? string.Empty).Contains("110,80")
                && g.Descendants().Any(d =>
                    (string?)d.Attribute("Text") == "{ReflectionBinding [LoadOrder_FileNameHeader], Source={StaticResource Loc}}"));
        var rowTemplate = Assert.Single(
            Elements(xaml.Root!, "Grid"),
            g => g != headerGrid
                && (A(g, "ColumnDefinitions") ?? string.Empty) == A(headerGrid, "ColumnDefinitions")
                && g.Ancestors().Any(a => a.Name.LocalName == "DataTemplate"));

        var columns = A(headerGrid, "ColumnDefinitions")!;
        Assert.Equal(7, columns.Split(',').Length);

        // The include checkbox binds the row's state two-way + unresolved
        // rows disable it.
        var checkbox = Assert.Single(Elements(rowTemplate, "CheckBox"));
        Assert.Equal("{Binding IsIncluded, Mode=TwoWay}", A(checkbox, "IsChecked"));
        Assert.Equal("{Binding IsIncludeEnabled}", A(checkbox, "IsEnabled"));

        // The open-on-Nexus link rides the last column, unresolved rows only.
        var link = Assert.Single(Elements(rowTemplate, "HyperlinkButton"));
        Assert.Equal("{Binding IsUnresolved}", A(link, "IsVisible"));
        Assert.Equal("OpenOnNexus_Click", A(link, "Click"));

        // The identification cells: the manual id/URL entry + the version
        // field live INSIDE the fixed columns (no definition change), and
        // their visibility keys off the row's identification projections so
        // the cells activate without reshuffling the table.
        var textBoxes = Elements(rowTemplate, "TextBox").ToList();
        Assert.Equal(2, textBoxes.Count);
        var manual = Assert.Single(textBoxes, t =>
            (string?)t.Attribute("Text") == "{Binding ManualId, Mode=TwoWay}");
        // The manual cell rides a Panel host pinned to column 4.
        Assert.Equal("4", (string?)((manual.Parent as XElement)?.Attribute("Grid.Column")));
        var version = Assert.Single(textBoxes, t =>
            (string?)t.Attribute("Text") == "{Binding Version, Mode=TwoWay}");
        Assert.Equal("5", (string?)version.Attribute("Grid.Column"));

        // The row's DataTemplate (the vertical host carrying the row grid +
        // everything that renders below it).
        var template = rowTemplate.Ancestors()
            .Single(a => a.Name.LocalName == "DataTemplate");

        // The per-line apply failure (a failed sibling import, or a download
        // resolve that produced no item) renders inside the row template,
        // keyed off the row's LineFailure.
        Assert.Contains(Elements(template, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding LineFailure}");

        // The candidate workspace + the expand affordance exist in the row's
        // DataTemplate (they render BELOW the row grid, inside the template's
        // vertical host), keyed off the row's workspace projections.
        Assert.Contains(Elements(template, "Button"),
            b => (string?)b.Attribute("Click") == "AcceptCandidate_Click");
        Assert.Contains(Elements(template, "Button"),
            b => (string?)b.Attribute("Click") == "ToggleAlternates_Click");
        // The workspace's own visibility keys off the row's projection.
        var workspace = Assert.Single(
            Elements(template, "StackPanel"),
            s => (string?)s.Attribute("IsVisible") == "{Binding ShowCandidateWorkspace}");
    }

    [Fact]
    public void The_alternate_accept_resolves_the_row_by_a_typed_ancestor_walk()
    {
        // The alternates ItemsControl wraps each candidate in its own
        // ContentPresenter, so the NEAREST presenter's DataContext is the
        // candidate, not the row. The handler must walk the presenters and
        // take the first DataContext of the row type; a first-ancestor cast
        // yields null on every alternate accept (a UI-runtime break the VM
        // tests cannot catch). Pinned as a source assertion.
        var text = File.ReadAllText(RequireSourceFile(
            "src/ui/Views/LoadOrderImportView.axaml.cs"));
        Assert.Contains("GetVisualAncestors()", text);
        Assert.Contains("OfType<ContentPresenter>()", text);
        Assert.Contains(".OfType<LoadOrderRowViewModel>()", text);
        Assert.DoesNotContain("FindAncestorOfType<ContentPresenter>()", text);
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
