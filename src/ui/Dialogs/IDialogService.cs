namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// The user's first-run Welcome choice, returned through
/// <see cref="IDialogService.ShowWelcomeAsync"/>. Both the explicit Continue
/// button and a close (ESC, title-bar close, window close) map to
/// <see cref="Continue"/>.
/// </summary>
public enum WelcomeChoice
{
    /// <summary>
    /// The user chose to skip Nexus setup (or closed the window).
    /// </summary>
    Continue,

    /// <summary>
    /// The user chose to set up Nexus.
    /// </summary>
    SetUpNexus,
}

/// <summary>
/// The application's true-modal dialog abstraction. Keeps view models free of
/// direct Avalonia <c>Window</c> construction so their logic stays unit-testable
/// against a fake of this seam. Each member shows exactly one modal over the
/// owning main window: the first-run Welcome, a yes/no confirm, the per-mod
/// import chooser, the launch discovery escape hatch, a single-button alert, or
/// a non-dismissable progress spinner. Hosted destinations (Profiles, Mods,
/// Nexus Integrations, Preferences, Settings) are not modals and live entirely
/// on the shell's SplitView content region.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows the first-run Welcome modal, returning the user's choice. The
    /// owner window must already be shown (Avalonia modal dialogs require a
    /// shown owner). ESC, title-bar close, and window close are equivalent to
    /// <see cref="WelcomeChoice.Continue"/>.
    /// </summary>
    Task<WelcomeChoice> ShowWelcomeAsync();

    /// <summary>
    /// Shows a modal confirmation prompt. Returns <c>true</c> when the user
    /// confirms, <c>false</c> otherwise (cancel / dismiss).
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// Shows the per-mod import modal (source chooser + conditional Version +
    /// URL), pre-filled from <paramref name="request"/>. Returns the confirmed
    /// <see cref="ImportModResult"/> (URL parsed to canonical source) when the
    /// user confirms, or <c>null</c> when they cancel / dismiss. A <c>null</c>
    /// cancels a remaining batch.
    /// </summary>
    Task<ImportModResult?> ShowImportModAsync(ImportModRequest request);

    /// <summary>
    /// Shows the discovery escape-hatch modal, focused on the missing discovery
    /// fields the launch reported. Inputs are shown <em>only</em> for the fields
    /// in <paramref name="missingFields"/>. Returns <c>true</c> when the user
    /// submitted (the entered paths are now persisted into the
    /// <c>Discovery.User*Path</c> section), <c>false</c> when they cancelled (no
    /// writes). There is no auto-retry: the caller does not re-launch on a
    /// <c>true</c> return; the user clicks Launch again.
    /// </summary>
    /// <param name="missingFields">The discovery field names the launch result
    /// reported missing (the values of <c>LaunchResult.MissingDiscoveryFields</c>,
    /// which match the <c>DiscoveryResult</c> property names).</param>
    Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields);

    /// <summary>
    /// Shows a simple modal alert (a single OK button, no cancel). For surfacing
    /// a condition where there is nothing for the user to decide, only
    /// acknowledge.
    /// </summary>
    Task ShowAlertAsync(string title, string message);

    /// <summary>
    /// Shows a buttonless modal spinner over the supplied async work, awaits
    /// the work, and closes the spinner when it completes. The user cannot
    /// dismiss the spinner (no buttons, no close affordance): the work runs to
    /// completion + the caller surfaces its result. The work's exception (if
    /// any) propagates to the caller; the spinner is closed in either case.
    /// </summary>
    /// <param name="title">The window title (also shown in the title bar).</param>
    /// <param name="message">The explanatory message shown above the spinner.</param>
    /// <param name="work">The async operation to run while the spinner is up.
    /// Started after the spinner is shown; its result (or exception) is
    /// returned to the caller.</param>
    /// <typeparam name="T">The work's result type.</typeparam>
    /// <returns>The work's result. The work's exception (if any) propagates to
    /// the caller after the spinner is closed.</returns>
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
