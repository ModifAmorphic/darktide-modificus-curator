using System.Globalization;
using Microsoft.Extensions.Logging;
using ValveKeyValue;

namespace Modificus.Curator.Steam;

/// <summary>
/// The three-state outcome of reading Steam's CompatToolMapping for Darktide:
/// a selected tool name (authoritative), a definitive absence of any mapping,
/// or an invalid/unreadable mapping that must fail resolution without falling
/// through.
/// </summary>
internal enum CompatToolSelectionKind
{
    Selected,
    Absent,
    Invalid,
}

internal sealed record CompatToolSelection(CompatToolSelectionKind Kind, string? ToolName, string? Reason)
{
    public static CompatToolSelection Selected(string toolName) =>
        new(CompatToolSelectionKind.Selected, toolName, null);

    public static CompatToolSelection Absent { get; } =
        new(CompatToolSelectionKind.Absent, null, null);

    public static CompatToolSelection Invalid(string reason) =>
        new(CompatToolSelectionKind.Invalid, null, reason);
}

/// <summary>
/// Resolves the effective Linux Proton from Steam's compatibility-tool
/// selection: the app-specific <c>CompatToolMapping</c> entry in
/// <c>config.vdf</c> is authoritative, the global <c>"0"</c> entry is the
/// fallback, and when both are absent, Darktide's appinfo recommended runtime
/// (the <c>steam_deck_compatibility</c> metadata Steam maintains) is Steam's
/// non-user default on any Linux host. The selected name resolves to a concrete
/// <c>proton</c> binary either as a custom tool (a
/// <c>compatibilitytool.vdf</c> manifest at or under a compatibility-tools
/// root) or a Valve-managed tool (the <c>compat_tools</c> alias table in
/// <c>appinfo.vdf</c> plus an <c>appmanifest</c>).
/// </summary>
/// <remarks>
/// All Steam-metadata access is best-effort: a missing or unreadable file
/// degrades to an unresolved Proton (warning), never a throw.
/// </remarks>
internal sealed class ProtonResolver
{
    private readonly SteamDiscoveryOptions _options;
    private readonly SteamAppInfoReader _appInfoReader;
    private readonly ILogger _logger;

    public ProtonResolver(SteamDiscoveryOptions options, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appInfoReader = new SteamAppInfoReader(logger);
    }

