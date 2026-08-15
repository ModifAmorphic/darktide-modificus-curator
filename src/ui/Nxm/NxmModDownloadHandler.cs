using Avalonia.Threading;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Nxm;

/// <summary>
/// The real <see cref="INxmModDownloadHandler"/>. Replaces the
/// no-op default via DI "last registration wins" (registered AFTER
/// <c>AddNxm()</c> in <see cref="CuratorComposition"/>). Receives a parsed
/// <see cref="NxmModDownloadUrl"/> (the result of clicking "Mod manager
/// download" on a Nexus file page, relayed by the handler exe + IPC
/// router), checks auth + active profile, calls the acquisition service to
/// download + import the mod, and registers it in the active profile. On any
/// failure, surfaces an error dialog via <see cref="IDialogService.ShowAlertAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lives in the UI assembly, not Integrations.</b> The handler coordinates UI
/// concerns: it reads the active profile from <see cref="IProfileSession"/> (the
/// single active-profile authority, in UI), shows error dialogs through
/// <see cref="IDialogService"/> (UI), and marshals those dialogs to the UI thread
/// via <see cref="Dispatcher.UIThread"/> (Avalonia). The reusable, backend-only
/// <see cref="IModAcquisitionService"/> lives in Integrations; this handler is the
/// thin UI-coordinating shell over it. Placing the handler in Integrations would
/// create a dependency cycle (Integrations cannot reference the UI assembly, which
/// is its consumer).</para>
/// <para>
/// <b>Auth is required.</b> <c>NexusAuthMethod != None</c> gates every download
/// (per the operator's "all downloads require login"). The nxm key/expires in the
/// URL is the per-file token for the free-user download endpoint, NOT a substitute
/// for auth. The handler reads the live config on each invocation so a user
/// configuring Nexus from the Integrations dialog mid-session does not need to
/// restart.</para>
/// <para>
/// <b>Active profile is required.</b> No active profile means nowhere to register
/// the mod, so the download does not proceed (a downloaded mod with nowhere to
/// land is a poor UX).</para>
/// <para>
/// <b>UI-thread marshaling.</b> The handler runs on the IPC server's background
/// task, but <see cref="IDialogService.ShowAlertAsync"/> shows an Avalonia window
/// (the main window owns it), which must happen on the UI thread. The
/// <see cref="InvokeOnUi"/> seam marshals the call; production wires it to
/// <see cref="Dispatcher.UIThread.InvokeAsync(Func{Task})"/>, tests inject a
/// pass-through so the handler is unit-testable without a live Dispatcher.</para>
/// <para>
/// <b>Policy on AddMod.</b> The handler registers the mod with
/// <see cref="ModVersionPolicy.Latest"/> (the newest downloaded version
/// auto-tracks). The user can pin later via Track B's existing per-mod pin
/// dropdown. This matches locally-imported mods (Track B also uses Latest for new
/// mods).</para>
/// </remarks>
internal sealed class NxmModDownloadHandler : INxmModDownloadHandler
{
    /// <summary>
    /// The marshaling seam: runs the supplied async operation on the UI thread.
    /// Production wires <see cref="Dispatcher.UIThread.InvokeAsync(Func{Task})"/>;
    /// tests inject a pass-through.
    /// </summary>
    private readonly Func<Func<Task>, Task> _invokeOnUi;

    private readonly IModAcquisitionService _acquisition;
    private readonly IProfileSession _session;
    private readonly IProfileService _profileService;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly Action<Guid>? _refreshModList;
    private readonly ILogger<NxmModDownloadHandler> _logger;

    /// <summary>
    /// The Darktide Nexus game domain. Curator supports only Darktide; any
    /// <c>nxm://</c> link for another game is rejected before auth / profile /
    /// acquisition with a localized alert.
    /// </summary>

