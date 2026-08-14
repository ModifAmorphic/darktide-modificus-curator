using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Proves <c>AddSteam()</c> registers <see cref="ISteamService"/> (and its
/// supporting services) so the whole thing is resolvable from DI with the
/// production defaults -- and that pre-registered overrides (the fixture's fakes)
/// win over the defaults via TryAdd.
/// </summary>
public sealed class SteamServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSteam_registers_resolvable_ISteamService_with_defaults()
    {
        // SteamService depends on IConfigLoader (provided by AddGeneral in
        // production; the live Discovery overrides are read through it). The
        // fake satisfies the dependency without touching the real config file.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSteam();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetService<ISteamService>();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<ISteamService>(service);
    }

    [Fact]
    public void AddSteam_resolves_supporting_services()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSteam();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<SteamDiscoveryOptions>());
        Assert.NotNull(provider.GetService<IProcessLookup>());

        // ISteamRegistryReader is a Windows-only capability: registered ONLY on
        // Windows. On Linux it is intentionally absent so resolving it fails fast
        // (the honest outcome for a Windows-only registry reader) -- the Windows
        // discoverer path is exercised on Linux CI via the fixture's FakeRegistryReader.
        if (OperatingSystem.IsWindows())
            Assert.NotNull(provider.GetService<ISteamRegistryReader>());
        else
            Assert.Null(provider.GetService<ISteamRegistryReader>());
    }

    [Fact]
    public void AddSteam_pre_registered_options_win_over_defaults()
    {
        // A host/tests can override the discovery inputs; TryAdd must defer.
        var custom = new SteamDiscoveryOptions { Platform = DiscoveryPlatform.Windows };

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(custom);
        services.AddSteam();
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<SteamDiscoveryOptions>();
        Assert.Same(custom, resolved);
    }

    [Fact]
    public void AddSteam_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddSteam();

        Assert.Same(services, returned);
    }

    [Fact]
    public void CreateDefault_populates_paths_for_current_os()
    {
        var opts = SteamDiscoveryOptions.CreateDefault();

        // Platform reflects the runtime; both OSes get their default paths wired.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(DiscoveryPlatform.Linux, opts.Platform);
            Assert.NotNull(opts.LinuxDefaultSteamRoot);
            Assert.NotNull(opts.LinuxFlatpakSteamRoot);
            Assert.NotNull(opts.LinuxCompatibilityToolsDir);
            Assert.NotEmpty(opts.LinuxSystemCompatibilityToolsDirs);
        }
        else
        {
            Assert.Equal(DiscoveryPlatform.Windows, opts.Platform);
            Assert.NotNull(opts.WindowsDefaultSteamRoot);
        }

        // Darktide identity is the real one regardless of OS.
        Assert.Equal(1361210, opts.DarktideAppId);
        Assert.Equal("Warhammer 40,000 DARKTIDE", opts.DarktideCommonDir);
        Assert.Equal("Darktide.exe", opts.GameBinaryName);
        Assert.Equal("Darktide", opts.GameProcessName);
    }
}
