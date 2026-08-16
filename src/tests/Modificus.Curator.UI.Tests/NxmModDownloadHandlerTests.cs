using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Nxm;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Exercises <see cref="NxmModDownloadHandler"/> against in-memory fakes for its
/// dependencies. Covers the Darktide-only gate, the two pre-flight gates (auth
/// configured, active profile), the happy path (acquire + AddMod with
/// LatestPolicy), the error path (acquisition failure surfaces an alert), and
/// cancellation.
/// </summary>
/// <remarks>
/// The handler's UI-thread marshaling seam (<c>invokeOnUi</c>) is injected as a
/// pass-through so the tests run without a live Avalonia Dispatcher. The
/// acquisition service is a hand-rolled fake (the real service is unit-tested
/// separately in <c>Modificus.Curator.Integrations.Tests</c>).
/// </remarks>
public sealed class NxmModDownloadHandlerTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly LocalizationService Localization = new();
    private static readonly NxmModDownloadUrl SampleUrl = new(
        "nxm://warhammer40kdarktide/mods/8/files/5820",
        "warhammer40kdarktide", ModId: 8, FileId: 5820,
        Key: "ABC", Expires: 12345L, UserId: null);

    // ---- Darktide-only gate ----------------------------------------------

    [Fact]
    public async Task HandleAsync_non_darktide_link_rejected_before_auth_profile_acquisition()
    {
        // Curator supports only Darktide. A link for another game is rejected
        // before the auth/profile/acquisition gates so nothing is attempted.
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            Session = { ActiveProfileId = ProfileId },
        };
        var handler = Build(acquisition);

        var skyrimUrl = new NxmModDownloadUrl(
            "nxm://skyrim/mods/1/files/2",
            "skyrim", ModId: 1, FileId: 2, Key: "k", Expires: 1L, UserId: null);

        await handler.HandleAsync(skyrimUrl);

        var alert = Assert.Single(acquisition.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NonDarktideTitle"], alert.Title);
        // The message names the game domain from the link.
        Assert.Contains("skyrim", alert.Message, StringComparison.Ordinal);
        Assert.Empty(acquisition.AcquireCalls);
        Assert.Empty(acquisition.Profiles.AddModCalls);
    }

    [Theory]
    [InlineData("WARHAMMER40KDARKTIDE")] // case-insensitive match still accepted
    [InlineData("warhammer40kdarktide")]
    public async Task HandleAsync_darktide_link_case_insensitive_proceeds_past_game_gate(
        string gameDomain)
    {
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            Session = { ActiveProfileId = ProfileId },
        };
        var handler = Build(acquisition);

        var url = new NxmModDownloadUrl(
            $"nxm://{gameDomain}/mods/8/files/5820",
            gameDomain, ModId: 8, FileId: 5820, Key: "ABC", Expires: 1L, UserId: null);

        await handler.HandleAsync(url);

        Assert.Single(acquisition.AcquireCalls);
        Assert.Empty(acquisition.Dialogs.AlertCalls);
    }

    // ---- auth gate ---------------------------------------------------------

    [Fact]
    public async Task HandleAsync_auth_not_configured_shows_alert_and_skips_download()
    {
        // NexusAuthMethod.None (the default) -> alert + no acquisition.
        var acquisition = new FakeAcquisitionService();
        var handler = Build(acquisition, config: AuthConfig(method: NexusAuthMethod.None));

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(acquisition.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NotConfiguredTitle"], alert.Title);
        Assert.Empty(acquisition.AcquireCalls);
        Assert.Empty(acquisition.Profiles.AddModCalls);
    }

    [Theory]
    [InlineData(NexusAuthMethod.OAuth)]
    [InlineData(NexusAuthMethod.ApiKey)]
    public async Task HandleAsync_auth_configured_proceeds_past_auth_gate(
        NexusAuthMethod method)
    {
        // With a non-None auth method + an active profile, the handler proceeds
        // to acquisition. Both OAuth and ApiKey satisfy the gate.
        var acquisition = new FakeAcquisitionService
        {
            Session = { ActiveProfileId = ProfileId },
            Config = AuthConfig(method),
        };
        var handler = Build(acquisition);

        await handler.HandleAsync(SampleUrl);

        Assert.Single(acquisition.AcquireCalls);
    }

    // ---- active-profile gate ----------------------------------------------

    [Fact]
    public async Task HandleAsync_no_active_profile_shows_alert_and_skips_download()
    {
        // Auth configured, but no active profile -> alert + no acquisition.
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            // ActiveProfileId stays null.
        };
        var handler = Build(acquisition);

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(acquisition.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_NoActiveProfileTitle"], alert.Title);
        Assert.Empty(acquisition.AcquireCalls);
        Assert.Empty(acquisition.Profiles.AddModCalls);
    }

    // ---- happy path --------------------------------------------------------

    [Fact]
    public async Task HandleAsync_happy_path_acquires_and_adds_mod_with_latest_policy()
    {
        var containerId = Guid.NewGuid();
        var versionId = "version-folder-1234";
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            Session = { ActiveProfileId = ProfileId },
            NextResult = (containerId, versionId),
        };
        var handler = Build(acquisition);

        await handler.HandleAsync(SampleUrl);

        // The acquisition was called with the URL's fields forwarded.
        var call = Assert.Single(acquisition.AcquireCalls);
        Assert.Equal(SampleUrl.Game, call.GameDomain);
        Assert.Equal(SampleUrl.ModId, call.ModId);
        Assert.Equal(SampleUrl.FileId, call.FileId);
        Assert.Equal(SampleUrl.Key, call.NxmKey);
        Assert.Equal(SampleUrl.Expires, call.NxmExpires);

        // AddMod was called once with the active profile + returned container +
        // LatestPolicy.
        var addMod = Assert.Single(acquisition.Profiles.AddModCalls);
        Assert.Equal(ProfileId, addMod.Id);
        Assert.Equal(containerId, addMod.ContainerId);
        Assert.IsType<LatestPolicy>(addMod.Policy);

        // No alert on success.
        Assert.Empty(acquisition.Dialogs.AlertCalls);
    }

    [Fact]
    public async Task HandleAsync_happy_path_acknowledges_and_reloads_the_mod_list()
    {
        // The nxm path owns the same acknowledge + reload contract as the
        // in-app installer: the persisted known-update entry for the acquired
        // container is cleared directly through the update-state store (no
        // extra API check), then the list reloads through the narrow
        // IModListRefresh seam so the new version + cleared flag show.
        var containerId = Guid.NewGuid();
        var versionId = "version-folder-1234";
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.ApiKey),
            Session = { ActiveProfileId = ProfileId },
            NextResult = (containerId, versionId),
        };
        var updateState = new FakeUpdateStateStore();
        var reloads = 0;
        var handler = new NxmModDownloadHandler(
            action => action(),
            acquisition,
            acquisition.Session,
            acquisition.Profiles,
            acquisition.Loader,
            updateState,
            new RefreshRecorder(() => reloads++),
            acquisition.Dialogs,
            Localization,
            NullLogger<NxmModDownloadHandler>.Instance);

        await handler.HandleAsync(SampleUrl);

        var acknowledge = Assert.Single(updateState.AcknowledgeCalls);
        Assert.Equal(ProfileId, acknowledge.ProfileId);
        Assert.Equal(containerId, acknowledge.ContainerId);
        Assert.Equal(1, reloads);
    }

    // ---- error path --------------------------------------------------------

    [Fact]
    public async Task HandleAsync_acquisition_failure_shows_alert_with_exception_message()
    {
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.OAuth),
            Session = { ActiveProfileId = ProfileId },
            Throw = new InvalidOperationException("rate limited"),
        };
        var handler = Build(acquisition);

        await handler.HandleAsync(SampleUrl);

        // AddMod was NOT called (the acquisition threw before registration).
        Assert.Empty(acquisition.Profiles.AddModCalls);
        var alert = Assert.Single(acquisition.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_DownloadFailedTitle"], alert.Title);
        Assert.Contains("rate limited", alert.Message);
    }

    [Fact]
    public async Task HandleAsync_addmod_failure_shows_alert()
    {
        // The acquisition succeeds, but AddMod throws (e.g. profile was deleted
        // mid-download). The handler surfaces the error.
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.OAuth),
            Session = { ActiveProfileId = ProfileId },
            Profiles = { AddModThrows = new InvalidOperationException("profile gone") },
        };
        var handler = Build(acquisition);

        await handler.HandleAsync(SampleUrl);

        var alert = Assert.Single(acquisition.Dialogs.AlertCalls);
        Assert.Equal(Localization["Nxm_DownloadFailedTitle"], alert.Title);
        Assert.Contains("profile gone", alert.Message);
    }

    // ---- cancellation ------------------------------------------------------

    [Fact]
    public async Task HandleAsync_cancellation_propagates_not_shown_as_alert()
    {
        // Cancellation surfaces as OperationCanceledException (propagated, not
        // caught by the error handler); no alert is shown.
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(NexusAuthMethod.OAuth),
            Session = { ActiveProfileId = ProfileId },
            Throw = new TaskCanceledException(),
        };
        var handler = Build(acquisition);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(SampleUrl));
        Assert.Empty(acquisition.Dialogs.AlertCalls);
    }

    // ---- UI-thread marshaling seam ----------------------------------------

    [Fact]
    public async Task HandleAsync_routes_alerts_through_the_invokeOnUi_seam()
    {
        // The invokeOnUi seam is what marshals the alert to the UI thread in
        // production. The handler must route every alert through it (not call
        // the dialog service directly).
        var invoked = false;
        var acquisition = new FakeAcquisitionService
        {
            Config = AuthConfig(method: NexusAuthMethod.None),
        };
        Task InvokeOnUi(Func<Task> action)
        {
            invoked = true;
            return action();
        }
        var handler = new NxmModDownloadHandler(
            InvokeOnUi,
            acquisition,
            acquisition.Session,
            acquisition.Profiles,
            acquisition.Loader,
            new FakeUpdateStateStore(),
            new RefreshRecorder(),
            acquisition.Dialogs,
            Localization,
            NullLogger<NxmModDownloadHandler>.Instance);

        await handler.HandleAsync(SampleUrl);

        Assert.True(invoked);
    }

    // ---- null arg ----------------------------------------------------------

    [Fact]
    public async Task HandleAsync_null_url_throws()
    {
        var handler = Build(new FakeAcquisitionService());
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Builds the handler from the bundle, wiring the invokeOnUi seam to a
    /// direct pass-through (production wires Dispatcher.UIThread.InvokeAsync).
    /// </summary>
    private static NxmModDownloadHandler Build(FakeAcquisitionService bundle, CuratorConfig? config = null)
    {
        if (config is not null)
        {
            bundle.Config = config;
        }
        return new NxmModDownloadHandler(
            action => action(),
            bundle,
            bundle.Session,
            bundle.Profiles,
            bundle.Loader,
            new FakeUpdateStateStore(),
            new RefreshRecorder(),
            bundle.Dialogs,
            Localization,
            NullLogger<NxmModDownloadHandler>.Instance);
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
}

/// <summary>
/// A counting <see cref="IModListRefresh"/> double: records Reload calls via
/// the optional callback so a test can assert the handler reloaded the list.
/// </summary>
internal sealed class RefreshRecorder : IModListRefresh
{
    private readonly Action? _onReload;

    public RefreshRecorder(Action? onReload = null) => _onReload = onReload;

    public int Reloads { get; private set; }

    public void Reload()
    {
        Reloads++;
        _onReload?.Invoke();
    }
}

/// <summary>
/// A bundle of the handler's in-memory dependencies that records the
/// interactions. Owns the fakes the handler resolves: the acquisition service
/// (itself), the profile session, the profile service, the config loader, and
/// the dialog service. Reuses the existing <see cref="FakeProfileService"/> +
/// <see cref="FakeProfileSession"/> + <see cref="FakeDialogService"/> +
/// <see cref="FakeConfigLoader"/> from this test project so the recording
/// surfaces (AddModCalls, AlertCalls) match the rest of the VM tests.
/// </summary>
internal sealed class FakeAcquisitionService : IModAcquisitionService
{
    public FakeAcquisitionService()
    {
        // Default config: auth None (the gate blocks by default). Tests override
        // via Config to drive the happy path / active-profile checks.
        Loader.Config = new CuratorConfig
        {
            Integrations = { Nexus = new NexusConfig() },
        };
    }

    public FakeProfileService Profiles { get; } = new(Array.Empty<ProfileSummary>());
    public FakeProfileSession Session { get; } = new();
    public FakeDialogService Dialogs { get; } = new();
    public FakeConfigLoader Loader { get; } = new();

    /// <summary>Live config the handler reads on each invocation.</summary>
    public CuratorConfig Config
    {
        get => Loader.Config;
        set => Loader.Config = value;
    }

    public (Guid ContainerId, string VersionId) NextResult { get; set; } =
        (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
    public Exception? Throw { get; set; }

    public List<(string GameDomain, int ModId, int FileId, string? NxmKey, long? NxmExpires)> AcquireCalls { get; } = new();

    public Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        AcquireCalls.Add((gameDomain, modId, fileId, nxmKey, nxmExpires));
        if (Throw is not null)
        {
            return Task.FromException<(Guid, string)>(Throw);
        }
        return Task.FromResult(NextResult);
    }

    // Not exercised by the nxm download handler (it routes the per-file id from
    // the nxm URL through AcquireFromNexusAsync). The mod-list VM's UpdateCommand
    // uses AcquireLatestNexusAsync; its tests use a separate fake.
    public Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<long>? progress = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
