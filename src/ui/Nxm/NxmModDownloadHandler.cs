using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Nxm;

/// <summary>
/// The real <see cref="INxmModDownloadHandler"/>: the enqueue adapter in front
/// of <see cref="IModDownloadQueue"/>. Replaces the no-op default via DI "last
/// registration wins" (registered AFTER <c>AddNxm()</c> in
/// <see cref="CuratorComposition"/>). Receives a parsed
/// <see cref="NxmModDownloadUrl"/> (the result of clicking "Mod manager
/// download" on a Nexus file page, relayed by the handler exe + IPC router),
/// gates it, peeks the repository for a row name, enqueues the download, and
/// returns within milliseconds. The queue owns the acquisition, the profile
/// registration, the acknowledge, and the reload.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three gates stay here.</b> Game domain, auth (live config read), and
/// active profile are checked before anything is enqueued; a gated refusal
/// surfaces the same modal alerts as before, because at gate time there is no
/// download row to host the failure on. Everything after the gates belongs to
/// the queue, whose failures render inline on the row.</para>
/// <para>
/// <b>Prompt return.</b> <see cref="HandleAsync"/> performs no acquisition and
/// no profile write: the passing path is an in-memory peek plus an enqueue, so
/// the IPC accept loop is freed immediately (spec 02's invariant that enqueue
/// order equals click order). The cancellation token is accepted for the
/// interface contract but unused: the queue owns each item's cancellation once
/// the request is admitted.</para>
/// <para>
/// <b>Naming at enqueue.</b> A repository peek (by Nexus mod id) supplies the
/// container id and the stored name; a miss falls back to the localized
/// "Nexus mod #&lt;id&gt;" format. No prefetch API call: a queued item that has
/// not started shows the peek name or the fallback, and the queue swaps in the
/// resolved name once the acquisition lands.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> The adapter coordinates UI concerns: it
/// reads the active profile from <see cref="IProfileSession"/> (the single
/// active-profile authority, in UI), shows gate dialogs through
/// <see cref="IDialogService"/> (UI), and marshals those dialogs to the UI
/// thread via the injected seam (production wires
/// <see cref="Avalonia.Threading.Dispatcher.UIThread"/>; tests inject a
/// pass-through).</para>
/// </remarks>
internal sealed class NxmModDownloadHandler : INxmModDownloadHandler
{
    /// <summary>
    /// The marshaling seam: runs the supplied async operation on the UI thread.
    /// Production wires <see cref="Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Func{Task})"/>;
    /// tests inject a pass-through.
    /// </summary>
    private readonly Func<Func<Task>, Task> _invokeOnUi;

    private readonly IModDownloadQueue _queue;
    private readonly IModRepository _repo;
    private readonly IProfileSession _session;
    private readonly IProfileService _profiles;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<NxmModDownloadHandler> _logger;

    public NxmModDownloadHandler(
        Func<Func<Task>, Task> invokeOnUi,
        IModDownloadQueue queue,
        IModRepository repo,
        IProfileSession session,
        IProfileService profiles,
        IConfigLoader configLoader,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<NxmModDownloadHandler> logger)
    {
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Gate refusals route through <see cref="ShowAlertAsync"/>, which marshals
    /// to the UI thread and waits for the user's OK (unchanged behavior). The
    /// passing path enqueues and returns promptly.
    /// </remarks>
    public async Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // 0. Darktide-only: Curator supports only Warhammer 40,000: Darktide
        //    Nexus downloads. Reject any other game before auth / profile /
        //    enqueue so the user gets a clear reason and nothing is queued.
        //    Case-insensitive domain match.
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

        // 2. Active-profile check (the single authority). The queue captures
        //    this id as the download's target profile.
        var profileId = _session.ActiveProfileId;
        if (profileId is null)
        {
            _logger.LogWarning("nxm download refused: no active profile.");
            await ShowAlertAsync(
                _localization["Nxm_NoActiveProfileTitle"],
                _localization["Nxm_NoActiveProfileMessage"]);
            return;
        }

        // 3. Peek + enqueue. An in-memory repository lookup names the row (the
        //    container's stored name, or the localized numeric fallback) and
        //    carries the container id; the profile read supplies the target
        //    name captured at enqueue. No acquisition, no AddMod, no reload:
        //    the queue owns those.
        try
        {
            var profileName = _profiles.GetProfile(profileId.Value).Name;
            var existing = _repo.FindBySource(new NexusSource { ModId = url.ModId });
            var displayName = existing is null
                ? _localization.Format("Nxm_ModNameFallback", url.ModId)
                : existing.Name;

            _queue.Enqueue(new ModDownloadRequest(
                url.Game, url.ModId, url.FileId, DownloadPurpose.ProfileAdd,
                existing?.Id, displayName, profileId.Value, profileName,
                url.Key, url.Expires));

            _logger.LogInformation(
                "Enqueued the nxm download of mod {Mod} file {File} for profile {Profile}.",
                url.ModId, url.FileId, profileId.Value);
        }
        catch (Exception ex)
        {
            // A profile that vanished between the session read and here, or a
            // queue admission failure: nothing was enqueued, so the failure is
            // still gate-shaped (no row exists to host it).
            _logger.LogError(ex,
                "Failed to enqueue the nxm download of mod {Mod} file {File}.",
                url.ModId, url.FileId);
            await ShowAlertAsync(_localization["Nxm_DownloadFailedTitle"], ex.Message);
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
