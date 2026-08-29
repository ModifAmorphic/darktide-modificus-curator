namespace Modificus.Curator.UI.Session;

/// <summary>
/// The shared activity hub over the Mods page's hosted card VMs (the inline
/// import workflow + the load-order import). Each card VM reports its own
/// activity at every state flip (the same places its own
/// <c>IsActive</c> re-fires); nothing else writes, so the gate never invents
/// state, it only aggregates what the VMs already own.
/// </summary>
/// <remarks>
/// <para><b>Why a hub:</b> the two card VMs must exclude each other (a batch,
/// an edit, and a load-order session can never overlap) without either VM
/// referencing the other, and the mod-list VM's any-card projections (the
/// Add disable, the toolbar lock, drop acceptance) need one re-fire source
/// rather than one subscription per card. The gate holds no references to
/// the VMs (they push their state), so the dependency graph stays acyclic
/// and each new hosted card is one registration + one report call.</para>
/// <para><b>Threading:</b> UI-thread only, like every card state flip it
/// mirrors. No locking.</para>
/// </remarks>
public sealed class ModCardsGate
{
    private readonly HashSet<object> _active = new();

    /// <summary>Raised (UI thread) whenever any card's activity flipped.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether any hosted card is active.</summary>
    public bool IsAnyCardActive { get; private set; }

    /// <summary>
    /// Whether any card OTHER than <paramref name="card"/> is active: the
    /// mutual-exclusion read each card VM performs before starting.
    /// </summary>
    public bool IsAnyOtherCardActive(object card) =>
        _active.Any(c => !ReferenceEquals(c, card));

    /// <summary>
    /// Reports a card's activity. Idempotent per (card, state) pair; raises
    /// <see cref="Changed"/> only on an actual flip.
    /// </summary>
    public void ReportActive(object card, bool active)
    {
        ArgumentNullException.ThrowIfNull(card);

        var flipped = active ? _active.Add(card) : _active.Remove(card);
        if (!flipped)
        {
            return;
        }

        IsAnyCardActive = _active.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
