using System.ComponentModel;
using System.Net;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="IntegrationsViewModel"/>: the redesigned two-block
/// Integrations dialog with the active-method indicator, the masked + persisted
/// API-key field with Show eye toggle, the validate-on-existing path, the
/// status-line wording that names the active method, the OAuth-login + API-key
/// commands routing through the service, the disabled-while-running gate, and
/// the Sign-out command clearing state.
/// </summary>
public sealed class IntegrationsViewModelTests
{
    private static readonly LocalizationService Localization = new();
    private static readonly ILogger<IntegrationsViewModel> Logger = NullLogger<IntegrationsViewModel>.Instance;

    // ---- status line on open ----------------------------------------------

    [Fact]
    public async Task RefreshAsync_shows_not_signed_in_when_None()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: null);

        Assert.Equal(Localization["Integrations_StatusNotSignedIn"], vm.StatusLine);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.None, vm.ActiveMethod);
        Assert.False(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_oauth_when_OAuth_premium_user()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "OAuthUser", IsPremium: true));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuthPremium", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.OAuth, vm.ActiveMethod);
        Assert.True(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_oauth_when_non_premium()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "OAuthUser", IsPremium: false));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuth", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsOAuthActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_apikey_when_ApiKey_user()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", IsPremium: false, ApiKey: "the-key"));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKey", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsApiKeyActive);
        Assert.False(vm.IsOAuthActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_apikey_when_premium_ApiKey_user()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", IsPremium: true, ApiKey: "the-key"));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKeyPremium", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_method_aware_unverified_when_verify_returns_null_name()
    {
        // When the verify call fails, the service returns a state with a null
        // name. The VM must show the method-aware unverified status (so the
        // user knows WHICH method is configured-but-unverifiable, not a generic
        // "signed in").
        var oauthState = new NexusAuthState(NexusAuthMethod.OAuth, Name: null, IsPremium: null);
        var (oauthVm, _, _, _) = await BuildAndRefresh(state: oauthState);
        Assert.Equal(Localization["Integrations_StatusSignedInOAuthUnverified"], oauthVm.StatusLine);
        Assert.True(oauthVm.IsOAuthActive);

        var apiKeyState = new NexusAuthState(
            NexusAuthMethod.ApiKey, Name: null, IsPremium: null, ApiKey: "the-key");
        var (apiKeyVm, _, _, _) = await BuildAndRefresh(state: apiKeyState);
        Assert.Equal(Localization["Integrations_StatusSignedInApiKeyUnverified"], apiKeyVm.StatusLine);
        Assert.True(apiKeyVm.IsApiKeyActive);
    }

    // ---- masked + persisted API-key field --------------------------------

    [Fact]
    public async Task RefreshAsync_populates_ApiKey_from_state_when_method_is_ApiKey()
    {
        // The masked field shows the persisted key (so the user sees one is
        // configured, without re-entering). The value is real (the masking is
        // purely visual via PasswordChar).
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "persisted-key"));

        Assert.Equal("persisted-key", vm.ApiKey);
        // Masked by default (no reveal-on-open).
        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    [Fact]
    public async Task RefreshAsync_clears_ApiKey_when_method_is_None()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: null);

        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_clears_ApiKey_when_method_is_OAuth()
    {
        // When OAuth is active, the API-key field is empty (the placeholder
        // shows; the field is the inactive alternative).
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "U", false));

        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_resets_reveal_on_each_apply()
    {
        // After a refresh (e.g. post-action), the field is masked again even if
        // the user had revealed it (no surprise plaintext after a state change).
        var (vm, _, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "k"));
        vm.ToggleApiKeyRevealCommand.Execute(null);
        Assert.True(vm.IsApiKeyRevealed);

        await vm.RefreshAsync();

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    // ---- Show eye toggle --------------------------------------------------

    [Fact]
    public async Task ToggleApiKeyReveal_flips_mask_char()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: null);

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);

        vm.ToggleApiKeyRevealCommand.Execute(null);

        Assert.True(vm.IsApiKeyRevealed);
        Assert.Equal('\0', vm.ApiKeyMaskChar);

        vm.ToggleApiKeyRevealCommand.Execute(null);

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    [Fact]
    public async Task ToggleApiKeyReveal_disabled_while_login_in_flight()
    {
        // Block the OAuth login on a TCS so we can observe the IsBusy state.
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        var tcs = new TaskCompletionSource<NexusAuthResult>();
        auth.NextOAuthTask = tcs.Task;

        var task = vm.LoginWithOAuthCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ToggleApiKeyRevealCommand.CanExecute(null));

        tcs.SetResult(NexusAuthResult.Success("U", false));
        await task;

        Assert.False(vm.IsBusy);
        Assert.True(vm.ToggleApiKeyRevealCommand.CanExecute(null));
    }

    // ---- API-key Validate -------------------------------------------------

    [Fact]
    public async Task ValidateApiKey_invokes_service_and_updates_status_on_success()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        auth.NextApiKeyResult = NexusAuthResult.Success("ApiUser", isPremium: false);
        auth.NextStateAfterApiKey = new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", false, ApiKey: "the-key");

        vm.ApiKey = "the-key";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ApiKeyLoginCalls);
        Assert.Equal("the-key", auth.LastApiKey);
        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKey", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsApiKeyActive);
        // The field still shows the (now-persisted) key, masked, so the user
        // can re-validate without re-entering (NOT cleared on success like the
        // legacy flow).
        Assert.Equal("the-key", vm.ApiKey);
        Assert.False(vm.IsApiKeyRevealed);
    }

    [Fact]
    public async Task ValidateApiKey_with_empty_key_shows_message_without_calling_service()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);

        vm.ApiKey = "   ";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(0, auth.ApiKeyLoginCalls);
        Assert.Equal(Localization["Integrations_ApiKeyEmpty"], vm.StatusLine);
    }

    [Fact]
    public async Task ValidateApiKey_surfaces_failure_inline_without_clearing_key()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        auth.NextApiKeyResult = NexusAuthResult.Failed("HTTP 401: invalid");

        vm.ApiKey = "bad-key";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Contains("HTTP 401", vm.StatusLine, StringComparison.Ordinal);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal("bad-key", vm.ApiKey); // kept so the user can correct it
    }

    [Fact]
    public async Task ValidateApiKey_revalidates_persisted_masked_key_without_reentry()
    {
        // The "validate the existing masked key" path: the dialog opens with an
        // ApiKey method, the field shows the persisted key (masked), and the
        // user clicks Validate without typing anything new. The VM passes the
        // existing key (held in ApiKey from the state) back into the service.
        var (vm, auth, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "existing-key"));
        auth.NextApiKeyResult = NexusAuthResult.Success("U2", isPremium: true);
        auth.NextStateAfterApiKey = new NexusAuthState(
            NexusAuthMethod.ApiKey, "U2", true, ApiKey: "existing-key");

        // No re-entry: ApiKey still holds the persisted value from refresh.
        Assert.Equal("existing-key", vm.ApiKey);
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ApiKeyLoginCalls);
        Assert.Equal("existing-key", auth.LastApiKey);
        Assert.True(vm.IsApiKeyActive);
    }

    // ---- OAuth login ------------------------------------------------------

    [Fact]
    public async Task LoginWithOAuth_invokes_service_and_updates_status()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        auth.NextOAuthResult = NexusAuthResult.Success("OAuthUser", isPremium: false);
        auth.NextStateAfterOAuth = new NexusAuthState(NexusAuthMethod.OAuth, "OAuthUser", false);

        await vm.LoginWithOAuthCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.OAuthLoginCalls);
        // The VM forwards a real (non-default) CancellationToken so the login
        // is cancelable on Detach (CancelOAuthOnToken is not set here; this
        // just proves the command passes a live token rather than default).
        Assert.NotEqual(CancellationToken.None, auth.LastOAuthCancellationToken);
        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuth", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsOAuthActive);
        // Switching to OAuth clears the API-key field (the field is the
        // inactive alternative now).
        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task LoginWithOAuth_is_canceled_when_dialog_detaches()
    {
        // The Integrations dialog calls Detach from OnClosed. Detach cancels the
        // in-flight login's token so the singleton LoopbackBrowser's listener
        // is freed promptly instead of waiting out the 3-minute flow timeout
        // (the retry-without-restart bug). The fake's login task completes as
        // canceled when the token it was handed fires.
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        auth.CancelOAuthOnToken = true;

        var commandTask = vm.LoginWithOAuthCommand.ExecuteAsync(null);
        Assert.NotNull(auth.LastOAuthTask);

        vm.Detach();

        // The login task must cancel promptly (not hang for the 3-minute
        // timeout). Assert it finishes within 2s and lands in the canceled state.
        var finished = await Task.WhenAny(auth.LastOAuthTask!, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(auth.LastOAuthTask, finished);
        Assert.True(auth.LastOAuthTask!.IsCanceled);

        // The command swallows the OperationCanceledException (the catch
        // surfaces it as a status line) and completes normally.
        await commandTask;
    }

    [Fact]
    public async Task LoginWithOAuth_surfaces_failure_inline()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        auth.NextOAuthResult = NexusAuthResult.Failed("User cancelled.");

        await vm.LoginWithOAuthCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.OAuthLoginCalls);
        Assert.Contains("User cancelled", vm.StatusLine, StringComparison.Ordinal);
        Assert.False(vm.IsAuthenticated);
    }

    // ---- Sign out ---------------------------------------------------------

    [Fact]
    public async Task SignOut_clears_state_and_resets_status()
    {
        var (vm, auth, _, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "k"));
        Assert.True(vm.IsAuthenticated); // signed in to start
        Assert.True(vm.IsApiKeyActive);

        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.SignOutCalls);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.None, vm.ActiveMethod);
        Assert.False(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
        Assert.Equal(Localization["Integrations_StatusNotSignedIn"], vm.StatusLine);
        Assert.Equal(string.Empty, vm.ApiKey); // cleared on sign-out
    }

    // ---- auth controls stay usable while the game runs -------------------

    [Fact]
    public async Task Auth_commands_remain_enabled_regardless_of_running_state()
    {
        // Auth controls no longer gate on the game running (only launch +
        // active-profile changes are blocked while Darktide runs). The VM has
        // no IsGameRunning/IsEnabled surface; the commands are gated only by
        // IsBusy (+ IsAuthenticated for sign-out).
        var (vm, _, _, _) = await BuildAndRefresh(state: null);

        Assert.True(vm.LoginWithOAuthCommand.CanExecute(null));
        Assert.True(vm.ValidateApiKeyCommand.CanExecute(null));
        // Sign-out is additionally gated on IsAuthenticated (not configured here).
        Assert.False(vm.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignOut_only_enabled_when_authenticated()
    {
        var (vm, _, _, _) = await BuildAndRefresh(state: null);
        Assert.False(vm.SignOutCommand.CanExecute(null)); // not authenticated
    }

    [Fact]
    public async Task RefreshAsync_enables_sign_out_after_resolving_authenticated_state()
    {
        // Regression: on dialog reopen, RefreshAsync resolves an authenticated
        // OAuth state. ApplyState sets ActiveMethod first (which re-evaluates
        // SignOutCommand while IsAuthenticated is still false), then sets
        // IsAuthenticated = true. Without [NotifyCanExecuteChangedFor] on
        // _isAuthenticated, Sign out stays disabled even though the status line
        // shows signed-in. The notification makes the post-IsAuthenticated
        // re-evaluation fire.
        var (vm, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.OAuth, "OAuthUser", IsPremium: true));

        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.SignOutCommand.CanExecute(null));
    }

    // ---- IsBusy gate ------------------------------------------------------

    [Fact]
    public async Task IsBusy_disables_commands_during_flight()
    {
        // Block the OAuth login on a TCS so we can observe the IsBusy state.
        var (vm, auth, _, _) = await BuildAndRefresh(state: null);
        var tcs = new TaskCompletionSource<NexusAuthResult>();
        auth.NextOAuthTask = tcs.Task;

        var task = vm.LoginWithOAuthCommand.ExecuteAsync(null);
        // The command is in flight; IsBusy must be true + commands disabled.
        Assert.True(vm.IsBusy);
        Assert.False(vm.LoginWithOAuthCommand.CanExecute(null));
        Assert.False(vm.ValidateApiKeyCommand.CanExecute(null));

        tcs.SetResult(NexusAuthResult.Success("U", false));
        await task;

        Assert.False(vm.IsBusy);
        Assert.True(vm.LoginWithOAuthCommand.CanExecute(null));
    }

    // ---- Detach -----------------------------------------------------------

    [Fact]
    public async Task Detach_is_safe_to_call_after_construction()
    {
        // Detach drops the localization subscription so the short-lived dialog
        // VM is collectable after its window closes. It must be a safe no-op
        // that does not throw.
        var (vm, _, _, _) = await BuildAndRefresh(state: null);

        vm.Detach();
    }

    // ---- auto-update settings (toggle + interval persistence) ------------

    [Fact]
    public async Task RefreshAsync_loads_toggle_and_interval_from_config()
    {
        // The dialog open reflects the persisted auto-update settings: the
        // toggle + the interval are read live so a prior session's change shows.
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckEnabled = false;
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes = 25;

        var (vm, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        Assert.False(vm.AutoUpdateCheckEnabled);
        Assert.Equal(25m, vm.AutoUpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task RefreshAsync_does_not_persist_when_loading_from_config()
    {
        // Populating the fields from config on open must NOT trigger the change
        // handlers' write-back (a redundant round-trip on every open). The
        // loader records zero saves from a pure RefreshAsync.
        var configLoader = new FakeConfigLoader();

        var (_, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        Assert.Equal(0, configLoader.SaveCalls);
    }

    [Fact]
    public async Task Toggling_auto_check_persists_through_config_save()
    {
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);
        Assert.True(vm.AutoUpdateCheckEnabled); // default

        vm.AutoUpdateCheckEnabled = false;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.False(configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckEnabled);
    }

    [Fact]
    public async Task Changing_interval_persists_clamped_to_int()
    {
        // The NumericUpDown bound value is decimal?; the save clamps + casts to
        // int so the config (an int field) stays consistent.
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = 30m;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.Equal(30, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    // ---- automatic-updates (Premium opt-in) -------------------------------

    [Fact]
    public async Task AutomaticUpdates_defaults_false_and_round_trips_config()
    {
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true),
            configLoader: configLoader);

        Assert.False(vm.AutomaticUpdatesEnabled); // opt-in default

        vm.AutomaticUpdatesEnabled = true;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.True(configLoader.LastSaved!.Integrations.Nexus.AutomaticUpdatesEnabled);
        // And the persisted value reloads on a fresh open.
        var (vm2, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true),
            configLoader: configLoader);
        Assert.True(vm2.AutomaticUpdatesEnabled);
    }

    [Fact]
    public async Task AutomaticUpdates_is_independent_of_AutoUpdateCheckEnabled()
    {
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true),
            configLoader: configLoader);

        vm.AutomaticUpdatesEnabled = true;

        // Turning OFF the periodic check must NOT clear the automatic-update
        // setting (the two are independent; periodic checking off never disables
        // automatic installation).
        vm.AutoUpdateCheckEnabled = false;

        Assert.True(configLoader.LastSaved!.Integrations.Nexus.AutomaticUpdatesEnabled);
        Assert.False(configLoader.LastSaved.Integrations.Nexus.AutoUpdateCheckEnabled);
    }

    [Fact]
    public async Task AutomaticUpdates_enabled_only_for_verified_premium_with_premium_required_tooltip()
    {
        // Regular account: visible, disabled, with the premium-required tooltip.
        var (vmRegular, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false));
        Assert.False(vmRegular.CanEditAutomaticUpdates);
        Assert.Equal(
            Localization["Integrations_AutomaticUpdatesPremiumRequired"],
            vmRegular.AutomaticUpdatesTooltip);

        // Verified Premium account: enabled, with the normal tooltip.
        var (vmPremium, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.OAuth, "prem", IsPremium: true));
        Assert.True(vmPremium.CanEditAutomaticUpdates);
        Assert.Equal(
            Localization["Integrations_AutomaticUpdatesTooltip"],
            vmPremium.AutomaticUpdatesTooltip);
    }

    [Fact]
    public async Task AutomaticUpdates_preserves_a_configured_true_when_premium_unavailable()
    {
        // A configured true value stays visible + checked even when the account
        // is not verified Premium (the user may have let Premium lapse); runtime
        // execution re-verifies before installing.
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutomaticUpdatesEnabled = true;
        var (vm, _, _, _) = await BuildAndRefresh(
            state: new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
            configLoader: configLoader);

        Assert.True(vm.AutomaticUpdatesEnabled); // preserved
        Assert.False(vm.CanEditAutomaticUpdates); // but disabled (not premium)
    }

    [Fact]
    public async Task Empty_interval_persists_as_default_and_clamps_above_max()
    {
        // A cleared NumericUpDown (null) defaults to 10 on save; values above
        // 1440 clamp down so the runner never gets an unreasonable interval.
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = null;
        Assert.Equal(10, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);

        vm.AutoUpdateCheckIntervalMinutes = 5000m;
        Assert.Equal(1440, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task Interval_below_floor_clamps_to_floor()
    {
        // Values below the 5-minute compliance floor (here 0) clamp up to
        // NexusConfig.MinAutoUpdateCheckIntervalMinutes on save.
        var configLoader = new FakeConfigLoader();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = 0m;

        Assert.Equal(
            NexusConfig.MinAutoUpdateCheckIntervalMinutes,
            configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);

        // A value above zero but still below the floor (3) also clamps up.
        vm.AutoUpdateCheckIntervalMinutes = 3m;

        Assert.Equal(
            NexusConfig.MinAutoUpdateCheckIntervalMinutes,
            configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    // ---- nxm handler registration -----------------------------------------

    [Fact]
    public async Task RefreshAsync_shows_not_registered_status_when_registrar_reports_false()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };
        var (vm, _, _, _) = await BuildAndRefresh(state: null, registrar: registrar);

        Assert.True(vm.IsNxmAvailable);
        Assert.False(vm.IsNxmRegistered);
        Assert.Equal(Localization["Integrations_NxmStatusNotRegistered"], vm.NxmStatusText);
        Assert.Equal(Localization["Integrations_NxmRegisterLabel"], vm.NxmActionLabel);
        Assert.True(vm.ToggleNxmHandlerCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshAsync_shows_registered_status_when_registrar_reports_true()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var (vm, _, _, _) = await BuildAndRefresh(state: null, registrar: registrar);

        Assert.True(vm.IsNxmRegistered);
        Assert.Equal(Localization["Integrations_NxmStatusRegistered"], vm.NxmStatusText);
        Assert.Equal(Localization["Integrations_NxmUnregisterLabel"], vm.NxmActionLabel);
    }

    [Fact]
    public async Task RefreshAsync_shows_unavailable_when_no_registrar()
    {
        // No registrar (unsupported platform): unavailable status + the toggle
        // command is disabled.
        var (vm, _, _, _) = await BuildAndRefresh(state: null, registrar: null);

        Assert.False(vm.IsNxmAvailable);
        Assert.False(vm.IsNxmRegistered);
        Assert.Equal(Localization["Integrations_NxmStatusUnavailable"], vm.NxmStatusText);
        Assert.False(vm.ToggleNxmHandlerCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleNxmHandler_register_confirms_before_registering()
    {
        // The register path is a system-wide change; it must show a confirm
        // first and only call Register() on Yes.
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmCalls);
        // The confirm message warns about the system-wide effect.
        Assert.Contains("system-wide", dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Equal(1, registrar.RegisterCalls);
        Assert.True(vm.IsNxmRegistered); // state refreshed
    }

    [Fact]
    public async Task ToggleNxmHandler_register_cancelled_does_not_register()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };
        var dialogs = new FakeDialogService { ConfirmResult = false }; // user says No
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(0, registrar.RegisterCalls);
        Assert.False(vm.IsNxmRegistered);
    }

    [Fact]
    public async Task ToggleNxmHandler_register_failure_shows_alert_and_keeps_status()
    {
        var registrar = new FakeNxmHandlerRegistrar
        {
            Registered = false,
            ThrowOnRegister = new UnauthorizedAccessException("denied"),
        };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(1, registrar.RegisterCalls);
        // A failure surfaces a localized alert.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Integrations_NxmRegisterFailedTitle"], alert.Title);
        Assert.Contains("denied", alert.Message, StringComparison.Ordinal);
        // The probe still reports not registered (Register threw before flipping).
        Assert.False(vm.IsNxmRegistered);
    }

    [Fact]
    public async Task ToggleNxmHandler_unregister_only_calls_when_curator_owns_handler()
    {
        // Unregister only runs when IsRegistered() is true (Curator is the
        // current owner). The toggle is in the registered state, so the user
        // clicked Release.
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var dialogs = new FakeDialogService();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        // No confirm on the unregister path (it only releases Curator's own
        // registration).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(1, registrar.UnregisterCalls);
        Assert.False(vm.IsNxmRegistered); // state refreshed
    }

    [Fact]
    public async Task ToggleNxmHandler_unregister_skips_when_curator_no_longer_owner()
    {
        // The toggle is in the registered state, but the OS state changed
        // out-of-band so the pre-unregister probe reports false. Unregister
        // must NOT be called (Curator is no longer the owner); the VM just
        // refreshes its state.
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var dialogs = new FakeDialogService();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        // Simulate another manager taking over between the refresh + the click.
        registrar.Registered = false;

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(0, registrar.UnregisterCalls);
        Assert.False(vm.IsNxmRegistered); // state re-synced to the real owner
    }

    [Fact]
    public async Task ToggleNxmHandler_unregister_failure_shows_alert_and_keeps_status()
    {
        // Unregister throws (e.g. the OS handler entry is locked). The failure
        // surfaces a localized alert; the state is refreshed afterward so the
        // toggle reflects whatever the registrar now reports.
        var registrar = new FakeNxmHandlerRegistrar
        {
            Registered = true,
            ThrowOnUnregister = new UnauthorizedAccessException("locked"),
        };
        var dialogs = new FakeDialogService();
        var (vm, _, _, _) = await BuildAndRefresh(state: null, dialogs: dialogs, registrar: registrar);

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(1, registrar.UnregisterCalls);
        // A failure surfaces a localized alert.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Integrations_NxmUnregisterFailedTitle"], alert.Title);
        Assert.Contains("locked", alert.Message, StringComparison.Ordinal);
        // The probe still reports registered (Unregister threw before flipping).
        Assert.True(vm.IsNxmRegistered);
    }

    [Fact]
    public async Task ToggleNxmHandler_remains_usable_when_command_disabled_without_registrar()
    {
        // With no registrar, ToggleNxmHandlerCommand.CanExecute is false and the
        // command is a defensive no-op when invoked directly.
        var (vm, _, dialogs, _) = await BuildAndRefresh(state: null, registrar: null);

        Assert.False(vm.ToggleNxmHandlerCommand.CanExecute(null));

        await vm.ToggleNxmHandlerCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<(IntegrationsViewModel vm, FakeNexusAuthService auth, FakeDialogService dialogs, FakeNxmHandlerRegistrar? registrar)> BuildAndRefresh(
        NexusAuthState? state = null,
        FakeConfigLoader? configLoader = null,
        FakeDialogService? dialogs = null,
        FakeNxmHandlerRegistrar? registrar = null)
    {
        var auth = new FakeNexusAuthService { CurrentState = state };
        configLoader ??= new FakeConfigLoader();
        dialogs ??= new FakeDialogService();

        var vm = new IntegrationsViewModel(auth, Localization, configLoader, dialogs, registrar, Logger);
        await vm.RefreshAsync(); // resolve the initial status line + nxm state
        return (vm, auth, dialogs, registrar);
    }

    /// <summary>
    /// A recording <see cref="INexusAuthService"/>: returns preset results for
    /// each call + records what the VM invoked. No real network.
    /// </summary>
    private sealed class FakeNexusAuthService : INexusAuthService
    {
        // Required by the interface; unused by the Integrations VM tests.
        public event EventHandler? AuthStateChanged
        {
            add { }
            remove { }
        }

        public NexusAuthState? CurrentState { get; set; }

        public NexusAuthResult NextOAuthResult { get; set; } =
            NexusAuthResult.Success("OAuthUser", isPremium: false);
        public NexusAuthResult NextApiKeyResult { get; set; } =
            NexusAuthResult.Success("ApiUser", isPremium: false);
        public Task<NexusAuthResult>? NextOAuthTask { get; set; }

        // When true, LoginWithOAuthAsync returns a task that completes as
        // canceled when the supplied CancellationToken fires, so a test can
        // prove the VM cancels an in-flight login on Detach.
        public bool CancelOAuthOnToken { get; set; }

        // The CancellationToken the VM passed into the last LoginWithOAuthAsync
        // call, so a test can assert the VM forwards a real (non-default) token.
        public CancellationToken LastOAuthCancellationToken { get; private set; }

        // The task returned by the last LoginWithOAuthAsync call (when
        // CancelOAuthOnToken is set), so a test can await it and assert it
        // cancels.
        public Task<NexusAuthResult>? LastOAuthTask { get; private set; }

        // After a successful OAuth/API-key login, the VM calls GetCurrentStateAsync
        // to resolve the verified name. Tests drive the resulting status line by
        // setting these.
        public NexusAuthState? NextStateAfterOAuth { get; set; }
        public NexusAuthState? NextStateAfterApiKey { get; set; }

        public int OAuthLoginCalls { get; private set; }
        public int ApiKeyLoginCalls { get; private set; }
        public int SignOutCalls { get; private set; }
        public int GetCurrentStateCalls { get; private set; }
        public string? LastApiKey { get; private set; }

        public Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default)
        {
            OAuthLoginCalls++;
            LastOAuthCancellationToken = ct;
            if (CancelOAuthOnToken)
            {
                // Return a task that only completes when the VM cancels the token
                // it handed us (Detach flips it). Records the task so the test can
                // await it and assert IsCanceled.
                var tcs = new TaskCompletionSource<NexusAuthResult>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                LastOAuthTask = tcs.Task;
                return tcs.Task;
            }
            if (NextOAuthTask is not null)
            {
                // Chain: after the awaited task completes, the VM will call
                // GetCurrentStateAsync. Make that return the NextStateAfterOAuth.
                var capturedState = NextStateAfterOAuth;
                return NextOAuthTask.ContinueWith(t =>
                {
                    CurrentState = capturedState;
                    return t.Result;
                }, TaskScheduler.Default);
            }
            CurrentState = NextStateAfterOAuth;
            return Task.FromResult(NextOAuthResult);
        }

        public Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default)
        {
            ApiKeyLoginCalls++;
            LastApiKey = apiKey;
            if (NextApiKeyResult.IsSuccess)
            {
                CurrentState = NextStateAfterApiKey;
            }
            return Task.FromResult(NextApiKeyResult);
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            SignOutCalls++;
            CurrentState = null;
            return Task.CompletedTask;
        }

        public Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default)
        {
            GetCurrentStateCalls++;
            return Task.FromResult(CurrentState);
        }
    }
}
