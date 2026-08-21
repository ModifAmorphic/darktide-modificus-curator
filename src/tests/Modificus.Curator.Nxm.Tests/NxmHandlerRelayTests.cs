using System.Diagnostics;
using System.Text;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="NxmHandlerRelay"/>: the hot-path (pipe connects first try, URL
/// delivered, exit 0, Curator not launched), the cold-start path (pipe refuses,
/// Curator launched, retry connects, URL delivered, exit 0), the cold-start
/// pre-check split (a reported running Curator skips the launch and prints the
/// waiting line; no running Curator launches exactly once with the launching
/// line), the cold-start timeout (retries exhausted, non-zero exit), the
/// no-URL-arg case (non-zero exit, Curator not launched), and the multi-arg
/// case (first non-flag used as the URL).
/// </summary>
/// <remarks>
/// Every external dependency is faked: <c>pipeConnect</c> returns a capturing
/// in-memory stream, <c>launchCuratorFactory</c> returns a marker ProcessStartInfo
/// (no real process is started), <c>curatorRunningCheck</c> reports a scripted
/// answer (the production default enumerates real processes, which would make
/// the cold-start tests depend on what else runs on the host), and the retry
/// interval/timeout are shrunk so tests are fast.
/// </remarks>
public sealed class NxmHandlerRelayTests
{
    private const string LaunchingLine = "curator nxm handler: Curator is not running; launching it.";
    private const string WaitingLine = "curator nxm handler: Curator is already running; waiting for the pipe.";

    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Hot_path_connects_and_delivers_without_launching_curator()
    {
        var url = "nxm://warhammer40kdarktide/mods/8/files/5820";
        var pipe = new ControllablePipe();
        pipe.ConnectNext(); // first connect succeeds.
        bool launched = false;

        int exit;
        using (var stderr = new StderrCapture())
        {
            exit = await NxmHandlerRelay.RunAsync(
                [url],
                pipeConnect: pipe.Connect,
                launchCuratorFactory: () => { launched = true; return Marker(); },
                curatorRunningCheck: () => false,
                retryInterval: FastRetry,
                retryTimeout: FastTimeout);

            // The hot path delivers without any cold-start stderr narration.
            Assert.DoesNotContain(LaunchingLine, stderr.Text);
            Assert.DoesNotContain(WaitingLine, stderr.Text);
        }

        Assert.Equal(0, exit);
        Assert.False(launched, "Curator must NOT be launched on the hot path.");
        Assert.Single(pipe.Delivered);
        Assert.Equal(url, pipe.Delivered[0]);
    }

    [Fact]
    public async Task Cold_path_launches_curator_once_then_delivers_on_retry()
    {
        var url = "nxm://oauth/callback?code=ABC&state=DEF";
        var pipe = new ControllablePipe();
        pipe.RefuseNext(1); // first connect refuses; subsequent succeed.
        var launches = 0;

        int exit;
        using (var stderr = new StderrCapture())
        {
            exit = await NxmHandlerRelay.RunAsync(
                [url],
                pipeConnect: pipe.Connect,
                launchCuratorFactory: () => { launches++; return Marker(); },
                curatorRunningCheck: () => false,
                retryInterval: FastRetry,
                retryTimeout: TimeSpan.FromSeconds(5));

            Assert.Contains(LaunchingLine, stderr.Text);
            Assert.DoesNotContain(WaitingLine, stderr.Text);
        }

        Assert.Equal(0, exit);
        Assert.Equal(1, launches);
        Assert.Single(pipe.Delivered);
        Assert.Equal(url, pipe.Delivered[0]);
    }

    [Fact]
    public async Task Cold_path_with_curator_already_running_skips_launch_and_waits()
    {
        // The burst case: another handler invocation already launched Curator,
        // so the pipe is refused while it starts. This handler must NOT launch
        // a duplicate; it prints the waiting line and delivers on retry.
        var url = "nxm://warhammer40kdarktide/mods/8/files/5820";
        var pipe = new ControllablePipe();
        pipe.RefuseNext(1); // still starting; the retry connects.
        var launches = 0;

        int exit;
        using (var stderr = new StderrCapture())
        {
            exit = await NxmHandlerRelay.RunAsync(
                [url],
                pipeConnect: pipe.Connect,
                launchCuratorFactory: () => { launches++; return Marker(); },
                curatorRunningCheck: () => true,
                retryInterval: FastRetry,
                retryTimeout: TimeSpan.FromSeconds(5));

            Assert.Contains(WaitingLine, stderr.Text);
            Assert.DoesNotContain(LaunchingLine, stderr.Text);
        }

        Assert.Equal(0, exit);
        Assert.Equal(0, launches);
        Assert.Single(pipe.Delivered);
        Assert.Equal(url, pipe.Delivered[0]);
    }

    [Fact]
    public async Task Cold_path_with_curator_already_running_times_out_without_launching()
    {
        // A running Curator that never binds the pipe (degraded bind, or it is
        // exiting): the handler waits out the full retry window, then exits
        // non-zero without ever launching a duplicate.
        var pipe = new ControllablePipe();
        pipe.RefuseForever();
        var launches = 0;

        var exit = await NxmHandlerRelay.RunAsync(
            ["nxm://warhammer40kdarktide/mods/8/files/5820"],
            pipeConnect: pipe.Connect,
            launchCuratorFactory: () => { launches++; return Marker(); },
            curatorRunningCheck: () => true,
            retryInterval: FastRetry,
            retryTimeout: FastTimeout);

        Assert.NotEqual(0, exit);
        Assert.Equal(0, launches);
        Assert.Empty(pipe.Delivered);
    }

    [Fact]
    public async Task Cold_path_timeout_exits_nonzero_after_launching()
    {
        var url = "nxm://warhammer40kdarktide/mods/8/files/5820";
        var pipe = new ControllablePipe();
        pipe.RefuseForever(); // never connects.
        bool launched = false;

        var exit = await NxmHandlerRelay.RunAsync(
            [url],
            pipeConnect: pipe.Connect,
            launchCuratorFactory: () => { launched = true; return Marker(); },
            curatorRunningCheck: () => false,
            retryInterval: FastRetry,
            retryTimeout: FastTimeout);

        Assert.NotEqual(0, exit);
        Assert.True(launched, "Curator must be launched even on the timeout path.");
    }

    [Fact]
    public async Task No_url_arg_exits_nonzero_without_launching()
    {
        var pipe = new ControllablePipe();
        bool launched = false;

        var exit = await NxmHandlerRelay.RunAsync(
            Array.Empty<string>(),
            pipeConnect: pipe.Connect,
            launchCuratorFactory: () => { launched = true; return Marker(); },
            retryInterval: FastRetry,
            retryTimeout: FastTimeout);

        Assert.NotEqual(0, exit);
        Assert.False(launched, "Curator must NOT be launched when no URL is provided.");
        Assert.Empty(pipe.Delivered);
    }

    [Fact]
    public async Task Multiple_args_uses_first_non_flag_as_url()
    {
        var url = "nxm://warhammer40kdarktide/mods/8/files/5820";
        var pipe = new ControllablePipe();
        pipe.ConnectNext();

        var exit = await NxmHandlerRelay.RunAsync(
            ["--silent", "/verbose", url, "extra"],
            pipeConnect: pipe.Connect,
            launchCuratorFactory: Marker,
            retryInterval: FastRetry,
            retryTimeout: FastTimeout);

        Assert.Equal(0, exit);
        Assert.Single(pipe.Delivered);
        Assert.Equal(url, pipe.Delivered[0]);
    }

    [Fact]
    public void ResolveCuratorMainExe_throws_when_sibling_exe_is_missing()
    {
        // A fresh empty base dir: no sibling Modificus.Curator(.exe) lives here,
        // so the existence check must throw before any ProcessStartInfo is built.
        var baseDir = Path.Combine(Path.GetTempPath(), "curator-nxm-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var ex = Assert.Throws<CuratorMainExeNotFoundException>(
                () => NxmHandlerRelay.ResolveCuratorMainExe(baseDir));

            // The message must say where the handler looked so the operator /
            // logs can diagnose a mis-deployed handler, and expose both pieces
            // of resolved state on the typed exception.
            var expectedName = OperatingSystem.IsWindows() ? "Modificus.Curator.exe" : "Modificus.Curator";
            Assert.EndsWith(expectedName, ex.ExpectedPath);
            Assert.Equal(baseDir, ex.BaseDirectory);
            Assert.Contains(ex.ExpectedPath, ex.Message);
            Assert.Contains(baseDir, ex.Message);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ---- fakes -----------------------------------------------------------

    /// <summary>
    /// Swaps <see cref="Console.Error"/> for a string writer so tests can
    /// assert the relay's stderr lines; restored on dispose. Safe under the
    /// assembly's DisableTestParallelization (no other test runs concurrently).
    /// </summary>
    private sealed class StderrCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public StderrCapture() => Console.SetError(_capture);

        public string Text => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }

    private static ProcessStartInfo Marker()
    {
        // A real, harmless executable so Process.Start succeeds (the relay really
        // spawns whatever the factory returns). /bin/true on Linux exits 0
        // instantly; cmd /c exit 0 on Windows does the same.
        if (OperatingSystem.IsWindows())
            return new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true };
        return new ProcessStartInfo("/bin/true");
    }

    /// <summary>
    /// A scripted pipe-connect seam. Captures the URL written on each
    /// "connected" attempt into <see cref="Delivered"/> (decoded from the framed
    /// message when the relay closes the stream). <see cref="RefuseNext"/> /
    /// <see cref="RefuseForever"/> / <see cref="ConnectNext"/> script the connect
    /// responses.
    /// </summary>
    private sealed class ControllablePipe
    {
        private readonly List<string> _delivered = new();
        private int _refusalsRemaining;
        private bool _refuseForever;

        public IReadOnlyList<string> Delivered => _delivered;

        // Resets to "all connects succeed": no refusals queued, not forever.
        public void ConnectNext()
        {
            _refuseForever = false;
            _refusalsRemaining = 0;
        }

        public void RefuseNext(int times)
        {
            _refuseForever = false;
            _refusalsRemaining = times;
        }

        public void RefuseForever() => _refuseForever = true;

        public Task<(bool connected, Stream? stream)> Connect(string pipeName, CancellationToken ct)
        {
            bool connect;
            if (_refuseForever)
                connect = false;
            else if (_refusalsRemaining > 0)
            {
                _refusalsRemaining--;
                connect = false;
            }
            else
                connect = true;

            if (!connect)
                return Task.FromResult<(bool, Stream?)>((false, null));

            // A fresh capturing stream per connect. The relay writes the framed
            // URL to it, then disposes it; we decode the frame on dispose.
            var stream = new CapturingStream(url => _delivered.Add(url));
            return Task.FromResult<(bool, Stream?)>((true, stream));
        }

        // Captures the URL by decoding the framed message the relay writes,
        // invoking the callback on dispose (the relay owns the stream lifecycle,
        // so dispose is the natural capture point).
        private sealed class CapturingStream : MemoryStream
        {
            private readonly Action<string> _onClosed;
            private bool _captured;

            public CapturingStream(Action<string> onClosed) => _onClosed = onClosed;

            public override ValueTask DisposeAsync()
            {
                CaptureOnce();
                return default; // do NOT call base; keep the buffer intact.
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) CaptureOnce();
                // do NOT call base; keep the buffer intact.
            }

            private void CaptureOnce()
            {
                if (_captured) return;
                _captured = true;

                var bytes = ToArray();
                if (bytes.Length < 4)
                {
                    _onClosed("(short)");
                    return;
                }
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (length == 0 || bytes.Length < 4 + (int)length)
                {
                    _onClosed("(partial)");
                    return;
                }
                _onClosed(Encoding.UTF8.GetString(bytes, 4, (int)length));
            }
        }
    }
}
