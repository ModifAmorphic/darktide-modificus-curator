namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="SingleInstanceGuard.IsAnotherInstanceRunning"/>: the
/// non-throwing query over the guard's process enumeration. True when the
/// enumerator reports other live pids, false when alone, and it passes the
/// caller's process name + excluding pid straight through to the enumerator
/// (the handler relay asks about the Curator exe's name, not its own).
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void Query_true_when_enumerator_reports_another_process()
    {
        var guard = new SingleInstanceGuard((_, _) => new[] { 4242 });

        Assert.True(guard.IsAnotherInstanceRunning("Modificus.Curator", 111));
    }

    [Fact]
    public void Query_false_when_enumerator_reports_alone()
    {
        var guard = new SingleInstanceGuard((_, _) => Array.Empty<int>());

        Assert.False(guard.IsAnotherInstanceRunning("Modificus.Curator", 111));
    }

    [Fact]
    public void Query_passes_name_and_excluding_pid_to_the_enumerator()
    {
        string? seenName = null;
        var seenPid = 0;
        var guard = new SingleInstanceGuard((name, pid) =>
        {
            seenName = name;
            seenPid = pid;
            return Array.Empty<int>();
        });

        guard.IsAnotherInstanceRunning("Modificus.Curator", 4242);

        Assert.Equal("Modificus.Curator", seenName);
        Assert.Equal(4242, seenPid);
    }
}
