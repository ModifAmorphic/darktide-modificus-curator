namespace Modificus.Curator.Config;

/// <summary>
/// Platform-appropriate default locations for Modificus Curator data, resolved
/// once from the OS local-application-data folder
/// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/.local/share</c> on Linux).
/// Shared by <see cref="CuratorConfig"/> and <see cref="LoggingConfig"/> so every
/// field has a default and the JSON binder only overwrites what the file sets.
/// </summary>
/// <remarks>
/// On Windows the data root nests under an org/app hierarchy
/// (<c>ModifAmorphic\Modificus Curator</c>) to keep Curator's user data distinct
/// from the Velopack install root (<c>%LOCALAPPDATA%\ModifAmorphic.ModificusCurator</c>).
/// Linux keeps the flat <c>Modificus Curator</c> segment under
/// <c>~/.local/share</c>, unchanged.
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// The app-data segment under the OS local-application-data folder:
    /// <c>ModifAmorphic\Modificus Curator</c> on Windows (org/app hierarchy,
    /// distinct from the Velopack install root), <c>Modificus Curator</c> on
    /// Linux.
    /// </summary>
    private static readonly string AppDataSegment = OperatingSystem.IsWindows()
        ? Path.Combine("ModifAmorphic", "Modificus Curator")
        : "Modificus Curator";

    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataSegment);

    public static readonly string DefaultLogFile = Path.Combine(AppDataDir, "logs", "curator-{DateTime}.log");
    public static readonly string DefaultProfilesBaseFolder = Path.Combine(AppDataDir, "profiles");
    public static readonly string DefaultModsFolder = Path.Combine(AppDataDir, "mods");
    public static readonly string DefaultRelayDir = Path.Combine(AppDataDir, "relay");
}
