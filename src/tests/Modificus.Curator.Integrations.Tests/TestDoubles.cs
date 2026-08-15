using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// In-memory <see cref="IExternalLauncher"/>: records opened URIs + paths and
/// returns a configurable outcome (success by default). Never touches the OS
/// shell, so tests can exercise browser-open paths safely.
/// </summary>
internal sealed class FakeExternalLauncher : IExternalLauncher
{
    private readonly List<Uri> _openedUris = new();

    /// <summary>Decides the OpenUri outcome; defaults to success.</summary>
    public Func<Uri, bool> OpenUriResult { get; set; } = _ => true;

    /// <summary>Every URI this launcher was asked to open, in order.</summary>
    public IReadOnlyList<Uri> OpenedUris => _openedUris;

    /// <inheritdoc />
    public bool OpenUri(Uri uri)
    {
        _openedUris.Add(uri);
        return OpenUriResult(uri);
    }

    /// <inheritdoc />
    public bool OpenPath(string path) => true;
}

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Load"/> returns
/// a configurable config; <see cref="Save"/> captures the last-written config.
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
/// In-memory <see cref="IAppStateStore"/> for the update-check + update-state
/// tests. Only <see cref="KnownUpdates"/> is exercised by the update path; the
/// other three members are no-op stubs kept to satisfy the interface.
/// </summary>
internal sealed class FakeAppStateStore : IAppStateStore
{
    public Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdatesData { get; set; }
    public int KnownUpdatesSetCount { get; private set; }

    public bool OnboardingCompleted { get; set; }
    public Guid? ActiveProfileId { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
    public DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }
    public AppWindowState? MainWindowState { get; set; }

    IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? IAppStateStore.KnownUpdates
    {
        get => KnownUpdatesData;
        set
        {
            KnownUpdatesData = value is null
                ? null
                : new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>(value);
            KnownUpdatesSetCount++;
        }
    }
}
