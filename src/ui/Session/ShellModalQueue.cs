using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The shell-owned queue of deferred modal operations. A service that needs a
/// modal to run the next time the user enters a particular destination (a
/// "show this over the freshly painted page" handshake) enqueues it here
/// instead of coupling the shell to the service; the shell drains the queue in
/// its navigation lifecycle, after the destination switch + the enter effects,
/// so the page is painted underneath the modal.
/// </summary>
/// <remarks>
/// <para><b>Semantics:</b> a queued modal for destination X runs once, after
/// <see cref="ShellDestination"/> actually switches to X, and survives visits
/// to other destinations in between. A newer enqueue from the same owner
/// replaces that owner's unconsumed entry (newest-wins); different owners queue
/// independently (at most one pending entry each). <see cref="DrainAsync"/>
/// consumes the matching entries (removes them) before running any, so a
/// thrown exception inside one modal cannot re-fire it on the next drain, and
/// awaits each sequentially in enqueue order.</para>
/// <para><b>Threading:</b> enqueue + drain run on the UI thread (the shell's
/// navigation + the profile-created trigger that feeds the one current
/// enqueuer both fire there).</para>
/// </remarks>
public interface IShellModalQueue
{
    /// <summary>
    /// Queues a modal to run when the shell next enters
    /// <paramref name="showOn"/>. Replaces this owner's unconsumed entry, if
    /// any (newest-wins); other owners' entries are untouched.
    /// </summary>
    /// <param name="owner">The enqueueing service's identity (use one stable
    /// key per service, e.g. <c>typeof(TheService)</c>).</param>
    /// <param name="showOn">The destination that must be entered for the modal
    /// to run.</param>
    /// <param name="modal">The modal operation (dialog, spinner, or any
    /// awaited flow). It runs at most once, on the UI thread, after the
    /// destination has switched + its enter effects have run.</param>
    void Enqueue(object owner, ShellDestination showOn, Func<Task> modal);

    /// <summary>
    /// Runs + removes every entry queued for <paramref name="destination"/>,
    /// sequentially in enqueue order. Entries for other destinations stay
    /// queued. A no-op when nothing matches.
    /// </summary>
    Task DrainAsync(ShellDestination destination);
}

/// <summary>
/// The single production implementation: an application-lifetime, UI-thread
/// queue of owner-keyed deferred modals.
/// </summary>
public sealed class ShellModalQueue : IShellModalQueue
{
    private readonly List<(object Owner, ShellDestination ShowOn, Func<Task> Modal)> _entries = new();

    /// <inheritdoc />
    public void Enqueue(object owner, ShellDestination showOn, Func<Task> modal)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(modal);

        // Newest-wins per owner: drop the owner's unconsumed entry first.
        _entries.RemoveAll(e => ReferenceEquals(e.Owner, owner));
        _entries.Add((owner, showOn, modal));
    }

    /// <inheritdoc />
    public async Task DrainAsync(ShellDestination destination)
    {
        // Consume before running: an exception inside a modal must not leave
        // its entry queued for the next drain.
        List<Func<Task>>? toRun = null;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].ShowOn == destination)
            {
                (toRun ??= new()).Add(_entries[i].Modal);
                _entries.RemoveAt(i);
            }
        }

        if (toRun is null)
        {
            return;
        }

        // The reverse walk collected newest-first; run in enqueue order.
        toRun.Reverse();
        foreach (var modal in toRun)
        {
            await modal();
        }
    }
}
