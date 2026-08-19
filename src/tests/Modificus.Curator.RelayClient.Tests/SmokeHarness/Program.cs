using Modificus.Curator.Config;
using Modificus.Curator.RelayClient;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient.Tests.Harness;

// =============================================================================
// Launch smoke-test harness -- USER-machine validation.
//
// The agent env has no Darktide/Windows/Proton, so the launch smoke test is
// run by the user on their box. This harness builds the REAL Curator composition
// (no fakes) and exposes IRelayLaunchService.Launch(profileId) at the CLI:
//
//   dotnet run --project src/tests/Modificus.Curator.RelayClient.Tests -- discover
//   dotnet run --project src/tests/Modificus.Curator.RelayClient.Tests -- list
//   dotnet run --project src/tests/Modificus.Curator.RelayClient.Tests -- launch <profileId>
//
// See README at the bottom of this file for the full smoke-test workflow.
// =============================================================================

/// <summary>
/// Entry point for <c>dotnet run</c>. <c>dotnet test</c> ignores this (the VSTest
/// adapter runs the xUnit suite independently). Exits 0 on a launched/healthy
/// result, non-zero on DiscoveryIncomplete/Error so a CI/script can detect failure.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "discover" => RunDiscover(),
            "list" => RunList(),
            "launch" => RunLaunch(args),
            _ => UnknownCommand(command),
        };
    }

    // ---- commands -----------------------------------------------------------

    private static int RunDiscover()
    {
        using var provider = BuildComposition();
        var steam = provider.GetRequiredService<ISteamService>();
        var config = provider.GetRequiredService<IConfigLoader>().Load();

        Console.WriteLine($"Platform:    {CurrentPlatformLabel()}");
        Console.WriteLine($"Relay dir: {config.RelayDir}");
        Console.WriteLine("Discovering Steam + Darktide + Proton...");
        var d = steam.Discover();

        Console.WriteLine();
        Console.WriteLine($"  Status:              {d.Status}");
        Console.WriteLine($"  SteamInstallPath:    {NullOrValue(d.SteamInstallPath)}");
        Console.WriteLine($"  DarktideGameBinary:  {NullOrValue(d.DarktideGameBinaryPath)}");
        Console.WriteLine($"  CompatdataPath:      {NullOrValue(d.CompatdataPath)}");
        Console.WriteLine($"  ProtonBinaryPath:    {NullOrValue(d.ProtonBinaryPath)}");
        Console.WriteLine($"  ProtonVersion:       {NullOrValue(d.ProtonVersion)}");
        if (d.Warnings.Count > 0)
        {
            Console.WriteLine("  Warnings:");
            foreach (var w in d.Warnings)
            {
                Console.WriteLine($"    - {w}");
            }
        }

        Console.WriteLine();
        var gameRunning = steam.IsGameRunning();
        Console.WriteLine($"  Darktide running?    {gameRunning}");
        Console.WriteLine();
        Console.WriteLine(d.Status == DiscoveryStatus.Complete
            ? "Discovery: OK -- ready to launch."
            : "Discovery: INCOMPLETE -- fix the missing fields above (or run the Curator UI's escape hatch).");
        return d.Status == DiscoveryStatus.Complete ? 0 : 2;
    }

    private static int RunList()
    {
        using var provider = BuildComposition();
        var profiles = provider.GetRequiredService<IProfileService>();

        Console.WriteLine("Profiles:");
        var list = profiles.ListProfiles();
        if (list.Count == 0)
        {
            Console.WriteLine("  (none -- create one via the Curator UI first)");
            return 0;
        }

        foreach (var p in list)
        {
            Console.WriteLine($"  {p.Id}  {p.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("Pass a profile id to: dotnet run -- launch <id>");
        return 0;
    }

    private static int RunLaunch(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var profileId))
        {
            Console.Error.WriteLine("Usage: dotnet run -- launch <profileId>");
            Console.Error.WriteLine("Run `dotnet run -- list` to see profile ids.");
            return 64; // EX_USAGE
        }

        using var provider = BuildComposition();
        var launch = provider.GetRequiredService<IRelayLaunchService>();

        Console.WriteLine($"Launching profile {profileId} on {CurrentPlatformLabel()}...");
        var result = launch.Launch(profileId);

        Console.WriteLine();
        Console.WriteLine($"  Status:              {result.Status}");
        if (result.Message is not null)
        {
            Console.WriteLine($"  Message:             {result.Message}");
        }
        if (result.MissingDiscoveryFields.Count > 0)
        {
            Console.WriteLine($"  MissingDiscovery:    {string.Join(", ", result.MissingDiscoveryFields)}");
        }

        Console.WriteLine();
        return result.Status switch
        {
            LaunchStatus.Launched => Ok(result),
            LaunchStatus.DiscoveryIncomplete => 2,
            LaunchStatus.Error => 1,
            _ => 1,
        };
    }

    private static int Ok(LaunchResult result)
    {
        Console.WriteLine("Launcher started -- watch the game window (and the Relay shell log).");
        return 0;
    }

    // ---- composition --------------------------------------------------------

    /// <summary>
    /// Builds the REAL composition: loads the user's config.json, wires the
    /// Serilog logger, and registers every library with its production
    /// implementation (real ProfileService, real SteamService, real
    /// ProcessLauncher). No fakes: this is the same wiring the Curator UI uses.
    /// </summary>
    private static ServiceProvider BuildComposition()
    {
        // One loader: used for the transient startup snapshot (logger) AND
        // registered as the live-read IConfigLoader singleton.
        var loader = new ConfigLoader();
        var config = loader.Load();
        var loggerFactory = LoggingBootstrap.CreateLoggerFactory(config);

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(loader);
        services.AddGeneral(loggerFactory);
        services.AddProfiles();
        services.AddSteam();
        services.AddRelayClient();
        // The game-dir mods host, mirroring the UI composition root (the link
        // primitive from AddProfiles + the receipts role from AddGeneral).
        services.AddSingleton<IGameDirModsHost>(sp => new GameDirModsHost(
            sp.GetRequiredService<StagingLinkCreator>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IRenamedModsFoldersState>(),
            sp.GetRequiredService<ILogger<GameDirModsHost>>()));
        return services.BuildServiceProvider();
    }

    // ---- small helpers ------------------------------------------------------

    private static bool IsHelp(string arg) =>
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static string NullOrValue(string? value) => value ?? "(missing)";

    private static string CurrentPlatformLabel() =>
        OperatingSystem.IsWindows() ? "Windows (native launcher)"
        : OperatingSystem.IsLinux() ? "Linux (proton run)"
        : Environment.OSVersion.Platform.ToString();

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Curator Relay launch smoke-test harness");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .../Modificus.Curator.RelayClient.Tests -- discover");
        Console.WriteLine("      Resolves Steam + Darktide + Proton + compatdata and prints the result.");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project .../Modificus.Curator.RelayClient.Tests -- list");
        Console.WriteLine("      Lists profile ids + names (use one with `launch`).");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project .../Modificus.Curator.RelayClient.Tests -- launch <profileId>");
        Console.WriteLine("      Prepares the mod root (writes mods.lst) and launches Darktide modded.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 = launched/discovery OK, 1 = launch error, 2 = discovery incomplete, 64 = bad usage.");
    }
}