    public NxmModDownloadHandler(
        Func<Func<Task>, Task> invokeOnUi,
        IModAcquisitionService acquisition,
        IProfileSession session,
        IProfileService profileService,
        IConfigLoader configLoader,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<NxmModDownloadHandler> logger,
        Action<Guid>? refreshModList = null)
    {
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refreshModList = refreshModList;
    }

    /// <inheritdoc />
    /// <remarks>
    /// All gating + acquisition errors route through <see cref="ShowAlertAsync"/>,
    /// which marshals to the UI thread. Cancellation propagates as
    /// <see cref="OperationCanceledException"/> (not surfaced as an error dialog:
    /// the user, or a shutdown, caused it).
    /// </remarks>
    public async Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // 0. Darktide-only: Curator supports only Warhammer 40,000: Darktide
        //    Nexus downloads. Reject any other game before auth / profile /
        //    acquisition so the user gets a clear reason and we do not attempt
        //    a download that cannot land. Case-insensitive domain match.
        if (!string.Equals(url.Game, NexusGameIdentity.DarktideDomain, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "nxm download refused: link is for game '{Game}'; Curator only handles Darktide.",
                url.Game);
            await ShowAlertAsync(
                _localization["Nxm_NonDarktideTitle"],
                _localization.Format("Nxm_NonDarktideMessage", url.Game));
            return;
        }

        // 1. Auth check (live config read so a mid-session sign-in takes effect).
        var nexus = _configLoader.Load().Integrations.Nexus;
        if (nexus.AuthMethod == NexusAuthMethod.None)
        {
            _logger.LogWarning("nxm download refused: Nexus auth not configured.");
            await ShowAlertAsync(
                _localization["Nxm_NotConfiguredTitle"],
                _localization["Nxm_NotConfiguredMessage"]);
            return;
        }

        // 2. Active-profile check (the single authority).
        var profileId = _session.ActiveProfileId;
        if (profileId is null)
        {
            _logger.LogWarning("nxm download refused: no active profile.");
            await ShowAlertAsync(
                _localization["Nxm_NoActiveProfileTitle"],
                _localization["Nxm_NoActiveProfileMessage"]);
            return;
        }

        // 3. Acquire + register. Any failure surfaces a single error dialog with
        //    the exception message; cancellation propagates.
        try
        {
            var (containerId, versionId) = await _acquisition.AcquireFromNexusAsync(
                url.Game, url.ModId, url.FileId, url.Key, url.Expires, ct: ct);

            _profileService.AddMod(profileId.Value, containerId, ModVersionPolicy.Latest);

            // Refresh the mod list on the UI thread so the newly-added (or
            // reinstalled) mod appears immediately without a profile switch.
            // The container id is forwarded so the refresh can also acknowledge
            // the install: clear the persisted known-update entry for this
            // container immediately (no extra API check), so the stale
            // known-update state (recorded before the version change) does not
            // re-apply the flag on the reload.
            if (_refreshModList is not null)
            {
                await _invokeOnUi(() => { _refreshModList(containerId); return Task.CompletedTask; });
            }

            _logger.LogInformation(
                "Acquired Nexus mod {Mod} file {File} into profile {Profile} (container {Container}, version {Version}).",
                url.ModId, url.FileId, profileId.Value, containerId, versionId);
        }
        catch (OperationCanceledException)
        {
            // Propagate: cancellation is expected (shutdown / user-driven), not
            // an error to surface as a dialog.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to acquire Nexus mod {Mod} file {File}.", url.ModId, url.FileId);
            await ShowAlertAsync(
                _localization["Nxm_DownloadFailedTitle"], ex.Message);
        }
    }

    /// <summary>
    /// Marshals the alert to the UI thread then shows it. Fire-and-forget (an OK
    /// button only, no return value the handler branches on).
    /// </summary>
    private async Task ShowAlertAsync(string title, string message)
    {
        await _invokeOnUi(() => _dialogs.ShowAlertAsync(title, message));
    }
}
