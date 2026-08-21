using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the download-row surfaces: the shared download
/// status content exists ONCE and is hosted by all three surfaces (the
/// Compact morph slot, the Detailed morph slot, and the appended row), the
/// appended section is a separate ItemsControl over DownloadRows with no
/// reorder affordances, the morph suppresses the update affordances, the
/// join-pulse flash is bound on every host, the new strings exist in
/// Strings.resx, and every download icon is drawn geometry (a Path, never a
/// glyph). Source assertions read the .axaml / .resx files directly (the
/// <see cref="ModRowSharedTemplatesTests"/> / GamingModeGatingXamlTests
/// pattern): the XAML loads as XML after comments are stripped, so structure
/// is asserted semantically and the UI project build proves the compiled
/// bindings.
/// </summary>
public sealed class DownloadRowXamlTests
{
    private static XDocument LoadStrippedXaml(string relativeFromRepo)
    {
        var text = File.ReadAllText(RequireSourceFile(relativeFromRepo));
        text = Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return XDocument.Parse(text);
    }

    private static string? A(XElement e, string name) => (string?)e.Attribute(name);

    /// <summary>
    /// Reads a namespaced attribute (x:Key, x:Name, x:DataType): XElement's
    /// plain Attribute(XName) only matches no-namespace names, so these are
    /// matched by local name.
    /// </summary>
    private static string? Ns(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    // ---- one shared template, three hosts ------------------------------------

    [Fact]
    public void The_download_status_template_exists_once_and_is_hosted_three_times()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // ONE definition (the shared-DataTemplate contract)...
        var template = Assert.Single(Elements(xaml.Root!, "DataTemplate"),
            t => Ns(t, "Key") == "ModRowDownloadStatusTemplate");
        Assert.Equal("vm:DownloadRowViewModel", Ns(template, "DataType"));

        // ...hosted by exactly three ContentControls: the Compact morph slot,
        // the Detailed morph slot, and the appended row.
        var hosts = Elements(xaml.Root!, "ContentControl")
            .Where(c => A(c, "ContentTemplate")?.Contains("ModRowDownloadStatusTemplate") == true)
            .ToList();
        Assert.Equal(3, hosts.Count);

        // The two morph hosts bind the row's ActiveDownload + gate on the
        // morph flag; the appended host binds the wrapper itself.
        Assert.Equal(2, hosts.Count(c => A(c, "Content") == "{Binding ActiveDownload}"));
        Assert.Single(hosts, c => A(c, "Content") == "{Binding}");
    }

    [Fact]
    public void Both_morph_roots_carry_the_morph_slot_and_its_visibility_gate()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The compact root: a morph slot with the status template, visible
        // only while morphed (the name TextBlock beside it is ungated).
        var compactRoot = Assert.Single(Elements(xaml.Root!, "Grid"),
            g => A(g, "Classes")?.Contains("compactRow") == true);
        var compactSlot = Assert.Single(
            compactRoot.Descendants(),
            e => e.Name.LocalName == "ContentControl"
                && A(e, "ContentTemplate")?.Contains("ModRowDownloadStatusTemplate") == true);
        Assert.Equal("{Binding IsDownloadMorphed}", A(compactSlot, "IsVisible"));

