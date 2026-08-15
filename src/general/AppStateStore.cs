using System.Text.Json;
using Modificus.Curator.Config;

namespace Modificus.Curator.General;

/// <summary>
/// The JSON-backed runtime app-state store: one implementation covering every
/// app-state role interface (<see cref="IOnboardingState"/>,
/// <see cref="IProfileActivationState"/>,
/// <see cref="IUpdateCheckScheduleState"/>, <see cref="IKnownUpdateState"/>,
/// <see cref="INexusMetadataBackfillState"/>,
/// <see cref="IMainWindowStatePersistence"/>). Loads + saves a single JSON
/// file at
/// <c>&lt;app-data&gt;/app-state.json</c> (<c>{ "OnboardingCompleted": true | false,
/// "ActiveProfileId": "&lt;guid&gt;" | null,
/// "LastUpdateCheckUtc": "&lt;iso-8601&gt;" | null,
/// "ManualRefreshTimestamps": [ "&lt;iso-8601&gt;", ... ] | null,
/// "KnownUpdates": { "&lt;profile-guid&gt;": [ { ...snapshot... }, ... ] } | null,
/// "LastNexusMetadataBackfillUtc": "&lt;iso-8601&gt;" | null,
/// "MainWindowState": { "Width": &lt;dip&gt;, "Height": &lt;dip&gt;, "IsMaximized": true | false } | null }</c>).
/// The app-data dir is derived the same way <see cref="ConfigLoader"/> derives its
/// config path (both via <see cref="AppPaths.AppDataDir"/>). JSON is handled with
/// <see cref="JsonSerializer"/> (direct, read+write) rather than
/// <c>Microsoft.Extensions.Configuration</c>. The latter is binding-oriented and
/// read-only; a tiny writable state file is the wrong fit for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cached model, written whole.</b> The store holds the deserialized
/// <see cref="StateModel"/> in memory (loaded lazily on first access) and
/// rewrites the WHOLE model on every property assignment. This keeps the
/// persisted fields independent: assigning one preserves every previously
/// written sibling (a naive per-field save would clobber the others on every
/// write). The store is a DI singleton for the app lifetime and is the sole
/// writer of the file, so an in-memory cache is the honest model.</para>
/// <para><b>First-run safe:</b> a missing or corrupt state file never throws;
/// the cache just seeds as defaults (every field <c>null</c>, except
/// <see cref="IOnboardingState.OnboardingCompleted"/> which defaults to
/// <c>false</c>). Writes are best-effort; runtime app-state is non-critical, so
/// a persistence failure (unwritable dir, full disk) is swallowed rather than
/// crashing the app mid-interaction. Any field absent from an older
/// <c>app-state.json</c> deserializes as its default (System.Text.Json default
/// for an absent nullable member), so a first run after upgrade sees no recorded
/// value and the consumers seed cleanly.</para>
/// </remarks>
public sealed class AppStateStore :
    IOnboardingState,
    IProfileActivationState,
    IUpdateCheckScheduleState,
    IKnownUpdateState,
    INexusMetadataBackfillState,
    IMainWindowStatePersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    // The lazily-loaded, sole-writer cache. Mutated under _sync (see below).
    private StateModel? _cache;
    private readonly object _sync = new();

    /// <summary>
    /// Creates a store for <paramref name="path"/>; <c>null</c> resolves to
    /// <see cref="DefaultStatePath"/>.
    /// </summary>
    public AppStateStore(string? path = null)
    {
        _path = path ?? DefaultStatePath();
    }

    /// <summary>The state file this store reads + writes.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public bool OnboardingCompleted
    {
        get => Load().OnboardingCompleted;
        set => Mutate(m => m.OnboardingCompleted = value);
    }

    /// <inheritdoc />
    public Guid? ActiveProfileId
    {
        get => Load().ActiveProfileId;
        set => Mutate(m => m.ActiveProfileId = value);
    }

    /// <inheritdoc />
    public DateTimeOffset? LastUpdateCheckUtc
    {
        get => Load().LastUpdateCheckUtc;
        set => Mutate(m => m.LastUpdateCheckUtc = value);
    }

    /// <inheritdoc />
    public IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps
    {
        get => Load().ManualRefreshTimestamps;
        set => Mutate(m => m.ManualRefreshTimestamps = value is null
            ? null
            : new List<DateTimeOffset>(value));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates
    {
        get => Load().KnownUpdates?
            .ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<KnownUpdateSnapshot>)kv.Value)
            .AsReadOnly();
        set => Mutate(m =>
        {
            if (value is null)
            {
                m.KnownUpdates = null;
                return;
            }

            // Copy into the mutable model shape (Dictionary<Guid, List<...>>).
            var copy = new Dictionary<Guid, List<KnownUpdateSnapshot>>();
            foreach (var (profileId, snapshots) in value)
            {
                copy[profileId] = snapshots is null
                    ? new List<KnownUpdateSnapshot>()
                    : new List<KnownUpdateSnapshot>(snapshots);
            }
            m.KnownUpdates = copy;
        });
    }

    /// <inheritdoc />
    public DateTimeOffset? LastNexusMetadataBackfillUtc
    {
        get => Load().LastNexusMetadataBackfillUtc;
        set => Mutate(m => m.LastNexusMetadataBackfillUtc = value);
    }

    /// <inheritdoc />
    public AppWindowState? MainWindowState
    {
        get => Load().MainWindowState;
        set => Mutate(m => m.MainWindowState = value);
    }

    /// <summary>The conventional state-file location: <c>&lt;app-data&gt;/app-state.json</c>.</summary>
    public static string DefaultStatePath() =>
        System.IO.Path.Combine(AppPaths.AppDataDir, "app-state.json");

    /// <summary>
    /// Lazily loads + caches the model. First-run safe: a missing or corrupt
    /// file seeds the cache as a fresh <see cref="StateModel"/> (all fields
    /// null) rather than throwing.
    /// </summary>
    private StateModel Load()
    {
        // Defensive lock: all current writers live on the UI thread
        // (ProfileSession's active-id setter + UpdateCheckRunner's check
        // stamp), so writes do not race in practice. The lock keeps the
        // lazy-init + the cache read/write atomic if a future background
        // writer is introduced, and the cost (an uncontended monitor enter)
        // is negligible for a state file touched a handful of times per
        // session.
        lock (_sync)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            StateModel? model = null;
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    model = JsonSerializer.Deserialize<StateModel>(json, JsonOptions);
                }
            }
            catch
            {
                // Missing/corrupt/permission-denied: treat as "no state recorded."
            }

            return _cache = model ?? new StateModel();
        }
    }

    /// <summary>
    /// Applies <paramref name="apply"/> to the cached model + persists the WHOLE
    /// cached model. Best-effort: a write failure is swallowed (the in-memory
    /// value still holds).
    /// </summary>
    private void Mutate(Action<StateModel> apply)
    {
        lock (_sync)
        {
            var model = Load();
            apply(model);
            Save(model);
        }
    }

    private void Save(StateModel model)
    {
        // Best-effort: runtime app-state is non-critical, so a persistence
        // failure must not crash the app mid-interaction.
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(model, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Swallow: the app keeps working off the in-memory cache without
            // persisted state.
        }
    }

    private sealed class StateModel
    {
        public bool OnboardingCompleted { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public DateTimeOffset? LastUpdateCheckUtc { get; set; }
        public List<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
        public Dictionary<Guid, List<KnownUpdateSnapshot>>? KnownUpdates { get; set; }
        public DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }
        public AppWindowState? MainWindowState { get; set; }
    }
}
