using Modificus.Curator.General;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The first-run Welcome onboarding coordinator. Shows the Welcome modal once,
/// the first time the app starts with <see cref="IOnboardingState.OnboardingCompleted"/>
/// still <c>false</c>, persists completion, and navigates the shell to Nexus
/// Integrations when the user chooses "Set up Nexus". After the first run, the
/// call is a no-op for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-shot, persisted.</b> <see cref="ShowWelcomeIfFirstRunAsync"/> reads
/// the persisted <see cref="IOnboardingState.OnboardingCompleted"/> flag; when it
/// is already <c>true</c> (a returning user, or a second call in the same
/// process after the first run persisted it) the method returns without showing
/// anything. The completion flag is persisted BEFORE the navigation runs, so
/// navigating away from Integrations (or the navigation failing) can never
/// cause the Welcome to repeat.</para>
/// <para>
/// <b>Owner window must be open.</b> Avalonia modal dialogs require a shown
/// owner, so the shell / App wires this call after the main window opens. The
/// coordinator itself is UI-thread-affine (no <c>ConfigureAwait(false)</c>,
/// per the UI-layer rule) and stays testable through the
/// <see cref="IDialogService.ShowWelcomeAsync"/> + state-store seams.</para>
/// <para>
/// <b>The Nexus step is one navigation call.</b> The coordinator depends on
/// <see cref="IShellNavigation"/> (implemented by the shell view model +
/// registered by the composition root as a lazy forward) and invokes it with
/// the Nexus destination after persisting completion, so the Welcome-driven
/// visit shares the shell's guarded leave/enter lifecycle with every other
/// navigation. The coordinator never constructs the page or drives navigation
/// mechanics itself.</para>
/// <para>
/// <b>Registered as a singleton</b> so the in-process <see cref="_shown"/> guard
/// reliably suppresses a second show even if persistence is best-effort and
/// fails to record. The persisted flag is the durable signal; the in-memory
/// guard is the within-process guarantee.</para>
/// </remarks>
public sealed class OnboardingService
{
    private readonly IOnboardingState _appState;
    private readonly IDialogService _dialogs;
    private readonly IShellNavigation _navigation;
    private readonly ILogger<OnboardingService> _logger;

    // In-process guard: once the Welcome has been shown this session, never show
    // it again even if the persisted flag could not be written (best-effort
    // persistence). Read + written on the UI thread only (the coordinator is
    // called once at startup from the main window's Opened handler).
    private bool _shown;

    public OnboardingService(
        IOnboardingState appState,
        IDialogService dialogs,
        IShellNavigation navigation,
        ILogger<OnboardingService> logger)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Shows the Welcome modal on the first run only. No-op when onboarding is
    /// already complete (persisted flag set, or already shown in this process).
    /// On a <see cref="WelcomeChoice.SetUpNexus"/> choice, persists completion
    /// first, then navigates the shell to Nexus. On
    /// <see cref="WelcomeChoice.Continue"/> (explicit button, ESC, or close)
    /// persists completion and returns, leaving the user at the default
    /// destination.
    /// </summary>
    public async Task ShowWelcomeIfFirstRunAsync()
    {
        // Already done: a returning user, or a second call in this process.
        // Both the persisted flag and the in-process guard suppress the show.
        if (_shown || _appState.OnboardingCompleted)
        {
            return;
        }

        _shown = true;

        var choice = await _dialogs.ShowWelcomeAsync();

        // Persist completion BEFORE navigating so leaving Integrations (or the
        // navigation failing) can never cause Welcome to repeat on the next
        // launch.
        _appState.OnboardingCompleted = true;

        if (choice == WelcomeChoice.SetUpNexus)
        {
            try
            {
                await _navigation.NavigateAsync(ShellDestination.NexusIntegrations);
            }
            catch (Exception ex)
            {
                // Navigation is the shell's; a failure there is unexpected.
                // Onboarding is already persisted, so log + continue rather than
                // re-showing Welcome.
                _logger.LogError(ex, "Navigating to Nexus after the Welcome choice failed.");
            }
        }
    }
}
