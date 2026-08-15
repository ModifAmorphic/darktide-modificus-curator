using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the Steam Deck Gaming Mode gating: the picker- and
/// file-manager-dependent controls disable via bindings, carry
/// <c>ToolTip.ShowOnDisabled</c> plus a guidance tooltip, and each surface
/// shows an inline hint bound to the gaming flag (reachable by touch /
/// controller without hover). Source assertions read the .axaml / .resx files
/// directly (the established <see cref="LaunchOverlayTests"/> approach): the
/// XAML loads as XML after comments are stripped, so structure (bindings,
/// attributes, control inventory) is asserted semantically. The VM-level
/// behavior (push-down, command guards) is covered by
/// <see cref="ModListGamingModeTests"/>,
/// <see cref="SettingsViewModelTests"/>, and
/// <see cref="DiscoveryEscapeHatchViewModelTests"/>; XAML compilation (the UI
/// project build) proves the markup itself is valid.
/// </summary>
public sealed class GamingModeGatingXamlTests
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

    // ---- ModListView: Add split button + toolbar hint + linked badges -------

    [Fact]
    public void ModList_Add_split_button_binds_IsAddEnabled_with_a_gaming_aware_tooltip()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");
        var add = Assert.Single(Elements(xaml.Root!, "SplitButton"));

        Assert.Equal("{Binding IsAddEnabled}", A(add, "IsEnabled"));
        Assert.Equal("{Binding AddButtonTooltip}", A(add, "ToolTip.Tip"));
        Assert.Equal("True", A(add, "ToolTip.ShowOnDisabled"));

        // Binding validity: both paths resolve on the mod-list VM.
        Assert.NotNull(typeof(ModListViewModel).GetProperty("IsAddEnabled"));
        Assert.NotNull(typeof(ModListViewModel).GetProperty("AddButtonTooltip"));
    }

    [Fact]
    public void ModList_toolbar_hint_is_gated_on_the_gaming_flag()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        // Exactly one toolbar hint, bound to the gaming flag + the add hint key.
        var hint = Assert.Single(Elements(xaml.Root!, "TextBlock"), t =>
            A(t, "IsVisible") == "{Binding IsGamingMode}"
                && A(t, "Text")!.Contains("[ModList_AddGamingModeHint]"));
        Assert.NotNull(typeof(ModListViewModel).GetProperty("IsGamingMode"));
    }

    [Fact]
    public void ModList_linked_badge_disables_under_gaming_in_both_row_roots()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        // The linked badge is the HyperlinkButton routing to OpenFolder_Click;
        // one instance per row root (Compact Grid + Detailed card).
        var badges = Elements(xaml.Root!, "HyperlinkButton")
            .Where(b => A(b, "Click") == "OpenFolder_Click")
            .ToList();
        Assert.Equal(2, badges.Count);
        foreach (var badge in badges)
        {
            Assert.Equal("{Binding !IsGamingMode}", A(badge, "IsEnabled"));
            Assert.Equal("{Binding LinkedBadgeTooltip}", A(badge, "ToolTip.Tip"));
            Assert.Equal("True", A(badge, "ToolTip.ShowOnDisabled"));
        }

        // Binding validity: the row VM exposes both paths.
        Assert.NotNull(typeof(ModItemViewModel).GetProperty("IsGamingMode"));
        Assert.NotNull(typeof(ModItemViewModel).GetProperty("LinkedBadgeTooltip"));
    }

    // ---- SettingsView: browse buttons + storage buttons + inline hints ------

    [Fact]
    public void Settings_browse_buttons_bind_IsBrowseEnabled_with_gaming_tooltip()
    {
        var xaml = LoadXaml("src/ui/Views/SettingsView.axaml");

        var browse = Elements(xaml.Root!, "Button")
            .Where(b => A(b, "Click") == "BrowseDiscovery_Click")
            .ToList();
        Assert.Single(browse);
        Assert.Equal("{Binding IsBrowseEnabled}", A(browse[0], "IsEnabled"));
        Assert.Equal("{Binding BrowseTooltip}", A(browse[0], "ToolTip.Tip"));
        Assert.Equal("True", A(browse[0], "ToolTip.ShowOnDisabled"));
    }

    [Fact]
    public void Settings_storage_buttons_disable_under_gaming_with_a_shared_tooltip()
    {
        var xaml = LoadXaml("src/ui/Views/SettingsView.axaml");

        var storage = Elements(xaml.Root!, "Button")
            .Where(b => A(b, "Command") is "{Binding OpenDataFolderCommand}"
                                or "{Binding OpenProfilesFolderCommand}")
            .ToList();
        Assert.Equal(2, storage.Count);
        foreach (var button in storage)
        {
            Assert.Equal("{Binding !IsGamingMode}", A(button, "IsEnabled"));
            Assert.Equal("{Binding StorageButtonsTooltip}", A(button, "ToolTip.Tip"));
            Assert.Equal("True", A(button, "ToolTip.ShowOnDisabled"));
        }
    }

    [Fact]
    public void Settings_shows_one_gating_hint_per_section_bound_to_the_gaming_flag()
    {
        var xaml = LoadXaml("src/ui/Views/SettingsView.axaml");

        var hints = Elements(xaml.Root!, "TextBlock")
            .Where(t => A(t, "IsVisible") == "{Binding IsGamingMode}")
            .ToList();
        Assert.Equal(2, hints.Count);
        Assert.Contains(hints, t => A(t, "Text")!.Contains("[GamingMode_PickerGuidance]"));
        Assert.Contains(hints, t => A(t, "Text")!.Contains("[GamingMode_FileManagerGuidance]"));
    }

    // ---- DiscoveryEscapeHatchDialog: browse buttons + inline hint -----------

    [Fact]
    public void EscapeHatch_browse_buttons_bind_IsBrowseEnabled_with_gaming_tooltip()
    {
        var xaml = LoadXaml("src/ui/Views/DiscoveryEscapeHatchDialog.axaml");

        var browse = Elements(xaml.Root!, "Button")
            .Where(b => A(b, "Click") == "Browse_Click")
            .ToList();
        Assert.Single(browse);
        Assert.Equal("{Binding IsBrowseEnabled}", A(browse[0], "IsEnabled"));
        Assert.Equal("{Binding BrowseTooltip}", A(browse[0], "ToolTip.Tip"));
        Assert.Equal("True", A(browse[0], "ToolTip.ShowOnDisabled"));
    }

    [Fact]
    public void EscapeHatch_shows_one_gating_hint_bound_to_the_gaming_flag()
    {
        var xaml = LoadXaml("src/ui/Views/DiscoveryEscapeHatchDialog.axaml");

        var hint = Assert.Single(Elements(xaml.Root!, "TextBlock"), t =>
            A(t, "IsVisible") == "{Binding IsGamingMode}");
        Assert.Equal("{Binding PickerGatingHint}", A(hint, "Text"));
        Assert.NotNull(typeof(DiscoveryEscapeHatchViewModel).GetProperty("PickerGatingHint"));
    }

    [Fact]
    public void EscapeHatch_TextBoxes_and_Submit_stay_ungated()
    {
        // Manual entry + submission must keep working in Gaming Mode: the
        // TextBoxes stay bound to the row value with no gaming binding, and
        // the Submit button carries no gaming gate.
        var xaml = LoadXaml("src/ui/Views/DiscoveryEscapeHatchDialog.axaml");

        foreach (var box in Elements(xaml.Root!, "TextBox"))
        {
            Assert.Null(A(box, "IsEnabled"));
        }

        var submit = Elements(xaml.Root!, "Button")
            .Single(b => A(b, "Click") == "Submit_Click");
        Assert.Null(A(submit, "IsEnabled"));
        Assert.Null(A(submit, "IsVisible"));
    }

    // ---- Strings.resx defines the guidance keys -----------------------------

    [Fact]
    public void The_resx_defines_the_gaming_mode_guidance_keys()
    {
        var resx = XDocument.Parse(
            File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx")));
        var names = resx.Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .ToHashSet();

        Assert.Contains("GamingMode_GuidanceTitle", names);
        Assert.Contains("GamingMode_PickerGuidance", names);
        Assert.Contains("GamingMode_FileManagerGuidance", names);
        Assert.Contains("GamingMode_BrowserGuidance", names);
        Assert.Contains("ModList_AddGamingModeHint", names);
        Assert.Contains("ModList_EmptyGamingModeHint", names);
        Assert.Contains("Dmf_DownloadMessageGamingMode", names);
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
