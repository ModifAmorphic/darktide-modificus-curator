using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Modificus.Curator.Mods;

/// <summary>
/// Filesystem-backed <see cref="IModImportService"/>. Resolves (or creates)
/// the container for the source, then extracts an archive / copies a folder
/// into the repository-provided opaque version folder, via
/// <see cref="IModRepository.AddVersion"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Container resolution:</b> Nexus by <see cref="NexusSource.ModId"/>,
/// Untracked by <see cref="ModContainer.Name"/> (the import <c>modName</c>).
/// A re-import of the same identity resolves to the existing container instead
/// of creating a new one, so the container is the dedup unit across imports.</para>
/// <para>
/// <b>Version resolution:</b> re-importing the same version dedups:
/// <see cref="IModRepository.AddVersion"/> reuses the existing version
/// folder + refreshes its files; a new version creates a new opaque version
/// folder. <see cref="ModVersion.IsLatest"/> follows the repository's
/// arrival rule (see <see cref="IModRepository.AddVersion"/>), not
/// import recency.</para>
/// <para>
/// <b>Archive support:</b> files are detected by content
/// (<see cref="ArchiveFactory.IsArchive(string, out ArchiveType?)"/>), not by
/// extension, so Curator handles whatever archive format a mod ships as (zip, 7z,
/// rar, and the others SharpCompress supports) without per-format wiring. The
/// folder path (a picked/extracted directory) is unchanged.</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds
/// (import the repository copy, then reference it from the profile).</para>
/// <para>
/// Registered as a singleton (no per-request state). The single-UI-thread
/// assumption holds (matching <c>ProfileService</c>); the underlying
/// <see cref="ModRepository"/> synchronizes its own access via an internal
/// lock, so a background-thread repository mutation (e.g. a reconciliation
/// write from <c>UpdateCheckService</c>) cannot race an import. The mods root
/// folder is read live from <see cref="IConfigLoader"/> on each import, so a
/// runtime folder change via the Settings window routes the next
/// import to the new path.</para>
/// </remarks>
internal sealed class ModImportService : IModImportService
{
    private readonly IModRepository _repo;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<ModImportService> _logger;

    public ModImportService(
        IModRepository repo,
        IConfigLoader configLoader,
        ILogger<ModImportService> logger)
    {
        _repo = repo;
        _configLoader = configLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public (Guid ContainerId, string VersionId) Import(
        string sourcePath,
        string modName,
        ModSource source,
        string version,
        DateTimeOffset? remoteUploadedAt = null,
        int? remoteFileId = null,
        ModDisplayMetadata? displayMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new ArgumentException("Mod name must not be null or whitespace.", nameof(modName));
        }
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(version);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Import source not found: '{sourcePath}'. Provide a folder or an archive.", sourcePath);
        }

        // One live snapshot for the whole import. ModsFolder is non-null by
        // CuratorConfig contract (defaults to <app-data>/mods); CreateDirectory
        // is idempotent, first-import safe, and runs on the live path so a
        // runtime folder change routes this import to the new dir.
        var modsFolder = _configLoader.Load().ModsFolder;
        Directory.CreateDirectory(modsFolder);

        // Confine modName to a single direct child of the mods root before any
        // filesystem op, even though the import target is an opaque UUID folder
        // (not modName-derived). modName is user-editable (the inline import
        // card lets the user rename), so the same confinement guards against names
        // carrying path separators, ".." or an absolute path, which would
        // confuse the untracked-by-name index (the dedup key). The explicit
        // separator check is cross-platform; GetFullPath then normalizes so the
        // prefix check is a real containment test (it also rejects a bare "..").
        ValidateModName(modName, modsFolder);

        // Content-based detection: a file is an archive iff SharpCompress
        // recognizes its magic bytes. This fail-fast gate produces an actionable
        // error for an unsupported file (before any decompression library throws
        // a technical exception). A folder source skips the gate and takes the
        // folder-copy path. The detected format is irrelevant beyond "supported
        // or not": one extraction code path handles every format SharpCompress
        // reads (zip, 7z, rar, tar, ...).
        var isArchive = false;
        if (File.Exists(sourcePath))
        {
            if (!ArchiveFactory.IsArchive(sourcePath, out _))
            {
                throw new InvalidOperationException(
                    $"Curator couldn't read '{Path.GetFileName(sourcePath)}' as a supported archive. " +
                    "You can extract the file yourself, then import the extracted folder here.");
            }
            isArchive = true;
        }

