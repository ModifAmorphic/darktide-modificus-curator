using System.Text;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Per-test fixture: scaffolds a synthetic Steam layout in a fresh temp dir +
/// builds a real <see cref="ISteamService"/> through <c>AddSteam()</c> with the
/// discovery options + platform seams pointed at the fixture. Disposes the temp
/// tree + the service provider on teardown so tests are isolated regardless of
/// outcome.
/// </summary>
/// <remarks>
/// Resolving via DI (rather than constructing the internal implementation
/// directly) keeps tests black-box against <see cref="ISteamService"/> and
/// proves the real registration path. <see cref="SteamDiscoveryOptions"/> /
/// <see cref="ISteamRegistryReader"/> / <see cref="IProcessLookup"/> /
/// <see cref="IConfigLoader"/> are pre-registered so <c>AddSteam()</c>'s
/// <c>TryAdd</c> defaults are skipped in favor of the fixture's fakes.
/// <see cref="Config"/> exposes the live <see cref="CuratorConfig"/> so overlay
/// tests can set <see cref="DiscoveryConfig"/> fields.
/// </remarks>
internal sealed class SteamFixture : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SteamDiscoveryOptions _options;

    public string TempRoot { get; }
    public string SteamRoot { get; }       // the "native" Linux / Windows fixture Steam install
    public string FlatpakRoot { get; }     // the Flatpak candidate
    public string CompatToolsDir { get; }  // compatibilitytools.d candidate (user root)
    public string SystemCompatToolsDir { get; } // a system-wide compatibilitytools.d
    public FakeRegistryReader Registry { get; } = new();
    public FakeProcessLookup Processes { get; } = new();
    public FakeConfigLoader ConfigLoader { get; } = new();
    public CuratorConfig Config => ConfigLoader.Config;
    public ISteamService Service { get; }

    public SteamFixture(
        DiscoveryPlatform platform = DiscoveryPlatform.Linux,
        Action<SteamDiscoveryOptions>? configure = null)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "curator-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);
        SteamRoot = Path.Combine(TempRoot, "Steam");
        FlatpakRoot = Path.Combine(TempRoot, "flatpak-Steam");
        CompatToolsDir = Path.Combine(TempRoot, "compatibilitytools.d");
        SystemCompatToolsDir = Path.Combine(TempRoot, "system-compatibilitytools.d");

        _options = new SteamDiscoveryOptions
        {
            Platform = platform,
            LinuxDefaultSteamRoot = SteamRoot,
            LinuxFlatpakSteamRoot = FlatpakRoot,
            LinuxCompatibilityToolsDir = CompatToolsDir,
            // Point system roots at fixture paths so tests never touch host state.
            LinuxSystemCompatibilityToolsDirs = new List<string> { SystemCompatToolsDir },
            // Reuse the fixture root for Windows tests (registry supplies it via
            // a fake rather than a real second path).
            WindowsDefaultSteamRoot = SteamRoot,
        };
        configure?.Invoke(_options);

        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton<ISteamRegistryReader>(Registry);
        services.AddSingleton<IProcessLookup>(Processes);
        services.AddSingleton<IConfigLoader>(ConfigLoader);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddSteam();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<ISteamService>();
    }

    // ---- layout helpers (fluent; return this) -----------------------------

    /// <summary>
    /// Writes a <c>libraryfolders.vdf</c> under the native Steam root listing the
    /// given library roots. With no args, lists the Steam root itself (a valid
    /// single-library layout).
    /// </summary>
    public SteamFixture WithLibraryFoldersAtSteamRoot(params string[] libraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        var libs = libraryPaths.Length == 0 ? new[] { SteamRoot } : libraryPaths;
        File.WriteAllText(
            Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"),
            BuildLibraryFoldersVdf(libs));
        return this;
    }

    /// <summary>Same as <see cref="WithLibraryFoldersAtSteamRoot"/> but writes
    /// under the Flatpak root so the Flatpak candidate is the one that resolves.</summary>
    public SteamFixture WithLibraryFoldersAtFlatpakRoot(params string[] libraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(FlatpakRoot, "steamapps"));
        var libs = libraryPaths.Length == 0 ? new[] { FlatpakRoot } : libraryPaths;
        File.WriteAllText(
            Path.Combine(FlatpakRoot, "steamapps", "libraryfolders.vdf"),
            BuildLibraryFoldersVdf(libs));
        return this;
    }

    /// <summary>Creates an empty Darktide.exe under <c>&lt;libraryRoot&gt;/steamapps/common/&lt;DarktideCommonDir&gt;/binaries/</c>.</summary>
    public SteamFixture WithDarktide(string libraryRoot)
    {
        var exe = Path.Combine(
            libraryRoot, "steamapps", "common",
            _options.DarktideCommonDir, "binaries", _options.GameBinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty);
        return this;
    }

    /// <summary>Creates the compatdata dir for the configured app id under the given Steam root.</summary>
    public SteamFixture WithCompatdata(string steamRoot)
    {
        Directory.CreateDirectory(Path.Combine(
            steamRoot, "steamapps", "compatdata", _options.DarktideAppId.ToString()));
        return this;
    }

    // ---- Proton (compatibility-tool) helpers ------------------------------

    /// <summary>
    /// Writes <c>config/config.vdf</c> under the given Steam root with a
    /// CompatToolMapping entry for the Darktide app id selecting
    /// <paramref name="toolName"/>. Pass <c>global: true</c> to write the
    /// mapping under key <c>"0"</c> instead.
    /// </summary>
    public SteamFixture WithCompatToolMapping(string steamRoot, string toolName, bool global = false)
    {
        var dir = Path.Combine(steamRoot, "config");
        Directory.CreateDirectory(dir);
        var key = global ? "0" : _options.DarktideAppId.ToString();
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
        sb.AppendLine($"                    \"{key}\"");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        \"name\"        \"{EscapeVdf(toolName)}\"");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "config.vdf"), sb.ToString());
        return this;
    }

    /// <summary>
    /// Scaffolds a custom Proton tool under <c>&lt;root&gt;/&lt;toolName&gt;/</c>
    /// with a <c>compatibilitytool.vdf</c> manifest + a <c>proton</c> file.
    /// </summary>
    /// <param name="root">The compatibility-tools root (e.g. compatibilitytools.d).</param>
    /// <param name="toolName">The tool's internal name (also the directory name).</param>
    /// <param name="installPath">The <c>install_path</c> value in the manifest (<c>.</c> = same dir).</param>
    /// <param name="displayName">Optional display name; defaults to <paramref name="toolName"/>.</param>
    public SteamFixture WithCustomProtonTool(
        string root, string toolName, string installPath = ".", string? displayName = null)
    {
        var toolDir = Path.Combine(root, toolName);
        Directory.CreateDirectory(toolDir);

        var manifest = Path.Combine(toolDir, "compatibilitytool.vdf");
        var display = displayName ?? toolName;
        var sb = new StringBuilder();
        sb.AppendLine("\"compatibilitytools\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"compat_tools\"");
        sb.AppendLine("    {");
        sb.AppendLine($"        \"{EscapeVdf(toolName)}\"");
        sb.AppendLine("        {");
        sb.AppendLine($"            \"install_path\"   \"{EscapeVdf(installPath)}\"");
        sb.AppendLine($"            \"display_name\"   \"{EscapeVdf(display)}\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(manifest, sb.ToString());

        // Place the proton file at the resolved install path.
        var resolvedInstall = installPath == "." ? toolDir : installPath;
        var protonDir = Path.IsPathRooted(resolvedInstall)
            ? resolvedInstall
            : Path.Combine(toolDir, resolvedInstall);
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), string.Empty);
        return this;
    }

    /// <summary>
    /// Scaffolds a Valve-managed Proton tool: writes a synthetic binary
    /// <c>appinfo.vdf</c> carrying <c>compat_tools</c>, writes the tool's
    /// <c>appmanifest_&lt;appId&gt;.acf</c> under a library, and places the
    /// <c>proton</c> file at <c>&lt;library&gt;/steamapps/common/&lt;installDir&gt;/</c>.
    /// </summary>
    public SteamFixture WithValveProtonTool(
        string steamRoot,
        string library,
        string toolName,
        int protonAppId,
        string installDir,
        string displayName,
        string? aliases = null)
    {
        var appInfoDir = Path.Combine(steamRoot, "appcache");
        Directory.CreateDirectory(appInfoDir);
        var appInfoPath = Path.Combine(appInfoDir, "appinfo.vdf");
        File.WriteAllBytes(appInfoPath, AppInfoFixture.Build(
            appId: 891390,
            toolName: toolName,
            protonAppId: protonAppId,
            displayName: displayName,
            aliases: aliases));

        // Write appmanifest under the library.
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        var manifestName = $"appmanifest_{protonAppId}.acf";
        var manifest = new StringBuilder();
        manifest.AppendLine("\"AppState\"");
        manifest.AppendLine("{");
        manifest.AppendLine($"    \"appid\"        \"{protonAppId}\"");
        manifest.AppendLine($"    \"installdir\"   \"{EscapeVdf(installDir)}\"");
        manifest.AppendLine("}");
        File.WriteAllText(Path.Combine(steamapps, manifestName), manifest.ToString());

        // Place the proton binary.
        var commonDir = Path.Combine(steamapps, "common", installDir);
        Directory.CreateDirectory(commonDir);
        File.WriteAllText(Path.Combine(commonDir, "proton"), string.Empty);
        return this;
    }

    /// <summary>
    /// Scaffolds the recommended-runtime Proton layout: writes the realistic
    /// multi-entry <c>appinfo.vdf</c> (the Steam Play manifest's
    /// <c>compat_tools</c> incl. <c>proton_11</c>, plus Darktide's recommended
    /// runtime), the tool's <c>appmanifest</c> under a library, and its
    /// <c>proton</c> file.
    /// </summary>
    public SteamFixture WithRecommendedRuntimeProton(
        string steamRoot,
        string library,
        string installDir = "Proton 11.0",
        string recommendedRuntime = AppInfoFixture.RecommendedRuntime)
    {
        var appInfoDir = Path.Combine(steamRoot, "appcache");
        Directory.CreateDirectory(appInfoDir);
        File.WriteAllBytes(
            Path.Combine(appInfoDir, "appinfo.vdf"),
            AppInfoFixture.BuildRecommendedRuntimeAppInfo(recommendedRuntime));

        // Write the proton_11 appmanifest + proton binary under the library.
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        var manifestName = $"appmanifest_{AppInfoFixture.Proton11AppId}.acf";
        var manifest = new StringBuilder();
        manifest.AppendLine("\"AppState\"");
        manifest.AppendLine("{");
        manifest.AppendLine($"    \"appid\"        \"{AppInfoFixture.Proton11AppId}\"");
        manifest.AppendLine($"    \"installdir\"   \"{EscapeVdf(installDir)}\"");
        manifest.AppendLine("}");
        File.WriteAllText(Path.Combine(steamapps, manifestName), manifest.ToString());

        var commonDir = Path.Combine(steamapps, "common", installDir);
        Directory.CreateDirectory(commonDir);
        File.WriteAllText(Path.Combine(commonDir, "proton"), string.Empty);
        return this;
    }

    // ---- expected-path helpers (assertions) -------------------------------

    public string ExpectedDarktidePath(string libraryRoot) => Path.Combine(
        libraryRoot, "steamapps", "common",
        _options.DarktideCommonDir, "binaries", _options.GameBinaryName);

    public string ExpectedCompatdataPath(string steamRoot) => Path.Combine(
        steamRoot, "steamapps", "compatdata", _options.DarktideAppId.ToString());

    public string ExpectedCustomProtonPath(string root, string toolName) =>
        Path.Combine(root, toolName, "proton");

    // ---- static VDF builder ------------------------------------------------

    /// <summary>Builds a realistic minimal <c>libraryfolders.vdf</c> body listing the given library roots.</summary>
    public static string BuildLibraryFoldersVdf(params string[] libraryPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"libraryfolders\"");
        sb.AppendLine("{");
        for (var i = 0; i < libraryPaths.Length; i++)
        {
            sb.AppendLine($"\t\"{i}\"");
            sb.AppendLine("\t{");
            sb.AppendLine($"\t\t\"path\"\t\t\"{EscapeVdfValue(libraryPaths[i])}\"");
            sb.AppendLine("\t\t\"label\"\t\t\"\"");
            sb.AppendLine("\t\t\"contentid\"\t\t\"0\"");
            sb.AppendLine("\t\t\"apps\"");
            sb.AppendLine("\t\t{");
            sb.AppendLine("\t\t}");
            sb.AppendLine("\t}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeVdfValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string EscapeVdf(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(TempRoot))
        {
            // Best-effort; temp dirs under the OS temp are harmless if left.
            try { Directory.Delete(TempRoot, recursive: true); }
            catch (IOException) { /* ignored */ }
        }
    }
}

