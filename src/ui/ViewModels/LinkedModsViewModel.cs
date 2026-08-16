using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The link-external-folder child of <see cref="ModListViewModel"/> (the
/// established <see cref="ImportWorkflowViewModel"/> pattern: an
/// application-lifetime singleton registered before the parent + exposed
/// read-only for view binding). Owns the picker-driven link flow (peek /
/// collision-check / <see cref="IModImportService.LinkFolder"/> /
/// <see cref="IProfileService.AddMod"/> loop with its modal alerts) and the
/// linked row's open-external-folder badge command; the parent keeps no
/// <see cref="IModImportService"/> dependency.
/// </summary>
/// <remarks>
/// <para><b>Link flow:</b> the Add split button's "Link external folder"
/// item reduces to <see cref="LinkModsCommand"/>, which peeks the base name
/// (validates the mod-folder shape), hard-blocks a base-name collision against
/// the active profile (refuse, create nothing, alert; excluding the container
/// a re-link would dedup to), then records the metadata-only container via
/// <see cref="IModImportService.LinkFolder"/> + adds the profile reference
/// with <see cref="ModVersionPolicy.Latest"/> (inert for linked). A failed
/// peek, a containment failure, or a collision cancels the whole remaining
/// batch (folders linked earlier in the batch stay linked). No modal card; the
/// folder is linked, not copied.</para>
/// <para><b>Reload is the parent's:</b> the child raises
/// <see cref="ModsLinked"/> when a link flow finishes, and the parent
/// reloads the active list on it (the child never touches the
/// row collection). The pending-changes flag is set here, on the session, only
/// when a path actually landed a linked mod.</para>
/// <para>No <c>ConfigureAwait(false)</c> anywhere: dialog calls + the session
/// flag stay on the captured UI context (the UI-layer convention).</para>
/// </remarks>
public partial class LinkedModsViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly IExternalLauncher _externalLauncher;
    private readonly IGamingModeState _gamingMode;
    private readonly ILogger<LinkedModsViewModel> _logger;

    public LinkedModsViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        IDialogService dialogs,
        LocalizationService localization,
        IExternalLauncher externalLauncher,
        IGamingModeState gamingMode,
        ILogger<LinkedModsViewModel> logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        _gamingMode = gamingMode ?? throw new ArgumentNullException(nameof(gamingMode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Raised when a link flow finished processing its paths. The parent
    /// reloads the active profile's rows on it; the raised order is one event
    /// per <see cref="LinkModsCommand"/> execution that got past its entry
    /// guards.
    /// </summary>
    public event EventHandler? ModsLinked;

    /// <summary>
    /// Processes a list of external folder paths from the link flow (the "Link
    /// external folder" picker), sequentially. Per path the flow mirrors the
    /// copied-import flow minus the inline workflow (the folder is linked, not
    /// copied): <b>(1)</b> peek the base folder name via
    /// <see cref="IModImportService.GetBaseName"/> (validates the mod-folder
    /// shape, throws on an invalid source); <b>(2)</b> hard-block a base-name
    /// collision against the active profile (refuse, create nothing, alert),
    /// excluding the container a re-link would dedup to (a re-link resolves to
    /// the same container, and <see cref="IProfileService.AddMod"/> is idempotent
    /// on it); <b>(3)</b> <see cref="IModImportService.LinkFolder"/> (record the
    /// metadata-only container, no copy) + <see cref="IProfileService.AddMod"/>
    /// with <see cref="ModVersionPolicy.Latest"/> (inert for linked; the external
    /// folder is the single implicit version). A failed peek, a containment /
    /// shape failure from <see cref="IModImportService.LinkFolder"/>, OR a
    /// collision cancels the whole remaining batch (folders linked earlier in the
    /// batch stay linked). Raises <see cref="ModsLinked"/> at the end (the parent
    /// reloads), whether or not anything linked.
    /// </summary>
    [RelayCommand]
    private async Task LinkMods(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid id)
        {
            _logger.LogWarning("Link flow ignored: no active profile");
            return;
        }

        // Tracks whether any path actually landed a linked mod in the profile.
        // A failed-peek or all-colliding batch links nothing, so it must not set
        // the pending-changes flag (no structural change occurred).
        var changed = false;
        foreach (var path in paths)
        {
            // (1) Peek the base folder name. The picked folder IS the base; this
            // validates the mod-folder shape (a matching <base>.mod descriptor)
            // BEFORE any container is created. An invalid source throws here;
            // catch it per path, surface an alert naming the failing source, and
            // abort the remaining batch (the cancel-aborts-batch posture).
            string baseName;
            try
            {
                baseName = _importService.GetBaseName(path);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException
                    or IOException or UnauthorizedAccessException
                    or System.IO.InvalidDataException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            // (2) Base-name collision hard-block (same rule as the inline
            // import workflow's collision check). The
            // container a re-link would dedup to is excluded: a re-link resolves
            // to the same linked container (Linked identity is the normalized
            // external path), and AddMod is idempotent on it, so it must NOT be
            // treated as a collision.
            var linkedSource = new LinkedSource { ExternalPath = path };
            var existing = _importService.FindExistingContainer(linkedSource, string.Empty);
            var collision = _profiles.GetBaseNameCollision(id, baseName, existing?.Id);
            if (collision is not null)
            {
                var conflictingName = _repo.Get(collision.ContainerId)?.Name ?? baseName;
                _logger.LogWarning(
                    "Link blocked at {Path}: base folder '{Base}' collides with existing mod '{Conflicting}' (container {Container}) on profile {Id}",
                    path, baseName, conflictingName, collision.ContainerId, id);
                await _dialogs.ShowAlertAsync(
                    _localization["Import_CollisionTitle"],
                    _localization.Format("Import_CollisionMessage", path, baseName, conflictingName));
                break;
            }

            // (3) Record the linked container (metadata only, no copy) then add
            // the profile reference with LatestPolicy (inert for linked).
            Guid containerId;
            try
            {
                containerId = _importService.LinkFolder(path);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            _profiles.AddMod(id, containerId, ModVersionPolicy.Latest);
            changed = true;
            _logger.LogInformation(
                "Linked {Mod} from {Path} (policy=Latest) onto container {Container}",
                baseName, path, containerId);
        }

        if (changed)
        {
            _session.HasPendingChanges = true;
        }

        ModsLinked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Surfaces a link-external-folder failure alert for a source path + the
    /// underlying exception, using the localized <c>Import_Failed</c> strings.
    /// Logs the exception with its stack + shows the message text to the user.
    /// (The copied local-import flow surfaces its failures inline via the
    /// workflow card; this alert path belongs only to the linked-folder flow.)
    /// </summary>
    private async Task AlertImportFailed(string path, Exception ex)
    {
        _logger.LogError(ex, "Import of {Path} failed", path);
        await _dialogs.ShowAlertAsync(
            _localization["Import_FailedTitle"],
            _localization.Format("Import_FailedMessage", path) + " " + ex.Message);
    }

    /// <summary>
    /// Opens the OS file manager at a linked row's external folder via the
    /// injected <see cref="IExternalLauncher"/>, surfacing a fallback alert on
    /// launch failure. No-op for a non-linked row, a broken row (the folder is
    /// missing), a row whose source carries no path, or while inside a Steam
    /// Deck Gaming Mode session (file-manager opens depend on a desktop shell;
    /// the disabled badge is the first gate, this is the programmatic one). The
    /// row carries state only; this command owns the launch + alert.
    /// </summary>
    [RelayCommand]
    private async Task OpenFolder(ModItemViewModel? row)
    {
        if (row is null || row.Source is not LinkedSource || row.IsExternalBroken)
        {
            return;
        }

        if (_gamingMode.IsGamingMode)
        {
            return;
        }

        var path = row.ExternalFolderPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (!_externalLauncher.OpenPath(path))
            {
                _logger.LogWarning("Opening the external folder for {Container} failed.", row.ContainerId);
                await LaunchAlerts.ShowAsync(
                    _dialogs,
                    _localization,
                    "ModList_OpenFolderFailedTitle",
                    "ModList_OpenFolderFailedMessage",
                    row.Name,
                    path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launching the external folder for {Container} threw.", row.ContainerId);
            await LaunchAlerts.ShowAsync(
                _dialogs,
                _localization,
                "ModList_OpenFolderFailedTitle",
                "ModList_OpenFolderFailedMessage",
                row.Name,
                path);
        }
    }
}

/// <summary>
/// The one shared launcher-failure alert (the files page, the Nexus Mods games
/// page, and the linked external folder all open through
/// <see cref="IExternalLauncher"/> and surface the same fallback shape on a
/// failed launch: the localized title + the formatted message carrying the
/// target so the user can reach it manually). A static helper rather than a
/// service: both callers already hold the dialog + localization dependencies,
/// and the helper owns no state.
/// </summary>
internal static class LaunchAlerts
{
    /// <summary>
    /// Shows the localized launcher-failure alert for the given title key,
    /// message key, and format args. Callers fire-and-forget or await per
    /// their call-site shape.
    /// </summary>
    public static async Task ShowAsync(
        IDialogService dialogs,
        LocalizationService localization,
        string titleKey,
        string messageKey,
        params object[] args)
    {
        await dialogs.ShowAlertAsync(
            localization[titleKey],
            localization.Format(messageKey, args));
    }
}
