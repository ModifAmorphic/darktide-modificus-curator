using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the Profiles page action row's clone action: the Add,
/// Clone, Delete button order, the command + visibility + tooltip bindings,
/// <c>ToolTip.ShowOnDisabled</c> (so the running-gate reason stays available on
/// the disabled button), the drawn <c>&lt;Path&gt;</c> icon (never a Unicode
/// glyph), and the localized label key. Source assertions read the
/// .axaml / .resx files directly (the established
/// <see cref="GamingModeGatingXamlTests"/> approach): the XAML loads as XML
/// after comments are stripped, so structure is asserted semantically. The
/// VM-level behavior is covered by <see cref="ProfilesViewModelTests"/>; XAML
/// compilation (the UI project build) proves the markup itself is valid.
/// </summary>
public sealed class ProfilesViewXamlTests
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
    public void Action_row_orders_Add_Clone_Delete_with_clone_bound_and_show_on_disabled()
    {
        var xaml = LoadXaml("src/ui/Views/ProfilesView.axaml");

        // The shared action row is the horizontal StackPanel gated on
        // ShowProfileActions (hidden while a draft is open).
        var row = Assert.Single(Elements(xaml.Root!, "StackPanel"), p =>
            A(p, "Orientation") == "Horizontal" && A(p, "IsVisible") == "{Binding ShowProfileActions}");
        var buttons = Elements(row, "Button").ToList();
        Assert.Equal(3, buttons.Count);
        Assert.Equal("{Binding AddProfileCommand}", A(buttons[0], "Command"));
        Assert.Equal("{Binding CloneProfileCommand}", A(buttons[1], "Command"));
        Assert.Equal("{Binding DeleteProfileCommand}", A(buttons[2], "Command"));

        var clone = buttons[1];
        Assert.Equal("{Binding CloneIsVisible}", A(clone, "IsVisible"));
        Assert.Equal("{Binding CloneTooltip}", A(clone, "ToolTip.Tip"));
        Assert.Equal("True", A(clone, "ToolTip.ShowOnDisabled"));

        // Binding validity: the VM exposes every bound path.
        Assert.NotNull(typeof(ProfilesViewModel).GetProperty("CloneIsVisible"));
        Assert.NotNull(typeof(ProfilesViewModel).GetProperty("CloneTooltip"));
    }

    [Fact]
    public void Clone_button_uses_a_drawn_path_icon_and_the_localized_label()
    {
        var xaml = LoadXaml("src/ui/Views/ProfilesView.axaml");

        var clone = Elements(xaml.Root!, "Button")
            .Single(b => A(b, "Command") == "{Binding CloneProfileCommand}");

        // Drawn geometry icon (Material content-copy), not a Unicode glyph.
        var path = Assert.Single(Elements(clone, "Path"));
        Assert.False(string.IsNullOrWhiteSpace(A(path, "Data")));
        Assert.NotNull(A(path, "Stretch"));

        var label = Assert.Single(Elements(clone, "TextBlock"));
        Assert.Contains("[Profiles_CloneButton]", A(label, "Text"));
    }

    [Fact]
    public void The_resx_defines_the_clone_keys()
    {
        var resx = XDocument.Parse(
            File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx")));
        var names = resx.Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .ToHashSet();

        Assert.Contains("Profiles_CloneButton", names);
        Assert.Contains("Profiles_CloneTooltip", names);
        Assert.Contains("Profiles_CloneLockedTooltip", names);
        Assert.Contains("Profiles_ErrCloneFailed", names);
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
