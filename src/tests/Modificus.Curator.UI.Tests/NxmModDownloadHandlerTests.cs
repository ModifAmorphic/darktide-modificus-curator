using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Nxm;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Exercises <see cref="NxmModDownloadHandler"/> (the enqueue adapter in front
/// of <see cref="IModDownloadQueue"/>) against in-memory fakes. Covers the
/// Darktide-only gate, the two pre-flight gates (auth configured, active
/// profile), the enqueue path (repo peek naming, request field forwarding,
/// prompt return), and the enqueue-failure alert. The acquisition, profile
/// registration, acknowledge, and reload live on the queue and are covered by
/// <see cref="ModDownloadQueueTests"/>.
/// </summary>
/// <remarks>
/// The handler's UI-thread marshaling seam (<c>invokeOnUi</c>) is injected as a
/// pass-through so the tests run without a live Avalonia Dispatcher.
/// </remarks>
public sealed class NxmModDownloadHandlerTests
{
    private static readonly LocalizationService Localization = new();
    private static readonly NxmModDownloadUrl SampleUrl = new(
        "nxm://warhammer40kdarktide/mods/8/files/5820",
        "warhammer40kdarktide", ModId: 8, FileId: 5820,
        Key: "ABC", Expires: 12345L, UserId: null);

    // ---- Darktide-only gate ----------------------------------------------

    [Fact]
    public async Task HandleAsync_non_darktide_link_rejected_before_auth_profile_enqueue()
    {
        // Curator supports only Darktide. A link for another game is rejected
        // before the auth/profile gates + enqueue so nothing is attempted.
        var bundle = ActiveBundle();
        var handler = bundle.BuildHandler();

        var skyrimUrl = new NxmModDownloadUrl(
            "nxm://skyrim/mods/1/files/2",
            "skyrim", ModId: 1, FileId: 2, Key: "k", Expires: 1L, UserId: null);

        await handler.HandleAsync(skyrimUrl);

        var alert = Assert.Single(bundle.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NonDarktideTitle"], alert.Title);
        // The message names the game domain from the link.
        Assert.Contains("skyrim", alert.Message, StringComparison.Ordinal);
        Assert.Empty(bundle.Queue.Requests);
    }

    [Theory]
    [InlineData("WARHAMMER40KDARKTIDE")] // case-insensitive match still accepted
    [InlineData("warhammer40kdarktide")]
    public async Task HandleAsync_darktide_link_case_insensitive_proceeds_past_game_gate(
        string gameDomain)
    {
        var bundle = ActiveBundle();
        var handler = bundle.BuildHandler();

        var url = new NxmModDownloadUrl(
            $"nxm://{gameDomain}/mods/8/files/5820",
            gameDomain, ModId: 8, FileId: 5820, Key: "ABC", Expires: 1L, UserId: null);

        await handler.HandleAsync(url);

        var request = Assert.Single(bundle.Queue.Requests);
        Assert.Equal(gameDomain, request.GameDomain);
        Assert.Empty(bundle.Dialogs.AlertCalls);
    }

    // ---- auth gate ---------------------------------------------------------

    [Fact]
    public async Task HandleAsync_auth_not_configured_shows_alert_and_enqueues_nothing()
    {
        // NexusAuthMethod.None (the default) -> alert + no enqueue.
        var bundle = ActiveBundle(method: NexusAuthMethod.None);
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(bundle.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NotConfiguredTitle"], alert.Title);
        Assert.Empty(bundle.Queue.Requests);
    }

    [Theory]
    [InlineData(NexusAuthMethod.OAuth)]
    [InlineData(NexusAuthMethod.ApiKey)]
    public async Task HandleAsync_auth_configured_proceeds_past_auth_gate(
        NexusAuthMethod method)
    {
        // With a non-None auth method + an active profile, the handler
        // enqueues. Both OAuth and ApiKey satisfy the gate.
        var bundle = ActiveBundle(method);
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        Assert.Single(bundle.Queue.Requests);
    }

    // ---- active-profile gate ----------------------------------------------

    [Fact]
    public async Task HandleAsync_no_active_profile_shows_alert_and_enqueues_nothing()
    {
        // Auth configured, but no active profile -> alert + no enqueue.
        var bundle = new HandlerBundle
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            // ActiveProfileId stays null.
        };
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(bundle.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NoActiveProfileTitle"], alert.Title);
        Assert.Empty(bundle.Queue.Requests);
    }

    // ---- enqueue path ------------------------------------------------------

    [Fact]
    public async Task HandleAsync_passing_gates_enqueue_one_request_with_forwarded_fields()
    {
        // The passing path performs no acquisition + no profile write: it peeks
        // the repo, enqueues exactly one request, and returns (the prompt-return
        // contract that frees the IPC pipe).
        var bundle = ActiveBundle();
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        var request = Assert.Single(bundle.Queue.Requests);
        Assert.Equal(SampleUrl.Game, request.GameDomain);
        Assert.Equal(SampleUrl.ModId, request.ModId);
        Assert.Equal(SampleUrl.FileId, request.FileId);
        Assert.Equal(SampleUrl.Key, request.NxmKey);
        Assert.Equal(SampleUrl.Expires, request.NxmExpires);
        Assert.Equal(DownloadPurpose.ProfileAdd, request.Purpose);
        Assert.Equal(bundle.ProfileId, request.TargetProfileId);
        Assert.Equal("Bundle Profile", request.TargetProfileName);
        // Repo peek missed: no container id + the localized numeric fallback.
        Assert.Null(request.ContainerId);
        Assert.Equal(Localization.Format("Nxm_ModNameFallback", SampleUrl.ModId), request.DisplayName);
        // No alert on the passing path.
        Assert.Empty(bundle.Dialogs.AlertCalls);
    }

    [Fact]
    public async Task HandleAsync_repo_peek_hit_carries_container_id_and_stored_name()
    {
        var bundle = ActiveBundle();
        var container = bundle.Repo.Seed(
            new NexusSource { ModId = SampleUrl.ModId }, "Darktide Mod Framework");
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        var request = Assert.Single(bundle.Queue.Requests);
        Assert.Equal(container.Id, request.ContainerId);
        Assert.Equal("Darktide Mod Framework", request.DisplayName);
    }

    [Fact]
    public async Task HandleAsync_enqueue_failure_shows_alert()
    {
        // The profile vanished between the session read and the enqueue (or the
        // queue refused admission): nothing was enqueued, so the failure keeps
        // the modal-alert path (no download row exists to host it).
        var bundle = new HandlerBundle
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            Session = { ActiveProfileId = Guid.NewGuid() }, // not in the fake service
        };
        var handler = bundle.BuildHandler();

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(bundle.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_DownloadFailedTitle"], alert.Title);
        Assert.Empty(bundle.Queue.Requests);
    }

    // ---- UI-thread marshaling seam ----------------------------------------

    [Fact]
    public async Task HandleAsync_routes_alerts_through_the_invokeOnUi_seam()
    {
        // The invokeOnUi seam is what marshals the alert to the UI thread in
        // production. The handler must route every alert through it (not call
        // the dialog service directly).
        var invoked = false;
        var bundle = new HandlerBundle();
        var handler = new NxmModDownloadHandler(
            action =>
            {
                invoked = true;
                return action();
            },
            bundle.Queue,
            bundle.Repo,
            bundle.Session,
            bundle.Profiles,
            bundle.Loader,
            bundle.Dialogs,
            Localization,
            NullLogger<NxmModDownloadHandler>.Instance);

        await handler.HandleAsync(SampleUrl);

        Assert.True(invoked);
    }

    // ---- null arg ----------------------------------------------------------

    [Fact]
    public async Task HandleAsync_null_url_throws()
    {
        var bundle = new HandlerBundle();
        var handler = bundle.BuildHandler();
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// A bundle whose session has its seeded profile active, with the given
    /// auth method configured (ApiKey by default).
    /// </summary>
    private static HandlerBundle ActiveBundle(NexusAuthMethod method = NexusAuthMethod.ApiKey)
    {
        var bundle = new HandlerBundle
        {
            Config = AuthConfig(method),
        };
        bundle.Session.ActiveProfileId = bundle.ProfileId;
        return bundle;
    }

    /// <summary>A config with the Nexus auth method set + a dummy API key.</summary>
    private static CuratorConfig AuthConfig(NexusAuthMethod method) =>
        new()
        {
            Integrations =
            {
                Nexus = new NexusConfig
                {
                    AuthMethod = method,
                    ApiKey = method == NexusAuthMethod.ApiKey ? "key" : null,
                },
            },
        };

    /// <summary>
    /// The handler's in-memory dependencies: the recording queue (the handler's
    /// whole downstream), the repo (the name peek), the profile session +
    /// service (the target profile), the config loader (the live auth read),
    /// and the dialog service (the gate alerts). Seeded with one profile whose
    /// id the tests set active; reuses the shared fakes so the recording
    /// surfaces match the rest of the UI tests.
    /// </summary>
    private sealed class HandlerBundle
    {
        public HandlerBundle()
        {
            var summary = Profiles.WithProfile("Bundle Profile");
            ProfileId = summary.Id;
            Loader.Config = new CuratorConfig
            {
                Integrations = { Nexus = new NexusConfig() },
            };
        }

        public Guid ProfileId { get; }
        public FakeProfileService Profiles { get; } = new();
        public FakeProfileSession Session { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeConfigLoader Loader { get; } = new();
        public FakeModRepository Repo { get; } = new();
        public RecordingDownloadQueue Queue { get; } = new();

        /// <summary>Live config the handler reads on each invocation.</summary>
        public CuratorConfig Config
        {
            get => Loader.Config;
            set => Loader.Config = value;
        }

        public NxmModDownloadHandler BuildHandler() =>
            new(
                action => action(),
                Queue,
                Repo,
                Session,
                Profiles,
                Loader,
                Dialogs,
                Localization,
                NullLogger<NxmModDownloadHandler>.Instance);
    }
}
