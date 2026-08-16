namespace Modificus.Curator.Integrations;

/// <summary>
/// Coordinates mod-update installs so only one runs at a time globally, shared
/// between the manual per-row update action and the automatic Premium updater
/// (both through <see cref="IModUpdateInstaller"/>). Keeps a manual click and
/// an automatic batch from installing the same mod concurrently without
/// relying on per-VM flags.
/// </summary>
/// <remarks>
/// <para>
/// <b>A single-slot gate.</b> Backed by a <see cref="SemaphoreSlim(1, 1)"/>. The
/// manual path uses <see cref="TryAcquire"/> (non-blocking: a second click while
/// an install runs is a clean no-op); the automatic path uses
/// <see cref="AcquireAsync"/> (awaits its turn so a sequential batch processes
/// one mod at a time, but the caller serializes the batch already so in practice
/// the await is uncontended).</para>
/// <para>
/// <b>Busy notification.</b> <see cref="IsBusy"/> flips on acquire + release and
/// raises <see cref="BusyChanged"/> (on the acquiring/releasing thread).
/// Subscribers marshal to the UI thread if they touch UI state.</para>
/// <para>
/// <b>Reentrancy.</b> The same thread acquiring twice would deadlock the
/// semaphore; both call sites acquire-then-release in a tight try/finally scope
/// and never re-enter, so this is not a concern in practice.</para>
/// </remarks>
public sealed class UpdateCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _isBusy;

    /// <summary>
    /// Whether an install (manual or automatic) is currently in flight. Raised
    /// via <see cref="BusyChanged"/> on acquire + release. Read on the acquiring
    /// + subscribing threads; the bool read/write is atomic, so the worst a
    /// reader sees is a one-tick-stale value (corrected on the next event).
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _isBusy);

    /// <summary>
    /// Raised (on the acquiring/releasing thread) when <see cref="IsBusy"/>
    /// changes. Subscribers marshal to the UI thread if they touch UI state.
    /// </summary>
    public event EventHandler? BusyChanged;

    /// <summary>
    /// Non-blocking acquire for the manual path. Returns <c>true</c> + a
    /// non-null <paramref name="scope"/> (whose <see cref="IDisposable.Dispose"/>
    /// releases the gate) when the gate was free; returns <c>false</c> + a null
    /// scope when another install is in flight (the manual click is a clean
    /// no-op in that case). Never blocks.
    /// </summary>
    public bool TryAcquire(out IDisposable? scope)
    {
        if (!_gate.Wait(0))
        {
            scope = null;
            return false;
        }

        SetBusy(true);
        scope = new Scope(this);
        return true;
    }

    /// <summary>
    /// Awaiting acquire for the automatic batch. Resolves when the gate is free
    /// (the caller serializes a batch, so in practice this is uncontended). The
    /// returned scope's <see cref="IDisposable.Dispose"/> releases the gate.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        SetBusy(true);
        return new Scope(this);
    }

    private void Release() => SetBusy(false, releaseGate: true);

    private void SetBusy(bool value, bool releaseGate = false)
    {
        Volatile.Write(ref _isBusy, value);
        if (releaseGate)
        {
            _gate.Release();
        }
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The disposable scope returned by <see cref="TryAcquire"/> /
    /// <see cref="AcquireAsync"/>; disposing releases the gate + flips the busy
    /// flag. Structurally a class (not a struct) so a null check at the call site
    /// is meaningful + the dispose is idempotent.
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private UpdateCoordinator? _owner;

        public Scope(UpdateCoordinator owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
