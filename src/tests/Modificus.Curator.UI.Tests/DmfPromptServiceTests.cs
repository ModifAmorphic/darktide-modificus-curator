using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Behaviors of the <see cref="DmfPromptService"/>: the two DMF cases (add
/// existing / enqueued premium download or browser-open), the new-profile
/// trigger, the decline path, the deferred-prompt guarantee (the prompt does
/// not fire from inside the event handler; it waits for the shell's modal
/// queue to drain on the next real navigation into Mods), and the drained
/// entry's post-prompt mod-list reload.
/// </summary>
/// <remarks>
/// All against the hand-rolled fakes in <see cref="TestDoubles"/>: the
/// <c>FakeProfileService</c> raises <c>ProfileCreated</c> from
/// <c>CreateProfile</c>; the <c>FakeDialogService</c> records every call. The
/// premium branch's downstream surface (the enqueue) is asserted through the
/// <see cref="RecordingDownloadQueue"/> double.
/// </remarks>
public sealed class DmfPromptServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds a coordinator + a tuple of its fakes so each test can seed +
    /// assert on the specific dependencies it cares about.
    /// </summary>
    /// <param name="launcher">Optional spy for the external launcher. When
    /// omitted the builder wires a fresh per-test
    /// <see cref="FakeExternalLauncher"/> (in-memory, never the OS shell). Tests
    /// that assert on the browser-open path pass their own per-test spy so the
    /// assertion cannot race with unrelated classes.</param>
    /// <param name="nxmRegistration">The shared registration state the
    /// download-confirm wording follows (last-known; the prompt never probes
    /// the OS).</param>
    /// <param name="gamingMode">The Gaming Mode state. When omitted, a
    /// non-gaming session (the ordinary desktop flow).</param>
    private static (DmfPromptService Service, ShellModalQueue Queue, RefreshRecorder Refresh,
        FakeProfileService Profiles, FakeProfileSession Session,
        FakeModRepository Repo, FakeModAcquisitionService Acquisition, RecordingDownloadQueue Downloads,
        FakeNexusAuthService Auth, FakeDialogService Dialogs) Build(
            FakeProfileService? profiles = null,
            FakeProfileSession? session = null,
            FakeModRepository? repo = null,
            FakeModAcquisitionService? acquisition = null,
            FakeNexusAuthService? auth = null,
            FakeDialogService? dialogs = null,
            FakeNxmRegistrationState? nxmRegistration = null,
            GamingModeState? gamingMode = null,
            FakeExternalLauncher? launcher = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        dialogs ??= new FakeDialogService();
        nxmRegistration ??= new FakeNxmRegistrationState();
        gamingMode ??= new GamingModeState(false);
        // SAFETY: an omitted launcher defaults to a fresh in-memory fake that
        // never touches the OS shell. Tests that assert on opens pass their own
        // per-test spy.
        launcher ??= new FakeExternalLauncher();
        var queue = new ShellModalQueue();
        var refresh = new RefreshRecorder();
        var downloads = new RecordingDownloadQueue();
        var service = new DmfPromptService(
            profiles, session, repo, acquisition, downloads, auth, dialogs,
            Localization, NullLogger<DmfPromptService>.Instance, nxmRegistration,
            gamingMode,
            launcher,
            queue,
            refresh);
        return (service, queue, refresh, profiles, session, repo, acquisition, downloads, auth, dialogs);
    }

    /// <summary>
    /// A per-test recording launcher: appends every opened URI to
    /// <paramref name="record"/> and succeeds. Each test owns its own list, so
    /// assertions cannot race with unrelated classes.
    /// </summary>
    private static FakeExternalLauncher NewRecordingSpy(List<Uri> record) =>
        FakeExternalLauncher.RecordingUris(record);

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
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

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

        var (service, queue, _, _, _, _, _, _, _, _) = Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(1, dialogs.ConfirmCalls); // prompt did fire
        Assert.Empty(profiles.AddModCalls); // nothing added
    }

    // ---- case 2: DMF not in repo, premium -> enqueued download -------------

    [Fact]
    public async Task NewProfile_case2_premium_user_enqueues_the_download()
    {
        // Premium users get the download enqueued onto the shared queue (not
        // the browser-open path, not a modal spinner): the head file is
        // resolved first, then exactly one ProfileAdd item targets the new
        // profile; the queue's completion owns the add + reload.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService { NextResolve = (5820, "1.1") };
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var dialogs = new FakeDialogService(); // ConfirmResult default = true

        var launchedUris = new List<Uri>();
        var (service, queue, _, _, _, _, _, downloads, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        // Confirm fired (the download confirm).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // Premium -> enqueue (not the browser-open path).
        Assert.Empty(launchedUris);
        // The head file was resolved once (one listing call, no download).
        var resolve = Assert.Single(acquisition.ResolveLatestCalls);
        Assert.Equal(DmfPromptService.DmfModId, resolve.ModId);
        Assert.Equal("warhammer40kdarktide", resolve.GameDomain);
        // Exactly one ProfileAdd request carrying the resolved file + the
        // just-created active profile; no nxm key (the auth-only premium path).
        var request = Assert.Single(downloads.Requests);
        Assert.Equal("warhammer40kdarktide", request.GameDomain);
        Assert.Equal(DmfPromptService.DmfModId, request.ModId);
        Assert.Equal(5820, request.FileId);
        Assert.Equal(DownloadPurpose.ProfileAdd, request.Purpose);
        Assert.Null(request.ContainerId);
        Assert.Null(request.NxmKey);
        Assert.Null(request.ExpectedVersion);
        Assert.Equal(created.Id, request.TargetProfileId);
        Assert.Equal("New", request.TargetProfileName);
        Assert.Equal("Darktide Mod Framework", request.DisplayName);
        // No direct acquisition call, no spinner, no prompt-owned add: the
        // queue's completion owns those.
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(dialogs.ProgressCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task NewProfile_case2_resolve_failure_alerts_and_enqueues_nothing()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService
        {
            ThrowOnResolve = new InvalidOperationException("boom"),
        };
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var dialogs = new FakeDialogService();

        var (service, queue, _, _, _, _, _, downloads, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        // The download confirm fired + the user accepted; the resolve was
        // attempted + failed (API down, no MAIN files).
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(acquisition.ResolveLatestCalls);
        // The same localized failure alert the in-flight download failure used.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Dmf_DownloadFailedTitle"], alert.Title);
        Assert.Equal(Localization.Format("Dmf_DownloadFailedMessage", "boom"), alert.Message);
        // Nothing was enqueued (no row exists to host the failure) + no add.
        Assert.Empty(downloads.Requests);
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
        var (service, queue, _, _, _, _, _, downloads, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        // Decline resolves nothing + enqueues nothing.
        Assert.Empty(acquisition.ResolveLatestCalls);
        Assert.Empty(downloads.Requests);
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
        // Registered last-known state: the manager-download wording, read
        // without probing the OS.
        var nxmRegistration = new FakeNxmRegistrationState { IsAvailable = true, IsRegistered = true };

        var launchedUris = new List<Uri>();
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, nxmRegistration,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        // The download confirm fired (the user accepted) with the
        // manager-download guidance; zero OS probes back it.
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(Localization["Dmf_DownloadMessage"], dialogs.LastConfirmMessage);
        Assert.Equal(0, nxmRegistration.RefreshFromOsCalls);
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
        var nxmRegistration = new FakeNxmRegistrationState { IsAvailable = true, IsRegistered = true };

        var launchedUris = new List<Uri>();
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, nxmRegistration,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

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
        // Not-registered last-known state: the manual-import wording, read
        // without probing the OS.
        var nxmRegistration = new FakeNxmRegistrationState { IsAvailable = true, IsRegistered = false };

        var launchedUris = new List<Uri>();
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, nxmRegistration,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(Localization["Dmf_DownloadMessageManual"], dialogs.LastConfirmMessage);
        Assert.Equal(0, nxmRegistration.RefreshFromOsCalls);
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
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

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
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs,
                launcher: NewRecordingSpy(launchedUris));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

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
        var nxmRegistration = new FakeNxmRegistrationState { IsAvailable = true, IsRegistered = true };

        var failingLauncher = new FakeExternalLauncher { OpenUriResult = _ => false }; // shell-open failed

        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, dialogs, nxmRegistration,
                launcher: failingLauncher);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(1, dialogs.ConfirmCalls);
        // One failure alert carrying the DMF URL.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(
            "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            alert.Message);
        // No in-app download.
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    // ---- case 2: Gaming Mode (Steam Deck) ---------------------------------

    /// <summary>
    /// Drives the case-2 prompt once (DMF not in the repo, new active profile)
    /// under the supplied Gaming Mode + auth states, returning the fakes the
    /// guidance assertions read (plus the created profile + the download
    /// queue for the premium branch's enqueue asserts).
    /// </summary>
    private static async Task<(FakeDialogService Dialogs, FakeModAcquisitionService Acquisition,
        FakeProfileService Profiles, List<Uri> Launched, RecordingDownloadQueue Downloads,
        Profile Created)> RunCase2Async(
            FakeNexusAuthService auth, GamingModeState gamingMode)
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService();
        var dialogs = new FakeDialogService();
        var launched = new List<Uri>();

        var (service, queue, _, _, _, _, _, downloads, _, _) = Build(
            profiles, session, repo, acquisition, auth, dialogs,
            gamingMode: gamingMode,
            launcher: NewRecordingSpy(launched));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);
        return (dialogs, acquisition, profiles, launched, downloads, created);
    }

    [Fact]
    public async Task Gaming_case2_regular_user_gets_guidance_instead_of_the_browser()
    {
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };

        var (dialogs, acquisition, profiles, launched, _, _) =
            await RunCase2Async(auth, new GamingModeState(true));

        // An informational guidance alert (not a Yes/No confirm): there is no
        // action that could run inside Gaming Mode to confirm.
        Assert.Equal(0, dialogs.ConfirmCalls);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Dmf_DownloadTitle"], alert.Title);
        Assert.Equal(Localization["Dmf_DownloadMessageGamingMode"], alert.Message);
        // No browser launch, no acquisition, no add, no spinner.
        Assert.Empty(launched);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(dialogs.ProgressCalls);
    }

    [Fact]
    public async Task Gaming_case2_unverified_premium_state_gets_guidance()
    {
        // IsPremium null (the verify call failed): treated as not premium, so
        // the guidance alert (not the in-app download, which would 403).
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "name", IsPremium: null),
        };

        var (dialogs, acquisition, _, launched, _, _) =
            await RunCase2Async(auth, new GamingModeState(true));

        Assert.Equal(0, dialogs.ConfirmCalls);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Dmf_DownloadMessageGamingMode"], alert.Message);
        Assert.Empty(launched);
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task Gaming_case2_no_auth_gets_guidance()
    {
        // Not signed in at all (state null): same guidance as regular users.
        var auth = new FakeNexusAuthService { State = null };

        var (dialogs, acquisition, _, launched, _, _) =
            await RunCase2Async(auth, new GamingModeState(true));

        Assert.Equal(0, dialogs.ConfirmCalls);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Dmf_DownloadMessageGamingMode"], alert.Message);
        Assert.Empty(launched);
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task Gaming_case2_premium_user_keeps_the_enqueued_download()
    {
        // The in-app download works in Gaming Mode, so Premium users get the
        // ordinary confirm + enqueued download there, with no guidance alert.
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };

        var (dialogs, acquisition, profiles, launched, downloads, created) =
            await RunCase2Async(auth, new GamingModeState(true));

        // The ordinary download confirm fired and was accepted (the wording
        // still follows the shared last-known handler state, unchanged by the
        // gaming gate).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // The head file was resolved + the download enqueued for the new
        // profile (the row owns progress from here; no browser is touched).
        var resolve = Assert.Single(acquisition.ResolveLatestCalls);
        Assert.Equal(DmfPromptService.DmfModId, resolve.ModId);
        var request = Assert.Single(downloads.Requests);
        Assert.Equal(DownloadPurpose.ProfileAdd, request.Purpose);
        Assert.Equal(created.Id, request.TargetProfileId);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(dialogs.ProgressCalls);
        // No browser, no guidance alert.
        Assert.Empty(launched);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task Gaming_case1_dmf_in_repo_still_offers_the_instant_add()
    {
        // Gaming Mode gates only the browser branch: DMF already in the repo
        // adds instantly on confirm regardless of the session type.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService();

        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs,
                gamingMode: new GamingModeState(true));

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(Localization["Dmf_AddMessage"], dialogs.LastConfirmMessage);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(dmf.Id, add.ContainerId);
        Assert.Empty(dialogs.AlertCalls);
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

        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;
        // Seed DMF into the new profile (already added).
        profiles.WithMods(created.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 });

        await queue.DrainAsync(ShellDestination.Mods);

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

        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Create a new profile while running; the active id stays on `existing`.
        profiles.CreateProfile("New", string.Empty, new LaunchSettings());

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- prompt does not fire synchronously inside CreateProfile -----------

    [Fact]
    public async Task ProfileCreated_does_not_synchronously_show_a_dialog()
    {
        // The signal fires synchronously from inside CreateProfile; the prompt
        // must NOT fire synchronously (that would nest a modal inside the
        // create call). It must wait for the modal queue's drain.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Simulate the create the Profiles page drives on Save.
        profiles.CreateProfile("New", string.Empty, new LaunchSettings());

        // No prompt fired yet (signal is pending).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- nothing pending -> no-op -----------------------------------------

    [Fact]
    public async Task Drain_with_no_enqueued_prompt_is_a_noop()
    {
        var (service, queue, refresh, _, _, _, _, _, _, dialogs) = Build();
        await queue.DrainAsync(ShellDestination.Mods);

        // No trigger -> no prompt + no reload (nothing was drained).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
        Assert.Equal(0, refresh.Reloads);
    }

    [Fact]
    public async Task Drain_runs_and_reloads_even_when_the_prompt_body_skips()
    {
        // A drained entry runs regardless of whether the prompt actually fires
        // (DMF already in the profile -> no confirm -> the post-prompt reload
        // still runs, matching the shell's former consumed-trigger reload).
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, queue, refresh, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);
        // Seed DMF in the repo + in the (about-to-be-created) profile's list:
        // create the profile first, then add DMF to it.
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;
        profiles.WithMods(created.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 });

        await queue.DrainAsync(ShellDestination.Mods);

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(1, refresh.Reloads);
    }

    // ---- trigger is consumed after processing -----------------------------

    [Fact]
    public async Task Drain_consumes_the_entry_so_a_second_drain_does_not_re_prompt()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, queue, _, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New", string.Empty, new LaunchSettings());
        session.ActiveProfileId = created.Id;

        await queue.DrainAsync(ShellDestination.Mods);
        Assert.Equal(1, dialogs.ConfirmCalls);

        // Second call: no new signal, no re-prompt.
        await queue.DrainAsync(ShellDestination.Mods);
        Assert.Equal(1, dialogs.ConfirmCalls);
    }
}