        // The detailed root: the summary TextBlock hides while morphed + a
        // sibling slot shows in its place.
        var detailedRoot = Assert.Single(Elements(xaml.Root!, "Border"),
            b => A(b, "Classes")?.Contains("detailedRow") == true);
        var summary = Assert.Single(
            detailedRoot.Descendants(),
            e => e.Name.LocalName == "TextBlock" && A(e, "Text") == "{Binding SummaryText}");
        Assert.Equal("{Binding !IsDownloadMorphed}", A(summary, "IsVisible"));
        var detailedSlot = Assert.Single(
            detailedRoot.Descendants(),
            e => e.Name.LocalName == "ContentControl"
                && A(e, "ContentTemplate")?.Contains("ModRowDownloadStatusTemplate") == true);
        Assert.Equal("{Binding IsDownloadMorphed}", A(detailedSlot, "IsVisible"));
    }

    [Fact]
    public void The_morph_suppresses_the_update_affordances_and_disables_policy()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The update-action cell (the reserved Panel) is not rendered while
        // morphed; the badge-area spinner follows the morph-aware projection.
        var updateCell = Assert.Single(Elements(xaml.Root!, "Panel"),
            p => A(p, "Classes")?.Contains("updateCell") == true);
        Assert.Equal("{Binding !IsDownloadMorphed}", A(updateCell, "IsVisible"));

        var spinner = Assert.Single(Elements(xaml.Root!, "ProgressBar"),
            p => A(p, "IsVisible") == "{Binding ShowUpdateSpinner}");
        Assert.NotNull(typeof(ModItemViewModel).GetProperty("ShowUpdateSpinner"));

        // Both policy ComboBoxes disable through IsPolicyEditable (which the
        // morph widens).
        var combos = Elements(xaml.Root!, "ComboBox")
            .Where(c => A(c, "IsEnabled") == "{Binding IsPolicyEditable}")
            .ToList();
        Assert.Equal(2, combos.Count);
    }

    [Fact]
    public void The_strip_cancel_binds_the_morph_wrapper_and_only_shows_while_morphed()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // Exactly one morph-strip Cancel: bound through ActiveDownload,
        // visible only while morphed, enabled while the item is live.
        var cancel = Assert.Single(Elements(xaml.Root!, "Button"),
            b => A(b, "Command") == "{Binding ActiveDownload.CancelCommand}");
        Assert.Equal("{Binding IsDownloadMorphed}", A(cancel, "IsVisible"));
        Assert.Equal("{Binding ActiveDownload.CanCancel}", A(cancel, "IsEnabled"));
        Assert.Equal("{Binding ActiveDownload.CancelTooltip}", A(cancel, "ToolTip.Tip"));
        Assert.Equal("{Binding ActiveDownload.CancelTooltip}", A(cancel, "AutomationProperties.Name"));

        // Binding validity: the row + wrapper expose the paths.
        Assert.NotNull(typeof(ModItemViewModel).GetProperty("ActiveDownload"));
        Assert.NotNull(typeof(ModItemViewModel).GetProperty("IsDownloadMorphed"));
        Assert.NotNull(typeof(DownloadRowViewModel).GetProperty("CanCancel"));
        Assert.NotNull(typeof(DownloadRowViewModel).GetProperty("CancelTooltip"));
    }

    [Fact]
    public void Retry_and_dismiss_exist_once_in_the_shared_status_template()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The failure affordances live in the shared template only (both
        // hosts inherit them through the hosting ContentControl).
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Command") == "{Binding RetryCommand}");
        Assert.Single(Elements(xaml.Root!, "Button"), b => A(b, "Command") == "{Binding DismissCommand}");

        var failure = Assert.Single(Elements(xaml.Root!, "StackPanel"),
            p => p.Attribute("IsVisible")?.Value == "{Binding IsFailed}");
        Assert.NotNull(failure.Descendants().Single(e =>
            e.Name.LocalName == "TextBlock" && A(e, "Text") == "{Binding FailureText}"));
    }

    // ---- the appended section -------------------------------------------------

    [Fact]
    public void The_appended_section_binds_DownloadRows_in_a_separate_items_control()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        var downloads = Assert.Single(Elements(xaml.Root!, "ItemsControl"),
            c => Ns(c, "Name") == "DownloadListItems");
        Assert.Equal("{Binding DownloadRows}", A(downloads, "ItemsSource"));
        Assert.Equal("{Binding HasDownloadRows}", A(downloads, "IsVisible"));

        // The mod-list ItemsControl keeps the visible projection, and the
        // scroll region follows the combined content flag.
        var mods = Assert.Single(Elements(xaml.Root!, "ItemsControl"),
            c => Ns(c, "Name") == "ModListItems");
        Assert.Equal("{Binding VisibleMods}", A(mods, "ItemsSource"));
        var scroll = Assert.Single(Elements(xaml.Root!, "ScrollViewer"));
        Assert.Equal("{Binding HasListContent}", A(scroll, "IsVisible"));

        // The appended row shows the name + the always-on target profile
        // label + its own Cancel + the shared status content.
        var template = Assert.Single(
            downloads.Descendants(), e => e.Name.LocalName == "DataTemplate");
        Assert.NotNull(template.Descendants().Single(e =>
            e.Name.LocalName == "TextBlock" && A(e, "Text") == "{Binding ProfileLabel}"));
        var appendedCancel = Assert.Single(Elements(template, "Button"),
            b => A(b, "Command") == "{Binding CancelCommand}");
        Assert.Equal("{Binding CanCancel}", A(appendedCancel, "IsEnabled"));

        // Binding validity.
        Assert.NotNull(typeof(ModListViewModel).GetProperty("DownloadRows"));
        Assert.NotNull(typeof(ModListViewModel).GetProperty("HasDownloadRows"));
        Assert.NotNull(typeof(ModListViewModel).GetProperty("HasListContent"));
    }

    [Fact]
    public void The_appended_template_carries_no_reorder_affordances()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        var downloads = Assert.Single(Elements(xaml.Root!, "ItemsControl"),
            c => Ns(c, "Name") == "DownloadListItems");
        var template = Assert.Single(
            downloads.Descendants(), e => e.Name.LocalName == "DataTemplate");

        // No grip pointer handlers, no per-row click handlers, no
        // commit-reorder binding, no marker bindings, no shared mod-row
        // template references: a download row can never become a drag source
        // or a drop target.
        Assert.DoesNotContain(template.Descendants(), e =>
            A(e, "PointerPressed") is not null ||
            A(e, "PointerMoved") is not null ||
            A(e, "PointerReleased") is not null ||
            A(e, "Click") is not null);
        Assert.DoesNotContain("CommitReorder", string.Join(" ", template.Descendants().Select(e => e.Value)));
        Assert.DoesNotContain(template.Descendants(), e =>
            A(e, "IsVisible")?.Contains("ShowReorderMarker") == true);
        Assert.DoesNotContain(template.Descendants(), e =>
            A(e, "ContentTemplate")?.Contains("ModRowGripTemplate") == true ||
            A(e, "ContentTemplate")?.Contains("ModRowActionStripTemplate") == true);
    }

    // ---- the join pulse -------------------------------------------------------

    [Fact]
    public void The_join_pulse_flash_is_bound_on_every_host_with_a_fading_animation()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // Three hosts carry the flash class: the two morph roots (bound
        // through ActiveDownload) + the appended row (bound directly).
        var pulses = Elements(xaml.Root!, "Grid")
            .Concat(Elements(xaml.Root!, "Border"))
            .Where(e => A(e, "Classes.downloadPulse") is { } binding)
            .ToList();
        Assert.Equal(3, pulses.Count);
        Assert.Equal(2, pulses.Count(e => A(e, "Classes.downloadPulse") == "{Binding ActiveDownload.IsPulsed}"));
        Assert.Single(pulses, e => A(e, "Classes.downloadPulse") == "{Binding IsPulsed}");

        // Both flash styles exist (the Grid roots + the Detailed card's
        // Border root) with a fade-out animation, and no host keeps a lit
        // background after it completes.
        var selectors = Elements(xaml.Root!, "Style")
            .Select(s => A(s, "Selector") ?? string.Empty)
            .ToList();
        Assert.Contains("Grid.compactRow.downloadPulse, Grid.downloadRow.downloadPulse", selectors);
        Assert.Contains("Border.detailedRow.downloadPulse", selectors);
        foreach (var style in Elements(xaml.Root!, "Style")
                     .Where(s => (A(s, "Selector") ?? string.Empty).Contains("downloadPulse")))
        {
            var animation = Assert.Single(Elements(style, "Animation"));
            var frames = Elements(animation, "KeyFrame").ToList();
            Assert.Equal(2, frames.Count);
            var last = Assert.Single(frames, f => A(f, "Cue") == "100%");
            var setter = Assert.Single(Elements(last, "Setter"));
            Assert.Equal("Transparent", (string?)setter.Attribute("Value"));
        }

        Assert.NotNull(typeof(DownloadRowViewModel).GetProperty("IsPulsed"));
    }

    // ---- i18n + icons -----------------------------------------------------------

    [Fact]
    public void Every_download_resx_key_exists()
    {
        var resx = XDocument.Load(RequireSourceFile("src/ui/Resources/Strings.resx"));
        var names = resx.Root!.Descendants()
            .Where(e => e.Name.LocalName == "data")
            .Select(e => (string?)e.Attribute("name"))
            .ToHashSet();

        var required = new[]
        {
            "ModRow_DownloadQueued",
            "ModRow_DownloadDownloading",
            "ModRow_DownloadImporting",
            "ModRow_DownloadFailed",
            "ModRow_DownloadForProfile",
            "ModRow_DownloadCancelTooltip",
            "ModRow_DownloadRetryTooltip",
            "ModRow_DownloadDismissTooltip",
            "ModRow_DownloadRowAutomation",
            "ModRow_DownloadRowProgressAutomation",
        };
        foreach (var key in required)
        {
            Assert.True(names.Contains(key), $"Missing resx key: {key}");
        }
    }

    [Fact]
    public void Every_download_icon_is_drawn_geometry_and_no_text_is_a_glyph()
    {
        var xaml = LoadStrippedXaml("src/ui/Views/ModListView.axaml");

        // The download action buttons (cancel x2 hosts, retry, dismiss) each
        // carry exactly one drawn Path and no text content.
        var iconButtons = Elements(xaml.Root!, "Button")
            .Where(b => (A(b, "Classes") ?? string.Empty).Contains("download"))
            .ToList();
        Assert.Equal(4, iconButtons.Count); // strip cancel, appended cancel, retry, dismiss
        foreach (var button in iconButtons)
        {
            var path = Assert.Single(Elements(button, "Path"));
            Assert.False(string.IsNullOrWhiteSpace(A(path, "Data")));
            Assert.Empty(Elements(button, "TextBlock"));
        }

        // The appended row's leading marker is a drawn Path too (the root
        // grid's direct icon child; the Cancel button carries its own).
        var downloads = Assert.Single(Elements(xaml.Root!, "ItemsControl"),
            c => Ns(c, "Name") == "DownloadListItems");
        var template = Assert.Single(
            downloads.Descendants(), e => e.Name.LocalName == "DataTemplate");
        var rootGrid = Assert.Single(Elements(template, "Grid"));
        var marker = Assert.Single(rootGrid.Elements(),
            e => e.Name.LocalName == "Path");
        Assert.False(string.IsNullOrWhiteSpace(A(marker, "Data")));

        // Every download-surface TextBlock is binding-driven (no literal
        // glyph or emoji text anywhere in the download markup).
        var downloadTexts = template.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Concat(Elements(xaml.Root!, "DataTemplate")
                .Single(t => Ns(t, "Key") == "ModRowDownloadStatusTemplate")
                .Descendants()
                .Where(e => e.Name.LocalName == "TextBlock"))
            .ToList();
        Assert.NotEmpty(downloadTexts);
        Assert.All(downloadTexts, t =>
            Assert.StartsWith("{Binding", A(t, "Text"), StringComparison.Ordinal));
    }

    // ---- required source lookup (the shared pattern) -------------------------

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
