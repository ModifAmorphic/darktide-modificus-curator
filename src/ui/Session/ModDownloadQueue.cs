using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>Why a download was requested; selects the completion strategy.</summary>
public enum DownloadPurpose
{
    /// <summary>
    /// Make the mod (or a specific version of it) part of a profile: the nxm
    /// click + the DMF prompt.
    /// </summary>
    ProfileAdd,

    /// <summary>
    /// Install a flagged update over a mod already in a profile.
    /// </summary>
    UpdateInstall,
}

/// <summary>The lifecycle phase of one queued download.</summary>
public enum DownloadPhase
{
    /// <summary>Admitted, waiting for the worker.</summary>
    Queued,

    /// <summary>The archive download is in flight (bytes flowing).</summary>
    Downloading,

    /// <summary>The bytes are in; the acquisition is extracting/importing.</summary>
    Importing,

    /// <summary>Terminal: the completion side effects ran; the item left the collection.</summary>
    Completed,

    /// <summary>Terminal: the item stays until dismissed or retried.</summary>
    Failed,

    /// <summary>Terminal: user-cancelled; the item left the collection.</summary>
    Canceled,
}

/// <summary>
/// One enqueue request. Immutable; carried on the resulting item so a retry can
/// re-issue the identical request.
/// </summary>
/// <param name="GameDomain">The Nexus game domain.</param>
/// <param name="ModId">The Nexus mod id.</param>
/// <param name="FileId">The Nexus file id (one third of the dedupe key).</param>
/// <param name="Purpose">The completion strategy to run.</param>
/// <param name="ContainerId">The repository container for the mod, when a peek
/// found one (null for brand-new mods).</param>
/// <param name="DisplayName">The row name: the peeked container's stored name,
/// or the caller's localized fallback for an unknown mod.</param>
/// <param name="TargetProfileId">The profile the completion writes to,
/// captured at enqueue.</param>
/// <param name="TargetProfileName">The target profile's name at enqueue,
/// display-only (the completion verifies existence through the profile
/// service, never this cached string).</param>
/// <param name="NxmKey">The per-file download key from the nxm URL, when the
/// request came from one (free-user download path).</param>
/// <param name="NxmExpires">The per-file download expiry from the nxm URL.</param>
/// <param name="ExpectedVersion">For <see cref="DownloadPurpose.UpdateInstall"/>
/// only: the installed version the update flag was recorded against (the
/// eligibility version rule). Required with a non-null
/// <paramref name="ContainerId"/> on that purpose.</param>
public sealed record ModDownloadRequest(
    string GameDomain,
    int ModId,
    int FileId,
    DownloadPurpose Purpose,
    Guid? ContainerId,
    string DisplayName,
    Guid TargetProfileId,
    string TargetProfileName,
    string? NxmKey = null,
    long? NxmExpires = null,
    string? ExpectedVersion = null);

/// <summary>
/// One queued download: coordinator-owned observable state the row UI projects
/// (never mutates). Identity fields forward the request; the mutable surface is
/// the download lifecycle.
/// </summary>
public sealed partial class DownloadItem : ObservableObject
{
    internal DownloadItem(ModDownloadRequest request) => Request = request;

    /// <summary>The enqueue request this item was admitted from.</summary>
    public ModDownloadRequest Request { get; }

    /// <summary>The Nexus game domain.</summary>
    public string GameDomain => Request.GameDomain;

    /// <summary>The Nexus mod id.</summary>
    public int ModId => Request.ModId;

    /// <summary>The Nexus file id (the dedupe key's file member).</summary>
    public int FileId => Request.FileId;

    /// <summary>The completion strategy to run.</summary>
    public DownloadPurpose Purpose => Request.Purpose;

    /// <summary>The captured target profile id.</summary>
    public Guid TargetProfileId => Request.TargetProfileId;

    /// <summary>The captured target profile name (display-only).</summary>
    public string TargetProfileName => Request.TargetProfileName;

