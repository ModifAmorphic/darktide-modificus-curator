using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The derived "Nexus, version unknown" row state: a Nexus container whose
/// resolved latest version carries an empty VersionString (an association
/// recorded without a version stamp). Covers the row's derivations (badge
/// guard, update-action enable + tooltip variants, pin-dropdown suppression,
/// the edit action's availability) and the list projection's inclusion under
/// the updates-only filter. Pure row-level: direct construction, no services
/// beyond the localization + row-context fakes.
/// </summary>
public sealed class ModItemVersionUnknownTests
{
    private static readonly LocalizationService Localization = new();

    private static ModVersion Version(string tag, bool isLatest = false) => new()
    {
        Folder = "folder-" + tag,
        VersionString = tag,
        IsLatest = isLatest,
        ImportedAt = DateTimeOffset.UtcNow,
    };

    private static ModItemViewModel Row(
        ModSource source,
        IReadOnlyList<ModVersion> versions,
        ModRowContext? context = null,
        string actualVersion = "",
        ModVersionPolicy? policy = null) => new(
        Localization,
        context ?? TestDoubles.RowContext(),
        Guid.NewGuid(),
        "Mod",
        source,
        actualVersion,
        true,
        0,
        policy ?? ModVersionPolicy.Latest,
        versions,
        found: true);

    // ---- the truth -----------------------------------------------------------

    [Fact]
    public void Unknown_is_a_nexus_container_whose_latest_tag_is_empty()
    {
        var row = Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            actualVersion: string.Empty);