// =============================================================================
// How to run the launch smoke test (user machine)
// =============================================================================
//
// Prereqs:
//   - Darktide installed via Steam (Steam closed for a clean launch).
//   - Mod Relay deployed: <RelayDir>/mod_relay.exe
//     + relay_shell.dll + mod_loader/. (RelayDir defaults to
//     <local-app-data>/Modificus Curator/relay; override in config.json.)
//   - At least one Curator profile (create it via the Curator UI, or drop a profile
//     dir under <local-app-data>/Modificus Curator/profiles/<guid>/profile.json).
//
// Steps:
//   1. cd <repo>/src
//   2. dotnet run --project tests/Modificus.Curator.RelayClient.Tests -- discover
//        -> confirm Status: Complete (fix any "(missing)" field before launching).
//   3. dotnet run --project tests/Modificus.Curator.RelayClient.Tests -- list
//        -> copy the profile id you want to launch.
//   4. dotnet run --project tests/Modificus.Curator.RelayClient.Tests -- launch <profileId>
//        -> expect: Status: Launched. Darktide should start modded.
//   5. Confirm the Relay shell log (next to the launcher) shows the shell
//        attaching + the mod loader running.
//
// Notes:
//   - Linux: discovery must also resolve CompatdataPath + ProtonBinaryPath; the
//     harness invokes `<proton> run <launcher.exe> ...` with
//     STEAM_COMPAT_DATA_PATH + STEAM_COMPAT_CLIENT_INSTALL_PATH set.
//   - The harness launches fire-and-forget (it returns once the launcher starts).
//   - This harness is a test-only convenience; the production surface is the
//     Curator UI calling the same IRelayLaunchService.Launch.