    /// <summary>
    /// The authoritative cancel signal. Canceled synchronously by
    /// <see cref="IModDownloadQueue.Cancel"/> (independent of the marshaled
    /// presentation transition), so the worker observes a cancel that raced the
    /// UI-thread phase write. Never disposed: the worker reads the token after
    /// arbitrary delays, and a cancelled source holds no resources worth the
    /// disposed-access hazard.
    /// </summary>
    internal CancellationTokenSource CancelSource { get; } = new();

    /// <summary>Whether the item is in a terminal phase.</summary>
    public bool IsTerminal => Phase is DownloadPhase.Completed or DownloadPhase.Failed or DownloadPhase.Canceled;

    /// <summary>The repository container once known (peek at enqueue or resolve).</summary>
    [ObservableProperty]
    private Guid? _containerId;

    /// <summary>The row name (peek or fallback at enqueue; the resolved name once known).</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>The release tag once resolved (pre-download on the miss path, at dequeue on the hit path).</summary>
    [ObservableProperty]
    private string? _version;

    /// <summary>The lifecycle phase.</summary>
    [ObservableProperty]
    private DownloadPhase _phase;

    /// <summary>Cumulative bytes received.</summary>
    [ObservableProperty]
    private long _receivedBytes;

    /// <summary>Total bytes, when the response carried a length.</summary>
    [ObservableProperty]
    private long? _totalBytes;

    /// <summary>The failure reason (terminal Failed items only).</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Transient join feedback: incremented when a same-key enqueue joins this
    /// item, so the row can flash. No other meaning.
    /// </summary>
    [ObservableProperty]
    private int _pulse;
}

/// <summary>
/// The serial Nexus download queue: one download at a time, FIFO, deduped by
/// (game domain, mod id, file id). Owns the per-item pipeline: dequeue-time auth
/// re-check, repository hit check (an exact file-id match completes with no
/// network), the acquisition with progress, and the per-purpose completion
/// (profile registration for <see cref="DownloadPurpose.ProfileAdd"/>,
/// acknowledge + applied event for <see cref="DownloadPurpose.UpdateInstall"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> <see cref="Enqueue"/> is safe from any thread; every
/// other member expects the UI thread. All item state and collection
/// mutations are published through the injected <see cref="Action{T}"/>
/// marshal seam, so <see cref="IModDownloadQueue.Items"/>,
/// <see cref="IModDownloadQueue.ItemChanged"/>, and the item property
/// notifications are observed on the UI thread.</para>
/// <para>
/// <b>Cancellation is token-authoritative.</b> <see cref="IModDownloadQueue.Cancel"/>
/// cancels the item's token synchronously and marshals only the presentation.
/// The worker re-checks the token at dequeue (a queued cancel never starts the
/// acquisition) and passes it into the acquisition (an active cancel interrupts
/// it), so no phase-write race can resurrect a cancelled download.</para>
/// <para>
/// <b>The enqueue gates are the caller's.</b> The queue re-checks only auth at
/// dequeue (a sign-out between enqueue and dequeue fails the item inline). Game
/// domain and active-profile gating stay in the enqueue adapter, where the
/// modal-alert path lives (there is no row to host those failures on).</para>
/// </remarks>
public interface IModDownloadQueue
{
    /// <summary>
    /// Every non-removed item (queued, active, and failed), in admission order.
    /// Completed and cancelled items leave; failed items stay until dismissed
    /// or retried. Mutated only on the UI thread; raises
    /// <see cref="INotifyCollectionChanged"/> for the row UI.
    /// </summary>
    ObservableCollection<DownloadItem> Items { get; }

    /// <summary>
    /// Raised on the UI thread (through the marshal seam) whenever an item is
    /// admitted, resolves, or reaches a terminal state.
    /// </summary>
    event Action<DownloadItem>? ItemChanged;

    /// <summary>
    /// Raised on the UI thread after a successful
    /// <see cref="DownloadPurpose.UpdateInstall"/> completion (the
    /// update-applied signal; the update-family consumers reload from it).
    /// </summary>
    event EventHandler? UpdatesApplied;

