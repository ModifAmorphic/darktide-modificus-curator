using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises the <see cref="LoopbackBrowser"/> + the production
/// <see cref="HttpListenerLoopbackListener"/>: a real listener binds an
/// ephemeral loopback port, an <see cref="HttpClient"/> plays the role of the
/// browser redirecting to the callback URL, and the listener returns the
/// callback's query string. No real OAuth provider or browser involved.
/// </summary>
/// <remarks>
/// The spec explicitly asks for this verification: "If you need to verify the
/// loopback listener binds, do it in a test against an ephemeral port, not by
/// launching the real app."
/// </remarks>
public sealed class LoopbackBrowserTests
{
    [Fact]
    public void Constructor_pre_binds_an_ephemeral_port_in_the_redirect_uri()
    {
        var browser = new LoopbackBrowser(new FakeExternalLauncher());

        Assert.StartsWith("http://127.0.0.1:", browser.RedirectUri);
        Assert.EndsWith(NexusOAuthConstants.CallbackPath, browser.RedirectUri);

        // The port must be a real, non-zero port (the OS assigned one).
        var port = new Uri(browser.RedirectUri).Port;
        Assert.InRange(port, 1024, 65535);
    }

    [Fact]
    public async Task InvokeAsync_opens_browser_at_start_url_and_returns_callback_query()
    {
        // The external launcher is a recorder: it captures the authorize URL
        // OidcClient would build + hand to the OS shell-open.
        var launcher = new FakeExternalLauncher();
        var browser = new LoopbackBrowser(
            timeout: TimeSpan.FromSeconds(10),
            createListener: opts => new HttpListenerLoopbackListener(opts),
            externalLauncher: launcher);

        // A background task that, once the browser is "opened" (the listener is
        // bound), simulates the OAuth provider redirecting to the callback URL
        // with a fake code + state. The listener receives it; InvokeAsync
        // resolves the result.
        var redirectUri = browser.RedirectUri;
        _ = Task.Run(async () =>
        {
            // Poll for the listener to be ready (the browser-launch callback
            // fires after the listener has bound, so this is mostly immediate).
            // Cap at 5s so a binding failure fails the test rather than hangs.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && launcher.OpenedUris.Count == 0)
            {
                await Task.Delay(20);
            }

            using var http = new HttpClient();
            // The callback URL the OAuth provider would redirect to. The query
            // string is what OidcClient parses.
            await http.GetAsync(redirectUri + "?code=fake-code&state=fake-state");
        });

        var result = await browser.InvokeAsync(new BrowserOptions(
            startUrl: "https://users.nexusmods.com/oauth/authorize?client_id=test&redirect_uri=" +
                Uri.EscapeDataString(redirectUri),
            endUrl: redirectUri));

        Assert.Equal(BrowserResultType.Success, result.ResultType);
        Assert.Contains("code=fake-code", result.Response, StringComparison.Ordinal);
        Assert.Contains("state=fake-state", result.Response, StringComparison.Ordinal);
        var openedUrl = Assert.Single(launcher.OpenedUris).AbsoluteUri;
        Assert.Contains("users.nexusmods.com/oauth/authorize", openedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_returns_Timeout_when_no_callback_arrives()
    {
        // Tight timeout + an in-memory launcher + no HttpClient redirecting
        // to the callback. InvokeAsync must surface Timeout rather than hang.
        var browser = new LoopbackBrowser(
            timeout: TimeSpan.FromMilliseconds(200),
            createListener: opts => new HttpListenerLoopbackListener(opts),
            externalLauncher: new FakeExternalLauncher());

        var result = await browser.InvokeAsync(new BrowserOptions(
            startUrl: "https://users.nexusmods.com/oauth/authorize",
            endUrl: "http://127.0.0.1:0/callback"));

        Assert.Equal(BrowserResultType.Timeout, result.ResultType);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task InvokeAsync_maps_a_failed_browser_launch_to_UnknownError()
    {
        // A launcher that cannot start the browser (false) ends the flow as
        // UnknownError: the OAuth consent cannot proceed without a browser.
        var browser = new LoopbackBrowser(
            timeout: TimeSpan.FromSeconds(5),
            createListener: opts => new HttpListenerLoopbackListener(opts),
            externalLauncher: new FakeExternalLauncher { OpenUriResult = _ => false });

        var result = await browser.InvokeAsync(new BrowserOptions(
            startUrl: "https://users.nexusmods.com/oauth/authorize",
            endUrl: "http://127.0.0.1:1/callback"));

        Assert.Equal(BrowserResultType.UnknownError, result.ResultType);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Constructor_rejects_non_positive_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackBrowser(TimeSpan.Zero, _ => null!, new FakeExternalLauncher()));
    }

    [Fact]
    public async Task HttpListenerListener_serves_a_friendly_html_response()
    {
        // The listener serves a "you can return to the app" page to the browser
        // on callback. The test fetches the response body + asserts it carries
        // the expected text + the right content type.
        var port = FreePort();
        var listener = new HttpListenerLoopbackListener(new LoopbackListenerOptions(port, "/callback"));
        listener.Start();
        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=c&state=s");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Curator", body, StringComparison.Ordinal);
            Assert.Equal("?code=c&state=s", await listener.WaitForCallbackAsync(CancellationToken.None));
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Grabs a free loopback port for the test (mirrors the production
    /// grab via TcpListener).</summary>
    private static int FreePort()
    {
        using var tcp = new TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            return ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        }
        finally
        {
            tcp.Stop();
        }
    }
}
