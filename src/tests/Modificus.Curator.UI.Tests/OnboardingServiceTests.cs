using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Session;
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
/// doubles. The Nexus-navigation delegate is a recording stub (no real shell
/// involved) so the tests can assert whether + when + how many times it ran.
/// </remarks>
public sealed class OnboardingServiceTests
{
    private static readonly ILogger<OnboardingService> Logger = NullLogger<OnboardingService>.Instance;

    [Fact]
    public async Task Already_completed_is_a_noop()
    {
        var state = new FakeAppStateStore { OnboardingCompleted = true };
        var dialogs = new FakeDialogService();
        var navRuns = 0;
        Func<Task> navigateToIntegrations = () => { navRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(0, dialogs.WelcomeCalls);
        Assert.Equal(0, navRuns);
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
        var navRuns = 0;
        Func<Task> navigateToIntegrations = () => { navRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Equal(0, navRuns); // no navigation
    }

    [Fact]
    public async Task SetUpNexus_persists_before_navigating_once()
    {
        var state = new FakeAppStateStore(); // OnboardingCompleted defaults false
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.SetUpNexus,
        };
        var navRuns = 0;
        bool? completedWhenNavigated = null;
        Func<Task> navigateToIntegrations = () =>
        {
            navRuns++;
            // Capture the persisted state at the moment navigation runs.
            completedWhenNavigated = state.OnboardingCompleted;
            return Task.CompletedTask;
        };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Equal(1, navRuns); // navigated exactly once
        // Ordering guarantee: onboarding was ALREADY persisted when navigation
        // began, so navigating away from Integrations can never cause Welcome to
        // repeat.
        Assert.True(completedWhenNavigated);
    }

    [Fact]
    public async Task SetUpNexus_invokes_the_supplied_navigation_callback()
    {
        // Focused assertion that the supplied delegate is what runs (not some
        // other shell-owned path), so composition owns which navigation runs.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService { WelcomeResult = WelcomeChoice.SetUpNexus };
        string? whichRan = null;
        Func<Task> navigateToIntegrations = () =>
        {
            whichRan = "supplied";
            return Task.CompletedTask;
        };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal("supplied", whichRan);
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
        var navRuns = 0;
        Func<Task> navigateToIntegrations = () => { navRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted even on close
        Assert.Equal(0, navRuns); // no navigation on close
    }

    [Fact]
    public async Task Repeated_call_in_same_process_is_a_noop()
    {
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue,
        };
        var navRuns = 0;
        Func<Task> navigateToIntegrations = () => { navRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);

        // Second call in the same process: no-op.
        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.Equal(0, navRuns);
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
        Func<Task> navigateToIntegrations = () => Task.FromException(new InvalidOperationException("boom"));

        var service = new OnboardingService(state, dialogs, navigateToIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted before the failure
    }
}
