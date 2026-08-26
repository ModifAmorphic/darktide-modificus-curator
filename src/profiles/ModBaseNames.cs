using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// The one base-folder-name resolution over a mod container, shared by
/// staging (<c>ProfileService.ResolveStagingTarget</c>) and the load-order
/// reconciler so the two cannot drift. Internal to Profiles: base-name
/// semantics (the single base directory inside a resolved version folder,
/// or a linked external folder's own name) belong with the library that
/// stages them.
/// </summary>
internal static class ModBaseNames
{
    /// <summary>
    /// Resolves a managed container's base directory: the single subdirectory
    /// inside the version folder the given policy resolves to (the import
    /// validation guarantees exactly one; a corrupted or missing structure
    /// yields null). Returns the base directory's full path; the base NAME is
    /// its <see cref="Path.GetFileName"/>. Pure filesystem read, no writes.
    /// </summary>
    internal static string? TryResolveBaseDir(
        ModContainer container,
        ModVersionPolicy policy,
        IModRepository repo)
    {
        var version = container.ResolveVersion(policy);
        if (version is null)
        {
            return null;
        }

        var versionFolder = repo.GetVersionFolderPath(container.Id, version.Folder);
        if (!Directory.Exists(versionFolder))
        {
            // Defensive: the manifest points at a folder that is not on disk
            // (a hand-delete between prune + stage).
            return null;
        }

        string[] baseDirs;
        try
        {
            baseDirs = Directory.GetDirectories(versionFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (baseDirs.Length != 1)
        {
            return null;
        }

        return baseDirs[0];
    }

    /// <summary>
    /// Resolves a linked container's base name: the external folder's own
    /// name, when the folder exists. A missing external folder yields null
    /// (the mod cannot stage, so it cannot match anything either).
    /// </summary>
    internal static string? TryResolveLinkedBaseName(LinkedSource linked)
    {
        if (!Directory.Exists(linked.ExternalPath))
        {
            return null;
        }

        // Trim trailing separators so a path stored with a trailing slash
        // still yields its folder name (ExternalPath is normalized at link
        // time, so this is defensive only).
        var baseName = Path.GetFileName(linked.ExternalPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(baseName) ? null : baseName;
    }
}