    /// <summary>
    /// Resolves the Proton binary + version label for the configured Darktide
    /// app id. The selected tool name comes from Steam's CompatToolMapping
    /// (app-specific, then global); when both are absent, Darktide's appinfo
    /// recommended runtime supplies the name. An invalid or unreadable mapping,
    /// or one whose named tool cannot be resolved, fails without falling
    /// through. Returns null when no tool resolves; a reason is appended to
    /// <paramref name="warnings"/> in those cases.
    /// </summary>
    public (string Path, string Version)? Resolve(
        string steamRoot,
        IReadOnlyList<string> libraries,
        List<string> warnings)
    {
        var selection = ReadSelectedTool(steamRoot);

        string selected;
        SteamAppInfoSnapshot? appInfo = null;

        switch (selection.Kind)
        {
            case CompatToolSelectionKind.Selected:
                selected = selection.ToolName!;
                break;

            case CompatToolSelectionKind.Invalid:
                warnings.Add(selection.Reason!);
                return null;

            default:
                // Both mappings are definitively absent, so appinfo cannot
                // bypass a user choice: read it once here and reuse the
                // snapshot for Valve-managed resolution below.
                appInfo = ReadAppInfoSnapshot(steamRoot, (uint)_options.DarktideAppId);
                var recommendation = appInfo?.RecommendedRuntime?.Trim();
                if (string.IsNullOrEmpty(recommendation))
                {
                    warnings.Add(
                        "No Steam compatibility tool mapping for Darktide and no usable recommended runtime in appinfo.vdf; Proton unresolved.");
                    return null;
                }

                if (string.Equals(recommendation, "native", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("Darktide's appinfo recommended runtime is native; Proton unresolved.");
                    return null;
                }

                selected = recommendation;
                break;
        }

        var roots = BuildCompatibilityToolRoots(steamRoot);

        if (ResolveCustomTool(selected, roots) is { } custom)
        {
            return custom;
        }

        if (ResolveValveTool(selected, steamRoot, libraries, appInfo) is { } valve)
        {
            return valve;
        }

        warnings.Add($"Steam compatibility tool '{selected}' could not be resolved to a Proton binary.");
        return null;
    }

    /// <summary>
    /// Reads <c>&lt;steamRoot&gt;/config/config.vdf</c>, locates
    /// <c>CompatToolMapping</c>, and classifies the tool selection for the
    /// Darktide app id. The app-specific mapping is authoritative: when present,
    /// its name is used as-is (a missing, non-string, empty, or whitespace name
    /// is invalid and fails resolution without falling through). The global
    /// <c>"0"</c> mapping is considered only when the app-specific mapping is
    /// absent. A missing config file, or a valid config with neither key, is
    /// definitively absent; an unreadable or unparseable config is invalid so a
    /// possible user choice is never bypassed.
    /// </summary>
    private CompatToolSelection ReadSelectedTool(string steamRoot)
    {
        var configVdf = Path.Combine(steamRoot, "config", "config.vdf");
        KVDocument doc;
        try
        {
            using var stream = new FileStream(configVdf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            doc = SteamTextVdf.Deserialize(stream);
        }
        catch (FileNotFoundException)
        {
            return CompatToolSelection.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return CompatToolSelection.Absent;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read config.vdf at {Path}.", configVdf);
            return CompatToolSelection.Invalid(
                $"Steam config.vdf at '{configVdf}' could not be read; Proton unresolved.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied reading config.vdf at {Path}.", configVdf);
            return CompatToolSelection.Invalid(
                $"Steam config.vdf at '{configVdf}' could not be read (permission denied); Proton unresolved.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Malformed config.vdf at {Path}.", configVdf);
            return CompatToolSelection.Invalid(
                $"Steam config.vdf at '{configVdf}' could not be parsed; Proton unresolved without bypassing a possible tool mapping.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Malformed config.vdf at {Path}.", configVdf);
            return CompatToolSelection.Invalid(
                $"Steam config.vdf at '{configVdf}' could not be parsed; Proton unresolved without bypassing a possible tool mapping.");
        }

        var mapping = TryGetChild(TryGetChild(TryGetChild(TryGetChild(
            doc.Root, "Software"), "Valve"), "Steam"), "CompatToolMapping");
        if (mapping is null)
        {
            return CompatToolSelection.Absent;
        }

        var appId = _options.DarktideAppId.ToString(CultureInfo.InvariantCulture);

        // App-specific mapping is authoritative when present: an invalid name
        // fails here and does NOT fall through to global or the
        // recommended-runtime fallback.
        if (mapping.TryGetValue(appId, out var appEntry))
        {
            return ReadEntrySelection(appEntry);
        }

        if (mapping.TryGetValue("0", out var globalEntry))
        {
            return ReadEntrySelection(globalEntry);
        }

        return CompatToolSelection.Absent;
    }

    private static CompatToolSelection ReadEntrySelection(KVObject entry)
    {
        if (!entry.TryGetValue("name", out var nameObj))
        {
            return CompatToolSelection.Invalid(
                "Steam compatibility tool mapping for Darktide has no tool name; Proton unresolved.");
        }

        if (nameObj.ValueType != KVValueType.String)
        {
            return CompatToolSelection.Invalid(
                "Steam compatibility tool mapping for Darktide has a non-string tool name; Proton unresolved.");
        }

        var name = (string)nameObj;
        if (string.IsNullOrWhiteSpace(name))
        {
            return CompatToolSelection.Invalid(
                "Steam compatibility tool mapping for Darktide has an empty tool name; Proton unresolved.");
        }

        return CompatToolSelection.Selected(name);
    }

    /// <summary>
    /// Builds the deduplicated, ordered list of compatibility-tool search roots:
    /// the resolved Steam root's <c>compatibilitytools.d</c> first, then the
    /// configured user root, then the system roots.
    /// </summary>
    private IEnumerable<string> BuildCompatibilityToolRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? YieldIfNew(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir))
            {
                return dir;
            }
            return null;
        }

