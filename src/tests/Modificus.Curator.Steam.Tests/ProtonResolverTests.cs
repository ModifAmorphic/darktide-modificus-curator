using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="ProtonResolver"/>: custom-tool resolution
/// (internal name, display name, relative/absolute install_path, missing-proton
/// rejection), Valve-tool resolution (canonical key + alias), precedence rules,
/// and best-effort degradation on missing/corrupt Steam metadata.
/// </summary>
public sealed class ProtonResolverTests
{
    private static SteamDiscoveryOptions Options(string tempRoot) => new()
    {
        Platform = DiscoveryPlatform.Linux,
        DarktideAppId = 1361210,
        LinuxCompatibilityToolsDir = Path.Combine(tempRoot, "user-ct"),
        LinuxSystemCompatibilityToolsDirs = new List<string> { Path.Combine(tempRoot, "sys-ct") },
    };

    private static string WriteCompatToolMapping(string steamRoot, string appId, string toolName)
    {
        var dir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.vdf");
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
        sb.AppendLine($"                    \"{appId}\"");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        \"name\"        \"{toolName}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void WriteCustomManifest(string dir, string toolName, string installPath, string? displayName = null)
    {
        Directory.CreateDirectory(dir);
        var display = displayName ?? toolName;
        var sb = new StringBuilder();
        sb.AppendLine("\"compatibilitytools\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"compat_tools\"");
        sb.AppendLine("    {");
        sb.AppendLine($"        \"{toolName}\"");
        sb.AppendLine("        {");
        sb.AppendLine($"            \"install_path\"   \"{installPath.Replace("\\", "\\\\", StringComparison.Ordinal)}\"");
        sb.AppendLine($"            \"display_name\"   \"{display}\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "compatibilitytool.vdf"), sb.ToString());
    }

    private static void PlaceProton(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "proton"), string.Empty);
    }

