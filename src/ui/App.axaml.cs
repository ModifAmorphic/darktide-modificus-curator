using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Modificus.Curator.General;
using Modificus.Curator.Nxm;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI;

/// <summary>
/// The Avalonia application. Also the startup log site: the composition root
/// runs here, config loading is logged so startup is observable, and the user's
/// preferences (theme, font scale, language) are applied to the running app
/// before the main window shows. The shell view model resolves the backend
/// services itself (no Phase-0 probe needed).
/// </summary>
public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        IServiceProvider services;
        ILogger<App> logger;

        try
        {
            services = CuratorComposition.Build();
            logger = services.GetRequiredService<ILogger<App>>();
        }
        catch (NxmSingleInstanceException ex)
        {
            // Single-instance enforcement: another Curator process is already
            // running. Exit before any window shows. Surface the reason on
            // stderr (the provider isn't available here since Build threw).
            //
            // Environment.Exit (not desktopLifetime.Shutdown): Shutdown called
            // from inside OnFrameworkInitializationCompleted breaks Avalonia's
            // MainLoop (StartCore tries to push a frame after the dispatcher
            // shut down -> unhandled InvalidOperationException -> SIGABRT).
            // At this point nothing important is initialized (no window, no
            // background tasks, the check is first in NxmIpcServer.Bind before
            // the pipe or accept loop), so an abrupt exit is safe.
            Console.Error.WriteLine($"Curator is already running; exiting. ({ex.Message})");
            Environment.Exit(1);
            return; // unreachable (Environment.Exit terminates); satisfies definite-assignment.
        }

        // A one-off startup snapshot for the startup log + applying initial
        // preferences before any window shows. This is NOT a cache: every
        // backend consumer reads live via IConfigLoader on each operation.
        var config = services.GetRequiredService<IConfigLoader>().Load();

        logger.LogInformation("Modificus Curator starting");
        logger.LogInformation(
            "Config loaded: ProfilesBaseFolder={Profiles}; ModsFolder={Mods}; " +
            "RelayDir={Runtime}; LogLevel={Level}; LogFile={LogFile}",
            config.ProfilesBaseFolder,
            config.ModsFolder,
            config.RelayDir,
            config.Logging.Level,
            LoggingBootstrap.CurrentLogFile ?? config.Logging.LogFile);

        // Apply the user's preferences (theme, font scale, language) before any
        // window shows, so the first paint already reflects them. Swaps the XAML
        // resource placeholder for the real DI singleton, so every view's
        // {Binding [Key], Source={StaticResource Loc}} resolves through the live
        // service (culture switches refresh the whole UI through INPC).
        var localization = services.GetRequiredService<LocalizationService>();
        Resources["Loc"] = localization;
        var prefs = config.Preferences;
        services.GetRequiredService<IPreferencesService>()
            .ApplyAndPersist(prefs.Theme, prefs.FontScale, prefs.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = services.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = mainWindow;

            // Fire the first-run Welcome onboarding once the owner window is
            // actually shown (Avalonia modal dialogs require a shown owner).
            // One-shot: unsubscribe before firing so a re-open never re-runs it.
            // Exception-safe: a failure inside onboarding is logged + swallowed
            // so it never crashes startup.
            void OnMainWindowOpened(object? sender, EventArgs e)
            {
                if (sender is Window window)
                {
                    window.Opened -= OnMainWindowOpened;
                }
                _ = FireOnboardingAsync(services, logger);
            }
            mainWindow.Opened += OnMainWindowOpened;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Runs the first-run Welcome onboarding on the UI thread, catching any
    /// exception so a wiring failure or unexpected throw never crashes startup.
    /// Fire-and-forget at the call site (the Opened handler); awaited
    /// internally so exceptions are observed + logged.
    /// </summary>
    private static async Task FireOnboardingAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            var onboarding = services.GetRequiredService<OnboardingService>();
            await onboarding.ShowWelcomeIfFirstRunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "First-run onboarding failed; startup continues.");
        }
    }
}