        if (YieldIfNew(Path.Combine(steamRoot, "compatibilitytools.d")) is { } steamRootTools)
        {
            yield return steamRootTools;
        }

        if (YieldIfNew(_options.LinuxCompatibilityToolsDir) is { } userTools)
        {
            yield return userTools;
        }

        foreach (var systemDir in _options.LinuxSystemCompatibilityToolsDirs)
        {
            if (YieldIfNew(systemDir) is { } sys)
            {
                yield return sys;
            }
        }
    }

    /// <summary>
    /// Searches every compatibility-tool root for a custom manifest whose
    /// <c>compat_tools</c> collection defines <paramref name="toolName"/>, then
    /// resolves its <c>install_path</c> (<c>.</c> or a relative path is relative
    /// to the manifest's directory; an absolute path is as-is) and requires the
    /// resolved <c>proton</c> file to exist.
    /// </summary>
    /// <remarks>
    /// Valve permits two registration layouts: a manifest at the root of a
    /// compatibility-tools directory (with a relative or absolute
    /// <c>install_path</c>), or a manifest inside a per-tool subdirectory. Each
    /// root is checked root-level first, then its subdirectories, preserving the
    /// established root ordering.
    /// </remarks>
    private (string Path, string Version)? ResolveCustomTool(
        string toolName, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            // Root-level manifest first (Valve permits compatibilitytool.vdf
            // directly at the root with a relative or absolute install_path).
            var rootManifest = Path.Combine(root, "compatibilitytool.vdf");
            if (TryResolveFromCustomManifest(toolName, rootManifest) is { } rootResolved)
            {
                return rootResolved;
            }

            // Then per-tool subdirectory manifests. Materialize the enumeration
            // inside the try so IO/permission failures during enumeration are
            // caught rather than escaping during iteration.
            List<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(root).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subdir in subdirs)
            {
                var manifestPath = Path.Combine(subdir, "compatibilitytool.vdf");
                if (TryResolveFromCustomManifest(toolName, manifestPath) is { } resolved)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private (string Path, string Version)? TryResolveFromCustomManifest(
        string toolName, string manifestPath)
    {
        KVObject? compatTools;
        try
        {
            if (!File.Exists(manifestPath)) return null;

            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // The document root is the value of the outer "compatibilitytools"
            // key, so "compat_tools" is a direct child of it.
            compatTools = TryGetChild(SteamTextVdf.Deserialize(stream).Root, "compat_tools");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read compatibilitytool.vdf at {Path}.", manifestPath);
            return null;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Malformed compatibilitytool.vdf at {Path}.", manifestPath);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Malformed compatibilitytool.vdf at {Path}.", manifestPath);
            return null;
        }

        if (compatTools is null || !compatTools.TryGetValue(toolName, out var toolEntry))
        {
            return null;
        }

        if (TryGetChild(toolEntry, "install_path") is not { } installPathObj)
        {
            return null;
        }

        var installPath = installPathObj.ValueType == KVValueType.String
            ? (string)installPathObj
            : null;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        var manifestDir = Path.GetDirectoryName(manifestPath)!;
        var resolved = ResolveInstallPath(installPath!, manifestDir);
        var proton = Path.Combine(resolved, "proton");
        if (!File.Exists(proton))
        {
            return null;
        }

        var displayName = TryGetChild(toolEntry, "display_name") is { } dn
            ? AsString(dn)
            : null;
        var version = !string.IsNullOrWhiteSpace(displayName) ? displayName! : toolName;

        return (proton, version);
    }

    private static string ResolveInstallPath(string installPath, string manifestDir)
    {
        if (installPath == ".")
        {
            return manifestDir;
        }

        return Path.IsPathRooted(installPath)
            ? installPath
            : Path.Combine(manifestDir, installPath);
    }

    /// <summary>
    /// Reads <c>appinfo.vdf</c>, finds the <c>compat_tools</c> entry whose key or
    /// comma-separated alias matches <paramref name="toolName"/>, then locates
    /// <c>appmanifest_&lt;appid&gt;.acf</c> across the libraries, parses its
    /// <c>installdir</c>, and requires
    /// <c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;/proton</c> to exist.
    /// </summary>
    /// <remarks>
    /// <paramref name="snapshot"/> reuses an appinfo read already made for the
    /// recommended runtime so one Resolve call parses the file at most
    /// once; it is read here only when null (with the runtime lookup
    /// skipped, since only the compat_tools registry is needed).
    /// </remarks>
    private (string Path, string Version)? ResolveValveTool(
        string toolName,
        string steamRoot,
        IReadOnlyList<string> libraries,
        SteamAppInfoSnapshot? snapshot)
    {
        snapshot ??= ReadAppInfoSnapshot(steamRoot, requestedAppId: 0);
        var compatTools = snapshot?.CompatTools;
        if (compatTools is null)
        {
            return null;
        }

        if (!TryResolveToolEntry(compatTools, toolName, out var entry))
        {
            return null;
        }

        if (entry.AppId == 0)
        {
            return null;
        }

        var manifestName = string.Format(
            CultureInfo.InvariantCulture, "appmanifest_{0}.acf", entry.AppId);

        foreach (var lib in libraries)
        {
            var manifestPath = Path.Combine(lib, "steamapps", manifestName);
            var installDir = ReadInstallDir(manifestPath);
            if (installDir is null) continue;

            var proton = Path.Combine(lib, "steamapps", "common", installDir, "proton");
            if (File.Exists(proton))
            {
                var version = !string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? entry.DisplayName
                    : toolName;
                return (proton, version);
            }
        }

        return null;
    }

    private SteamAppInfoSnapshot? ReadAppInfoSnapshot(string steamRoot, uint requestedAppId) =>
        _appInfoReader.ReadSnapshot(
            Path.Combine(steamRoot, "appcache", "appinfo.vdf"),
            requestedAppId);

    private static bool TryResolveToolEntry(
        IReadOnlyDictionary<string, CompatToolEntry> compatTools,
        string toolName,
        out CompatToolEntry entry)
    {
        if (compatTools.TryGetValue(toolName, out var direct))
        {
            entry = direct;
            return true;
        }

        // Fall back to the comma-separated aliases list of each entry.
        foreach (var candidate in compatTools.Values)
        {
            foreach (var alias in candidate.Aliases)
            {
                if (string.Equals(alias, toolName, StringComparison.Ordinal))
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = default!;
        return false;
    }

    private string? ReadInstallDir(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;

            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var root = SteamTextVdf.Deserialize(stream).Root;

            // The document root is the value of the outer "AppState" key, so
            // installdir is a direct child of it.
            if (!root.TryGetValue("installdir", out var installDirObj))
            {
                return null;
            }

            return installDirObj.ValueType == KVValueType.String
                ? (string)installDirObj
                : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read app manifest at {Path}.", manifestPath);
            return null;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Malformed app manifest at {Path}.", manifestPath);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Malformed app manifest at {Path}.", manifestPath);
            return null;
        }
    }

    private static KVObject? TryGetChild(KVObject? parent, string key) =>
        parent is not null && parent.TryGetValue(key, out var child) ? child : null;

    private static string? AsString(KVObject obj) =>
        obj.ValueType == KVValueType.String ? (string)obj : null;
}
