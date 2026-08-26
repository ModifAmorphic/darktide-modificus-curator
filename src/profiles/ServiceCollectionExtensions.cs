using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modificus.Curator.Profiles;

/// <summary>DI registration for the Profiles library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProfileService"/> → <see cref="ProfileService"/>,
    /// plus the staging dependencies it needs: the <see cref="StagingLinkCreator"/>
    /// default (platform-selective: an NTFS junction on Windows, a symlink via
    /// <see cref="System.IO.Directory.CreateSymbolicLink"/> on Linux) and,
    /// defensively, <see cref="IModRepository"/> via <c>AddMods()</c>
    /// so a lone <c>AddProfiles()</c> yields a resolvable
    /// <see cref="IProfileService"/> (idempotent; the composition root also
    /// calls <c>AddMods()</c> for discoverability).
    /// </summary>
    /// <remarks>
    /// Resolves <c>IConfigLoader</c> (the live config reader) + <c>ILogger&lt;&gt;</c>
    /// from the container (both provided by <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </remarks>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        // Bring in the mod repository the staging seam depends on. Idempotent,
        // safe to call again from the composition root / other libraries.
        services.AddMods();

        // Default staging-link impl, platform-selective: a privilege-free NTFS
        // junction on Windows (no Developer Mode / admin required) and the BCL
        // symlink on Linux. TryAdd so a caller may pre-register an override
        // (e.g. tests inject a throwing delegate to exercise the staging-link
        // IOException path without platform hacks).
        services.TryAddSingleton<StagingLinkCreator>(_ => CreateStagingLink);

        services.AddSingleton<IProfileService, ProfileService>();

        // The load-order reconciliation glue over the pure planner: resolves
        // both sides' base names from the live profile + repository. Stateless
        // per call; singleton.
        services.AddSingleton<ILoadOrderReconciler, LoadOrderReconciler>();
        return services;
    }

    /// <summary>
    /// Selects the staging-link primitive for the current OS: an NTFS junction on
    /// Windows (<see cref="Junction.Create"/>) or a symlink on Linux
    /// (<see cref="Directory.CreateSymbolicLink"/>). The junction call is behind
    /// an <see cref="OperatingSystem.IsWindows"/> guard so the Windows-only native
    /// interop is never reached on Linux.
    /// </summary>
    private static void CreateStagingLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Junction.Create(linkPath, targetPath);
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }
}