        // Validate the source structure BEFORE resolving the container or
        // creating a version: an invalid source throws immediately, placing no
        // files and creating no container/version. The source must contain
        // exactly one base directory with a <base>.mod descriptor inside it (the
        // descriptor filename matches the base folder name). This is the
        // structure the mod loader needs: mods bake their folder name into their
        // code (e.g. Mods.file.dofile("dmf/scripts/...")), so the staged symlink
        // MUST carry the base name for the mod's hardcoded paths to resolve.
        // Validating at import time guarantees the staging invariant (exactly
        // one base subdir per version folder) without storing the name anywhere
        // (staging re-derives it from the on-disk structure). Both import kinds
        // fail fast; neither places files on a validation failure.
        var baseName = isArchive
            ? ValidateArchiveStructure(sourcePath)
            : ValidateFolderStructure(sourcePath);

        // Resolve or create the container (the dedup unit). Untracked dedups by
        // the modName; Nexus dedups by source identity. Runs only after the
        // source validated, so an invalid source creates no container.
        var container = ResolveContainer(source, modName);

        // The populate callback: extract archive or copy folder into the empty
        // temp path the repo provides. AddVersion stages into a sibling temp of
        // the version folder and atomically swaps it into place on success
        // (replacing the prior contents on a dedup); on a thrown exception the
        // temp is deleted and the existing version folder (if any) is left
        // untouched, so a failed re-import is non-destructive. Both import kinds
        // preserve the base folder under <versionDir>/<base>/:
        //   - archive: extraction reproduces the archive's single top-level
        //     directory (validated above), yielding <versionDir>/<base>/<files>.
        //   - folder: the picked folder IS the base, so it is copied itself
        //     (not its contents) into <versionDir>/<base>/.
        // The result is a consistent <versionDir>/<base>/ shape across both
        // import kinds, which is what staging's base-folder discovery relies on.
        void Populate(string versionDir)
        {
            if (isArchive)
            {
                _logger.LogInformation(
                    "Importing {Mod} (base '{Base}') from archive '{Source}' -> '{Target}'",
                    modName, baseName, sourcePath, versionDir);
                ExtractArchive(sourcePath, versionDir);
            }
            else
            {
                var target = Path.Combine(versionDir, baseName);
                _logger.LogInformation(
                    "Importing {Mod} (base '{Base}') from folder '{Source}' -> '{Target}'",
                    modName, baseName, sourcePath, target);
                DirectoryCopy.Copy(sourcePath, target);
            }
        }

        var updated = _repo.AddVersion(container.Id, version, Populate, remoteUploadedAt, remoteFileId, displayMetadata);

        // Resolve the just-imported version's opaque folder id so the caller can
        // pin to it. AddVersion is an upsert by VersionString: a new tag creates
        // a fresh folder + entry; a re-import of the same tag reuses the
        // existing entry (refreshed files, unchanged Folder). Either way the
        // entry with this VersionString is the one we just imported.
        var versionId = updated.Versions.First(v =>
            string.Equals(v.VersionString, version, StringComparison.Ordinal)).Folder;