    [Fact]
    public void Custom_tool_relative_install_path_resolves()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "my-proton");

        // Tool under the user root; install_path is "." (same dir as manifest).
        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "my-proton");
        WriteCustomManifest(toolDir, "my-proton", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(toolDir, "proton"), result!.Value.Path);
        Assert.Equal("my-proton", result.Value.Version);
    }

    [Fact]
    public void Custom_tool_display_name_carried_as_version()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "my-proton");

        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "my-proton");
        WriteCustomManifest(toolDir, "my-proton", ".", "Custom Display 1.0");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal("Custom Display 1.0", result!.Value.Version);
    }

    [Fact]
    public void Custom_tool_absolute_install_path_resolves()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "my-proton");

        // install_path is an absolute directory separate from the manifest dir.
        var absInstall = Path.Combine(temp.Path, "abs-install");
        PlaceProton(absInstall);

        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "my-proton");
        WriteCustomManifest(toolDir, "my-proton", absInstall);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(absInstall, "proton"), result!.Value.Path);
    }

    [Fact]
    public void Custom_tool_missing_proton_binary_returns_null()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "no-binary");

        // Manifest present but no proton file.
        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "no-binary");
        WriteCustomManifest(toolDir, "no-binary", ".");

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.Null(result);
        Assert.Contains(warnings, w => w.Contains("no-binary", StringComparison.Ordinal));
    }

    [Fact]
    public void System_root_is_searched_when_user_root_has_no_match()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "sys-tool");

        var toolDir = Path.Combine(opts.LinuxSystemCompatibilityToolsDirs[0], "sys-tool");
        WriteCustomManifest(toolDir, "sys-tool", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(toolDir, "proton"), result!.Value.Path);
    }

    [Fact]
    public void Steam_root_compatibilitytools_d_is_searched()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps")); // make it look valid
        WriteCompatToolMapping(steamRoot, "1361210", "root-tool");

        // Tool under <steamRoot>/compatibilitytools.d, not the user or system root.
        var toolDir = Path.Combine(steamRoot, "compatibilitytools.d", "root-tool");
        WriteCustomManifest(toolDir, "root-tool", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(toolDir, "proton"), result!.Value.Path);
    }

    [Fact]
    public void Duplicate_roots_are_not_searched_twice()
    {
        // When the user root == <steamRoot>/compatibilitytools.d, the dedup
        // ensures a single search pass (the tool resolves once either way, but
        // this guards against wasted IO + potential double-resolution).
        using var temp = new TempDir();
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        Directory.CreateDirectory(Path.Combine(steamRoot, "compatibilitytools.d"));

        var opts = new SteamDiscoveryOptions
        {
            Platform = DiscoveryPlatform.Linux,
            DarktideAppId = 1361210,
            // Same path as <steamRoot>/compatibilitytools.d.
            LinuxCompatibilityToolsDir = Path.Combine(steamRoot, "compatibilitytools.d"),
            LinuxSystemCompatibilityToolsDirs = new List<string>(),
        };

        WriteCompatToolMapping(steamRoot, "1361210", "dup-tool");
        var toolDir = Path.Combine(steamRoot, "compatibilitytools.d", "dup-tool");
        WriteCustomManifest(toolDir, "dup-tool", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
    }

    // ---- precedence ---------------------------------------------------------

    [Fact]
    public void App_specific_mapping_is_authoritative()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);

        // Write both app-specific + global, app-specific wins.
        var configDir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.vdf"), """
            "InstallConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "CompatToolMapping"
                            {
                                "1361210"
                                {
                                    "name"        "app-tool"
                                }
                                "0"
                                {
                                    "name"        "global-tool"
                                }
                            }
                        }
                    }
                }
            }
            """);

        var appDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "app-tool");
        WriteCustomManifest(appDir, "app-tool", ".");
        PlaceProton(appDir);

        var globalDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "global-tool");
        WriteCustomManifest(globalDir, "global-tool", ".");
        PlaceProton(globalDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(appDir, "proton"), result!.Value.Path);
    }

    [Fact]
    public void Empty_app_specific_name_fails_without_falling_through()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);

        var configDir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.vdf"), """
            "InstallConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "CompatToolMapping"
                            {
                                "1361210"
                                {
                                    "name"        ""
                                }
                                "0"
                                {
                                    "name"        "global-tool"
                                }
                            }
                        }
                    }
                }
            }
            """);

        var globalDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "global-tool");
        WriteCustomManifest(globalDir, "global-tool", ".");
        PlaceProton(globalDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        // The empty app-specific name fails; no fall-through to global.
        Assert.Null(result);
    }

    [Fact]
    public void Missing_config_vdf_yields_null_with_no_mapping_warning()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.Null(result);
        Assert.Contains(warnings, w => w.Contains("No Steam compatibility tool mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_config_vdf_degrades_gracefully()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        var configDir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.vdf"), "this is not valid VDF {{{");

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.Null(result);
        Assert.Contains(warnings, w => w.Contains("No Steam compatibility tool mapping", StringComparison.Ordinal));
    }

    // ---- Valve-managed tool resolution --------------------------------------

    [Fact]
    public void Valve_tool_resolves_by_canonical_key_via_appinfo()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "proton_experimental");

        // Write a synthetic binary appinfo.vdf.
        var appcacheDir = Path.Combine(steamRoot, "appcache");
        Directory.CreateDirectory(appcacheDir);
        File.WriteAllBytes(
            Path.Combine(appcacheDir, "appinfo.vdf"),
            AppInfoFixture.Build(891390, "proton_experimental", 1493710, "Proton Experimental"));

        // Write the appmanifest + proton under the library.
        var library = steamRoot;
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1493710.acf"), """
            "AppState"
            {
                "appid"        "1493710"
                "installdir"   "Proton - Experimental"
            }
            """);
        var commonDir = Path.Combine(steamapps, "common", "Proton - Experimental");
        PlaceProton(commonDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { library }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(commonDir, "proton"), result!.Value.Path);
        Assert.Equal("Proton Experimental", result.Value.Version);
    }

    [Fact]
    public void Valve_tool_resolves_by_alias()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "alias_name");

        var appcacheDir = Path.Combine(steamRoot, "appcache");
        Directory.CreateDirectory(appcacheDir);
        File.WriteAllBytes(
            Path.Combine(appcacheDir, "appinfo.vdf"),
            AppInfoFixture.Build(891390, "proton_experimental", 1493710, "Proton Experimental", "alias_name,other"));

        var library = steamRoot;
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1493710.acf"), """
            "AppState"
            {
                "appid"        "1493710"
                "installdir"   "Proton - Experimental"
            }
            """);
        PlaceProton(Path.Combine(steamapps, "common", "Proton - Experimental"));

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { library }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal("Proton Experimental", result!.Value.Version);
    }

    [Fact]
    public void Missing_appinfo_degrades_to_null_without_throwing()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "proton_experimental");
        // No appinfo.vdf, no custom tool.

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.Null(result);
        Assert.Contains(warnings, w => w.Contains("proton_experimental", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_appinfo_degrades_to_null_without_throwing()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "proton_experimental");

        var appcacheDir = Path.Combine(steamRoot, "appcache");
        Directory.CreateDirectory(appcacheDir);
        File.WriteAllBytes(Path.Combine(appcacheDir, "appinfo.vdf"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.Null(result);
    }

    // ---- escape sequences: real config.vdf regression -----------------------

    [Fact]
    public void Config_vdf_with_escaped_quotes_after_mapping_parses_correctly()
    {
        // Regression for the operator's real config.vdf: after CompatToolMapping,
        // Steam writes JSON values with escaped quotes (e.g.
        // WebStorage/DownloadsStoreRecentlyCompleted). ValveKeyValue defaults
        // HasEscapeSequences=false, which treats \" as a string terminator and
        // throws InvalidOperationException mid-parse. With HasEscapeSequences=true
        // the resolver reads the mapping cleanly.
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);

        WriteRealisticConfigVdf(steamRoot, "1361210", "my-proton");

        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "my-proton");
        WriteCustomManifest(toolDir, "my-proton", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(toolDir, "proton"), result!.Value.Path);
        // No malformed-config warning should appear.
        Assert.DoesNotContain(warnings, w => w.Contains("No Steam compatibility tool mapping", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes a config.vdf matching the operator's real shape: a valid
    /// CompatToolMapping followed by a JSON scalar with escaped quotes. The
    /// escaped-quote section is what broke the default-options parser.
    /// </summary>
    private static void WriteRealisticConfigVdf(string steamRoot, string appId, string toolName)
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
        sb.AppendLine($"                    \"{appId}\"");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        \"name\"        \"{toolName}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        // JSON value with escaped quotes AFTER the mapping, matching the real
        // file's WebStorage/DownloadsStoreRecentlyCompleted section.
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

    // ---- root-level manifest layout -----------------------------------------

    [Fact]
    public void Root_level_manifest_with_relative_install_path_resolves()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "root-manifest-tool");

        // Manifest directly at the root, install_path relative to the root.
        var root = opts.LinuxCompatibilityToolsDir!;
        WriteCustomManifest(root, "root-manifest-tool", "subdir/proton-install");
        PlaceProton(Path.Combine(root, "subdir", "proton-install"));

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(root, "subdir", "proton-install", "proton"), result!.Value.Path);
    }

    [Fact]
    public void Root_level_manifest_with_multiple_tool_entries_selects_exact_name()
    {
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "tool-b");

        // One root-level manifest defining two tools; only tool-b has a proton.
        var root = opts.LinuxCompatibilityToolsDir!;
        Directory.CreateDirectory(root);
        var sb = new StringBuilder();
        sb.AppendLine("\"compatibilitytools\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"compat_tools\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"tool-a\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"install_path\"   \"dir-a\"");
        sb.AppendLine("            \"display_name\"   \"Tool A\"");
        sb.AppendLine("        }");
        sb.AppendLine("        \"tool-b\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"install_path\"   \"dir-b\"");
        sb.AppendLine("            \"display_name\"   \"Tool B\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(root, "compatibilitytool.vdf"), sb.ToString());

        PlaceProton(Path.Combine(root, "dir-b"));

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(root, "dir-b", "proton"), result!.Value.Path);
        Assert.Equal("Tool B", result.Value.Version);
    }

    [Fact]
    public void Root_level_manifest_takes_precedence_within_same_root()
    {
        // When both a root-level manifest and a subdir manifest define the tool,
        // the root-level manifest is checked first within that root.
        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steamRoot);
        WriteCompatToolMapping(steamRoot, "1361210", "shared-name");

        var root = opts.LinuxCompatibilityToolsDir!;
        // Root-level manifest points at dir-root.
        WriteCustomManifest(root, "shared-name", "dir-root", "From Root");
        PlaceProton(Path.Combine(root, "dir-root"));

        // Subdir manifest also defines the same name, pointing at dir-sub.
        var subdir = Path.Combine(root, "shared-name");
        WriteCustomManifest(subdir, "shared-name", ".", "From Subdir");
        PlaceProton(subdir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(root, "dir-root", "proton"), result!.Value.Path);
        Assert.Equal("From Root", result.Value.Version);
    }

    // ---- end-to-end: sanitized real config.vdf fixture ----------------------

    [Fact]
    public void Realistic_fixture_config_vdf_resolves_proton_with_escaped_json()
    {
        // End-to-end regression: copies the checked-in sanitized config.vdf
        // fixture (matching the operator's real file shape with ~hundreds of
        // escaped quotes in WebStorage sections after CompatToolMapping) into a
        // temp Steam layout, scaffolds a matching custom tool, and proves the
        // resolver picks up the selected tool name despite the escaped JSON.
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "config-escaped.vdf");
        Assert.True(File.Exists(fixturePath), "Fixture file missing: " + fixturePath);

        using var temp = new TempDir();
        var opts = Options(temp.Path);
        var steamRoot = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "config"));

        // Copy the sanitized fixture into the temp Steam layout.
        File.Copy(fixturePath, Path.Combine(steamRoot, "config", "config.vdf"));

        // Scaffold a fabricated custom tool matching the fixture's mapping name.
        var toolDir = Path.Combine(opts.LinuxCompatibilityToolsDir!, "fixture-proton");
        WriteCustomManifest(toolDir, "fixture-proton", ".");
        PlaceProton(toolDir);

        var resolver = new ProtonResolver(opts, NullLogger.Instance);
        var warnings = new List<string>();
        var result = resolver.Resolve(steamRoot, new[] { steamRoot }, warnings);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(toolDir, "proton"), result!.Value.Path);
        Assert.Equal("fixture-proton", result.Value.Version);
        // No malformed-config warning despite the heavy escaped JSON content.
        Assert.DoesNotContain(warnings, w =>
            w.Contains("No Steam compatibility tool mapping", StringComparison.Ordinal));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proton-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