/// <summary>Test double for <see cref="ISteamRegistryReader"/>; returns <see cref="SteamPath"/>.</summary>
internal sealed class FakeRegistryReader : ISteamRegistryReader
{
    public string? SteamPath { get; set; }
    public string? GetSteamPath() => SteamPath;
}

/// <summary>Test double for <see cref="IProcessLookup"/>; reports the names in <see cref="Running"/> as running.</summary>
internal sealed class FakeProcessLookup : IProcessLookup
{
    public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
    public bool IsRunning(string processName) => Running.Contains(processName);
}

/// <summary>
/// Minimal <see cref="IConfigLoader"/> double for the steam tests: serves a
/// mutable <see cref="CuratorConfig"/> (so overlay tests can set
/// <see cref="DiscoveryConfig"/> fields before calling
/// <see cref="ISteamService.Discover"/>). <see cref="Save"/> mirrors the real
/// loader's round-trip: it promotes the written config to the live snapshot, so
/// the next <see cref="Load"/> returns what was saved (and a read-modify-save
/// in <see cref="SteamService.Discover"/> sees the prior Save's effect).
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load() => Config;

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        // Promote to the live Config so a subsequent Load returns the saved
        // state (mirrors the real loader's round-trip through the disk file).
        Config = config;
    }
}