    /// <summary>
    /// Admits a download request (or joins an identical in-flight one).
    /// Thread-safe.
    /// </summary>
    /// <returns>The admitted item, or the existing non-terminal item with the
    /// same (game domain, mod id, file id), pulsed, when one is live.</returns>
    /// <exception cref="ArgumentException">An
    /// <see cref="DownloadPurpose.UpdateInstall"/> request without a container
    /// id or expected version (the completion's eligibility revalidation
    /// requires both).</exception>
    DownloadItem Enqueue(ModDownloadRequest request);

    /// <summary>
    /// Cancels an item. A queued item is removed without worker involvement; an
    /// active item's acquisition token is cancelled and the item lands in
    /// <see cref="DownloadPhase.Canceled"/> with no completion side effects.
    /// No-op on a terminal item.
    /// </summary>
    void Cancel(DownloadItem item);

    /// <summary>
    /// Removes a <see cref="DownloadPhase.Failed"/> item from the collection.
    /// No-op on any other phase.
    /// </summary>
    void Dismiss(DownloadItem item);

    /// <summary>
    /// Re-issues a <see cref="DownloadPhase.Failed"/> item's original request
    /// as a fresh item (which replaces the failed row). Returns the existing
    /// item unchanged when it is not failed.
    /// </summary>
    DownloadItem Retry(DownloadItem item);
}

/// <summary>
/// Default <see cref="IModDownloadQueue"/>: an application-lifetime singleton.
/// See the interface remarks for the threading, cancellation, and gate
/// contracts.
/// </summary>
internal sealed class ModDownloadQueue : IModDownloadQueue
{
    /// <summary>
    /// How many downloads run at once. One worker loop consumes the queue, so
    /// raising this constant starts more loops over the same FIFO gate; most
    /// of the pipeline follows. The exception is cancel's active-vs-queued
    /// detection, which consults the single <c>_activeItem</c> slot: it
    /// assumes exactly one active item, so parallelism means reworking that
    /// detection (a set of active items), not just raising this knob.
    /// </summary>
    private const int MaxConcurrent = 1;

    private readonly IModAcquisitionService _acquisition;
    private readonly IModRepository _repo;
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IUpdateStateStore _updateState;
    private readonly IConfigLoader _configLoader;
    private readonly Func<IModListRefresh> _modListRefresh;
    private readonly LocalizationService _localization;
    private readonly Action<Action> _invokeOnUi;
    private readonly ILogger<ModDownloadQueue> _logger;

    private readonly object _sync = new();
    private readonly ObservableCollection<DownloadItem> _items = new();
    /// <summary>The dedupe index: exactly the items in a non-terminal phase.</summary>
    private readonly List<DownloadItem> _live = new();
    /// <summary>Items admitted to the worker, in admission (FIFO) order.</summary>
    private readonly Queue<DownloadItem> _waiting = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private Task?[]? _workers;
    private DownloadItem? _activeItem;

    public ModDownloadQueue(
        IModAcquisitionService acquisition,
        IModRepository repo,
        IProfileService profiles,
        IProfileSession session,
        IUpdateStateStore updateState,
        IConfigLoader configLoader,
        Func<IModListRefresh> modListRefresh,
        LocalizationService localization,
        Action<Action> invokeOnUi,
        ILogger<ModDownloadQueue> logger)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _modListRefresh = modListRefresh ?? throw new ArgumentNullException(nameof(modListRefresh));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ObservableCollection<DownloadItem> Items => _items;

    /// <inheritdoc />
    public event Action<DownloadItem>? ItemChanged;

    /// <inheritdoc />
    public event EventHandler? UpdatesApplied;

    /// <inheritdoc />
    public DownloadItem Enqueue(ModDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purpose == DownloadPurpose.UpdateInstall &&
            (request.ContainerId is null || string.IsNullOrWhiteSpace(request.ExpectedVersion)))
        {
            throw new ArgumentException(
                "An update-install request requires a container id and an expected version.", nameof(request));
        }

