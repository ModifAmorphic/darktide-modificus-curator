using Avalonia.Controls;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Views;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// Production <see cref="IDialogService"/>. Owns all real Avalonia
/// <c>Window</c>/<c>ShowDialog</c> wiring so view models never construct windows
/// directly. Dialogs are shown modally over the owning main window. This is the
/// only place the app news-up a dialog window, everything else flows through
/// the <see cref="IDialogService"/> seam, which tests replace with a fake.
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
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IPreferencesService _preferences;
    private readonly LocalizationService _localization;
    private readonly IConfigLoader _configLoader;
    private readonly INexusAuthService _nexusAuth;
    private readonly IAppUpdateService _appUpdate;
    private readonly Action<Action> _invokeOnUi;
    private readonly INxmHandlerRegistrar? _nxmRegistrar;
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="owner">The window dialog parents are shown over (the main window).</param>
    /// <param name="profiles">Resolved lazily to construct the manage-profiles VM.</param>
    /// <param name="session">The active-profile authority; handed to the manage-profiles
    /// VM so its marker reads the live active id and its create/delete route active
    /// changes through the session's gate.</param>
    /// <param name="preferences">The Preferences authority; handed to the Preferences VM
    /// so its controls apply + persist through the single authority.</param>
    /// <param name="localization">The Localization service; handed to the manage-profiles
    /// VM (delete-confirm message is localized + the marker tooltip), the Preferences VM
    /// (language picker reads the live culture), the Settings VM (section headers +
    /// per-row labels), and the escape-hatch VM (header + per-row labels).</param>
    /// <param name="configLoader">The live config reader/writer; handed to the
    /// Preferences VM (initial picker state), the Settings VM (read-modify-save per
    /// field change), and the escape-hatch VM (one read-modify-save on submit).</param>
    /// <param name="nexusAuth">The Nexus auth service; handed to the Integrations VM
    /// for OAuth login + API-key validate + sign-out + current-state reads.</param>
    /// <param name="appUpdate">The app self-update service; handed to the Settings
    /// VM for the Updates section (current version, manual check, download +
    /// restart).</param>
    /// <param name="invokeOnUi">The UI-thread marshal seam; handed to the Settings
    /// VM so its off-thread <c>UpdateStateChanged</c> handler refreshes the inline
    /// status on the UI thread.</param>
    /// <param name="nxmRegistrar">The platform nxm:// handler registrar (null on
    /// unsupported platforms); handed to the Integrations VM so its "Nexus download
    /// links" section can query + toggle the OS handler registration.</param>
    /// <param name="loggerFactory">The logger factory; the Settings VM + the
    /// Integrations VM get typed loggers from it so their log lines reach the
    /// configured sinks (the other dialog VMs take no logger).</param>
    public DialogService(
        Window owner,
        IProfileService profiles,
        IProfileSession session,
        IPreferencesService preferences,
        LocalizationService localization,
        IConfigLoader configLoader,
        INexusAuthService nexusAuth,
        IAppUpdateService appUpdate,
        Action<Action> invokeOnUi,
        INxmHandlerRegistrar? nxmRegistrar,
        ILoggerFactory loggerFactory)
    {
        _owner = owner;
        _profiles = profiles;
        _session = session;
        _preferences = preferences;
        _localization = localization;
        _configLoader = configLoader;
        _nexusAuth = nexusAuth;
        _appUpdate = appUpdate;
        _invokeOnUi = invokeOnUi;
        _nxmRegistrar = nxmRegistrar;
        _loggerFactory = loggerFactory;
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
    /// launch-settings modal opened from inside the manage-profiles modal). The
    /// owner is only re-enabled when the <em>outermost</em> modal's guard
    /// disposes; an inner modal closing does not prematurely re-enable the owner
    /// while an outer modal is still open. For the common single-modal case
    /// (depth 0 -> 1 -> 0) the behavior is unchanged.
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
    public async Task ShowManageProfilesAsync()
    {
        var viewModel = new ManageProfilesViewModel(_profiles, this, _session, _localization);
        var window = new ManageProfilesWindow
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task ShowLaunchSettingsAsync(Guid profileId)
    {
        // Loaded from GetLaunchSettings at construction; Save persists via
        // SetLaunchSettings (closing only on success). Editing is unlocked while
        // Darktide runs (a profile.json write that does not touch the running
        // process).
        var viewModel = new LaunchSettingsViewModel(profileId, _profiles, _localization);
        var window = new LaunchSettingsWindow
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task ShowPreferencesAsync()
    {
        // The VM reads its initial state from a live snapshot (no cached
        // singleton); subsequent changes flow through IPreferencesService, which
        // read-modify-saves via the same loader. The console-toggle support is
        // platform-resolved: functional on Windows, non-functional on Linux
        // (Wine spawns the console regardless of CreateNoWindow).
        var viewModel = new PreferencesViewModel(
            _preferences, _configLoader, _localization, OperatingSystem.IsWindows());
        var window = new PreferencesWindow
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task<ImportModResult?> ShowImportModAsync(ImportModRequest request)
    {
        var viewModel = new ImportModViewModel(request, _localization);
        var window = new ImportModDialog
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
        return viewModel.Result;
    }

    /// <inheritdoc />
    public async Task ShowSettingsAsync()
    {
        // The VM reads its initial state from a live snapshot (no cached
        // singleton); subsequent changes do a read-modify-save per field via
        // the same loader. The Storage section's two buttons open the OS file
        // manager at the Curator data root + profiles roots. A typed logger is
        // created here so the open-folder success/failure lines reach the
        // configured sinks (not a NullLogger that drops them).
        var viewModel = new SettingsViewModel(
            _configLoader,
            _localization,
            _appUpdate,
            this,
            _invokeOnUi,
            _loggerFactory.CreateLogger<SettingsViewModel>());
        var window = new SettingsWindow
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task ShowIntegrationsAsync()
    {
        // The VM resolves its initial auth state server-side on open
        // (Window.OnOpened calls vm.RefreshAsync) and its update-check toggle +
        // interval from the persisted config. Each auth action applies +
        // persists through the NexusAuthService; the toggle + interval persist
        // on each change via the VM's read-modify-save over the same loader.
        var viewModel = new IntegrationsViewModel(
            _nexusAuth,
            _localization,
            _configLoader,
            this,
            _nxmRegistrar,
            _loggerFactory.CreateLogger<IntegrationsViewModel>());
        var window = new IntegrationsWindow
        {
            DataContext = viewModel,
        };

        using var _ = DisableOwnerForModal();
        await window.ShowDialog(_owner);
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

        var viewModel = new DiscoveryEscapeHatchViewModel(missingFields, _configLoader, _localization);
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
