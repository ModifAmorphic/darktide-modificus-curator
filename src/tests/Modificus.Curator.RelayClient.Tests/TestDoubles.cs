using System.Collections.Immutable;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.Steam;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Load"/> returns
/// a configurable mutable config (the same instance each call, so a test may
/// mutate it between launches and the next <see cref="IConfigLoader.Load"/>
/// sees the new value). <see cref="Save"/> captures the last-written config.
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load()
    {
        LoadCalls++;
        return Config;
    }

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
    }
}

/// <summary>
/// Hand-rolled test double for <see cref="IProfileService"/>. Only
/// <see cref="PrepareModRoot"/> is exercised by the launch path; the rest of the
/// surface throws <see cref="NotSupportedException"/> to catch accidental misuse.
/// </summary>
internal sealed class FakeProfileService : IProfileService
{
    /// <summary>Unused stub. Only <see cref="PrepareModRoot"/> is exercised by
    /// the launch path; the event is required by the interface but never raised
    /// here.</summary>
    public event EventHandler<ProfileSummary>? ProfileCreated
    {
        add { }
        remove { }
    }

    /// <summary>The path returned by <see cref="PrepareModRoot"/> (the --mod-path).</summary>
    public string PrepareModRootResult { get; set; } = "/home/u/.local/share/Modificus Curator/profiles/<id>/staged";

    /// <summary>When set, <see cref="PrepareModRoot"/> throws KeyNotFoundException (unknown profile).</summary>
    public bool UnknownProfile { get; set; }

    /// <summary>When set, <see cref="PrepareModRoot"/> throws
    /// <see cref="IOException"/> (a staging-link failure) to drive the
    /// <see cref="LaunchStatus.StagingFailed"/> path.</summary>
    public bool PrepareModRootThrows { get; set; }

    /// <summary>
    /// The <see cref="LaunchSettings"/> returned by
    /// <see cref="GetLaunchSettings"/> (the launch path reads it on each launch
    /// + passes it through to the strategy). Default empty (a fresh / no-settings
    /// profile).
    /// </summary>
    public LaunchSettings LaunchSettingsResult { get; set; } = new();

    /// <summary>The (profileId, settings) pairs passed to
    /// <see cref="SetLaunchSettings"/>, in call order. The launch path never
    /// calls the setter; the launch-settings UI does.</summary>
    public IReadOnlyList<(Guid Id, LaunchSettings Settings)> SetLaunchSettingsCalls { get; }
        = new List<(Guid, LaunchSettings)>();

    public Guid LastPrepareModRootId { get; private set; }
    public int PrepareModRootCalls { get; private set; }

    /// <inheritdoc />
    public string PrepareModRoot(Guid id)
    {
        PrepareModRootCalls++;
        LastPrepareModRootId = id;
        if (UnknownProfile)
        {
            throw new KeyNotFoundException($"No profile exists with id '{id}'.");
        }
        if (PrepareModRootThrows)
        {
            throw new IOException("simulated staging-link failure");
        }
        return PrepareModRootResult;
    }

    // The remainder of the surface is unused by the launch path.
    public IReadOnlyList<ProfileSummary> ListProfiles() => throw new NotSupportedException();
    public Profile GetProfile(Guid id) => throw new NotSupportedException();
    public Profile CreateProfile(string name) => throw new NotSupportedException();
    public void RenameProfile(Guid id, string newName) => throw new NotSupportedException();
    public void DeleteProfile(Guid id) => throw new NotSupportedException();
    public IReadOnlyList<ModListEntry> GetModList(Guid id) => throw new NotSupportedException();
    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder) => throw new NotSupportedException();
    public void SetModEnabled(Guid id, Guid containerId, bool enabled) => throw new NotSupportedException();
    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy) => throw new NotSupportedException();
    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy) => throw new NotSupportedException();
    public void RemoveMod(Guid id, Guid containerId) => throw new NotSupportedException();
    public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId) => throw new NotSupportedException();

    public LaunchSettings GetLaunchSettings(Guid id) => LaunchSettingsResult;

    public void SetLaunchSettings(Guid id, LaunchSettings settings) =>
        ((List<(Guid, LaunchSettings)>)SetLaunchSettingsCalls).Add((id, settings));
}

/// <summary>Hand-rolled test double for <see cref="ISteamService"/>.</summary>
internal sealed class FakeSteamService : ISteamService
{
    public DiscoveryResult Result { get; set; } = FakeDiscovery.CompleteLinux;
    public int DiscoverCalls { get; private set; }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        DiscoverCalls++;
        return Result;
    }

    /// <inheritdoc />
    public bool IsGameRunning() => false;
}

/// <summary>
/// Hand-rolled test double for <see cref="IProcessLauncher"/>. Records the last
/// invocation's immutable <see cref="ProcessLaunchRequest"/> and returns a
/// configurable boolean (default <c>true</c> = started).
/// </summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    /// <summary>The value returned by <see cref="Start"/> (default true = started).</summary>
    public bool Returns { get; set; } = true;

    public ProcessLaunchRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    // Convenience projections over the last recorded request, so tests stay
    // readable for the common assertions (filePath / args / env overrides).
    public string? FilePath => LastRequest?.FilePath;
    public IReadOnlyList<string>? Arguments => LastRequest?.Arguments;
    public IReadOnlyDictionary<string, string>? Environment => LastRequest?.EnvironmentOverrides;
    public IReadOnlySet<string> RemovedVariables => LastRequest?.EnvironmentVariablesToRemove ?? ImmutableHashSet<string>.Empty;
    public bool CreateNoWindow => LastRequest?.CreateNoWindow ?? false;

    /// <inheritdoc />
    public bool Start(ProcessLaunchRequest request)
    {
        Calls++;
        LastRequest = request;
        return Returns;
    }
}

/// <summary>
/// Realistic complete <see cref="DiscoveryResult"/> fixtures for each platform --
/// the values a real Steam discovery would yield on a healthy install. Tests
/// selectively null fields to exercise the DiscoveryIncomplete path.
/// </summary>
internal static class FakeDiscovery
{
    public const string LinuxSteam = "/home/u/.steam/steam";
    public const string LinuxGameBinary =
        "/home/u/.steam/steam/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe";
    public const string LinuxCompatdata =
        "/home/u/.steam/steam/steamapps/compatdata/1361210";
    public const string LinuxProton =
        "/home/u/.steam/steam/steamapps/common/Proton - Experimental/proton";
    public const string LinuxProtonVersion = "Proton - Experimental";

    public const string WindowsSteam = @"C:\Program Files (x86)\Steam";
    public const string WindowsGameBinary =
        @"C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe";

    public static DiscoveryResult CompleteLinux { get; } = new(
        SteamInstallPath: LinuxSteam,
        DarktideGameBinaryPath: LinuxGameBinary,
        CompatdataPath: LinuxCompatdata,
        ProtonBinaryPath: LinuxProton,
        ProtonVersion: LinuxProtonVersion,
        Status: DiscoveryStatus.Complete,
        Warnings: Array.Empty<string>());

    public static DiscoveryResult CompleteWindows { get; } = new(
        SteamInstallPath: WindowsSteam,
        DarktideGameBinaryPath: WindowsGameBinary,
        CompatdataPath: null,
        ProtonBinaryPath: null,
        ProtonVersion: null,
        Status: DiscoveryStatus.Complete,
        Warnings: Array.Empty<string>());
}