        _logger.LogInformation(
            "Imported {Mod} (base '{Base}', source={Source}, version={Version}) onto container {Id} (folder {Folder})",
            modName, baseName, source, version, container.Id, versionId);
        return (container.Id, versionId);
    }

    /// <inheritdoc />
    public string GetBaseName(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Import source not found: '{sourcePath}'. Provide a folder or an archive.", sourcePath);
        }
        // Pure peek: reuse the same structure validation as Import (no duplicated
        // logic, no container/version created, no files placed). The add flow
        // calls this before Import to pre-check a base-name collision; Import
        // re-validates (cheap, and must stay self-validating for direct callers).
        //
        // Same content-based detection as Import: a file must be a supported
        // archive (fail fast with the same actionable error), a folder takes the
        // folder path.
        var isArchive = false;
        if (File.Exists(sourcePath))
        {
            if (!ArchiveFactory.IsArchive(sourcePath, out _))
            {
                throw new InvalidOperationException(
                    $"Curator couldn't read '{Path.GetFileName(sourcePath)}' as a supported archive. " +
                    "You can extract the file yourself, then import the extracted folder here.");
            }
            isArchive = true;
        }
        return isArchive ? ValidateArchiveStructure(sourcePath) : ValidateFolderStructure(sourcePath);
    }

    /// <inheritdoc />
    public ModContainer? FindExistingContainer(ModSource source, string modName)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Mirrors ResolveContainer minus the create: the dedup lookup only. The
        // add flow uses this to exclude a re-add (same container) from the
        // collision check. The dedup rules live here, not at the call site.
        // Untracked dedups by name; Nexus + Linked by source identity (the
        // modName arg is irrelevant for the latter two).
        return source is UntrackedSource
            ? _repo.FindUntrackedByName(modName)
            : _repo.FindBySource(source);
    }

    /// <inheritdoc />
    public Guid LinkFolder(string externalPath)
    {
        ArgumentNullException.ThrowIfNull(externalPath);

        if (!Path.IsPathRooted(externalPath))
        {
            throw new ArgumentException(
                $"External path must be absolute (received '{externalPath}').", nameof(externalPath));
        }

        if (!Directory.Exists(externalPath))
        {
            // Directory.Exists returns false for a file too, so this covers both
            // "missing" and "not a directory" with one clear message.
            throw new InvalidOperationException(
                $"Cannot link '{externalPath}': the folder does not exist or is not a directory.");
        }

        // Normalize before any comparison so containment + identity checks are
        // on a canonical form. GetFullPath also rejects illegal path forms.
        string normalized;
        try
        {
            normalized = Path.GetFullPath(externalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
        {
            throw new ArgumentException(
                $"External path '{externalPath}' could not be normalized: {ex.Message}", nameof(externalPath), ex);
        }

        // Validate the mod-folder shape (the picked folder IS the base; it must
        // directly contain <base>.mod). Reuses the same validator as the folder
        // import so the two cannot drift. An unreadable folder surfaces here as
        // UnauthorizedAccessException from the validator's enumeration; remap it
        // to a clear InvalidOperationException so all validation failures share
        // one exception type per the contract.
        string baseName;
        try
        {
            baseName = ValidateFolderStructure(normalized);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Cannot link '{normalized}': the folder is not readable.", ex);
        }

        // Containment: reject any overlap with a Curator-managed root in either
        // direction, so Curator's own operations cannot recurse into the target
        // and staging cannot create a cycle. The profiles-base check covers
        // every profile root + every staged/ dir (all under the base).
        var config = _configLoader.Load();
        AssertNoCuratorRootOverlap(normalized, config.ModsFolder, config.ProfilesBaseFolder);

        // Refresh: an existing linked container for this exact external path is
        // returned as-is. The external folder is the user's; a refresh re-
        // validated the shape above but copies, deletes, and rewrites nothing.
        var source = new LinkedSource { ExternalPath = normalized };
        var existing = _repo.FindBySource(source);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Re-linked external folder '{Path}' (container {Id} already exists; no changes made).",
                normalized, existing.Id);
            return existing.Id;
        }

        var created = _repo.CreateContainer(source, baseName);
        _logger.LogInformation(
            "Linked external folder '{Path}' as container {Id} ('{Name}').",
            normalized, created.Id, baseName);
        return created.Id;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Platform-appropriate string comparison for paths (case-insensitive on
    /// Windows where drive letters are case-insensitive, ordinal on Linux).
    /// Mirrors <c>ModRepository.SamePath</c> so the two path comparisons cannot
    /// drift.
    /// </summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether <paramref name="candidate"/> is the same path as
    /// <paramref name="root"/> or lives underneath it, after full normalization.
    /// </summary>
    private static bool IsSameOrChild(string candidate, string root)
    {
        var candidateFull = Path.GetFullPath(candidate);
        var rootFull = Path.GetFullPath(root);
        return string.Equals(candidateFull, rootFull, PathComparison)
            || candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// Rejects a link target that overlaps the mods root or the profiles root
    /// in either direction. Linking a Curator-managed root (or a folder that
    /// contains one) would let Curator's own operations (prune, staging)
    /// recurse into the target, and staging would create a cycle
    /// (<c>staged/mods/&lt;baseName&gt;</c> pointing at an ancestor of
    /// <c>staged/</c>). The profiles-root check covers every profile root and
    /// every <c>staged/</c> dir, since all live under the base.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <paramref name="target"/>
    /// overlaps <paramref name="modsRoot"/> or <paramref name="profilesBase"/>
    /// in either direction.</exception>
    private static void AssertNoCuratorRootOverlap(string target, string modsRoot, string profilesBase)
    {
        if (IsSameOrChild(target, modsRoot) || IsSameOrChild(modsRoot, target))
        {
            throw new InvalidOperationException(
                $"Cannot link '{target}': it overlaps the mods repository root '{modsRoot}'. " +
                "Link a folder outside Curator's managed directories.");
        }
        if (IsSameOrChild(target, profilesBase) || IsSameOrChild(profilesBase, target))
        {
            throw new InvalidOperationException(
                $"Cannot link '{target}': it overlaps the profiles root '{profilesBase}'. " +
                "Link a folder outside Curator's managed directories.");
        }
    }

    private ModContainer ResolveContainer(ModSource source, string modName)
    {
        // Untracked dedups by name; Nexus by source identity. Create the
        // container if absent.
        if (source is UntrackedSource)
        {
            return _repo.FindUntrackedByName(modName) ?? _repo.CreateContainer(source, modName);
        }
        return _repo.FindBySource(source) ?? _repo.CreateContainer(source, modName);
    }

    private static void ValidateModName(string modName, string modsFolder)
    {
        var rootFull = Path.GetFullPath(modsFolder);
        var targetFull = Path.GetFullPath(Path.Combine(rootFull, modName));
        if (modName.IndexOf('/') >= 0
            || modName.IndexOf('\\') >= 0
            || !targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Mod name must not contain path separators, '..', or be an absolute path.", nameof(modName));
        }
    }

    // ---- archive extraction ------------------------------------------------
    //
    // Traversal-safe per-entry extraction, grounded in the CVE-2026-44788 /
    // GHSA-6c8g-7p36-r338 advisory. The vulnerable code was the convenience
    // archive.WriteToDirectory()'s directory-entry branch; the per-entry
    // WriteEntryToDirectory path (used here) applies the correct containment
    // guard. Three defense-in-depth measures beyond pinning SharpCompress >=
    // 0.48.0:
    //   1. Per-entry extraction (never the convenience extractor).
    //   2. Directory entries skipped explicitly (the vulnerable branch).
    //   3. AssertSafePath: our own containment check per entry before writing.
    // No SymbolicLinkHandler is supplied (the TAR-only symlink escalation
    // requires a caller-supplied handler; Darktide mods do not use symlinks).

    /// <summary>
    /// Extracts every file entry from the archive at <paramref name="sourcePath"/>
    /// into <paramref name="versionDir"/>, iterating <see cref="IArchive.Entries"/>
    /// (random-access, supported by zip + 7z + rar) and calling the per-entry
    /// <c>WriteToDirectory</c> on each. Directory entries are skipped; directories
    /// are created implicitly by the file-entry writer.
    /// </summary>
    /// <remarks>
    /// The per-entry path (not the convenience <c>archive.WriteToDirectory()</c>)
    /// is the CVE-advisory-blessed route: it applies
    /// <c>EnsurePathInDestinationDirectory</c> on every file write. Skipping
    /// directory entries explicitly means the directory-entry code path is never
    /// reached, and <see cref="AssertSafePath"/> adds our own containment check
    /// per entry as defense-in-depth.
    /// </remarks>
    /// <exception cref="InvalidDataException">Thrown (with the original exception
    /// as <see cref="Exception.InnerException"/>) when SharpCompress raises a
    /// corrupt-archive/CRC error mid-extraction. The caller (Import) propagates
    /// it; the UI surfaces <see cref="Exception.Message"/>.</exception>
    private static void ExtractArchive(string sourcePath, string versionDir)
    {
        using var archive = ArchiveFactory.OpenArchive(sourcePath, ReaderOptions.ForFilePath);
        // Ensure the destination exists. AddVersion's contract guarantees this,
        // but extractors conventionally ensure their dest (ZipFile.ExtractToDirectory
        // did it implicitly; SharpCompress's WriteEntryToDirectory requires it),
        // so this is defense-in-depth against any caller-side regression.
        Directory.CreateDirectory(versionDir);
        // ExtractFullPath (recreate the entry's relative subdirs) + Overwrite
        // (idempotent re-import) are the load-bearing options. PreserveFileTime
        // is left at its default (true in SharpCompress: extracted files inherit
        // the archive's mod times, matching the prior ZipFile behavior).
        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
        };
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }
            AssertSafePath(versionDir, entry.Key);
            // AssertSafePath already ran on this entry + would have thrown
            // InvalidOperationException for any traversal attempt. Anything
            // reaching this catch is a genuine extraction failure (CRC, corrupt
            // data, I/O), not a traversal refusal. If the library's own
            // EnsurePathInDestinationDirectory guard ever fired here it would
            // indicate an AssertSafePath regression worth investigating; the
            // safety property holds either way (nothing escapes the root).
            try
            {
                entry.WriteToDirectory(versionDir, options);
            }
            catch (Exception ex) when (IsCorruptArchiveException(ex))
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(sourcePath)}' could not be extracted. " +
                    "It may be corrupted or incomplete. Try downloading it again.", ex);
            }
        }
    }

    /// <summary>
    /// Containment check (defense-in-depth, mirrors the library's own guard for
    /// file entries). Normalizes the entry key's separators to '/' before
    /// combining with the root, then verifies the resolved path stays inside the
    /// root. Throws <see cref="InvalidOperationException"/> on escape. A null or
    /// empty key is a no-op (nothing to combine, so nothing to escape).
    /// </summary>
    /// <remarks>
    /// Separator normalization (replace '\' with '/') is defensive across
    /// formats: zip uses '/' per spec, other formats may use '\'. On Linux,
    /// an un-normalized backslash is a filename character rather than a
    /// separator, which would let a '..\escape' entry slip past the prefix check.
    /// </remarks>
    private static void AssertSafePath(string root, string? entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return;
        }
        var normalized = entryKey.Replace('\\', '/');
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Archive entry '{entryKey}' escapes the extraction directory.");
        }
    }

    /// <summary>
    /// Whether an exception represents a corrupt/truncated/CRC-failed archive
    /// (rather than a control-flow or argument error). Used to rewrap SharpCompress
    /// + I/O failures as <see cref="InvalidDataException"/> at the import boundary.
    /// </summary>
    private static bool IsCorruptArchiveException(Exception ex) =>
        ex is SharpCompressException or IOException or EndOfStreamException;

    // ---- source-structure validation ---------------------------------------
    //
    // Both import kinds validate the source BEFORE any file is placed (and
    // before the container/version is created). The required shape is exactly
    // one base directory containing a <base>.mod descriptor whose filename
    // matches the base folder name. See PrepareModRoot for why the base name is
    // load-bearing (mods bake their folder name into their code).

    /// <summary>
    /// Validates an archive's structure <em>before</em> extraction: exactly one
    /// top-level directory, no loose top-level files, and a
    /// <c>&lt;base&gt;/&lt;base&gt;.mod</c> descriptor inside the base directory (the
    /// descriptor filename matches the base folder name). Performs no extraction.
    /// Format-agnostic: enumerates <see cref="IArchive.Entries"/> via
    /// <see cref="ArchiveFactory.OpenArchive(string, ReaderOptions)"/>, so the
    /// same invariant applies to every archive format SharpCompress reads.
    /// </summary>
    /// <param name="archivePath">The archive to inspect.</param>
    /// <returns>The validated base folder name (the single top-level
    /// directory).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the archive has a
    /// loose top-level file, zero or multiple top-level directories, or no
    /// matching <c>&lt;base&gt;/&lt;base&gt;.mod</c> descriptor.</exception>
    /// <exception cref="InvalidDataException">Thrown when the archive is corrupt
    /// or truncated and cannot be opened/enumerated.</exception>
    /// <remarks>
    /// Nexus mod archives ship the mod folder at the archive root (e.g.
    /// <c>dmf.zip</c> contains <c>dmf/dmf.mod</c>, <c>dmf/scripts/...</c>). The
    /// descriptor filename convention (<c>&lt;base&gt;.mod</c>) is what the mod
    /// loader resolves, so the base name is load-bearing. Inspecting entries
    /// before extracting (rather than catching a post-extraction mismatch) means
    /// an invalid archive leaves nothing on disk.
    /// </remarks>
    private static string ValidateArchiveStructure(string archivePath)
    {
        // Step 1: open + collect entry keys. A corrupt or truncated archive
        // fails here (SharpCompressException / IOException / EndOfStream) and is
        // rewrapped as InvalidDataException; the structure checks in step 2 run
        // on the successfully-collected keys and throw InvalidOperationException
        // for shape failures, so the two failure modes are cleanly separated.
        List<string> entryKeys;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            // File-entry keys only: directory entries are skipped because they
            // are not loose files and the base folder is defined by the file
            // paths. (RAR directory entries carry keys without trailing slashes,
            // e.g. 'rarfixture', so including them would falsely trip the
            // loose-top-level-file check.) Null/empty keys (malformed entries)
            // are filtered out too.
            entryKeys = archive.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => e.Key)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .ToList();
        }
        catch (Exception ex) when (IsCorruptArchiveException(ex))
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(archivePath)}' could not be read. " +
                "It may be corrupted or incomplete. Try downloading it again.", ex);
        }

        // Step 2: structural checks on the collected keys (no SharpCompress
        // call can throw here; the entries are already materialized).
        var topLevelDirs = new HashSet<string>(StringComparer.Ordinal);
        string? looseFile = null;
        foreach (var fullName in entryKeys)
        {
            // Zip entries use '/' per spec; other formats may use '\'. Normalize
            // defensively so a backslash-separated top-level dir isn't mistaken
            // for a single-segment loose file.
            var normalized = fullName.Replace('\\', '/');
            var slash = normalized.IndexOf('/');
            if (slash < 0)
            {
                looseFile ??= normalized; // a top-level file with no parent dir
                continue;
            }
            topLevelDirs.Add(normalized.Substring(0, slash));
        }

        if (looseFile is not null)
        {
            throw new InvalidOperationException(
                $"Invalid mod archive '{archivePath}': found a loose top-level file '{looseFile}'. " +
                "The archive must contain a single mod folder with its '<base>.mod' descriptor inside " +
                "(for example, 'dmf/dmf.mod'). Repackage the mod so its base folder is at the archive root.");
        }

        if (topLevelDirs.Count == 0)
        {
            // An archive whose entries are all loose files (no directory at all)
            // is distinct from the multi-directory case; surfaced with its own
            // message so the cause is clear.
            throw new InvalidOperationException(
                $"Invalid mod archive '{archivePath}': the archive has no mod folder. " +
                "It must contain a single mod folder with its '<base>.mod' descriptor inside.");
        }

        if (topLevelDirs.Count != 1)
        {
            var list = string.Join(", ", topLevelDirs.OrderBy(n => n, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Invalid mod archive '{archivePath}': expected exactly one top-level mod folder, found " +
                $"{topLevelDirs.Count} ({list}). The archive must contain a single mod folder with its " +
                "'<base>.mod' descriptor inside.");
        }

        var baseName = topLevelDirs.Single();
        var descriptor = baseName + "/" + baseName + ".mod";
        var hasDescriptor = entryKeys.Any(k =>
            k.Replace('\\', '/').Equals(descriptor, StringComparison.Ordinal));
        if (!hasDescriptor)
        {
            throw new InvalidOperationException(
                $"Invalid mod archive '{archivePath}': no '{descriptor}' descriptor found inside the mod folder. " +
                "The descriptor filename must match the mod folder name (for example, 'dmf/dmf.mod').");
        }

        return baseName;
    }

    /// <summary>
    /// Validates a folder's structure: non-empty, and containing a
    /// <c>&lt;folderName&gt;.mod</c> descriptor whose filename matches the folder
    /// name. The picked folder IS the mod's base folder (single-base is inherent
    /// to picking one folder; the <c>.mod</c> check also rejects a pick that is
    /// actually a <em>parent</em> of several mods). Performs no copy.
    /// </summary>
    /// <remarks>
    /// Internal so it is the single folder-shape validator shared by the folder
    /// <see cref="Import"/> path, <see cref="GetBaseName"/>, and
    /// <see cref="LinkFolder"/> (each passes the picked folder directly). The
    /// archive path (<see cref="ValidateArchiveStructure"/>) is separate.
    /// </remarks>
    /// <param name="sourceDir">The picked folder.</param>
    /// <returns>The validated base folder name (the picked folder's name).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the base name
    /// can't be derived, the folder is empty, or no matching
    /// <c>&lt;folderName&gt;.mod</c> descriptor exists.</exception>
    internal static string ValidateFolderStructure(string sourceDir)
    {
        var baseName = GetFolderBaseName(sourceDir);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': could not derive a base folder name. " +
                "Pick a folder whose name is the mod's base name.");
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': the folder '{baseName}' is empty. " +
                "It must contain the mod's files, including a '<base>.mod' descriptor.");
        }

        var descriptor = Path.Combine(sourceDir, baseName + ".mod");
        if (!File.Exists(descriptor))
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': no '{baseName}.mod' descriptor found inside the folder. " +
                "The descriptor filename must match the folder name (the picked folder must BE the mod, " +
                "not a parent containing several mods).");
        }

        return baseName;
    }

    /// <summary>
    /// Derives the base folder name from a folder path: the final path segment
    /// after trimming trailing directory separators. Used to name the copied
    /// folder under the version directory.
    /// </summary>
    private static string GetFolderBaseName(string sourceDir)
    {
        var trimmed = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }
}