        DownloadItem? joined;
        DownloadItem? created = null;
        lock (_sync)
        {
            // Dedupe: only (domain, mod, file), case-insensitive on the domain
            // (the gate accepts any casing of the Darktide domain). A live item
            // with the same key is joined, never duplicated.
            joined = _live.FirstOrDefault(i =>
                string.Equals(i.GameDomain, request.GameDomain, StringComparison.OrdinalIgnoreCase) &&
                i.ModId == request.ModId &&
                i.FileId == request.FileId);
            if (joined is null)
            {
                created = new DownloadItem(request)
                {
                    DisplayName = request.DisplayName,
                    ContainerId = request.ContainerId,
                    Phase = DownloadPhase.Queued,
                };
                _live.Add(created);
                _waiting.Enqueue(created);
                if (_workers is null)
                {
                    _workers = new Task[MaxConcurrent];
                    for (var i = 0; i < MaxConcurrent; i++)
                    {
                        // Explicit Task.Run: the worker has no UI affinity; its
                        // awaits resume on the threadpool and every observable
                        // mutation marshals through the seam.
                        _workers[i] = Task.Run(WorkerLoopAsync);
                    }
                }
            }
        }

        if (joined is not null)
        {
            var joinedItem = joined;
            _invokeOnUi(() => joinedItem.Pulse++);
            return joinedItem;
        }

