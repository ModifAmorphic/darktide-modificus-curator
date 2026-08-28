using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Structural tests for the load-order import workspace's entry + hosting +
/// markup shape: the sticky fifth Add mode, the full workspace that replaces
/// the normal Mods content, the two mode tiles, the virtualized fill-height
/// results list over a deterministic shared column layout, the fixed
/// header/footer, and the banned controls (no CheckBox, no ComboBox, no
/// open-on-Nexus link). Source assertions read the .axaml files directly (the
/// <see cref="GamingModeGatingXamlTests"/> approach); the VM-level behavior
/// is covered by <see cref="LoadOrderImportViewModelTests"/>; XAML compilation
/// (the UI project build) proves the markup itself is valid.
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

    /// <summary>An attribute in the x: namespace (e.g. x:Name).</summary>
    private static string? XA(XElement e, string name) =>
        (string?)e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}" + name);

    private static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    /// <summary>The review row's DataTemplate (the x:DataType discriminates it
    /// from the nested alternates template).</summary>
    private static XElement RowTemplate(XElement root) => Assert.Single(
        Elements(root, "DataTemplate"),
        t => XA(t, "DataType") == "vm:LoadOrderRowViewModel");

    [Fact]
    public void The_second_flyout_item_starts_the_mod_list_import_mode()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        var items = Elements(xaml.Root!, "MenuItem").ToList();
        // One entry, at position 2: Add Nexus Mods; Import mod list; Add Mod
        // (archive); Add Mod (folder); Link external folder.
        Assert.Equal(5, items.Count);
        Assert.Equal("AddNexusMods_Click", A(items[0], "Click"));
        Assert.Equal("ImportLoadOrder_Click", A(items[1], "Click"));
        Assert.Equal("AddArchive_Click", A(items[2], "Click"));
        Assert.Equal("AddFolder_Click", A(items[3], "Click"));
        Assert.Equal("LinkFolder_Click", A(items[4], "Click"));
        Assert.Equal(
            "{ReflectionBinding [ModList_AddLoadOrder], Source={StaticResource Loc}}",
            A(items[1], "Header"));

        // The mode exists + the face label tracks it.
        Assert.Contains(typeof(ModAddMode).GetEnumNames(), n => n == nameof(ModAddMode.LoadOrder));
        Assert.NotNull(typeof(ModListViewModel).GetProperty(nameof(ModListViewModel.AddModeLabel)));
    }

    [Fact]
    public void No_obsolete_watermark_attribute_remains_in_any_ui_axaml()
    {
        // Avalonia 12.1 flags TextBox.Watermark as obsolete (AVLN5001); the
        // supported property is PlaceholderText. Scan EVERY ui axaml so a
        // reintroduction anywhere fails, not just in the load-order view.
        var uiDir = Path.Combine(RepoRoot(), "src", "ui");
        Assert.True(Directory.Exists(uiDir), $"UI source directory missing: {uiDir}");
        var offenders = Directory.EnumerateFiles(uiDir, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(t => t.Text.Contains("Watermark=", StringComparison.Ordinal))
            .Select(t => RelativeFromRepo(t.File))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Obsolete TextBox.Watermark remains (use PlaceholderText, per Avalonia 12.1 AVLN5001):\n"
            + string.Join("\n", offenders));
    }

    private static string RelativeFromRepo(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute);

    [Fact]
    public void The_workspace_replaces_the_normal_mods_content()
    {
        var xaml = LoadXaml("src/ui/Views/ModListView.axaml");

        // The normal content (toolbar, import card, banner, list, empty
        // states) hides while the workspace is active; the workspace host is
        // the ONLY LoadOrderImportView in the page.
        var normalHost = Assert.Single(
            Elements(xaml.Root!, "Grid"),
            g => A(g, "IsVisible") == "{Binding !LoadOrder.IsActive}");
        var workspaceHost = Assert.Single(
            Elements(xaml.Root!, "Panel"),
            p => A(p, "IsVisible") == "{Binding LoadOrder.IsActive}");
        var workspaceView = Assert.Single(
            Elements(xaml.Root!, "LoadOrderImportView"));
        Assert.Contains(workspaceHost.Descendants(), d => d == workspaceView);

        // The normal toolbar + list live inside the gated host (so the one
        // binding swaps the whole destination state).
        Assert.Contains(Elements(normalHost, "SplitButton"), s => A(s, "Click") == "Add_Click");
        Assert.Contains(Elements(normalHost, "ScrollViewer"), s => XA(s, "Name") == "ModListScroll");
    }

    [Fact]
    public void The_workspace_has_two_mode_tiles_with_drawn_icons()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        var tiles = Elements(xaml.Root!, "Button")
            .Where(b => (A(b, "Classes") ?? string.Empty).Contains("modeTile"))
            .ToList();
        Assert.Equal(2, tiles.Count);
        Assert.Contains(tiles, t => A(t, "Command") == "{Binding ChooseReorderCommand}");
        Assert.Contains(tiles, t => A(t, "Command") == "{Binding ChooseImportCommand}");

        // Each tile carries a drawn Path icon (never a Unicode glyph) + the
        // localized title + explanation.
        foreach (var tile in tiles)
        {
            Assert.Single(Elements(tile, "Path"));
            Assert.Contains(Elements(tile, "TextBlock"),
                tb => (A(tb, "Classes") ?? string.Empty).Contains("tileTitle"));
            Assert.Contains(Elements(tile, "TextBlock"),
                tb => (A(tb, "Classes") ?? string.Empty).Contains("tileBody"));
        }

        // No radio buttons, checkboxes, or combo boxes on the choice.
        Assert.DoesNotContain(Elements(xaml.Root!, "RadioButton"), _ => true);
        Assert.Empty(Elements(xaml.Root!, "CheckBox"));
        Assert.Empty(Elements(xaml.Root!, "ComboBox"));
    }

    [Fact]
    public void The_results_list_is_virtualized_and_fills_the_workspace_height()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // The root is the fixed header / fill list / fixed footer shape (the
        // UserControl's single direct content grid).
        var rootGrid = xaml.Root!.Elements()
            .Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,*,Auto", A(rootGrid, "RowDefinitions"));

        // The rows host is an ItemsControl whose items panel virtualizes (the
        // core mechanism; Avalonia 12.1 ships no ItemsRepeater) inside a
        // ScrollViewer that takes the workspace's remaining height: no nested
        // MaxHeight cap anywhere in the workspace.
        var rowsHost = Assert.Single(
            Elements(xaml.Root!, "ItemsControl"),
            c => A(c, "ItemsSource") == "{Binding Rows}");
        Assert.Equal("{Binding !IsApplying}", A(rowsHost, "IsEnabled"));
        Assert.Single(Elements(rowsHost, "VirtualizingStackPanel"));
        RowTemplate(rowsHost); // exactly one review-row template
        Assert.DoesNotContain(
            xaml.Root!.Descendants().SelectMany(e => e.Attributes()),
            a => a.Name.LocalName == "MaxHeight");

        // The rows' ScrollViewer sits in the * row of the review grid (the
        // fill-height region), with a fixed header above it.
        var reviewGrid = Assert.Single(
            Elements(xaml.Root!, "Grid"),
            g => A(g, "RowDefinitions") == "Auto,Auto,*"
                && Elements(g, "ScrollViewer").Any(s => Elements(s, "ItemsControl").Contains(rowsHost)));
        var scroller = Assert.Single(Elements(reviewGrid, "ScrollViewer"));
        Assert.Equal("2", A(scroller, "Grid.Row"));

        // The footer's universal Apply button sizes naturally (no local
        // MinWidth left over from the former longer mode labels) with the
        // label centered, like Back + Cancel.
        var apply = Assert.Single(
            Elements(xaml.Root!, "Button"),
            b => (string?)b.Attribute("Content") == "{ReflectionBinding [LoadOrder_ApplyButton], Source={StaticResource Loc}}");
        Assert.Null(A(apply, "MinWidth"));
        Assert.Equal("Center", A(apply, "HorizontalContentAlignment"));
    }

    [Fact]
    public void The_column_layout_is_one_shared_deterministic_definition()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // Every aligned grid (the column header, each review row, the
        // candidate proposal, each alternate) carries the SAME fixed
        // ColumnDefinitions + ColumnSpacing: star/fixed columns with shared
        // spacing, never independently measured Auto columns that drift per
        // row. ColumnDefinitions cannot live in a style (not a styled
        // property in Avalonia), so this source pin is the drift guard.
        var rowGrids = Elements(xaml.Root!, "Grid")
            .Where(g => (A(g, "Classes") ?? string.Empty).Contains("loRow"))
            .ToList();
        Assert.Equal(4, rowGrids.Count);
        Assert.All(rowGrids, g =>
        {
            Assert.Equal("2*,3*,1.5*,190,130", A(g, "ColumnDefinitions"));
            Assert.Equal("12", A(g, "ColumnSpacing"));
        });

        // The header labels the five columns (Folder | Match | Action |
        // Mod ID | Version), with the Mod ID + Version labels bound to the
        // import mode.
        var headerGrid = rowGrids.Single(g =>
            Elements(g, "TextBlock").Any(tb =>
                (string?)tb.Attribute("Text") == "{ReflectionBinding [LoadOrder_FileNameHeader], Source={StaticResource Loc}}"));
        var idHeader = Assert.Single(Elements(headerGrid, "TextBlock"),
            tb => (string?)tb.Attribute("Text") == "{ReflectionBinding [LoadOrder_ModIdHeader], Source={StaticResource Loc}}");
        Assert.Equal("{Binding IsImportMode}", A(idHeader, "IsVisible"));
    }

    [Fact]
    public void The_choice_tiles_reflow_at_narrow_widths_through_a_container_query()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        var query = Assert.Single(
            Elements(xaml.Root!, "ContainerQuery"),
            q => A(q, "Name") == "loadOrderChoice");
        Assert.Equal("max-width:720", A(query, "Query"));
        // The query restyles the tiles' grid (the UniformGrid.modeTiles
        // selector flips the column count to one), and the tiles' wrapper is
        // the named width-measuring container.
        var style = Assert.Single(Elements(query, "Style"));
        Assert.Equal("UniformGrid.modeTiles", A(style, "Selector"));
        var columns = Assert.Single(Elements(style, "Setter"),
            s => A(s, "Property") == "Columns");
        Assert.Equal("1", A(columns, "Value"));
        var namedContainer = Assert.Single(
            xaml.Root!.Descendants(),
            e => (string?)e.Attribute("Container.Name") == "loadOrderChoice");
        Assert.Equal("Width", (string?)namedContainer.Attribute("Container.Sizing"));
    }

    [Fact]
    public void The_action_rail_reflows_to_a_full_width_line_at_narrow_widths()
    {
        // At the window minimum the shared star Action rail is too narrow for
        // the longest localized labels and would crowd the fixed Mod ID
        // input. The same action controls MOVE (styles, not duplicated
        // markup): wide = the natural (row 0, column 2) cell; narrow (a
        // per-line container query) = a full-width second line. Every aligned
        // grid is a named width container, so the header + every row +
        // candidate line reflow together while the wide-mode column
        // definition stays shared + deterministic.
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // The wide default: the loAction cells sit at (row 0, column 2) via
        // styles, never local values (local values would outrank the query's
        // override). The direct UserControl.Styles scope, outside any query.
        var wideStyle = Assert.Single(
            Elements(xaml.Root!, "Style"),
            s => A(s, "Selector")!.Contains("loAction")
                && s.Ancestors().Any(a => a.Name.LocalName == "UserControl.Styles")
                && !s.Ancestors().Any(a => a.Name.LocalName == "ContainerQuery"));
        Assert.Contains("0", Elements(wideStyle, "Setter")
            .Where(s => A(s, "Property") == "Grid.Row")
            .Select(s => A(s, "Value")));
        Assert.Contains("2", Elements(wideStyle, "Setter")
            .Where(s => A(s, "Property") == "Grid.Column")
            .Select(s => A(s, "Value")));

        // The narrow query: the same cells move to (row 1, column 0) spanning
        // all five columns.
        var query = Assert.Single(
            Elements(xaml.Root!, "ContainerQuery"),
            q => A(q, "Name") == "loLine");
        Assert.Equal("max-width:700", A(query, "Query"));
        var narrowStyle = Assert.Single(Elements(query, "Style"));
        Assert.Contains("loAction", A(narrowStyle, "Selector"));
        var setters = Elements(narrowStyle, "Setter")
            .ToDictionary(s => A(s, "Property")!, s => A(s, "Value"));
        Assert.Equal("1", setters["Grid.Row"]);
        Assert.Equal("0", setters["Grid.Column"]);
        Assert.Equal("5", setters["Grid.ColumnSpan"]);

        // Every aligned grid is a named width container with the two-row
        // layout the reflow needs, and the action cells carry the class with
        // no local Grid placement of their own.
        var rowGrids = Elements(xaml.Root!, "Grid")
            .Where(g => (A(g, "Classes") ?? string.Empty).Contains("loRow"))
            .ToList();
        Assert.Equal(4, rowGrids.Count);
        Assert.All(rowGrids, g =>
        {
            Assert.Equal("loLine", A(g, "Container.Name"));
            Assert.Equal("Width", A(g, "Container.Sizing"));
            Assert.Equal("Auto,Auto", A(g, "RowDefinitions"));
        });
        var actionHosts = xaml.Root!.Descendants()
            .Where(e => (A(e, "Classes") ?? string.Empty).Contains("loAction"))
            .ToList();
        // The header's Action label + each parent row's action rail ONLY:
        // the candidate identity clusters (#id + Accept in the Mod ID cell)
        // deliberately carry no loAction class, so they never reflow out of
        // their fixed cell.
        Assert.Equal(2, actionHosts.Count);
        Assert.All(actionHosts, h =>
        {
            Assert.Null(A(h, "Grid.Column"));
            Assert.Null(A(h, "Grid.Row"));
        });
        Assert.DoesNotContain(xaml.Root!.Descendants(), e =>
            (A(e, "Classes") ?? string.Empty).Contains("loAction")
            && e.Descendants().Any(d => (string?)d.Attribute("Click") == "AcceptCandidate_Click"));

        // Defensive trimming on the action label (full text in the tooltip).
        var actionLabel = Assert.Single(
            Elements(xaml.Root!, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding ActionText}");
        Assert.Equal("CharacterEllipsis", A(actionLabel, "TextTrimming"));
        Assert.Equal("{Binding ActionText}", A(actionLabel, "ToolTip.Tip"));
    }

    [Fact]
    public void Textboxes_and_progress_bars_follow_the_app_conventions()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // The manual + version inputs carry no local FontSize: they inherit
        // the app font scale (the preference's text scaling applies).
        var textBoxes = Elements(xaml.Root!, "TextBox").ToList();
        Assert.Equal(2, textBoxes.Count);
        Assert.All(textBoxes, t => Assert.Null(A(t, "FontSize")));

        // Every indeterminate spinner carries an accessible name (localized,
        // describing the busy state it represents).
        var spinners = Elements(xaml.Root!, "ProgressBar")
            .Where(p => A(p, "IsIndeterminate") == "True")
            .ToList();
        Assert.Equal(4, spinners.Count);
        Assert.All(spinners, p =>
        {
            var name = A(p, "AutomationProperties.Name");
            Assert.NotNull(name);
            Assert.Contains("LoadOrder_", name);
        });
    }

    [Fact]
    public void No_search_on_nexus_link_exists_anywhere()
    {
        // The operator explicitly rejected the per-row Search on Nexus link:
        // no link in the markup, no handler routing for it, and no leftover
        // resource keys.
        var view = File.ReadAllText(RequireSourceFile("src/ui/Views/LoadOrderImportView.axaml"));
        var codeBehind = File.ReadAllText(RequireSourceFile("src/ui/Views/LoadOrderImportView.axaml.cs"));
        var resx = File.ReadAllText(RequireSourceFile("src/ui/Resources/Strings.resx"));

        Assert.DoesNotContain("SearchOnNexus", view);
        Assert.DoesNotContain("OpenOnNexus", view);
        Assert.DoesNotContain("HyperlinkButton", view);
        Assert.DoesNotContain("OpenOnNexus", codeBehind);
        Assert.DoesNotContain("LoadOrder_SearchOnNexus", resx);
        Assert.DoesNotContain("LoadOrder_SearchFailed", resx);

        // The launcher left the workspace's dependencies with it.
        Assert.DoesNotContain("IExternalLauncher", codeBehind);
    }

    [Fact]
    public void The_row_template_carries_the_new_surface_not_the_old_one()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        // The row template (the DataTemplate typed to the row VM inside the
        // virtualized rows host) carries the manual entry + Find, the
        // identified fact + Change, the Skip/Undo text action, the version
        // input + note, the candidate proposal, and the per-line failure: the
        // new surface, with no include checkbox anywhere.
        var rowsHost = Assert.Single(
            Elements(xaml.Root!, "ItemsControl"), c => A(c, "ItemsSource") == "{Binding Rows}");
        var template = RowTemplate(rowsHost);

        var textBoxes = Elements(template, "TextBox").ToList();
        Assert.Equal(2, textBoxes.Count);
        Assert.Contains(textBoxes, t => (string?)t.Attribute("Text") == "{Binding ManualId, Mode=TwoWay}");
        Assert.Contains(textBoxes, t => (string?)t.Attribute("Text") == "{Binding Version, Mode=TwoWay}");

        var buttons = Elements(template, "Button").ToList();
        Assert.Contains(buttons, b => A(b, "Click") == "Skip_Click");
        Assert.Contains(buttons, b => A(b, "Click") == "Find_Click");
        Assert.Contains(buttons, b => A(b, "Click") == "ChangeIdentity_Click");
        Assert.Contains(buttons, b => A(b, "Click") == "AcceptCandidate_Click");
        Assert.Contains(buttons, b => A(b, "Click") == "ToggleAlternates_Click");
        Assert.Contains(buttons, b => A(b, "Click") == "AcceptAlternate_Click");

        // The identified fact renders the id (never the title again) + the
        // subtle Change action; the manual entry + the fact are the two
        // projections of one cell.
        Assert.Contains(Elements(template, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding ModIdText}");

        // The candidate proposal (inside the row card's candidate region):
        // the title under Match (column 1), the identity cluster (#id +
        // Accept together) in the Mod ID cell (column 3), and the plan Action
        // column EMPTY on the child line (it belongs to the parent's plan).
        var candidateGrid = Assert.Single(
            Elements(template, "Grid"),
            g => HasClass(g, "loRow")
                && g.Descendants().Any(t => (string?)t.Attribute("Text") == "{Binding TopCandidate.Name}"));
        Assert.Equal("1", A(candidateGrid
            .Elements("{https://github.com/avaloniaui}TextBlock")
            .First(t => (string?)t.Attribute("Text") == "{Binding TopCandidate.Name}"), "Grid.Column"));
        var cluster = Assert.Single(
            Elements(candidateGrid, "WrapPanel"),
            w => (string?)w.Attribute("Grid.Column") == "3");
        var clusterChildren = cluster.Elements().ToList();
        var idText = Assert.Single(Elements(cluster, "TextBlock"));
        Assert.Equal("{Binding TopCandidate.ModId, StringFormat='#{0}'}", A(idText, "Text"));
        Assert.Contains("loCandidateId", A(idText, "Classes")); // the shared fixed slot
        var accept = Assert.Single(Elements(cluster, "Button"),
            b => A(b, "Click") == "AcceptCandidate_Click");
        // #id FIRST, then Accept: the choice reads as "#12345 Accept".
        Assert.Same(idText, clusterChildren[0]);
        Assert.Same(accept, clusterChildren[1]);
        Assert.DoesNotContain("loAction", A(accept, "Classes"));
        // The alternates' expand chevron stays with the cluster.
        Assert.Single(Elements(cluster, "Button"),
            b => A(b, "Click") == "ToggleAlternates_Click");
        // The plan Action column carries no candidate content.
        Assert.DoesNotContain(candidateGrid.Descendants(), e =>
            (string?)e.Attribute("Grid.Column") == "2"
            && (e.Name.LocalName is "Button" or "StackPanel"));

        // The per-line apply failure renders inside the row template.
        Assert.Contains(Elements(template, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding LineFailure}");
    }

    [Fact]
    public void Row_actions_carry_visible_resting_chrome()
    {
        // The former transparent-link treatment vanished until hover in both
        // themes. The textual actions now keep the STANDARD Fluent chrome
        // (the style authors only compact sizing: no Background=Transparent,
        // no BorderThickness=0), and the icon actions (the Find button + the expand
        // chevron) get their own compact icon-button style. Runtime visual
        // approval in both themes remains the operator's gate; this pins the
        // structural intent.
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");

        var textStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Button.textAction");
        var properties = Elements(textStyle, "Setter").Select(s => A(s, "Property")).ToHashSet();
        Assert.DoesNotContain("Background", properties);
        Assert.DoesNotContain("BorderThickness", properties);
        Assert.Contains("Padding", properties);

        var iconStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Button.iconAction");
        var iconProperties = Elements(iconStyle, "Setter").Select(s => A(s, "Property")).ToHashSet();
        Assert.DoesNotContain("Background", iconProperties);
        Assert.DoesNotContain("BorderThickness", iconProperties);

        // Every textual action uses the chrome style (Skip/Undo, Accept,
        // Change, Stop Search); the Find button + expand chevron use the
        // icon style.
        var rowsHost = Assert.Single(
            Elements(xaml.Root!, "ItemsControl"), c => A(c, "ItemsSource") == "{Binding Rows}");
        var template = RowTemplate(rowsHost);
        Assert.All(
            Elements(template, "Button").Where(b => A(b, "Content") is not null),
            b => Assert.Contains("textAction", A(b, "Classes")));
        var find = Assert.Single(
            Elements(template, "Button"), b => A(b, "Click") == "Find_Click");
        Assert.Contains("iconAction", A(find, "Classes"));
        var expand = Assert.Single(
            Elements(template, "Button"), b => A(b, "Click") == "ToggleAlternates_Click");
        Assert.Contains("iconAction", A(expand, "Classes"));

        var stop = Assert.Single(
            Elements(xaml.Root!, "Button"), b => A(b, "Command") == "{Binding StopSearchCommand}");
        Assert.Contains("textAction", A(stop, "Classes"));
    }

    [Fact]
    public void The_manual_id_cell_is_one_line_with_an_inline_find_icon_button()
    {
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");
        var rowsHost = Assert.Single(
            Elements(xaml.Root!, "ItemsControl"), c => A(c, "ItemsSource") == "{Binding Rows}");
        var template = RowTemplate(rowsHost);

        var manual = Assert.Single(
            Elements(template, "TextBox"), t => (string?)t.Attribute("Text") == "{Binding ManualId, Mode=TwoWay}");
        var line = Assert.Single(
            Elements(template, "Grid"), g => g.Elements().Contains(manual));
        Assert.Equal("*,Auto", A(line, "ColumnDefinitions")); // one line: input + trailing slot

        // The trailing slot hosts the drawn-geometry Find icon button and
        // the busy spinner in the SAME stable footprint (swap, no shift),
        // with the localized "Find Nexus mod" tooltip + name. The button
        // disables while either lookup state claims the row (CanFind), so a
        // manual search never interleaves with the row's automatic turn.
        var trailing = Assert.Single(
            Elements(line, "Panel"), p => (string?)p.Attribute("Grid.Column") == "1");
        Assert.NotNull(A(trailing, "MinWidth")); // the stable width
        var find = Assert.Single(Elements(trailing, "Button"));
        Assert.Contains("iconAction", A(find, "Classes"));
        Assert.Equal("{Binding CanFind}", A(find, "IsEnabled"));
        Assert.Single(Elements(find, "Path")); // drawn geometry, never a glyph
        Assert.Equal(
            "{ReflectionBinding [LoadOrder_FindTooltip], Source={StaticResource Loc}}",
            A(find, "ToolTip.Tip"));
        Assert.Equal(
            "{ReflectionBinding [LoadOrder_FindTooltip], Source={StaticResource Loc}}",
            A(find, "AutomationProperties.Name"));
        var spinner = Assert.Single(Elements(trailing, "ProgressBar"));
        Assert.Equal("{Binding IsFinding}", A(spinner, "IsVisible"));

        // Enter in the field routes to the same Find path (the handler
        // attribute; the routing itself is pinned in the code-behind source
        // test below).
        Assert.Equal("ManualId_KeyDown", A(manual, "KeyDown"));

        // Error + no-results lines render only when present (beneath the
        // one-line cell, never part of the idle line).
        var error = Assert.Single(Elements(template, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding ManualError}");
        Assert.Equal(
            "{Binding ManualError, Converter={x:Static StringConverters.IsNotNullOrEmpty}}",
            A(error, "IsVisible"));
    }

    [Fact]
    public void Enter_in_the_manual_field_routes_to_the_same_find_command()
    {
        // The code-behind routing, pinned as a source assertion (the
        // ancestor-walk test's pattern): Enter filters to Key.Enter, marks
        // the key handled, and invokes the SAME FindNexusModCommand the icon
        // button uses, for the field's row. No validation logic is
        // duplicated in code-behind.
        var text = File.ReadAllText(RequireSourceFile(
            "src/ui/Views/LoadOrderImportView.axaml.cs"));
        Assert.Contains("ManualId_KeyDown", text);
        Assert.Contains("Key.Enter", text);
        Assert.Contains("e.Handled = true", text);
        Assert.Contains("FindNexusModCommand.Execute(row)", text);
        // Exactly one command invocation site shares by both entry points.
        Assert.Equal(2, CountOccurrences(text, "FindNexusModCommand.Execute(row)"));
    }

    [Fact]
    public void Candidates_render_as_visual_children_inside_their_parent_row_card()
    {
        // Each top-level line renders inside a containing card; the
        // candidate proposal + alternates + per-line failure render INSIDE
        // that card below the main line, in a fill-free inset region (the
        // card's background shows through) with a left accent rule; the
        // parent-to-candidate gap is smaller than the gap between top-level
        // rows. Structural pins only: the operator's visual pass remains the
        // approval gate.
        var xaml = LoadXaml("src/ui/Views/LoadOrderImportView.axaml");
        var rowsHost = Assert.Single(
            Elements(xaml.Root!, "ItemsControl"), c => A(c, "ItemsSource") == "{Binding Rows}");
        var template = RowTemplate(rowsHost);

        var card = Assert.Single(Elements(template, "Border"), b => HasClass(b, "loItem"));
        var mainLine = Assert.Single(
            Elements(card, "Grid"), g => HasClass(g, "loRow") && Elements(g, "TextBlock")
                .Any(t => (string?)t.Attribute("Text") == "{Binding MatchText}"));

        var region = Assert.Single(Elements(card, "Border"), b => HasClass(b, "loCandidates"));
        Assert.Equal("{Binding ShowCandidateArea}", A(region, "IsVisible"));
        Assert.Single(Elements(region, "Border"), b => HasClass(b, "loAccentRule"));

        // The inset region carries NO fill of its own (transparently showing
        // the parent card's background); the accent rule stays.
        var regionStyleAssert = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Border.loCandidates");
        Assert.DoesNotContain(Elements(regionStyleAssert, "Setter"),
            s => A(s, "Property") == "Background" || A(s, "Property") == "BorderBrush"
                || A(s, "Property") == "BorderThickness");
        var accentStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Border.loAccentRule");
        Assert.NotNull(Elements(accentStyle, "Setter")
            .Single(s => A(s, "Property") == "Background"));

        // Every candidate id renders through ONE shared fixed-width slot
        // (the 64-DIP loCandidateId style, not a duplicated numeric value),
        // so Accept begins at the same horizontal position regardless of id
        // length, across the proposal and every alternate.
        var idSlotStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "TextBlock.loCandidateId");
        Assert.Equal("64", A(Elements(idSlotStyle, "Setter")
            .Single(w => A(w, "Property") == "Width"), "Value"));
        // The region renders BELOW the main line inside the card, not nested
        // inside the line grid (a sibling section of the card).
        Assert.DoesNotContain(region, mainLine.Descendants());

        // The proposal + the alternates + the failure all live inside the
        // card (associated with their parent), never as detached siblings.
        Assert.Contains(Elements(card, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding TopCandidate.Name}");
        var alternates = Assert.Single(Elements(card, "ItemsControl"),
            c => (string?)c.Attribute("ItemsSource") == "{Binding AlternateCandidates}");
        Assert.Contains(alternates, region.Descendants());

        // Every alternate renders the same identity-cluster shape: #id then
        // Accept in ONE wrapping cluster inside the Mod ID cell (column 3),
        // with the plan Action column empty and no loAction reflow.
        var alternateCluster = Assert.Single(
            Elements(alternates, "WrapPanel"), w => (string?)w.Attribute("Grid.Column") == "3");
        var altChildren = alternateCluster.Elements().ToList();
        var altId = Assert.Single(Elements(alternateCluster, "TextBlock"));
        Assert.Equal("{Binding ModId, StringFormat='#{0}'}", A(altId, "Text"));
        Assert.Contains("loCandidateId", A(altId, "Classes")); // the same shared slot
        var altAccept = Assert.Single(Elements(alternateCluster, "Button"));
        Assert.Equal("AcceptAlternate_Click", A(altAccept, "Click"));
        Assert.Same(altId, altChildren[0]); // #id first, then Accept
        Assert.Same(altAccept, altChildren[1]);
        Assert.DoesNotContain("loAction", A(altAccept, "Classes"));
        Assert.Contains(Elements(card, "TextBlock"),
            t => (string?)t.Attribute("Text") == "{Binding LineFailure}");

        // The gap between top-level rows (the card's vertical margin) is
        // larger than the parent-to-candidate gap (the region's top margin).
        var cardStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Border.loItem");
        var cardMargin = Margin(Elements(cardStyle, "Setter"));
        var regionStyle = Assert.Single(
            Elements(xaml.Root!, "Style"), s => A(s, "Selector") == "Border.loCandidates");
        var regionMargin = Margin(Elements(regionStyle, "Setter"));
        var topGap = cardMargin.top * 2; // adjacent cards' margins collapse additively
        Assert.True(topGap > regionMargin.top,
            $"the top-level gap ({topGap}) must exceed the parent-to-candidate gap ({regionMargin.top})");

        // The column header keeps its horizontal margin matched to the card
        // padding so the shared columns stay aligned.
        var header = Assert.Single(
            Elements(xaml.Root!, "Grid"),
            g => HasClass(g, "loRow") && Elements(g, "TextBlock").Any(t =>
                (string?)t.Attribute("Text") == "{ReflectionBinding [LoadOrder_FileNameHeader], Source={StaticResource Loc}}"));
        var headerMargin = A(header, "Margin");
        Assert.NotNull(headerMargin);
        var cardPadding = Padding(Elements(cardStyle, "Setter"));
        Assert.StartsWith(cardPadding.left.ToString(), headerMargin);
    }

    private static bool HasClass(XElement e, string className)
    {
        var classes = A(e, "Classes") ?? string.Empty;
        return classes.Split(' ').Contains(className);
    }

    private static (double left, double top) Margin(IEnumerable<XElement> setters) =>
        Edge(setters, "Margin", index: 0, second: 1);

    private static (double left, double top) Padding(IEnumerable<XElement> setters) =>
        Edge(setters, "Padding", index: 0, second: 1);

    private static (double left, double top) Edge(
        IEnumerable<XElement> setters, string property, int index, int second)
    {
        var value = setters.Single(s => A(s, "Property") == property) is { } setter
            ? A(setter, "Value")!
            : throw new InvalidOperationException();
        var parts = value.Split(',').Select(double.Parse).ToArray();
        return (parts[index], parts[second]);
    }

    private static int CountOccurrences(string text, string fragment)
    {
        var count = 0;
        for (var i = text.IndexOf(fragment, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(fragment, i + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void The_mod_list_picker_filter_discovers_both_supported_shapes()
    {
        // The "Mod list" filter surfaces .txt (mod_load_order.txt + friends)
        // AND .lst (mods.lst) directly; the All files entry stays for any
        // other compatible line-format file. Pinned as a source assertion
        // (the picker itself is a native dialog).
        var text = File.ReadAllText(RequireSourceFile(
            "src/ui/Views/ModListView.axaml.cs"));
        Assert.Contains("\"*.txt\"", text);
        Assert.Contains("\"*.lst\"", text);
        Assert.Contains("FilePickerFileTypes.All", text);
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