        Assert.True(row.IsVersionUnknown);
    }

    [Fact]
    public void A_tagged_latest_is_not_unknown()
    {
        var row = Row(
            new NexusSource { ModId = 8 },
            new[] { Version("1.0", isLatest: true) },
            actualVersion: "1.0");

        Assert.False(row.IsVersionUnknown);
    }

    [Fact]
    public void An_untracked_container_is_never_unknown()
    {
        // The untracked empty tag is ordinary (untracked versions carry no
        // tags by construction); unknown is a NEXUS-association state.
        var row = Row(
            new UntrackedSource(),
            new[] { Version(string.Empty, isLatest: true) });

        Assert.False(row.IsVersionUnknown);
    }

    [Fact]
    public void A_versionless_nexus_container_is_not_unknown()
    {
        // No latest at all (a degenerate container) is not the unknown state
        // (nothing is associated-but-unstamped; there is simply no version).
        var row = Row(new NexusSource { ModId = 8 }, Array.Empty<ModVersion>());

        Assert.False(row.IsVersionUnknown);
    }

    // ---- the badge guard -------------------------------------------------------

    [Fact]
    public void The_badge_never_appends_an_empty_version()
    {
        // An empty ActualVersion (the unknown row) must not render a dangling
        // separator: the badge falls to the plain "Nexus #{id}" form.
        var row = Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            actualVersion: string.Empty);

        var badge = row.SourceBadgeText;

        Assert.Equal(Localization.Format("ModRow_SourceNexus", 8), badge);
        Assert.Equal(
            Localization.Format("ModRow_SourceNexusWithVersion", 8, "1.0"),
            Row(
                new NexusSource { ModId = 8 },
                new[] { Version("1.0", isLatest: true) },
                actualVersion: "1.0").SourceBadgeText);
    }

    // ---- the update-action cell --------------------------------------------------

    [Fact]
    public void An_unknown_row_is_actionable_without_a_flagged_update()
    {
        var row = Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            actualVersion: string.Empty);

        Assert.False(row.UpdateAvailable);
        Assert.True(row.UpdateActionEnabled);

        // The ordinary row stays disabled without a flag.
        var tagged = Row(
            new NexusSource { ModId = 8 },
            new[] { Version("1.0", isLatest: true) },
            actualVersion: "1.0");
        Assert.False(tagged.UpdateActionEnabled);
        tagged.UpdateAvailable = true;
        Assert.True(tagged.UpdateActionEnabled);
    }

    [Fact]
    public void The_unknown_tooltip_variants_follow_the_established_precedence()
    {
        var premium = TestDoubles.RowContext(); // the default fake is premium
        var regular = TestDoubles.RowContext(
            auth: new FakeNexusAuthService
            {
                State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false),
            });
        var gaming = TestDoubles.RowContext(
            auth: new FakeNexusAuthService
            {
                State = new NexusAuthState(NexusAuthMethod.OAuth, "free", IsPremium: false),
            },
            gamingMode: new GamingModeState(true));

        ModItemViewModel UnknownRow(ModRowContext context) => Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            context: context,
            actualVersion: string.Empty);

        // Unknown + Premium: the resolution install tooltip (gaming does not
        // change it, same as the flagged-update behavior).
        Assert.Equal(
            Localization["ModRow_UpdateTooltipInstallUnknown"],
            UnknownRow(premium).UpdateActionTooltip);

        // Unknown + regular: the files-page variant.
        Assert.Equal(
            Localization["ModRow_UpdateTooltipOpenFilesUnknown"],
            UnknownRow(regular).UpdateActionTooltip);

        // Unknown + regular + Gaming Mode: the Desktop Mode guidance still
        // wins over the files-page variant.
        Assert.Equal(
            Localization["GamingMode_BrowserGuidance"],
            UnknownRow(gaming).UpdateActionTooltip);

        // A plain up-to-date row keeps the no-update tooltip.
        Assert.Equal(
            Localization["ModRow_UpdateTooltipNoUpdate"],
            Row(
                new NexusSource { ModId = 8 },
                new[] { Version("1.0", isLatest: true) },
                actualVersion: "1.0").UpdateActionTooltip);
    }

    // ---- the policy editor --------------------------------------------------------

    [Fact]
    public void The_pin_dropdown_is_suppressed_for_unknown_rows()
    {
        var unknown = Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            actualVersion: string.Empty);
        var tagged = Row(
            new NexusSource { ModId = 8 },
            new[] { Version("1.0", isLatest: true) },
            actualVersion: "1.0");

        // Latest (the default policy): no dropdown either way.
        Assert.False(unknown.CanShowVersionDropdown);
        Assert.False(tagged.CanShowVersionDropdown);

        // Pinned: the tagged row offers the dropdown; the unknown row does
        // not (its only version carries an empty tag: nothing to pin to).
        unknown.PolicyChoice = ModItemViewModel.PolicyPinned;
        tagged.PolicyChoice = ModItemViewModel.PolicyPinned;
        Assert.False(unknown.CanShowVersionDropdown);
        Assert.True(tagged.CanShowVersionDropdown);
    }

    // ---- the edit action -----------------------------------------------------------

    [Fact]
    public void The_edit_action_is_offered_for_unknown_and_missing_rows_only()
    {
        // Suppressed for linked rows + download-morphed rows; offered for
        // ordinary + unknown rows.
        var unknown = Row(
            new NexusSource { ModId = 8 },
            new[] { Version(string.Empty, isLatest: true) },
            actualVersion: string.Empty);
        Assert.True(unknown.CanEditImportDetails);

        var linked = Row(
            new LinkedSource { ExternalPath = "/tmp/x" },
            Array.Empty<ModVersion>());
        Assert.False(linked.CanEditImportDetails);

        var morphed = Row(
            new NexusSource { ModId = 8 },
            new[] { Version("1.0", isLatest: true) },
            actualVersion: "1.0");
        morphed.ActiveDownload = new DownloadRowViewModel(
            Localization,
            new FakeModDownloadQueue(),
            new DownloadItem(new ModDownloadRequest(
                "warhammer40kdarktide", 8, 1, DownloadPurpose.ProfileAdd,
                null, "Mod", Guid.NewGuid(), "Target")));
        Assert.False(morphed.CanEditImportDetails);
    }
}
