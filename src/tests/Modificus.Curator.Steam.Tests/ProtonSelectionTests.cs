using System.Text;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// The Proton compatibility-tool selection behavior exercised through the public
/// <see cref="ISteamService.Discover"/> surface:
/// - App-specific CompatToolMapping is authoritative.
/// - Global "0" mapping is used only when the app-specific mapping is absent.
/// - A present-but-malformed app-specific mapping fails (no fall-through).
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
        WriteConfigVdfWithBothMappings(fx.SteamRoot, appTool: "app-tool", globalTool: "global-tool");
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
        WriteConfigVdfWithBothMappings(fx.SteamRoot, appTool: "", globalTool: "global-tool");
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

    // ---- helper: write config.vdf with both app-specific + global mappings ----

    private static void WriteConfigVdfWithBothMappings(string steamRoot, string appTool, string globalTool)
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
        sb.AppendLine($"                        \"name\"        \"{appTool}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                    \"0\"");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        \"name\"        \"{globalTool}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "config.vdf"), sb.ToString());
    }
}
