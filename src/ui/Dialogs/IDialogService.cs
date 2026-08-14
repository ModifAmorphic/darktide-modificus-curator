namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// The user's first-run Welcome choice, returned through
/// <see cref="IDialogService.ShowWelcomeAsync"/>. Both the explicit Continue
/// button and a close (ESC, title-bar close, window close) map to
/// <see cref="WelcomeChoice.Continue"/>.
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
/// The user's choice in an unsaved-changes prompt, returned through
/// <see cref="IDialogService.ShowUnsavedChangesAsync"/>. <see cref="Cancel"/>
/// is the zero value so ESC, the title-bar close, and a window close all map
/// to "preserve the staged state and stop the attempted transition."
/// </summary>
public enum UnsavedChangesChoice
{
    /// <summary>
    /// Preserve the staged state and stop the attempted navigation / switch /
    /// new-draft. The default value so ESC, the title-bar X, and a window
    /// close all behave the same way as the explicit Cancel button.
    /// </summary>
    Cancel,

    /// <summary>
    /// Save the staged changes, then proceed. The caller runs the same atomic
    /// create/update the Save button uses; on a service rejection the caller
    /// stops the transition and surfaces the existing localized save error.
    /// </summary>
    Save,

    /// <summary>
    /// Discard the staged changes (reload the authoritative state), then
    /// proceed.
    /// </summary>
    DontSave,
}

/// <summary>
/// The application's true-modal dialog abstraction. Keeps view models free of
/// direct Avalonia <c>Window</c> construction so their logic stays unit-testable
/// against a fake of this seam. Each member shows exactly one modal over the
/// owning main window: the first-run Welcome, a yes/no confirm, the launch
/// discovery escape hatch, a single-button alert, an unsaved-changes three-
/// choice prompt, or a non-dismissable progress spinner. Hosted destinations
/// (Profiles, Mods, Nexus, Preferences, Settings) are not modals and live
/// entirely on the shell's SplitView content region; the inline import card is
/// a hosted UserControl, not a modal.
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
    /// confirms, <c>false</c> otherwise (cancel / dismiss). Used for binary
    /// decisions (delete, DMF, updates, nxm registration). The three-choice
    /// unsaved-changes flow uses <see cref="ShowUnsavedChangesAsync"/> instead.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// Shows the discovery escape-hatch modal, focused on the missing discovery
    /// fields the launch reported. Inputs are shown <em>only</em> for the fields
    /// in <paramref name="missingFields"/>. Returns <c>true</c> when the user
    /// submitted (the entered paths are now persisted into
    /// <c>CuratorConfig.Discovery</c>), <c>false</c> when they cancelled (no
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
    /// Shows the unsaved-changes modal: three choices left to right
    /// (<see cref="UnsavedChangesChoice.Cancel"/>,
    /// <see cref="UnsavedChangesChoice.DontSave"/>,
    /// <see cref="UnsavedChangesChoice.Save"/>), with Save the accent button.
    /// ESC, the title-bar close, and a window close return
    /// <see cref="UnsavedChangesChoice.Cancel"/> (the enum default). When
    /// <paramref name="canSave"/> is <c>false</c>, Save is disabled and a
    /// concise localized explanation is shown so the disabled action is not
    /// mysterious; Cancel and Don't save stay available. Positive framing: ask
    /// whether to save changes before continuing.
    /// </summary>
    /// <param name="title">The localized dialog title.</param>
    /// <param name="message">The localized positive-framing prompt body.</param>
    /// <param name="canSave">Whether Save may be enabled. Pass <c>false</c> when
    /// the staged state has a validation error the inline pass surfaced (so the
    /// user cannot save what they have, only discard it or keep editing).</param>
    /// <returns>The user's choice. Callers branch on it; <see cref="UnsavedChangesChoice.Save"/>
    /// means the caller should try the same save the Save button runs and only
    /// proceed on success.</returns>
    Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(string title, string message, bool canSave);

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
