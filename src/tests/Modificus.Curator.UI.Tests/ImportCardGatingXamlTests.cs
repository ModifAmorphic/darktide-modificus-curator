using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the import card's presentation gating: the edit
/// mode's name field locks as read-only rather than disabled (read-only text
/// renders at full contrast in the Fluent dark theme; disabled text is
/// near-invisible, which read as an empty field). Source assertions read the
/// .axaml files directly (the <see cref="GamingModeGatingXamlTests"/>
/// approach): the XAML loads as XML after comments are stripped, so bindings
/// + attributes are asserted semantically. The VM-level behavior (the
/// IsNameEditable matrix) is covered by
/// <see cref="ImportWorkflowEditModeTests"/>; XAML compilation (the UI
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
