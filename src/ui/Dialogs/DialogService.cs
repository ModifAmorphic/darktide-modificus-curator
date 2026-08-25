using Avalonia.Controls;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// Production <see cref="IDialogService"/>. Owns all real Avalonia
/// <c>Window</c>/<c>ShowDialog</c> wiring so view models never construct windows
/// directly. Each method shows exactly one true modal over the owning main
/// window (Welcome, confirm, discovery escape hatch, alert, unsaved changes,
/// game-dir conflict, progress). This is the only place the app brings up a
/// dialog window; everything else flows through the <see cref="IDialogService"/>
/// seam, which tests replace with a fake. The one dialog whose view model
/// carries service dependencies (the escape hatch) builds that VM through the
/// narrow <see cref="IDiscoveryEscapeHatchFactory"/>; this service never
/// constructs view models itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>X11 modality workaround:</b> on Linux/X11, <see cref="Window.ShowDialog(Window)"/>
/// with a custom-chrome dialog (<c>WindowDecorations="None"</c>, which every
/// Curator modal uses for its <c>DialogTitleBar</c>) does not reliably block
/// parent interaction: the parent window can still receive input while the
/// modal is open. The workaround applied here is the common Avalonia remedy:
/// explicitly disable the owner (<c>_owner.IsEnabled = false</c>) before
/// <c>ShowDialog</c> and re-enable it on close (via a <c>using</c> disposable
/// so an exception never strands the parent disabled). This is harmless on
/// Win32 + macOS (where <c>ShowDialog</c> is already modal at the platform
/// level) and closes the gap on X11. See <see cref="DisableOwnerForModal"/>.</para>
/// <para>
/// Tracked as an Avalonia-upstream concern; if a future Avalonia release fixes
/// X11 modality for custom-chrome dialogs natively, this guard can be removed.
/// </para>
/// </remarks>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private readonly LocalizationService _localization;
    private readonly IDiscoveryEscapeHatchFactory _escapeHatchFactory;

    /// <param name="owner">The window dialog parents are shown over (the main
    /// window).</param>
    /// <param name="localization">The Localization service; handed to the
    /// Welcome title.</param>
    /// <param name="escapeHatchFactory">Builds the escape-hatch dialog's view
    /// model (the one dialog VM with service dependencies: the live config
    /// reader/writer, the Steam discovery service, and the Gaming Mode
    /// state).</param>
    public DialogService(
        Window owner,
        LocalizationService localization,
        IDiscoveryEscapeHatchFactory escapeHatchFactory)
    {
        _owner = owner;
        _localization = localization;
        _escapeHatchFactory = escapeHatchFactory
            ?? throw new ArgumentNullException(nameof(escapeHatchFactory));
    }

    /// <summary>
    /// Disables the owner window for the duration of a modal <c>ShowDialog</c>
    /// call, returning an <see cref="IDisposable"/> that releases that hold on
    /// dispose. Used to work around the X11 custom-chrome modality gap (the
    /// class remarks explain why). Wrap in a <c>using</c> so the parent is
    /// always re-enabled, even on exception. No-op-safe on Win32 + macOS (where
    /// <c>ShowDialog</c> is already modal at the platform level); on X11 it is
    /// what actually blocks parent interaction.
    /// </summary>
    /// <remarks>
    /// <b>Nesting-safe:</b> a reference count tracks overlapping modals (a
    /// progress spinner opened from within a confirm). The owner is only
    /// re-enabled when the <em>outermost</em> modal's guard disposes; an inner
    /// modal closing does not prematurely re-enable the owner while an outer
    /// modal is still open. For the common single-modal case (depth 0 -> 1 -> 0)
    /// the behavior is unchanged.
    /// </remarks>
    private IDisposable DisableOwnerForModal()
    {
        _modalDepth++;
        _owner.IsEnabled = false;
        return new ModalDepthGuard(this);
    }

    private int _modalDepth;

    /// <summary>
    /// Releases one hold from <see cref="DisableOwnerForModal"/>; re-enables the
    /// owner only when the outermost modal closes (the depth drops to 0). Called
    /// by <see cref="ModalDepthGuard.Dispose"/>. All <c>ShowDialog</c> calls run
    /// sequentially on the UI thread, so a plain int counter is sufficient.
    /// </summary>
    private void ReleaseModal()
    {
        if (_modalDepth == 0)
        {
            return;
        }
        _modalDepth--;
        if (_modalDepth == 0)
        {
            _owner.IsEnabled = true;
        }
    }

    /// <summary>
    /// The disposable returned by <see cref="DisableOwnerForModal"/>. Decrements
    /// the modal depth on <see cref="Dispose"/>; the owner is re-enabled only
    /// when the outermost modal closes. A duplicate <c>Dispose</c> is a no-op.
    /// </summary>
    private sealed class ModalDepthGuard : IDisposable
    {
        private readonly DialogService _service;
        private bool _disposed;

        public ModalDepthGuard(DialogService service) => _service = service;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _service.ReleaseModal();
        }
    }

    /// <inheritdoc />
    public async Task<WelcomeChoice> ShowWelcomeAsync()
    {
        var dialog = new WelcomeWindow
        {
            Title = _localization["Welcome_Title"],
        };

        using var _ = DisableOwnerForModal();
        await dialog.ShowDialog(_owner);
        return dialog.Result;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
        };
        dialog.SetMessage(message);

        using var _ = DisableOwnerForModal();
        await dialog.ShowDialog(_owner);
        return dialog.Result;
    }

    /// <inheritdoc />
    public async Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields)
    {
        // No rows to fill: skip the modal entirely (the caller should not have
        // shown the hatch for an empty list, but defensive is cheaper than a
        // confusing empty dialog).
        if (missingFields is null || missingFields.Count == 0)
        {
            return false;
        }

        var viewModel = _escapeHatchFactory.Create(missingFields);
        var window = new DiscoveryEscapeHatchDialog
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
        return viewModel.Result;
    }

    /// <inheritdoc />
    public async Task ShowAlertAsync(string title, string message)
    {
        // Reuses the ConfirmDialog chrome (title bar + message + button) in its
        // single-button mode: Cancel is hidden, so the only affordance is OK.
        // A dedicated AlertDialog would carry the same chrome; this keeps one
        // chrome implementation for all simple message dialogs.
        var dialog = new ConfirmDialog
        {
            Title = title,
            ShowCancel = false,
        };
        dialog.SetMessage(message);

        using var _ = DisableOwnerForModal();
        await dialog.ShowDialog(_owner);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the dedicated <see cref="UnsavedChangesDialog"/> rather than
    /// parameterizing <see cref="ConfirmAsync"/> into a generic N-button
    /// dialog: the three choices have distinct caller-side semantics (Save runs
    /// the caller's save core, Don't save reloads authority, Cancel preserves
    /// state), and the optional disabled-Save explanation is specific to this
    /// prompt. The Cancel default + ESC / title-bar close / window close all
    /// fall out of the dialog's <see cref="UnsavedChangesDialog.Result"/>
    /// default without a special close-handler path here.
    /// </remarks>
    public async Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(
        string title, string message, bool canSave)
    {
        var dialog = new UnsavedChangesDialog
        {
            Title = title,
            CanSave = canSave,
        };
        dialog.SetMessage(message);

        using var _ = DisableOwnerForModal();
        await dialog.ShowDialog(_owner);
        return dialog.Result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the dedicated <see cref="GameDirConflictDialog"/> for the same
    /// reason the unsaved-changes prompt has its own: the three choices have
    /// distinct caller-side semantics (takeover + retry, preference + retry,
    /// abort), so a generic N-button dialog would couple unrelated prompts.
    /// The Cancel default + ESC / title-bar close / window close all fall out
    /// of the dialog's <see cref="GameDirConflictDialog.Result"/> default
    /// without a special close-handler path here.
    /// </remarks>
    public async Task<GameDirConflictChoice> ShowGameDirConflictAsync(string title, string message)
    {
        var dialog = new GameDirConflictDialog
        {
            Title = title,
        };
        dialog.SetMessage(message);

        using var _ = DisableOwnerForModal();
        await dialog.ShowDialog(_owner);
        return dialog.Result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Spinner lifecycle:</b> the <see cref="ProgressDialog"/> is shown with
    /// <c>ShowDialog</c> (nested event loop on the UI thread, owner disabled via
    /// <see cref="DisableOwnerForModal"/>), then the work is started. When the
    /// work completes (success or fault), the spinner is closed on the UI thread
    /// via <c>Dispatcher.Post</c> (the work may run on a thread-pool task; the
    /// close must marshal back). After the close, <see cref="ShowDialog"/>'s
    /// task completes + the owner is re-enabled.</para>
    /// <para>
    /// <b>Exception safety:</b> the close is in a <c>finally</c> so an
    /// exception (from <paramref name="work"/> or anywhere else) still dismisses
    /// the spinner. The exception propagates to the caller after the spinner is
    /// gone, so the caller's error-handling alert is the only dialog visible at
    /// that point.</para>
    /// <para>
    /// <b>The user cannot dismiss the spinner:</b> the title bar's close button
    /// is hidden (<see cref="DialogTitleBar.ShowCloseProperty"/> = false). There
    /// are no buttons in the content. The work runs to completion + this method
    /// closes the spinner.</para>
    /// </remarks>
    public async Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var dialog = new ProgressDialog
        {
            Title = title,
        };
        dialog.SetMessage(message);

        using var ownerGuard = DisableOwnerForModal();
        var showDialogTask = dialog.ShowDialog(_owner);

        // Start the work AFTER the spinner is up; capture the task so the
        // continuation can close the dialog on either outcome. The continuation
        // is intentionally fire-and-forget (we await workTask itself below; the
        // continuation just dismisses the spinner), so assign to discard to
        // silence the CS4014.
        var workTask = work();
        _ = workTask.ContinueWith(
            _ => dialog.Close(),
            TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            // Await the work first so its exception (if any) propagates after
            // the spinner is closed (the close-continuation runs as part of the
            // await's continuation). If we awaited showDialogTask first, an
            // exception in work would never close the spinner.
            var result = await workTask;
            await showDialogTask;
            return result;
        }
        finally
        {
            // Belt-and-suspenders: if the continuation has not run yet (an
            // early-await on showDialogTask racing the close), make sure the
            // dialog is closed before this method returns.
            try { dialog.Close(); }
            catch { /* already closed; harmless */ }
        }
    }
}
