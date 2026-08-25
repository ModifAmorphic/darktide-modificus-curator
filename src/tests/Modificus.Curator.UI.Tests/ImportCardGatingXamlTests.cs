using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the import card's activity gating on the Mods page:
/// the toolbar's projection-touching controls (the search box, the density +
/// filter cluster, the check-now refresh cluster) disable while the card is
/// active in either mode, and the edit mode's name field locks as read-only
/// rather than disabled (read-only text renders at full contrast in the
/// Fluent dark theme; disabled text is near-invisible, which read as an empty
/// field). Source assertions read the .axaml files directly (the
/// <see cref="GamingModeGatingXamlTests"/> approach): the XAML loads as XML
/// after comments are stripped, so bindings + attributes are asserted
/// semantically. The VM-level behavior (the gate's value + propagation, the
/// row-command exemption) is covered by <see cref="ModListViewModelTests"/>
/// and <see cref="ImportWorkflowEditModeTests"/>; XAML compilation (the UI
/// project build) proves the markup itself is valid.
/// </summary>
public sealed class ImportCardGatingXamlTests
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
    public void The_toolbar_projection_controls_disable_while_the_card_is_active()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        // The search box itself...
        var search = Assert.Single(
            Elements(xaml.Root!, "TextBox"),
            t => A(t, "Text") == "{Binding SearchText, Mode=TwoWay}");
        Assert.Equal("{Binding IsListToolingEnabled}", A(search, "IsEnabled"));

        // ...and the two toolbar clusters: the refresh cluster (hosting the
        // check-now button + its spinner) and the density + filter cluster
        // (hosting both density buttons + the hide-disabled and updates-only
        // toggles). One binding per cluster covers every control inside
        // (IsEnabled cascades from the parent).
        var refreshButton = Assert.Single(
            Elements(xaml.Root!, "Button"),
            b => A(b, "Click") == "RefreshUpdates_Click");
        var refreshCluster = refreshButton.Ancestors()
            .Single(e => e.Name.LocalName == "StackPanel");
        Assert.Equal("{Binding IsListToolingEnabled}", A(refreshCluster, "IsEnabled"));

        var densityButtons = Elements(xaml.Root!, "Button")
            .Where(b => (A(b, "Classes") ?? string.Empty).Contains("density"))
            .ToList();
        Assert.Equal(4, densityButtons.Count); // 2 density + 2 filter toggles
        foreach (var cluster in densityButtons
            .Select(b => b.Ancestors().Single(e => e.Name.LocalName == "StackPanel"))
            .Distinct())
        {
            Assert.Equal("{Binding IsListToolingEnabled}", A(cluster, "IsEnabled"));
        }

        // Binding validity: the path resolves on the mod-list VM.
        Assert.NotNull(typeof(ModListViewModel).GetProperty("IsListToolingEnabled"));
    }

    [Fact]
    public void Only_the_toolbar_binds_the_tooling_gate()
    {
        // The lock is the toolbar only: across the whole document exactly the
        // search box + the two toolbar clusters bind the gate, and no row
        // control (a Button, ComboBox, or CheckBox anywhere in the shared
        // templates) does.
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");
        var gated = xaml.Root!.Descendants()
            .Where(e => (string?)e.Attribute("IsEnabled") == "{Binding IsListToolingEnabled}")
            .ToList();
        Assert.Equal(3, gated.Count);
        Assert.Equal(1, gated.Count(e => e.Name.LocalName == "TextBox"));
        Assert.Equal(2, gated.Count(e => e.Name.LocalName == "StackPanel"));
        Assert.DoesNotContain(gated, e =>
            e.Name.LocalName is "Button" or "ComboBox" or "CheckBox");
    }

    [Fact]
    public void The_edit_card_name_field_locks_as_read_only_never_disabled()
    {
        var xaml = LoadXaml("src/ui/Views/ImportWorkflowView.axaml");
        var name = Assert.Single(
            Elements(xaml.Root!, "TextBox"),
            t => A(t, "Text") == "{Binding ModName, Mode=TwoWay}");

        // Read-only, not disabled: read-only text renders at full contrast +
        // stays selectable, so the name being edited is always legible (the
        // Fluent dark theme renders disabled text near-invisibly).
        Assert.Equal("{Binding !IsNameEditable}", A(name, "IsReadOnly"));
        Assert.Null(A(name, "IsEnabled"));

        // Binding validity: the path resolves on the workflow VM.
        Assert.NotNull(typeof(ImportWorkflowViewModel).GetProperty("IsNameEditable"));
    }

    [Fact]
    public void The_top_card_host_is_batch_only()
    {
        // The edit mode renders as an in-row band (never the top card); the
        // top host's visibility binds the workflow's batch-only projection.
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        var topHost = Assert.Single(
            Elements(xaml.Root!, "Panel"),
            p => A(p, "IsVisible") == "{Binding ImportWorkflow.IsBatchActive}");
        Assert.Single(topHost.Descendants(), e => e.Name.LocalName == "ImportWorkflowView");

        // Binding validity: the projection resolves on the workflow VM.
        Assert.NotNull(typeof(ImportWorkflowViewModel).GetProperty("IsBatchActive"));
        Assert.NotNull(typeof(ImportWorkflowViewModel).GetProperty("EditTargetContainerId"));
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