        var newItem = created!;
        // Publish the row + admission event before releasing the worker: the
        // marshal seam is FIFO (a dispatcher queue), so an item the worker
        // finishes fast (the hit path) still observes its own add + admission
        // ItemChanged ahead of its terminal transition.
        _invokeOnUi(() => _items.Add(newItem));
        OnItemChanged(newItem);
        _signal.Release();
        return newItem;
    }

    /// <inheritdoc />
    public void Cancel(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool finishHere;
        lock (_sync)
        {
            if (item.IsTerminal)
            {
                return;
            }

            // Authoritative first: the worker observes the token regardless of
            // when (or whether) the marshaled phase write lands. Closing the
            // dedupe slot here also lets a fresh click re-enqueue the same file
            // while the old attempt winds down (the worker stays serial).
            item.CancelSource.Cancel();
            _live.Remove(item);
            finishHere = !ReferenceEquals(_activeItem, item);
        }

        if (finishHere)
        {
            // Still queued (the worker never picked it up): resolve the row
            // here; the worker skips it at dequeue via the cancelled token.
            _invokeOnUi(() =>
            {
                if (item.Phase != DownloadPhase.Canceled && !item.IsTerminal)
                {
                    item.Phase = DownloadPhase.Canceled;
                }
                _items.Remove(item);
            });
            OnItemChanged(item);
        }

        // Active: the acquisition observes the token and the worker's
        // cancellation path performs the transition + removal.
    }

    /// <inheritdoc />
    public void Dismiss(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _invokeOnUi(() =>
        {
            if (item.Phase != DownloadPhase.Failed)
            {
                return;
            }
            _items.Remove(item);
        });
        OnItemChanged(item);
    }

    /// <inheritdoc />
    public DownloadItem Retry(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Phase != DownloadPhase.Failed)
        {
            return item;
        }

        _invokeOnUi(() => _items.Remove(item));
        return Enqueue(item.Request);
    }

    // ---- the worker ---------------------------------------------------------

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _signal.WaitAsync();
            DownloadItem? item;
            lock (_sync)
            {
                item = _waiting.Count > 0 ? _waiting.Dequeue() : null;
            }
            if (item is null)
            {
                continue;
            }

            try
            {
                await ProcessItemAsync(item);
            }
            catch (Exception ex)
            {
                // ProcessItemAsync owns its failure transitions; this backstop
                // only keeps the worker alive through an unexpected throw.
                _logger.LogError(ex,
                    "Download worker iteration for mod {Mod} file {File} failed unexpectedly.",
                    item.ModId, item.FileId);
                Fail(item, ex.Message);
            }
        }
    }

    private async Task ProcessItemAsync(DownloadItem item)
    {
        bool canceledAtDequeue;
        lock (_sync)
        {
            _activeItem = item;
            canceledAtDequeue = item.CancelSource.IsCancellationRequested;
        }
        try
        {
            if (canceledAtDequeue)
            {
                // Cancelled while queued; Cancel already removed the row.
                return;
            }

            // 1. Dequeue-time auth re-check (the queue's own gate; a sign-out
            //    between enqueue and dequeue fails inline).
            if (_configLoader.Load().Integrations.Nexus.AuthMethod == NexusAuthMethod.None)
            {
                _logger.LogWarning(
                    "Queued download of mod {Mod} file {File} failed: Nexus auth was cleared before it started.",
                    item.ModId, item.FileId);
                Fail(item, _localization["ModDownloadQueue_SignedOutMessage"]);
                return;
            }

            // 2. UpdateInstall eligibility revalidation before any network or
            //    profile work, the same in-gate revalidation the install path
            //    performs: a stale flag is a silent no-op, not an error row.
            if (item.Purpose == DownloadPurpose.UpdateInstall && !IsStillEligible(item))
            {
                Complete(item);
                return;
            }

            // 3. Repository hit check: the exact file id against every version
            //    entry of the mod's container. A hit completes with no network.
            var container = _repo.FindBySource(new NexusSource { ModId = item.ModId });
            var hit = container?.Versions.FirstOrDefault(v => v.FileId == item.FileId);

            Guid containerId;
            string pinId;
            bool isHead;
            if (hit is not null)
            {
                containerId = container!.Id;
                pinId = hit.Folder;
                // Head-ness reads the matched version's IsLatest flag.
                // Accepted edge: a legacy manifest (pre-FileId, or with an
                // IsLatest persisted before the effective-timestamp fix) can
                // disagree with the current latest key until that version's
                // next mutation; no migration (legacy entries self-heal by
                // attrition).
                isHead = hit.IsLatest;
                Resolve(item, containerId, hit.VersionString, container.Name);
            }
            else
            {
                var result = await AcquireAsync(item);
                if (result is null)
                {
                    // Canceled or failed; the transition already ran.
                    return;
                }
                containerId = result.ContainerId;
                pinId = result.VersionId;
                isHead = result.IsHeadFile;
            }

            // 4. Policy (both paths): head -> Latest; non-head -> pinned to
            //    the clicked version (the hit's folder, or the acquisition's).
            var policy = isHead
                ? new LatestPolicy()
                : (ModVersionPolicy)new PinnedPolicy(pinId);

            try
            {
                if (item.Purpose == DownloadPurpose.ProfileAdd)
                {
                    if (!CompleteProfileAdd(item, containerId, policy))
                    {
                        return;
                    }
                }
                else if (!CompleteUpdateInstall(item, containerId))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Completing the download of mod {Mod} file {File} failed.", item.ModId, item.FileId);
                Fail(item, ex.Message);
                return;
            }

            Complete(item);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeItem, item))
                {
                    _activeItem = null;
                }
            }
        }
    }

    /// <summary>
    /// The miss path: acquire with progress wired to the item. Returns null
    /// when the attempt was cancelled or failed (the transition already ran).
    /// </summary>
    private async Task<NexusAcquisitionResult?> AcquireAsync(DownloadItem item)
    {
        CancellationToken token;
        lock (_sync)
        {
            token = item.CancelSource.Token;
        }

        Transition(item, DownloadPhase.Downloading);
        var progress = new ItemProgress(this, item);
        try
        {
            var result = await _acquisition.AcquireFromNexusAsync(
                item.GameDomain, item.ModId, item.FileId,
                item.Request.NxmKey, item.Request.NxmExpires, progress, token);

            // The import recorded the real name on the container; read it from
            // the repository index (no API call) so the row stops showing the
            // enqueue fallback.
            var name = _repo.Get(result.ContainerId)?.Name;
            Resolve(item, result.ContainerId, result.Version, name);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Download of mod {Mod} file {File} was cancelled.", item.ModId, item.FileId);
            CancelTerminal(item);
            return null;
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            // A live cancel can surface as a wrapped abort (e.g. IOException
            // from an interrupted native read) rather than OCE; the token is
            // authoritative, so it lands Canceled with no error row.
            _logger.LogInformation(ex,
                "Download of mod {Mod} file {File} was cancelled.", item.ModId, item.FileId);
            CancelTerminal(item);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Download of mod {Mod} file {File} failed.", item.ModId, item.FileId);
            Fail(item, ex.Message);
            return null;
        }
    }

    // ---- completion, by purpose ---------------------------------------------

    /// <summary>
    /// ProfileAdd completion: register the mod in the target profile, acknowledge
    /// the install best-effort, and reload the list when the target is still
    /// the active profile. Returns false when the item was failed inline (the
    /// caller must not run the terminal completion).
    /// </summary>
    private bool CompleteProfileAdd(DownloadItem item, Guid containerId, ModVersionPolicy policy)
    {
        try
        {
            // Verify the target still exists through the service; the cached
            // name is display-only. Deleted mid-flight = inline failure.
            _ = _profiles.GetProfile(item.TargetProfileId);

            var inProfile = _profiles.GetModList(item.TargetProfileId)
                .Any(m => m.ContainerId == containerId);
            if (inProfile)
            {
                // AddMod no-ops on policy; the user's click must win, so an
                // existing membership is rewritten through SetModPolicy.
                try
                {
                    _profiles.SetModPolicy(item.TargetProfileId, containerId, policy);
                }
                catch (KeyNotFoundException)
                {
                    // SetModPolicy throws KeyNotFoundException for an unknown
                    // profile AND a container missing from the list; the
                    // profile was verified one call earlier, so this is the
                    // removed-mid-flight race, not a deleted profile.
                    _logger.LogWarning(
                        "Mod {Container} was removed from profile {Profile} before its download completed.",
                        containerId, item.TargetProfileId);
                    Fail(item, _localization["ModDownloadQueue_ModRemovedMessage"]);
                    return false;
                }
            }
            else
            {
                _profiles.AddMod(item.TargetProfileId, containerId, policy);
            }
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning(
                "Download target profile {Profile} (mod {Mod} file {File}) no longer exists.",
                item.TargetProfileId, item.ModId, item.FileId);
            Fail(item, _localization["ModDownloadQueue_ProfileDeletedMessage"]);
            return false;
        }

        // Best-effort acknowledge (the current nxm posture): a persistence
        // failure is logged and never blocks the completion.
        try
        {
            _updateState.AcknowledgeInstall(item.TargetProfileId, containerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Acknowledging update for container {Container} failed; the next check reconciles.",
                containerId);
        }

        // Reload only when the target is still the active profile: a completed
        // registration for a background profile has nothing to show here.
        // The refresh seam resolves lazily (first completion), so the queue
        // can be constructed before the list VM singleton it forwards to.
        if (_session.ActiveProfileId == item.TargetProfileId)
        {
            _invokeOnUi(() => _modListRefresh().Reload());
        }
        return true;
    }

    /// <summary>
    /// UpdateInstall completion: eligibility was revalidated at dequeue, so this
    /// acknowledges the install once (best-effort, matching the ProfileAdd
    /// posture) and raises <see cref="UpdatesApplied"/>. No profile write: the
    /// mod is already a LatestPolicy member (eligibility proved it) and the new
    /// version resolves through the container automatically.
    /// </summary>
    private bool CompleteUpdateInstall(DownloadItem item, Guid containerId)
    {
        try
        {
            _updateState.AcknowledgeInstall(item.TargetProfileId, containerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Acknowledging update for container {Container} failed; the next check reconciles.",
                containerId);
        }

        _logger.LogInformation(
            "Installed the latest Nexus release for container {Container} (mod {Mod}).",
            containerId, item.ModId);
        _invokeOnUi(() => UpdatesApplied?.Invoke(this, EventArgs.Empty));
        return true;
    }

    /// <summary>
    /// The UpdateInstall eligibility revalidation: the same four
    /// <see cref="UpdateEligibility"/> rules the install path enforces under
    /// its gate, evaluated here at dequeue (the queue worker is the gate).
    /// </summary>
    private bool IsStillEligible(DownloadItem item)
    {
        var containerId = item.Request.ContainerId!.Value;
        try
        {
            var candidates = _profiles.GetModList(item.TargetProfileId).ToCandidates();
            var candidate = candidates.FirstOrDefault(c => c.ContainerId == containerId);
            if (UpdateEligibility.IsEligible(
                    candidate, _repo.Get(containerId), item.ModId,
                    item.Request.ExpectedVersion!, out var reason))
            {
                return true;
            }

            _logger.LogDebug(
                "Queued update install for container {Container} skipped: {Reason}.",
                containerId, reason);
            return false;
        }
        catch (KeyNotFoundException)
        {
            // The target profile is gone; nothing to update.
            _logger.LogDebug(
                "Queued update install for container {Container} skipped: target profile is gone.",
                containerId);
            return false;
        }
    }

    // ---- presentation transitions (all through the marshal seam) ------------

    private void Transition(DownloadItem item, DownloadPhase phase) =>
        _invokeOnUi(() =>
        {
            // The cancel guard keeps a cancel that raced the transition from
            // flashing a new phase onto a row that already resolved.
            if (!item.IsTerminal && !item.CancelSource.IsCancellationRequested)
            {
                item.Phase = phase;
            }
        });

    private void Resolve(DownloadItem item, Guid containerId, string version, string? displayName)
    {
        _invokeOnUi(() =>
        {
            item.ContainerId = containerId;
            item.Version = version;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                item.DisplayName = displayName;
            }
        });
        OnItemChanged(item);
    }

    private void Fail(DownloadItem item, string message)
    {
        lock (_sync)
        {
            _live.Remove(item);
        }
        _invokeOnUi(() =>
        {
            if (item.IsTerminal)
            {
                return;
            }
            item.ErrorMessage = message;
            item.Phase = DownloadPhase.Failed;
        });
        OnItemChanged(item);
    }

    private void Complete(DownloadItem item)
    {
        lock (_sync)
        {
            _live.Remove(item);
        }
        _invokeOnUi(() =>
        {
            if (item.Phase == DownloadPhase.Completed)
            {
                return;
            }
            if (!item.IsTerminal)
            {
                item.Phase = DownloadPhase.Completed;
            }
            _items.Remove(item);
        });
        OnItemChanged(item);
    }

    private void CancelTerminal(DownloadItem item)
    {
        lock (_sync)
        {
            _live.Remove(item);
        }
        _invokeOnUi(() =>
        {
            if (item.Phase != DownloadPhase.Canceled && !item.IsTerminal)
            {
                item.Phase = DownloadPhase.Canceled;
            }
            _items.Remove(item);
        });
        OnItemChanged(item);
    }

    private void OnItemChanged(DownloadItem item) =>
        _invokeOnUi(() => ItemChanged?.Invoke(item));

    /// <summary>
    /// The acquisition progress adapter: writes the bytes onto the item and
    /// moves the row to Importing once the byte stream has reached a known
    /// total (the acquisition call's remaining work after the last byte is the
    /// extract/import). With an unknown total (no Content-Length) the row stays
    /// Downloading until completion; the phase is presentational only.
    /// </summary>
    private sealed class ItemProgress : IProgress<(long Received, long? Total)>
    {
        private readonly ModDownloadQueue _queue;
        private readonly DownloadItem _item;

        public ItemProgress(ModDownloadQueue queue, DownloadItem item)
        {
            _queue = queue;
            _item = item;
        }

        public void Report((long Received, long? Total) value) =>
            _queue._invokeOnUi(() =>
            {
                _item.ReceivedBytes = value.Received;
                if (value.Total is { } total)
                {
                    _item.TotalBytes = total;
                    if (total <= value.Received && _item.Phase == DownloadPhase.Downloading)
                    {
                        _item.Phase = DownloadPhase.Importing;
                    }
                }
            });
    }
}
