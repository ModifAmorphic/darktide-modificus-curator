using System.Text;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// The Proton compatibility-tool selection behavior exercised through the public
/// <see cref="ISteamService.Discover"/> surface:
/// - App-specific CompatToolMapping is authoritative.
/// - Global "0" mapping is used only when the app-specific mapping is absent.
/// - A present-but-malformed app-specific mapping fails (no fall-through).
/// - With neither mapping, Darktide's appinfo recommended runtime is Steam's
///   non-user default and resolves like any selection (identical regardless of
///   Deck identity).
/// - Custom tools resolve by internal name from compatibilitytools.d roots.
/// - Valve-managed tools resolve through appinfo.vdf + appmanifests.
/// </summary>
public sealed class ProtonSelectionTests
{
    [Fact]
    public void App_specific_mapping_resolves_custom_tool()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
        Assert.Equal("GE-Proton9-3", result.ProtonVersion);
    }

    [Fact]
    public void Custom_tool_display_name_used_as_version()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3", displayName: "GE-Proton 9-3 Custom");

        var result = fx.Service.Discover();

        Assert.Equal("GE-Proton 9-3 Custom", result.ProtonVersion);
    }

    [Fact]
    public void Global_mapping_used_when_app_specific_is_absent()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // Global mapping (key "0"), not app-specific.
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3", global: true);
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
    }

    [Fact]
    public void App_specific_mapping_wins_over_global()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);

        // Write a config.vdf with BOTH app-specific + global mappings.
        WriteCompatToolMappings(fx.SteamRoot, appTool: "app-tool", globalTool: "global-tool");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "app-tool");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "global-tool");

        var result = fx.Service.Discover();

        // App-specific wins.
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "app-tool"), result.ProtonBinaryPath);
    }

    [Fact]
    public void Malformed_app_specific_mapping_does_not_fall_through_to_global()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);

        // App-specific mapping with an EMPTY name (present but malformed).
        // Global mapping points at a valid tool. The empty app-specific name
        // must fail resolution without falling through.
        WriteCompatToolMappings(fx.SteamRoot, appTool: "", globalTool: "global-tool");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "global-tool");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
    }

    [Fact]
    public void No_mapping_yields_null_proton_with_escape_hatch_warning()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No config.vdf at all.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Contains(result.Warnings, w => w.Contains("No Steam compatibility tool mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolvable_selected_tool_yields_null_with_warning()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "missing-tool");
        // No tool scaffolded under that name.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Contains(result.Warnings, w => w.Contains("missing-tool", StringComparison.Ordinal));
    }

    [Fact]
    public void System_compatibility_tools_root_is_searched()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "system-tool");
        // Tool is only under the system root, not the user root.
        fx.WithCustomProtonTool(fx.SystemCompatToolsDir, "system-tool");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.SystemCompatToolsDir, "system-tool"), result.ProtonBinaryPath);
    }

    [Fact]
    public void Custom_tool_missing_proton_binary_is_rejected()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "no-proton-file");

        // Write a manifest but DON'T place the proton binary.
        var toolDir = Path.Combine(fx.CompatToolsDir, "no-proton-file");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "compatibilitytool.vdf"), """
            "compatibilitytools"
            {
                "compat_tools"
                {
                    "no-proton-file"
                    {
                        "install_path"   "."
                        "display_name"   "No Proton"
                    }
                }
            }
            """);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
    }

    [Fact]
    public void Config_vdf_with_escaped_json_after_mapping_resolves_through_discovery()
    {
        // Full discovery-level regression: the operator's real config.vdf has a
        // JSON section with escaped quotes after CompatToolMapping. Discovery
        // must resolve the selected tool, not report Partial.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        WriteRealisticConfigVdfWithEscapedQuotes(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
    }

    /// <summary>
    /// Writes a config.vdf whose WebStorage section carries a JSON value with
    /// escaped quotes, matching the shape that broke the default-options parser
    /// on the operator's machine.
    /// </summary>
    private static void WriteRealisticConfigVdfWithEscapedQuotes(string steamRoot, string toolName)
    {
        var dir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("\"InstallConfigStore\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"Software\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"Valve\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"Steam\"");
        sb.AppendLine("            {");
        sb.AppendLine("                \"CompatToolMapping\"");
        sb.AppendLine("                {");
        sb.AppendLine("                    \"1361210\"");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        \"name\"        \"{toolName}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("                \"WebStorage\"");
        sb.AppendLine("                {");
        sb.AppendLine("                    \"DownloadsStoreRecentlyCompleted\"");
        sb.AppendLine("                    {");
        sb.Append("                        \"recent_downloads\"        \"{\\\"version\\\":\\\"2\\\",\\\"data\\\":\\\"test\\\"}\"");
        sb.AppendLine();
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "config.vdf"), sb.ToString());
    }

    // ---- Valve-managed tool resolution through appinfo.vdf -------------------

    [Fact]
    public void Valve_tool_resolves_by_canonical_key()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "proton_experimental");
        fx.WithValveProtonTool(
            fx.SteamRoot, fx.SteamRoot,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            installDir: "Proton - Experimental",
            displayName: "Proton Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        var expectedProton = Path.Combine(fx.SteamRoot, "steamapps", "common", "Proton - Experimental", "proton");
        Assert.Equal(expectedProton, result.ProtonBinaryPath);
        Assert.Equal("Proton Experimental", result.ProtonVersion);
    }

    [Fact]
    public void Valve_tool_resolves_by_alias()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // The mapping selects an alias, not the canonical key.
        fx.WithCompatToolMapping(fx.SteamRoot, "aliased_name");
        fx.WithValveProtonTool(
            fx.SteamRoot, fx.SteamRoot,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            installDir: "Proton - Experimental",
            displayName: "Proton Experimental",
            aliases: "aliased_name,other_alias");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.NotNull(result.ProtonBinaryPath);
        Assert.Equal("Proton Experimental", result.ProtonVersion);
    }

    [Fact]
    public void Valve_tool_found_in_secondary_library()
    {
        using var fx = new SteamFixture();
        var secondary = Path.Combine(fx.TempRoot, "secondary-lib");
        Directory.CreateDirectory(secondary);
        fx.WithLibraryFoldersAtSteamRoot(fx.SteamRoot, secondary);
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "proton_experimental");
        // appmanifest + proton are in the secondary library.
        fx.WithValveProtonTool(
            fx.SteamRoot, secondary,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            installDir: "Proton - Experimental",
            displayName: "Proton Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        var expectedProton = Path.Combine(secondary, "steamapps", "common", "Proton - Experimental", "proton");
        Assert.Equal(expectedProton, result.ProtonBinaryPath);
    }

    // ---- no-user-mapping recommended-runtime fallback (public Discover surface) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_mapping_resolves_recommended_runtime_regardless_of_deck_identity(bool isSteamDeck)
    {
        // The exact live shape: Darktide recommends proton-11.0-beta; the
        // Steam Play manifest's proton_11 entry aliases it to app id 4628710
        // with display name "Proton 11.0". With no config.vdf mapping and a
        // resolvable install, discovery is Complete, and toggling Deck
        // identity does not change that.
        using var fx = new SteamFixture(configure: o => o.IsSteamDeck = isSteamDeck);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No config.vdf: no app-specific or global mapping.
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        var expectedProton = Path.Combine(fx.SteamRoot, "steamapps", "common", "Proton 11.0", "proton");
        Assert.Equal(expectedProton, result.ProtonBinaryPath);
        Assert.Equal("Proton 11.0", result.ProtonVersion);
    }

    [Fact]
    public void App_specific_mapping_wins_over_recommended_runtime()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
    }

    [Fact]
    public void Global_mapping_wins_over_recommended_runtime()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3", global: true);
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCustomProtonPath(fx.CompatToolsDir, "GE-Proton9-3"), result.ProtonBinaryPath);
    }

    [Fact]
    public void Invalid_app_specific_mapping_blocks_global_and_recommendation()
    {
        // The app-specific mapping carries a whitespace-only name: invalid, so
        // neither the valid global mapping nor the recommended runtime applies.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        WriteCompatToolMappings(fx.SteamRoot, appTool: "   ", globalTool: "global-tool");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "global-tool");
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
    }

    [Fact]
    public void Invalid_global_mapping_blocks_recommendation()
    {
        // Only a global mapping exists and its name is empty: invalid, so the
        // recommended runtime must not be used.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        WriteCompatToolMappings(fx.SteamRoot, appTool: null, globalTool: "");
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
    }

    [Fact]
    public void Unresolvable_selected_tool_blocks_recommendation()
    {
        // The selected global tool does not exist: the selection stays
        // authoritative and the recommended runtime is not consulted.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "missing-tool", global: true);
        fx.WithRecommendedRuntimeProton(fx.SteamRoot, fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Contains(result.Warnings, w => w.Contains("missing-tool", StringComparison.Ordinal));
    }

    // ---- helper: write config.vdf CompatToolMapping entries -------------------

    /// <summary>
    /// Writes a config.vdf CompatToolMapping with an app-specific entry
    /// (<paramref name="appTool"/>, null to omit) and a global <c>"0"</c> entry
    /// (<paramref name="globalTool"/>, null to omit).
    /// </summary>
    private static void WriteCompatToolMappings(string steamRoot, string? appTool, string? globalTool)
    {
        var dir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("\"InstallConfigStore\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"Software\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"Valve\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"Steam\"");
        sb.AppendLine("            {");
        sb.AppendLine("                \"CompatToolMapping\"");
        sb.AppendLine("                {");
        if (appTool is not null)
        {
            sb.AppendLine("                    \"1361210\"");
            sb.AppendLine("                    {");
            sb.AppendLine($"                        \"name\"        \"{appTool}\"");
            sb.AppendLine("                    }");
        }
        if (globalTool is not null)
        {
            sb.AppendLine("                    \"0\"");
            sb.AppendLine("                    {");
            sb.AppendLine($"                        \"name\"        \"{globalTool}\"");
            sb.AppendLine("                    }");
        }
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "config.vdf"), sb.ToString());
    }
}
