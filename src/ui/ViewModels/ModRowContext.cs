using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The one shared observable context for the row-affecting global mod-update
/// state: whether the Nexus account is Premium (read once at construction),
/// whether any install is in flight (the installer's coordinator-gated busy
/// flag), and whether the app runs inside a Steam Deck Gaming Mode session
/// (constant for the process lifetime). The list VM creates/owns one per
/// application lifetime and passes it to every <see cref="ModItemViewModel"/>
/// once; rows read their derived state off it instead of receiving per-flag
/// value pushes, so a new row-affecting global is one context member rather
/// than another push path.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> the installer's events fire on the acquiring or
/// installing thread, so the busy mirror + the progress re-raise are marshaled
/// to the UI thread through the injected seam (the established
/// <c>Action&lt;Action&gt;</c> pattern). Consumers observe UI-thread
/// notifications only.</para>
/// <para><b>Premium is read once:</b> a construction-time fire-and-forget read
/// of <see cref="INexusAuthService.GetCurrentStateAsync"/>; on failure the
/// flag stays false (a restart re-reads). No mid-session refresh by design
/// (re-checking on every surface would burn API calls; a user signing in
/// mid-session restarts for the install behavior to change).</para>
/// <para><b>The install front:</b> <see cref="InstallLatestAsync"/> delegates
/// to the shared <see cref="IModUpdateInstaller"/> (the single install path
/// whose busy + progress state this context already carries), so the list VM's
/// manual update action + its row-rendered state share one seam instead of the
/// VM holding the installer solely to forward one call.</para>
/// </remarks>
public partial class ModRowContext : ObservableObject
{
    private readonly IModUpdateInstaller _installer;
    private readonly Action<Action> _invokeOnUi;
    private readonly ILogger<ModRowContext> _logger;

    /// <param name="auth">The Nexus auth service; read once at construction
    /// for the Premium flag (fire-and-forget; no mid-session refresh).</param>
    /// <param name="installer">The shared install path: its busy flag drives
    /// <see cref="AnyRowUpdating"/>, its per-container progress is re-raised
    /// (marshaled), and <see cref="InstallLatestAsync"/> delegates to it.</param>
    /// <param name="gamingMode">Whether the app runs inside a Steam Deck Gaming
    /// Mode session (constant for the process lifetime).</param>
    /// <param name="invokeOnUi">The UI-thread marshal seam for the installer's
    /// off-thread events.</param>
    /// <param name="logger">Structured logger for the premium-read failure.</param>
    public ModRowContext(
        INexusAuthService auth,
        IModUpdateInstaller installer,
        IGamingModeState gamingMode,
        Action<Action> invokeOnUi,
        ILogger<ModRowContext> logger)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(gamingMode);

        IsGamingMode = gamingMode.IsGamingMode;
        _installer.BusyChanged += OnInstallerBusyChanged;
        _installer.ModUpdateProgress += OnModUpdateProgress;

        _ = LoadPremiumStateAsync(auth);
    }

    /// <summary>
    /// Whether the app runs inside a Steam Deck Gaming Mode session (fixed for
    /// the process lifetime). Constant; rows + the list VM read it directly.
    /// </summary>
    public bool IsGamingMode { get; }

    /// <summary>
    /// Whether the Nexus account was verified Premium. Read once at
    /// construction; false until the read lands (or on a read failure; a
    /// restart re-reads). Drives the per-row update action's click behavior
    /// (Premium -> in-app install; regular/unknown -> open the Nexus files
    /// page). Publicly settable so the async read (and only it) lands the
    /// value.
    /// </summary>
    [ObservableProperty]
    private bool _isPremiumUser;

    /// <summary>
    /// Whether the mod-update installer reports an install in flight (manual
    /// or automatic; the coordinator-backed busy flag), mirrored from
    /// <see cref="IModUpdateInstaller.BusyChanged"/> on the UI thread. Drives
    /// the per-row Premium update action's enabled state (the global
    /// "one install at a time" coordination).
    /// </summary>
    [ObservableProperty]
    private bool _anyRowUpdating;

    /// <summary>
    /// The installer's per-install progress (a container's install attempt
    /// started or finished, for BOTH the manual Premium path and the automatic
    /// batch), re-raised on the UI thread. The list VM finds the row by
    /// container id and drives its spinner; an event for a row no longer
    /// present is the VM's to ignore.
    /// </summary>
    public event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <summary>
    /// Reads the Nexus premium state once (fire-and-forget from the
    /// constructor). On success flips <see cref="IsPremiumUser"/>; on failure
    /// logs + leaves it false.
    /// </summary>
    private async Task LoadPremiumStateAsync(INexusAuthService auth)
    {
        try
        {
            var state = await auth.GetCurrentStateAsync();
            IsPremiumUser = state?.IsPremium == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Nexus premium state read failed; per-row update actions stay regular-tier until restart.");
        }
    }

    private void OnInstallerBusyChanged(object? sender, EventArgs e) =>
        _invokeOnUi(() => AnyRowUpdating = _installer.IsBusy);

    private void OnModUpdateProgress(object? sender, ModUpdateProgressEventArgs e) =>
        _invokeOnUi(() => ModUpdateProgress?.Invoke(this, e));

    /// <summary>
    /// The manual Premium install front: delegates to
    /// <see cref="IModUpdateInstaller.TryInstallLatestAsync"/> (the shared
    /// install path: coordinator-gated one-install-at-a-time, in-gate
    /// eligibility revalidation, acknowledge-on-success). The busy + progress
    /// sides of the same installer arrive through this context's observables,
    /// so the install call lives beside the state it produces.
    /// </summary>
    public Task<ModInstallOutcome> InstallLatestAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates) =>
        _installer.TryInstallLatestAsync(profileId, containerId, modId, expectedVersion, candidates);
}
