using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Per-test filesystem + DI fixture: fresh temp <c>ProfilesBaseFolder</c> +
/// <c>ModsFolder</c>, and an <see cref="IProfileService"/> +
/// <see cref="IModRepository"/> resolved through the real
/// <c>AddProfiles()</c> / <c>AddMods()</c> registrations pointing at them.
/// Disposes the temp trees (and the service provider) on teardown so tests are
/// isolated regardless of outcome.
/// </summary>
/// <remarks>
/// Resolving via DI, rather than constructing the implementation directly,
/// keeps the tests black-box against <see cref="IProfileService"/> +
/// <see cref="IModRepository"/> and proves the real registration paths on every
/// test. <see cref="StagingLinkCreator"/> defaults to the real platform-selective
/// staging link (an NTFS junction on Windows, a symlink on Linux); pass a custom
/// one to the constructor to exercise the staging-link-failure path.
/// </remarks>
internal sealed class ProfileServiceFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public string BaseFolder { get; } = Path.Combine(Path.GetTempPath(), "curator-profiles-" + Guid.NewGuid());
    public string ModsFolder { get; } = Path.Combine(Path.GetTempPath(), "curator-mods-" + Guid.NewGuid());

    public IProfileService Service { get; }
    public IModRepository Repo { get; }

    /// <summary>
    /// The <see cref="IModImportService"/> resolved through the same DI tree
    /// (AddProfiles calls AddMods). Exposed so linked-folder tests can call
    /// <see cref="IModImportService.LinkFolder"/> against the same mods root +
    /// repository the profile service stages from.
    /// </summary>
    public IModImportService Imports { get; }

    /// <summary>
    /// The <see cref="ILoadOrderReconciler"/> resolved through the same DI
    /// tree, so load-order tests reconcile over the same live profile +
    /// repository the rest of the fixture uses.
    /// </summary>
    public ILoadOrderReconciler Reconciler { get; }

    /// <param name="createLink">Optional override for the staging-link seam
    /// (default: the platform-selective link, a junction on Windows or
    /// <see cref="Directory.CreateSymbolicLink"/> on Linux). Pass a throwing
    /// delegate to exercise the staging-link <see cref="IOException"/> path.</param>
    public ProfileServiceFixture(StagingLinkCreator? createLink = null)
    {
        var config = CuratorConfig.CreateDefault();
        config.ProfilesBaseFolder = BaseFolder;
        config.ModsFolder = ModsFolder;

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config });
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddMods();
        if (createLink is not null)
        {
            services.AddSingleton(createLink);
        }
        services.AddProfiles();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<IProfileService>();
        Repo = _provider.GetRequiredService<IModRepository>();
        Imports = _provider.GetRequiredService<IModImportService>();
        Reconciler = _provider.GetRequiredService<ILoadOrderReconciler>();
    }

    // ---- profile-tree path helpers -----------------------------------------

    public string ProfileDir(Guid id) => Path.Combine(BaseFolder, id.ToString());
    public string ProfileJson(Guid id) => Path.Combine(ProfileDir(id), "profile.json");

    // staged/ is the --mod-path (regenerated each PrepareModRoot).
    public string StagedDir(Guid id) => Path.Combine(ProfileDir(id), "staged");
    public string ModsLst(Guid id) => Path.Combine(StagedDir(id), "mods", "mods.lst");
    public string StagedModLink(Guid id, string linkName) => Path.Combine(StagedDir(id), "mods", linkName);

    // ---- repository path helpers -------------------------------------------

    public string ContainerDir(Guid containerId) => Path.Combine(ModsFolder, containerId.ToString());
    public string VersionDir(Guid containerId, string versionFolder) =>
        Path.Combine(ContainerDir(containerId), versionFolder);

    // ---- container seeding -------------------------------------------------

    /// <summary>
    /// Creates an external mod folder <em>outside</em> the mods root (under a
    /// fresh temp sibling) shaped as <see cref="IModImportService.LinkFolder"/>
    /// requires: the folder IS the base and directly contains
    /// <c>&lt;baseName&gt;.mod</c>. A <paramref name="sentinelName"/> marker file
    /// is written inside it (default <c>sentinel.txt</c>) so safety tests can
    /// assert the external target is never modified by any Curator operation.
    /// Returns the absolute path to the external folder.
    /// </summary>
    public string MakeExternalModFolder(string baseName, string sentinelName = "sentinel.txt")
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-external-" + Guid.NewGuid(), baseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, baseName + ".mod"), baseName);
        File.WriteAllText(Path.Combine(dir, sentinelName), "untouched");
        _externalTargets.Add(dir);
        return dir;
    }

    private readonly List<string> _externalTargets = new();

    /// <summary>
    /// Creates a container + a single version whose version folder contains the
    /// mod's base folder (named after the container, sanitized), with a
    /// <c>&lt;base&gt;.mod</c> descriptor + a marker file inside it. Mirrors the
    /// shape <c>ModImportService</c> now produces, so staging's base-folder
    /// discovery resolves the container name as the base name (existing mods.lst
    /// assertions stay valid). The version becomes the container's
    /// <c>IsLatest</c>. Used by tests to make a container stage-able.
    /// </summary>
    public ModContainer AddContainerWithVersion(
        string name,
        string versionString = "1.0.0",
        ModSource? source = null)
    {
        var container = Repo.CreateContainer(source ?? new UntrackedSource(), name);
        return Repo.AddVersion(container.Id, versionString, dir =>
        {
            var baseName = SanitizeBaseName(name);
            var baseDir = Path.Combine(dir, baseName);
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, baseName + ".mod"), name);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), name);
        });
    }

    /// <summary>
    /// Adds a second version to an existing container whose version folder
    /// contains the mod's base folder (named after the container, sanitized) +
    /// marker, and returns the updated container. The new version becomes the
    /// container's <c>IsLatest</c>.
    /// </summary>
    public ModContainer AddVersion(Guid containerId, string versionString)
    {
        var name = Repo.Get(containerId)?.Name ?? "mod";
        return Repo.AddVersion(containerId, versionString, dir =>
        {
            var baseName = SanitizeBaseName(name);
            var baseDir = Path.Combine(dir, baseName);
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, baseName + ".mod"), versionString);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), versionString);
        });
    }

    /// <summary>
    /// Sanitizes a container name into a valid base folder name (illegal
    /// filename chars replaced with <c>_</c>), mirroring what a real import can
    /// place on disk. Normal names ("DMF", "ModB") are unaffected.
    /// </summary>
    private static string SanitizeBaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "mod" : sanitized;
    }

    public void Dispose()
    {
        _provider.Dispose();
        // staged/ holds staging links (junctions on Windows); a naive
        // Directory.Delete(root, recursive: true) throws UnauthorizedAccessException
        // on a directory junction on Windows, so teardown walks each tree
        // entry-by-entry and removes reparse points as LINKS (never following
        // them into the repository). Mirrors ProfileService's staged-entry delete.
        DeleteTree(BaseFolder);
        DeleteTree(ModsFolder);
        // External linked targets are outside the Curator-managed roots; clean
        // them up too so tests don't leak temp dirs. The linked staging links
        // under staged/ were already removed above (as links, never followed),
        // so these deletes never recurse into the profile/mods trees.
        foreach (var target in _externalTargets)
        {
            DeleteTree(target);
            var parent = Path.GetDirectoryName(target);
            if (parent is not null && Directory.Exists(parent))
            {
                try { Directory.Delete(parent); } catch { /* sibling may hold other test's dirs */ }
            }
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            DeleteEntry(entry);
        }

        Directory.Delete(root); // empty (links + children removed above)
    }

    private static void DeleteEntry(string entry)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(entry);
        }
        catch (FileNotFoundException) { return; } // raced away
        catch (DirectoryNotFoundException) { return; }

        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Junction/symlink: remove the link only, never follow into its target.
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        else if ((attrs & FileAttributes.Directory) != 0)
        {
            DeleteTree(entry);
        }
        else
        {
            File.Delete(entry);
        }
    }
}
