using Modificus.Curator.General;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Behaviors of the first-run <see cref="OnboardingService"/>: the no-op when
/// onboarding is already complete, the two Welcome choices (Continue vs. Set up
/// Nexus), persistence-before-navigation ordering, the close == Continue
/// equivalence, and the in-process one-shot guard.
/// </summary>
/// <remarks>
/// Uses the shared <see cref="FakeAppStateStore"/> + <see cref="FakeDialogService"/>
/// doubles. The navigation is a recording <see cref="IShellNavigation"/> fake
/// (no real shell involved) so the tests can assert whether + when + where it
/// navigated.
/// </remarks>
public sealed class OnboardingServiceTests
{
    private static readonly ILogger<OnboardingService> Logger = NullLogger<OnboardingService>.Instance;

    /// <summary>
    /// A recording <see cref="IShellNavigation"/> double: counts navigations +
    /// captures the destinations (and the persisted onboarding state at each
    /// navigation, for the ordering assertions). Optionally throws, so a test
    /// can drive the navigation-failure path.
    /// </summary>
    private sealed class RecordingNavigation : IShellNavigation
    {
        private readonly Func<Task>? _impl;

        public RecordingNavigation(Func<Task>? impl = null) => _impl = impl;

        public List<ShellDestination> Destinations { get; } = new();

        public Task NavigateAsync(ShellDestination destination)
        {
            Destinations.Add(destination);
            return _impl?.Invoke() ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Already_completed_is_a_noop()
    {
        var state = new FakeAppStateStore { OnboardingCompleted = true };
        var dialogs = new FakeDialogService();
        var navigation = new RecordingNavigation();

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(0, dialogs.WelcomeCalls);
        Assert.Empty(navigation.Destinations);
        Assert.True(state.OnboardingCompleted);
    }

    [Fact]
    public async Task Continue_persists_and_does_not_navigate()
    {
        var state = new FakeAppStateStore(); // OnboardingCompleted defaults false
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue,
        };
        var navigation = new RecordingNavigation();

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Empty(navigation.Destinations); // no navigation
    }

    [Fact]
    public async Task SetUpNexus_persists_before_navigating_once()
    {
        var state = new FakeAppStateStore(); // OnboardingCompleted defaults false
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.SetUpNexus,
        };
        bool? completedWhenNavigated = null;
        var navigation = new RecordingNavigation(() =>
        {
            // Capture the persisted state at the moment navigation runs.
            completedWhenNavigated = state.OnboardingCompleted;
            return Task.CompletedTask;
        });

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Equal(ShellDestination.NexusIntegrations, Assert.Single(navigation.Destinations));
        // Ordering guarantee: onboarding was ALREADY persisted when navigation
        // began, so navigating away from Integrations can never cause Welcome to
        // repeat.
        Assert.True(completedWhenNavigated);
    }

    [Fact]
    public async Task SetUpNexus_navigates_through_IShellNavigation_to_the_nexus_destination()
    {
        // Focused assertion that the navigation goes through the injected
        // IShellNavigation with the Nexus destination (not some other
        // shell-owned path), so composition owns which navigation runs.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService { WelcomeResult = WelcomeChoice.SetUpNexus };
        var navigation = new RecordingNavigation();

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(ShellDestination.NexusIntegrations, Assert.Single(navigation.Destinations));
    }

    [Fact]
    public async Task Close_result_behaves_as_continue()
    {
        // The default WelcomeResult on FakeDialogService is Continue, which is
        // also what ESC / title-bar close / window close map to. This mirrors a
        // close without an explicit Continue click.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue, // the close equivalent
        };
        var navigation = new RecordingNavigation();

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted even on close
        Assert.Empty(navigation.Destinations); // no navigation on close
    }

    [Fact]
    public async Task Repeated_call_in_same_process_is_a_noop()
    {
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue,
        };
        var navigation = new RecordingNavigation();

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);

        // Second call in the same process: no-op.
        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.Empty(navigation.Destinations);
    }

    [Fact]
    public async Task SetUpNexus_navigation_failure_does_not_crash_or_unpersist()
    {
        // Navigation is the shell's; if it throws, onboarding is already
        // persisted so the Welcome will not repeat. The exception is swallowed
        // so startup continues.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.SetUpNexus,
        };
        var navigation = new RecordingNavigation(() => Task.FromException(new InvalidOperationException("boom")));

        var service = new OnboardingService(state, dialogs, navigation, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted before the failure
    }
}
