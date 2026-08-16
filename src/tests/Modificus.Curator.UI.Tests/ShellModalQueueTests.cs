using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The <see cref="ShellModalQueue"/> contract: a queued modal runs once, after
/// the destination it targets is drained, survives drains of other
/// destinations, an owner's newer enqueue replaces its unconsumed entry, and a
/// thrown exception inside one drained modal cannot re-fire it.
/// </summary>
public sealed class ShellModalQueueTests
{
    private static readonly object OwnerA = new();
    private static readonly object OwnerB = new();

    [Fact]
    public async Task Drain_runs_the_matching_entry_once()
    {
        var queue = new ShellModalQueue();
        var runs = 0;
        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { runs++; return Task.CompletedTask; });

        await queue.DrainAsync(ShellDestination.Preferences);
        Assert.Equal(0, runs);

        await queue.DrainAsync(ShellDestination.Mods);
        Assert.Equal(1, runs);

        // Consumed: a second drain of the same destination runs nothing.
        await queue.DrainAsync(ShellDestination.Mods);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_newer_enqueue_from_the_same_owner_replaces_the_unconsumed_one()
    {
        var queue = new ShellModalQueue();
        var first = 0;
        var second = 0;
        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { first++; return Task.CompletedTask; });
        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { second++; return Task.CompletedTask; });

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Different_owners_queue_independently_and_run_in_enqueue_order()
    {
        var queue = new ShellModalQueue();
        var order = new List<string>();
        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { order.Add("a"); return Task.CompletedTask; });
        queue.Enqueue(OwnerB, ShellDestination.Mods, () => { order.Add("b"); return Task.CompletedTask; });

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public async Task A_replacement_after_a_partial_drain_only_affects_the_unconsumed_entry()
    {
        var queue = new ShellModalQueue();
        var runs = new List<string>();
        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { runs.Add("first"); return Task.CompletedTask; });
        await queue.DrainAsync(ShellDestination.Mods);

        queue.Enqueue(OwnerA, ShellDestination.Mods, () => { runs.Add("second"); return Task.CompletedTask; });
        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(new[] { "first", "second" }, runs);
    }

    [Fact]
    public async Task A_drained_entry_that_throws_is_consumed_not_requeued()
    {
        var queue = new ShellModalQueue();
        var attempts = 0;
        queue.Enqueue(OwnerA, ShellDestination.Mods, () =>
        {
            attempts++;
            throw new InvalidOperationException("modal wiring bug");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.DrainAsync(ShellDestination.Mods));

        await queue.DrainAsync(ShellDestination.Mods);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Drain_leaves_other_destinations_entries_queued()
    {
        var queue = new ShellModalQueue();
        var settingsRuns = 0;
        queue.Enqueue(OwnerA, ShellDestination.Settings, () => { settingsRuns++; return Task.CompletedTask; });

        // Draining an unrelated destination neither runs nor consumes the
        // Settings entry; its own drain later runs it.
        await queue.DrainAsync(ShellDestination.Preferences);
        Assert.Equal(0, settingsRuns);

        await queue.DrainAsync(ShellDestination.Settings);
        Assert.Equal(1, settingsRuns);
    }
}
