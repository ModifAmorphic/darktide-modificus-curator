using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The one shared observable context for the row-affecting global mod-update
/// state: whether the Nexus account is Premium (read once at construction)
/// and whether the app runs inside a Steam Deck Gaming Mode session (constant
/// for the process lifetime). The list VM creates/owns one per application
/// lifetime and passes it to every <see cref="ModItemViewModel"/> once; rows
/// read their derived state off it instead of receiving per-flag value
/// pushes, so a new row-affecting global is one context member rather than
/// another push path.
/// </summary>
/// <remarks>
/// <para><b>Premium is read once:</b> a construction-time fire-and-forget read
/// of <see cref="INexusAuthService.GetCurrentStateAsync"/>; on failure the
/// flag stays false (a restart re-reads). No mid-session refresh by design
/// (re-checking on every surface would burn API calls; a user signing in
/// mid-session restarts for the install behavior to change).</para>
/// <para><b>Install-busy state is not here:</b> an update in flight is a
/// queue item, and the row renders it as the download morph (the row's
/// <see cref="ModItemViewModel.ActiveDownload"/>); there is no separate
/// global busy flag to mirror.</para>
/// </remarks>
public partial class ModRowContext : ObservableObject
{
    private readonly ILogger<ModRowContext> _logger;

    /// <param name="auth">The Nexus auth service; read once at construction
    /// for the Premium flag (fire-and-forget; no mid-session refresh).</param>
    /// <param name="gamingMode">Whether the app runs inside a Steam Deck
    /// Gaming Mode session (constant for the process lifetime).</param>
    /// <param name="logger">Structured logger for the premium-read failure.</param>
    public ModRowContext(
        INexusAuthService auth,
        IGamingModeState gamingMode,
        ILogger<ModRowContext> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(gamingMode);

        IsGamingMode = gamingMode.IsGamingMode;

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
}
