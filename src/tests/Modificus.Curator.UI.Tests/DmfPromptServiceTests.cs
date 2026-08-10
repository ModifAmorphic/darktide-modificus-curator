using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Behaviors of the <see cref="DmfPromptService"/>: the two DMF cases (add
/// existing / download + add or browser-open), the new-profile trigger, the
/// decline path, and the deferred-prompt guarantee (the prompt does not fire
/// from inside the event handler; it waits for the Profiles page to await
/// <see cref="DmfPromptService.ProcessPendingAsync"/> after the create +
/// activation).
/// </summary>
/// <remarks>
/// All against the hand-rolled fakes in <see cref="TestDoubles"/>: the
/// <c>FakeProfileService</c> raises <c>ProfileCreated</c> from
/// <c>CreateProfile</c>; the <c>FakeDialogService</c> records every call +
/// drives the <c>ShowProgressAsync</c> work to completion.
/// </remarks>
public sealed class DmfPromptServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds a coordinator + a tuple of its fakes so each test can seed +
    /// assert on the specific dependencies it cares about.
    /// </summary>
    /// <param name="launchExternal">Optional spy for the browser-launcher seam.
    /// When omitted the builder wires a local no-op that returns <c>true</c>
    /// without recording anywhere (never the production
    /// <c>Process.Start</c> fallback, never the process-wide
    /// <c>TestLauncher</c> shared state). Tests that assert on the browser-open
    /// path pass their own per-test spy so the assertion cannot race with
    /// unrelated classes that also use <c>TestLauncher</c>.</param>
    private static (DmfPromptService Service, FakeProfileService Profiles, FakeProfileSession Session,
        FakeModRepository Repo, FakeModAcquisitionService Acquisition, FakeNexusAuthService Auth,
        FakeDialogService Dialogs) Build(
            FakeProfileService? profiles = null,
            FakeProfileSession? session = null,
            FakeModRepository? repo = null,
            FakeModAcquisitionService? acquisition = null,
            FakeNexusAuthService? auth = null,
            FakeDialogService? dialogs = null,
            FakeNxmHandlerRegistrar? nxmRegistrar = null,
            Func<Uri, bool>? launchExternal = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        dialogs ??= new FakeDialogService();
        // SAFETY: an omitted launcher seam defaults to a local no-op that
        // returns success without touching the OS shell or any shared static
        // state. Tests that assert on opens pass their own per-test spy.
        var service = new DmfPromptService(
            profiles, session, repo, acquisition, auth, dialogs,
            Localization, NullLogger<DmfPromptService>.Instance, nxmRegistrar,
            launchExternal ?? LocalNoOpLauncher);
        return (service, profiles, session, repo, acquisition, auth, dialogs);
    }

    /// <summary>
    /// A local, stateless launcher default: returns <c>true</c> (success)
    /// without recording and without touching the OS shell. Used only as the
    /// <c>Build</c> default so DMF tests that don't assert on opens never touch
    /// the process-wide <c>TestLauncher</c> shared state. Tests that assert on
    /// opens pass their own per-test spy.
    /// </summary>
    private static readonly Func<Uri, bool> LocalNoOpLauncher = _ => true;

    /// <summary>
    /// Builds a per-test recorder spy: appends every opened URI to
    /// <paramref name="record"/> and returns <c>true</c> (success). Each test
    /// owns its own list, so assertions cannot race with unrelated classes.
    /// </summary>
    private static Func<Uri, bool> NewRecordingSpy(List<Uri> record) =>
        uri =>
        {
            record.Add(uri);
            return true;
        };

    // ---- case 1: DMF in repo, not in profile -> offer add -----------------

    [Fact]
    public async Task NewProfile_case1_dmf_in_repo_not_in_profile_offers_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService();

        // Build the coordinator FIRST so its ProfileCreated subscription is in
        // place, then drive the create (which fires the signal), then process.
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // Confirm fired; one AddMod call against the existing DMF container.
        Assert.Equal(1, dialogs.ConfirmCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(created.Id, add.Id);
        Assert.Equal(dmf.Id, add.ContainerId);
    }

    [Fact]
    public async Task NewProfile_case1_decline_does_not_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService { ConfirmResult = false }; // user says No

        var (service, _, _, _, _, _, _) = Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls); // prompt did fire
        Assert.Empty(profiles.AddModCalls); // nothing added
    }

    // ---- case 2: DMF not in repo, premium -> in-app download + add ---------

    [Fact]
    public async Task NewProfile_case2_premium_user_uses_in_app_download()
    {
        // Premium users get the in-app API download (not the browser-open path).
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var dialogs = new FakeDialogService(); // ConfirmResult default = true

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // Confirm fired (the download confirm).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // Premium -> in-app API download (not the browser-open path).
        Assert.Empty(launchedUris);
        var acquireCall = Assert.Single(acquisition.LatestNexusCalls);
        Assert.Equal(DmfPromptService.DmfModId, acquireCall.ModId);
        Assert.Equal("warhammer40kdarktide", acquireCall.GameDomain);
        Assert.Single(dialogs.ProgressCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(created.Id, add.Id);
        Assert.Equal(acquisition.NextResult.ContainerId, add.ContainerId);
    }

    [Fact]
    public async Task NewProfile_case2_download_failure_alerts_and_does_not_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService
        {
            ThrowNext = new InvalidOperationException("boom"),
        };
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // The download confirm fired + the user accepted.
        Assert.Equal(1, dialogs.ConfirmCalls);
        // The acquisition was attempted.
        Assert.Single(acquisition.LatestNexusCalls);
        // A failure alert was shown.
        Assert.Single(dialogs.AlertCalls);
        // No AddMod: the download did not succeed.
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task NewProfile_case2_decline_does_not_download_or_open_browser()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var dialogs = new FakeDialogService { ConfirmResult = false }; // user says No

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        // Decline opens no browser + shows no alert.
        Assert.Empty(launchedUris);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- case 2: DMF not in repo, non-premium / no auth -> open browser ---

    [Fact]
    public async Task NewProfile_case2_non_premium_user_opens_browser_at_dmf_files_url()
    {
        // The Nexus download_link endpoint is premium-only. Non-premium users
        // must visit the site, so on a Yes the prompt opens the DMF files page
        // in the browser. The browser opens regardless of nxm handler
        // registration (the message tailors to manager-download vs. manual
        // import; the open is unconditional).
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var dialogs = new FakeDialogService(); // ConfirmResult default = true
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, registrar,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // The download confirm fired (the user accepted).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // The browser launcher was called exactly once with DMF's files URL.
        var launched = Assert.Single(launchedUris);
        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files", launched.ToString());
        // No in-app API download, no AddMod (that happens later via the nxm
        // handler), no progress spinner, no failure alert (launch succeeded).
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(dialogs.ProgressCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task NewProfile_case2_premium_state_unknown_opens_browser()
    {
        // When the verify call failed, IsPremium is null. Safer to fall back to
        // the browser-open path (a premium user just visits the site; a
        // non-premium user avoids a 403).
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "name", IsPremium: null),
        };
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, registrar,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(launchedUris);
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task NewProfile_case2_not_registered_still_opens_browser()
    {
        // Even when Curator is NOT the nxm handler, the browser opens at DMF's
        // files page (the confirm message already told the user to download the
        // archive and import it manually). No dead-end informational alert.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, registrar,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        // Browser opened regardless of registrar state.
        Assert.Single(launchedUris);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task NewProfile_case2_no_registrar_still_opens_browser()
    {
        // Same as above but with no registrar at all (unsupported platform): the
        // browser still opens.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var dialogs = new FakeDialogService();

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(launchedUris);
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task NewProfile_case2_no_auth_still_opens_browser()
    {
        // Auth NOT configured (state null): the user is not premium, so the
        // browser opens. No dead-end informational alert (the old case 3 is
        // gone); the confirm + browser-open path runs regardless of auth.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService { State = null };
        var dialogs = new FakeDialogService();

        var launchedUris = new List<Uri>();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launchExternal: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(launchedUris);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task NewProfile_case2_browser_launch_failure_alerts_with_url()
    {
        // If the OS shell-open fails (no default browser, headless), surface the
        // URL in an alert so the user can copy it manually instead of a silent
        // no-op.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        Func<Uri, bool> failingLauncher = _ => false; // shell-open failed

        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, registrar, failingLauncher);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        // One failure alert carrying the DMF URL.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(
            "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            alert.Message);
        // No in-app download.
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    // ---- DMF already in the profile -> no prompt --------------------------

    [Fact]
    public async Task NewProfile_skips_when_dmf_already_in_profile()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;
        // Seed DMF into the new profile (already added).
        profiles.WithMods(created.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 });

        await service.ProcessPendingAsync();

        // DMF is already in the profile: no prompt, no add, no alert.
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
        // No new AddMod call (the entry is already there; the test seeded it directly).
        Assert.Empty(profiles.AddModCalls);
    }

    // ---- new-profile trigger is gated on the new profile being active -----

    [Fact]
    public async Task NewProfile_skips_when_the_new_profile_did_not_become_active()
    {
        // A profile created while the game is running does NOT become active
        // (the session gates it); the new-profile trigger should not fire.
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing", "");
        var profiles = TestDoubles.Profiles(existing);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
            IsRunning = true, // gate: RequestActive is a no-op while running
        };
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Create a new profile while running; the active id stays on `existing`.
        profiles.CreateProfile("New", string.Empty, new LaunchSettings());

        await service.ProcessPendingAsync();

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- prompt does not fire synchronously inside CreateProfile -----------

    [Fact]
    public async Task ProfileCreated_does_not_synchronously_show_a_dialog()
    {
        // The signal fires synchronously from inside CreateProfile; the prompt
        // must NOT fire synchronously (that would nest a modal inside the
        // create call). It must wait for ProcessPendingAsync.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Simulate the create the Profiles page drives on Save.
        profiles.CreateProfile("New", string.Empty, new LaunchSettings());

        // No prompt fired yet (signal is pending).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- nothing pending -> no-op -----------------------------------------

    [Fact]
    public async Task ProcessPending_with_no_signals_is_a_noop()
    {
        var (service, _, _, _, _, _, dialogs) = Build();
        await service.ProcessPendingAsync();
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- trigger is consumed after processing -----------------------------

    [Fact]
    public async Task ProcessPending_consumes_signal_so_second_call_does_not_re_prompt()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();
        Assert.Equal(1, dialogs.ConfirmCalls);

        // Second call: no new signal, no re-prompt.
        await service.ProcessPendingAsync();
        Assert.Equal(1, dialogs.ConfirmCalls);
    }
}
