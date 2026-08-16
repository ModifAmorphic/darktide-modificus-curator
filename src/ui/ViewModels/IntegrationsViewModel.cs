using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Nxm;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Nexus content (hosted by
/// <see cref="Views.IntegrationsView"/>). Nexus-only: two clearly alternative, visually separated blocks, "Sign in to
/// Nexus" (OAuth) and "Use an API key", only one of which is active at a time.
/// The active method is shown by the method-aware status line ("Signed in as X
/// (Premium) via Nexus login" vs "...via API key"). The OAuth block is a single
/// dual-state button (the same two-button visibility-toggle pattern as the
/// API-key field's eye toggle): "Sign in to Nexus" when not signed in via OAuth
/// (starts the OAuth flow), "Clear Nexus sign-in" when signed in via OAuth
/// (clears the stored tokens / signs out). There is no separate Sign out in
/// the OAuth block: the signed-in state is the clear path, so there is no
/// re-login-over-existing (to re-login you Clear then Sign in). The
/// API-key field is masked by default, persisted across reopens (so the user
/// sees one is configured), revealed on a Show eye toggle, + re-validatable
/// without re-entering. Auth controls stay usable while Darktide runs (only
/// launch + active-profile changes are blocked while the game runs). Below the
/// auth blocks, an "Update checks" sub-section holds the periodic update-check
/// toggle + interval, persisted live through <see cref="IConfigLoader"/>
/// (read-modify-save on each change). A final "Nexus download links" section
/// exposes the explicit OS <c>nxm://</c> handler registration: register is a
/// confirm-first action (it is a system-wide change that can affect other mod
/// managers); unregister only releases Curator's own registration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> An application-lifetime singleton. <see cref="RefreshAsync"/>
/// is the activation operation (entering Integrations);
/// <see cref="Deactivate"/> is the navigation-away operation (cancels only the
/// in-flight auth so the loopback listener releases promptly, without
/// unsubscribing localization). There is no detach path: the VM stays
/// subscribed for the application lifetime.</para>
/// <para>
/// <b>Auth method is the user's explicit choice.</b> Clicking "Sign in to
/// Nexus" starts the OAuth loopback flow (<c>AuthMethod = OAuth</c>); pasting +
/// validating an API key sets <c>AuthMethod = ApiKey</c>; Sign out (the OAuth
/// block's "Clear Nexus sign-in" button, or the API-key block's Sign out)
/// resets to <c>None</c>. Switching methods clears the other method's
/// credentials (handled in <see cref="NexusAuthService"/>). One active method at
/// a time, no leftovers.</para>
/// <para>
/// <b>Status line is resolved server-side on activation + after each
/// action.</b> <see cref="RefreshAsync"/> calls
/// <see cref="NexusAuthService.GetCurrentStateAsync"/> to resolve the current
/// display name + premium state (one network call). A failed verify (network or
/// stale credentials) yields a method-aware "signed in (unverified)" status; the
/// user can still sign out.</para>
/// <para>
/// <b>Masked API-key field.</b> When the configured method is <c>ApiKey</c>,
/// the field shows the persisted key masked (via <see cref="ApiKeyMaskChar"/>);
/// a Show eye toggle flips the mask char to <c>'\0'</c> (plain). The field is
/// bound two-way to <see cref="ApiKey"/>: the user can paste a new key (replacing
/// the displayed one) + click Validate to switch methods. The Validate button
/// always validates whatever <see cref="ApiKey"/> currently holds, so it
/// re-validates the existing masked key when the field has not been touched +
/// validates a freshly typed key otherwise.</para>
/// <para>
/// <b>Localization.</b> Every user-facing string resolves through
/// <see cref="LocalizationService"/>; the bound properties re-resolve on a
/// culture flip.</para>
/// </remarks>
public partial class IntegrationsViewModel : LocalizedViewModel
{
    private readonly INexusAuthService _auth;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly INxmHandlerRegistrar? _nxmRegistrar;
    private readonly INxmRegistrationState _nxmRegistration;
    private readonly IExternalLauncher _externalLauncher;
    private readonly ILogger<IntegrationsViewModel> _logger;

    // Backs the in-flight OAuth login or API-key validate. Swapped per attempt
    // via NewLoginToken + canceled on Deactivate (navigation away). Lets the two
    // auth commands cancel each other's in-flight call, and frees the loopback
    // listener promptly when the user leaves Integrations mid-flight.
    private CancellationTokenSource? _loginCts;

    public IntegrationsViewModel(
        INexusAuthService auth,
        LocalizationService localization,
        IConfigLoader configLoader,
        IDialogService dialogs,
        INxmHandlerRegistrar? nxmRegistrar,
        INxmRegistrationState nxmRegistration,
        IExternalLauncher externalLauncher,
        ILogger<IntegrationsViewModel> logger)
        : base(localization)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _nxmRegistrar = nxmRegistrar;
        _nxmRegistration = nxmRegistration ?? throw new ArgumentNullException(nameof(nxmRegistration));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    }

    // ---- state -----------------------------------------------------------

    /// <summary>
    /// The currently configured Nexus auth method (None / OAuth / ApiKey),
    /// mirrored from the auth state on every refresh. Drives the per-block
    /// active indicator (the Sign out button visibility) + the
    /// <see cref="IsOAuthActive"/> / <see cref="IsApiKeyActive"/> helpers the
    /// view binds visibility to.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuthActive))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyActive))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private NexusAuthMethod _activeMethod = NexusAuthMethod.None;

    /// <summary>Whether OAuth is the currently configured method (block-active
    /// indicator for the view).</summary>
    public bool IsOAuthActive => ActiveMethod == NexusAuthMethod.OAuth;

    /// <summary>Whether API key is the currently configured method (block-active
    /// indicator for the view).</summary>
    public bool IsApiKeyActive => ActiveMethod == NexusAuthMethod.ApiKey;

    /// <summary>
    /// Whether the Integrations view's API-key block is shown. Read live from
    /// <c>NexusConfig.ApiKeyAuthEnabled</c> on activation (a developer-only
    /// config.json toggle; there is no UI control for it). Default false: the
    /// API-key block is hidden and OAuth is the sole sign-in path unless the flag
    /// is set in config.json.
    /// </summary>
    [ObservableProperty]
    private bool _isApiKeyAuthEnabled;

    /// <summary>
    /// The API key as the TextBox sees it. Two-way bound. When the configured
    /// method is <c>ApiKey</c>, <see cref="RefreshAsync"/> populates this with
    /// the persisted key (the field masks it via
    /// <see cref="ApiKeyMaskChar"/>); the user can paste a new key over it +
    /// click Validate to switch. When the method is <c>None</c> or <c>OAuth</c>,
    /// this is <see cref="string.Empty"/> (the field shows the placeholder).
    /// </summary>
    /// <remarks>
    /// Carries the real key in-process only; the masking is purely visual (the
    /// TextBox's PasswordChar). The Show toggle (<see cref="IsApiKeyRevealed"/>)
    /// flips the mask so the user can verify / copy the key.
    /// </remarks>
    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// Whether the API-key field is in revealed (plain) mode. The Show eye
    /// toggle flips this; <see cref="ApiKeyMaskChar"/> recomputes on flip so
    /// the TextBox re-paints. Defaults to <c>false</c> (masked).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyMaskChar))]
    private bool _isApiKeyRevealed;

    /// <summary>
    /// The mask char the API-key TextBox binds to its <c>PasswordChar</c>.
    /// <c>'\0'</c> when revealed (no masking, plain text); <c>'\u2022'</c>
    /// (bullet) when masked. Recomputes on a
    /// <see cref="IsApiKeyRevealed"/> flip.
    /// </summary>
    public char ApiKeyMaskChar => IsApiKeyRevealed ? '\0' : '\u2022';

    /// <summary>
    /// The status line text, resolved through <see cref="LocalizationService"/>,
    /// reflecting the signed-in state (the API-key path appends a "via API key"
    /// suffix). Re-resolves on a culture change. Updated by
    /// <see cref="RefreshAsync"/>.
    /// </summary>
    [ObservableProperty]
    private string _statusLine = string.Empty;

    /// <summary>
    /// Whether a Nexus auth method is currently configured (OAuth or ApiKey).
    /// Drives the Sign-out button availability (sign-out only enabled when
    /// authenticated). Notifies <see cref="SignOutCommand"/> so a refresh that
    /// flips this without an <see cref="IsBusy"/> toggle (e.g. the on-enter
    /// <see cref="RefreshAsync"/> resolving an authenticated state) still
    /// re-evaluates the Sign-out CanExecute.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private bool _isAuthenticated;

    /// <summary>
    /// Whether the verified Nexus account is Premium. Read from the auth state on
    /// open + after each auth action + after a culture flip (which re-resolves
    /// state). Drives the automatic-updates checkbox's enabled state + tooltip:
    /// a verified Premium user can toggle it on; a regular or unverified account
    /// sees it visible, checked (preserving any configured value), and disabled
    /// with a Premium-required explanation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAutomaticUpdates))]
    [NotifyPropertyChangedFor(nameof(AutomaticUpdatesTooltip))]
    private bool _isPremiumVerified;

    /// <summary>
    /// Whether the VM is mid-flight on an OAuth login or API-key validate
    /// (both are async + hit the network). Disables the buttons + shows a
    /// "working" status while in flight so the user gets feedback that the click
    /// registered.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginWithOAuthCommand))]
    [NotifyCanExecuteChangedFor(nameof(ValidateApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleApiKeyRevealCommand))]
    private bool _isBusy;

    // ---- update-check settings -------------------------------------------

    /// <summary>
    /// Set while <see cref="LoadAutoUpdateSettings"/> populates the toggle +
    /// interval from the live config so the resulting property-change handlers
    /// do not write back (which would be a no-op round-trip on every activation
    /// open). Cleared after the load completes; user-driven changes then persist
    /// through <see cref="OnAutoUpdateCheckEnabledChanged"/> /
    /// <see cref="OnAutoUpdateCheckIntervalMinutesChanged"/>.
    /// </summary>
    private bool _isLoadingAutoUpdate;

    /// <summary>
    /// Whether the periodic background update check runs while a profile is
    /// active. Loaded live from <c>NexusConfig.AutoUpdateCheckEnabled</c> on
    /// activation; persisted on each user change via read-modify-save. The
    /// toggle gates ONLY the periodic timer (profile-load + manual checks still
    /// run); the runner reads it live, so a change here takes effect without a
    /// restart.
    /// </summary>
    [ObservableProperty]
    private bool _autoUpdateCheckEnabled;

    /// <summary>
    /// The periodic update-check interval, in minutes, as the
    /// <c>NumericUpDown</c> sees it (decimal to match the control's Value type).
    /// Two-way bound; persisted on each user change via read-modify-save,
    /// clamped to [<see cref="NexusConfig.MinAutoUpdateCheckIntervalMinutes"/>,
    /// <see cref="NexusConfig.MaxAutoUpdateCheckIntervalMinutes"/>] on write.
    /// Loaded from <c>NexusConfig.AutoUpdateCheckIntervalMinutes</c> on activation
    /// open.
    /// </summary>
    [ObservableProperty]
    private decimal? _autoUpdateCheckIntervalMinutes;

    /// <summary>
    /// Whether Premium accounts have flagged mod updates installed automatically
    /// after a check runs (opt-in, default false). Loaded live from
    /// <c>NexusConfig.AutomaticUpdatesEnabled</c> on activation; persisted on
    /// each user change via read-modify-save. Independent of
    /// <see cref="AutoUpdateCheckEnabled"/>: turning this on never requires
    /// periodic checking, and changing the periodic-check toggle never clears a
    /// configured <c>true</c> value here.
    /// </summary>
    [ObservableProperty]
    private bool _automaticUpdatesEnabled;

    /// <summary>
    /// Persisted when the user flips <see cref="AutoUpdateCheckEnabled"/>.
    /// Skipped during the activation load (guarded by
    /// <c>_isLoadingAutoUpdate</c>) so populating the field from config does not
    /// trigger a redundant write-back round-trip.
    /// </summary>
    partial void OnAutoUpdateCheckEnabledChanged(bool value) => SaveAutoUpdateSettings();

    /// <summary>
    /// Persisted when the user edits <see cref="AutoUpdateCheckIntervalMinutes"/>.
    /// Skipped during the activation load (guarded by
    /// <c>_isLoadingAutoUpdate</c>).
    /// </summary>
    partial void OnAutoUpdateCheckIntervalMinutesChanged(decimal? value) => SaveAutoUpdateSettings();

    /// <summary>
    /// Persisted when the user flips <see cref="AutomaticUpdatesEnabled"/>.
    /// Skipped during the activation load (guarded by
    /// <c>_isLoadingAutoUpdate</c>). Independent of
    /// <see cref="OnAutoUpdateCheckEnabledChanged"/>: toggling the periodic check
    /// never touches <c>AutomaticUpdatesEnabled</c>, so a configured true value
    /// survives turning periodic checking off (and vice versa).
    /// </summary>
    partial void OnAutomaticUpdatesEnabledChanged(bool value) => SaveAutoUpdateSettings();

    /// <summary>
    /// Read-modify-saves the toggle + interval + automatic-updates setting into
    /// the live config so the runner picks them up on its next tick. Best-effort
    /// (the ConfigLoader swallows write failures); clamps the interval to
    /// [<see cref="NexusConfig.MinAutoUpdateCheckIntervalMinutes"/>,
    /// <see cref="NexusConfig.MaxAutoUpdateCheckIntervalMinutes"/>] minutes + null
    /// defaults to 10. No-op while <c>_isLoadingAutoUpdate</c> is set.
    /// </summary>
    private void SaveAutoUpdateSettings()
    {
        if (_isLoadingAutoUpdate)
        {
            return;
        }

        var config = _configLoader.Load();
        config.Integrations.Nexus.AutoUpdateCheckEnabled = AutoUpdateCheckEnabled;
        config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes =
            (int)Math.Clamp(AutoUpdateCheckIntervalMinutes ?? 10,
                NexusConfig.MinAutoUpdateCheckIntervalMinutes,
                NexusConfig.MaxAutoUpdateCheckIntervalMinutes);
        // Independent of the periodic-check settings: this is preserved exactly
        // as toggled, never cleared when periodic checking changes.
        config.Integrations.Nexus.AutomaticUpdatesEnabled = AutomaticUpdatesEnabled;
        _configLoader.Save(config);
    }

    /// <summary>
    /// Loads the toggle + interval + automatic-updates setting from the live
    /// config into the bound properties, suppressing the change-triggered save
    /// while populating. Called from <see cref="RefreshAsync"/> so the dialog
    /// reflects the persisted state on every open (a prior session may have
    /// changed it).
    /// </summary>
    private void LoadAutoUpdateSettings()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        _isLoadingAutoUpdate = true;
        try
        {
            AutoUpdateCheckEnabled = nexus.AutoUpdateCheckEnabled;
            AutoUpdateCheckIntervalMinutes = nexus.AutoUpdateCheckIntervalMinutes;
            AutomaticUpdatesEnabled = nexus.AutomaticUpdatesEnabled;
        }
        finally
        {
            _isLoadingAutoUpdate = false;
        }
    }

    /// <summary>
    /// Whether the automatic-updates checkbox is enabled: only a verified Premium
    /// account can opt in. A regular or unverified account sees the checkbox
    /// visible (preserving any configured value) but disabled, with the
    /// Premium-required tooltip explaining why.
    /// </summary>
    public bool CanEditAutomaticUpdates => IsPremiumVerified;

    /// <summary>
    /// The automatic-updates checkbox tooltip, distinguished by the account state:
    /// a verified Premium user gets the normal explanation; a regular or
    /// unverified account gets the Premium-required explanation. The view sets
    /// <c>ToolTip.ShowOnDisabled</c> so the latter shows even while the checkbox
    /// is disabled.
    /// </summary>
    public string AutomaticUpdatesTooltip => IsPremiumVerified
        ? _localization["Integrations_AutomaticUpdatesTooltip"]
        : _localization["Integrations_AutomaticUpdatesPremiumRequired"];

    // ---- localized labels -------------------------------------------------

    public string NexusHeader => _localization["Integrations_NexusHeader"];
    public string LoginWithOAuthLabel => _localization["Integrations_LoginWithNexus"];
    public string ClearNexusSignInLabel => _localization["Integrations_ClearNexusSignIn"];
    public string ApiKeyLabel => _localization["Integrations_ApiKeyLabel"];
    public string ValidateLabel => _localization["Integrations_ValidateButton"];
    public string ApiKeyHelpLink => _localization["Integrations_ApiKeyHelpUrl"];
    public string ApiKeyHelpLabel => _localization["Integrations_ApiKeyHelp"];
    public string SignOutLabel => _localization["Integrations_SignOutButton"];
    public string AutoUpdateHeader => _localization["Integrations_AutoUpdateHeader"];
    public string AutoUpdateEnabledLabel => _localization["Integrations_AutoUpdateEnabled"];
    public string AutoUpdateIntervalLabel => _localization["Integrations_AutoUpdateInterval"];
    public string AutomaticUpdatesLabel => _localization["Integrations_AutomaticUpdates"];
    public string ShowApiKeyTooltip => _localization["Integrations_ShowApiKeyTooltip"];
    public string HideApiKeyTooltip => _localization["Integrations_HideApiKeyTooltip"];

    // ---- commands --------------------------------------------------------

    /// <summary>
    /// Opens the Nexus API-keys page (where the user gets their key) in the
    /// default browser via the injected <see cref="IExternalLauncher"/>. The
    /// link's tooltip carries the URL (<see cref="ApiKeyHelpLink"/>) so the
    /// user can copy it manually; a failed or throwing open is silently
    /// ignored (best-effort; the help link is not a critical action).
    /// </summary>
    [RelayCommand]
    private void OpenApiKeyHelp()
    {
        try
        {
            if (Uri.TryCreate(ApiKeyHelpLink, UriKind.Absolute, out var uri))
            {
                _externalLauncher.OpenUri(uri);
            }
        }
        catch
        {
            // Best-effort: the URL stays available in the tooltip.
        }
    }

    /// <summary>
    /// Starts the Nexus OAuth loopback flow: opens the browser, awaits the
    /// callback, exchanges for tokens, and reads the display name + Premium
    /// state from the access token's JWT payload. Updates the status line on
    /// success; surfaces a localized error inline on failure. Disabled while
    /// the game runs or another auth op is in flight.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAuth))]
    private async Task LoginWithOAuth()
    {
        IsBusy = true;
        StatusLine = _localization["Integrations_StartingOAuth"];
        try
        {
            var token = NewLoginToken();
            var result = await _auth.LoginWithOAuthAsync(token);
            await RefreshStatusAsync(result);
        }
        catch (OperationCanceledException)
        {
            // Expected when the VM is deactivated mid-login (navigation away or
            // navigation away); not an error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus OAuth login threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Validates the API key currently held in <see cref="ApiKey"/>. On success,
    /// sets <c>AuthMethod = ApiKey</c> + clears any OAuth tokens + updates the
    /// status line. On failure, surfaces the error inline + keeps the entered
    /// key so the user can correct it. The field always validates whatever it
    /// holds, so this works equally for re-validating an existing persisted key
    /// (when the field shows the masked key on dialog reopen) and for validating
    /// a freshly typed key.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAuth))]
    private async Task ValidateApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusLine = _localization["Integrations_ApiKeyEmpty"];
            return;
        }

        IsBusy = true;
        StatusLine = _localization["Integrations_Validating"];
        try
        {
            var token = NewLoginToken();
            var result = await _auth.LoginWithApiKeyAsync(ApiKey, token);
            await RefreshStatusAsync(result);
        }
        catch (OperationCanceledException)
        {
            // Expected when the VM is deactivated mid-validate (navigation away
            // or navigation away); not an error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus API-key validate threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Flips <see cref="IsApiKeyRevealed"/> so <see cref="ApiKeyMaskChar"/>
    /// swaps between bullet + plain, re-painting the field. Disabled only while
    /// a login is in flight (the field is meaningless to toggle mid-network-op);
    /// the running gate is not enforced here because revealing a masked key
    /// while the game runs is a read-only op that changes no credentials.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleReveal))]
    private void ToggleApiKeyReveal()
    {
        IsApiKeyRevealed = !IsApiKeyRevealed;
        // Re-resolve the tooltip so the eye toggle's accessible name flips with
        // the state ("Show" vs "Hide").
        OnPropertyChanged(nameof(ShowApiKeyTooltip));
        OnPropertyChanged(nameof(HideApiKeyTooltip));
    }

    /// <summary>
    /// Signs out: clears the persisted OAuth tokens + API key + sets
    /// <c>AuthMethod = None</c>. Idempotent. Routed to by both blocks' sign-out
    /// affordances: the OAuth block's "Clear Nexus sign-in" button + the
    /// API-key block's Sign out button. Only the active block's is visible at a
    /// time (the OAuth block's Clear button is bound to
    /// <see cref="IsOAuthActive"/>; the API-key block's Sign out to
    /// <see cref="IsApiKeyActive"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOut()
    {
        IsBusy = true;
        try
        {
            await _auth.SignOutAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus sign-out threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Auth controls stay usable while Darktide runs (only launch + active-
    // profile changes are blocked). The IsBusy + IsAuthenticated gates remain.
    private bool CanStartAuth() => !IsBusy;
    private bool CanSignOut() => !IsBusy && IsAuthenticated;
    private bool CanToggleReveal() => !IsBusy;

    /// <summary>
    /// Cancels + disposes any prior login CTS and returns a fresh token for a
    /// new auth attempt. OAuth login and API-key validate share this so the two
    /// commands cancel each other's in-flight call (one auth attempt at a time),
    /// and <see cref="Deactivate"/> cancels whichever is in flight when the user
    /// navigates away from Integrations.
    /// </summary>
    private CancellationToken NewLoginToken()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();
        return _loginCts.Token;
    }

    // ---- live state -------------------------------------------------------

    /// <summary>
    /// Refreshes the status line + active-method indicator + masked-key field
    /// from the persisted auth state, the update-check toggle + interval from
    /// the persisted config, and the nxm handler registration state from the OS
    /// (one probe through the shared registration state). Called by the
    /// shell's Nexus-enter effect, which is the registration state's deliberate
    /// probe point. Resolves the display name +
    /// premium state by method: OAuth reads it from the access token's JWT
    /// payload in memory; API key hits <c>/v1/users/validate.json</c>.
    /// </summary>
    public async Task RefreshAsync()
    {
        var state = await _auth.GetCurrentStateAsync();
        ApplyState(state);
        LoadAutoUpdateSettings();
        RefreshNxmState();
        IsApiKeyAuthEnabled = _configLoader.Load().Integrations.Nexus.ApiKeyAuthEnabled;
    }

    /// <summary>
    /// Refreshes the status line from an explicit auth result (the
    /// just-completed OAuth login or API-key validate), then re-resolves the
    /// server-side state for the verified name + premium flag.
    /// </summary>
    private async Task RefreshStatusAsync(NexusAuthResult result)
    {
        if (!result.IsSuccess)
        {
            // Surface the failure inline; do NOT re-resolve (the network just
            // failed, no point pinging again).
            StatusLine = _localization.Format("Integrations_ErrorFormat", result.ErrorMessage ?? string.Empty);
            return;
        }

        // Success: re-resolve the verified state. If the network fails here we
        // fall back to a method-aware signed-in state.
        var state = await _auth.GetCurrentStateAsync();
        ApplyState(state);
    }

    private void ApplyState(NexusAuthState? state)
    {
        ActiveMethod = state?.Method ?? NexusAuthMethod.None;
        IsAuthenticated = state is not null;
        IsPremiumVerified = state?.IsPremium == true;
        // The API-key field reflects the persisted key when the method is
        // ApiKey (so the user sees one is configured, masked, + can re-validate
        // without re-entering); empty otherwise (placeholder visible). Clearing
        // the reveal flag on each apply keeps the field masked by default after
        // a method switch / sign-out.
        ApiKey = state is { Method: NexusAuthMethod.ApiKey, ApiKey: { } key }
            ? key
            : string.Empty;
        IsApiKeyRevealed = false;

        StatusLine = state switch
        {
            null => _localization["Integrations_StatusNotSignedIn"],

            { Method: NexusAuthMethod.OAuth, Name: { } name, IsPremium: true } =>
                _localization.Format("Integrations_StatusSignedInOAuthPremium", name),
            { Method: NexusAuthMethod.OAuth, Name: { } name } =>
                _localization.Format("Integrations_StatusSignedInOAuth", name),
            { Method: NexusAuthMethod.OAuth } =>
                _localization["Integrations_StatusSignedInOAuthUnverified"],

            { Method: NexusAuthMethod.ApiKey, Name: { } name, IsPremium: true } =>
                _localization.Format("Integrations_StatusSignedInApiKeyPremium", name),
            { Method: NexusAuthMethod.ApiKey, Name: { } name } =>
                _localization.Format("Integrations_StatusSignedInApiKey", name),
            { Method: NexusAuthMethod.ApiKey } =>
                _localization["Integrations_StatusSignedInApiKeyUnverified"],

            // Defensive: any other method falls back to the generic signed-in
            // line. Not expected in practice (None is null above; OAuth +
            // ApiKey are the only non-None values).
            _ => _localization["Integrations_StatusSignedInGeneric"],
        };
    }

    // ---- nxm handler registration ----------------------------------------

    /// <summary>
    /// Whether a platform <see cref="INxmHandlerRegistrar"/> is available.
    /// Mirrored from the shared registration state (false on platforms other
    /// than Windows + Linux); the NXM controls show an unavailable state + the
    /// toggle is disabled. Drives <see cref="CanToggleNxmHandler"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleNxmHandlerCommand))]
    private bool _isNxmAvailable = true;

    /// <summary>
    /// Whether Curator is currently the OS <c>nxm://</c> handler, mirrored from
    /// the shared registration state after each of its deliberate probes (the
    /// Nexus-enter refresh + each register/release action). Drives the status
    /// line, the toggle button label, and which branch the toggle command takes
    /// (register vs unregister).
    /// </summary>
    [ObservableProperty]
    private bool _isNxmRegistered;

    /// <summary>Header over the nxm section.</summary>
    public string NxmSectionHeader => _localization["Integrations_NxmHeader"];

    /// <summary>
    /// The nxm status line: registered / not registered / unavailable, resolved
    /// through <see cref="LocalizationService"/>. Re-resolves on a culture flip.
    /// </summary>
    public string NxmStatusText =>
        !IsNxmAvailable
            ? _localization["Integrations_NxmStatusUnavailable"]
            : IsNxmRegistered
                ? _localization["Integrations_NxmStatusRegistered"]
                : _localization["Integrations_NxmStatusNotRegistered"];

    /// <summary>
    /// The toggle button label: "Enable Darktide download links" when not
    /// registered, "Disable Darktide download links" when registered.
    /// Re-resolves on a culture flip.
    /// </summary>
    public string NxmActionLabel =>
        IsNxmRegistered
            ? _localization["Integrations_NxmUnregisterLabel"]
            : _localization["Integrations_NxmRegisterLabel"];

    /// <summary>
    /// The toggle button tooltip, resolved through <see cref="LocalizationService"/>
    /// for the current state. Re-resolves on a culture flip.
    /// </summary>
    public string NxmActionTooltip =>
        !IsNxmAvailable
            ? _localization["Integrations_NxmActionTooltipUnavailable"]
            : IsNxmRegistered
                ? _localization["Integrations_NxmActionTooltipRegistered"]
                : _localization["Integrations_NxmActionTooltipNotRegistered"];

    /// <summary>
    /// Toggles the OS <c>nxm://</c> handler registration. The register path
    /// first shows a confirmation dialog (it is a system-wide change that can
    /// affect Vortex / Mod Organizer 2 / Nexus Mod Manager / other managers),
    /// then calls <see cref="INxmHandlerRegistrar.Register"/>. The unregister
    /// path delegates directly to <see cref="INxmHandlerRegistrar.Unregister"/>:
    /// the registrar self-guards ownership (a logged no-op when Curator is not
    /// the current handler), so the VM performs no pre-check probe. A failure
    /// surfaces a localized alert; either way the shared registration state is
    /// refreshed once (one probe) so every consumer re-syncs. Unavailable (no
    /// registrar) is a no-op (the command is also disabled). Usable while
    /// Darktide runs (only launch + active-profile changes are blocked).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleNxmHandler))]
    private async Task ToggleNxmHandler()
    {
        if (_nxmRegistrar is null)
        {
            return;
        }

        if (!IsNxmRegistered)
        {
            // Register path: confirm first (system-wide change).
            var confirmed = await _dialogs.ConfirmAsync(
                _localization["Integrations_NxmConfirmTitle"],
                _localization["Integrations_NxmConfirmMessage"]);
            if (!confirmed)
            {
                return;
            }

            try
            {
                _nxmRegistrar.Register();
                _logger.LogInformation("Registered Curator as the nxm:// handler via Integrations.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register the nxm:// handler via Integrations.");
                await _dialogs.ShowAlertAsync(
                    _localization["Integrations_NxmRegisterFailedTitle"],
                    _localization.Format("Integrations_NxmRegisterFailedMessage", ex.Message));
            }
        }
        else
        {
            // Unregister path: the registrar's own ownership guard decides
            // whether anything is released (Curator's registration only).
            try
            {
                _nxmRegistrar.Unregister();
                _logger.LogInformation("Released the nxm:// handler via Integrations.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister the nxm:// handler via Integrations.");
                await _dialogs.ShowAlertAsync(
                    _localization["Integrations_NxmUnregisterFailedTitle"],
                    _localization.Format("Integrations_NxmUnregisterFailedMessage", ex.Message));
            }
        }

        RefreshNxmState();
    }

    private bool CanToggleNxmHandler() => IsNxmAvailable;

    /// <summary>
    /// Refreshes the shared registration state from the OS (its one deliberate
    /// probe point per Nexus enter / post-action), then copies
    /// <see cref="IsNxmAvailable"/> + <see cref="IsNxmRegistered"/> from it and
    /// fires the change notifications for the derived status/label/tooltip so
    /// the view refreshes.
    /// </summary>
    private void RefreshNxmState()
    {
        _nxmRegistration.RefreshFromOs();
        IsNxmAvailable = _nxmRegistration.IsAvailable;
        IsNxmRegistered = _nxmRegistration.IsRegistered;

        OnPropertyChanged(nameof(NxmStatusText));
        OnPropertyChanged(nameof(NxmActionLabel));
        OnPropertyChanged(nameof(NxmActionTooltip));
    }

    // ---- live state -------------------------------------------------------

    /// <summary>
    /// Re-resolves the localized strings (labels, status line) when the UI
    /// culture flips so the destination refreshes in-step with the rest of the
    /// UI on a language switch.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(NexusHeader),
        nameof(LoginWithOAuthLabel),
        nameof(ClearNexusSignInLabel),
        nameof(ApiKeyLabel),
        nameof(ValidateLabel),
        nameof(ApiKeyHelpLink),
        nameof(ApiKeyHelpLabel),
        nameof(SignOutLabel),
        nameof(AutoUpdateHeader),
        nameof(AutoUpdateEnabledLabel),
        nameof(AutoUpdateIntervalLabel),
        nameof(AutomaticUpdatesLabel),
        nameof(AutomaticUpdatesTooltip),
        nameof(ShowApiKeyTooltip),
        nameof(HideApiKeyTooltip),
        nameof(NxmSectionHeader),
        nameof(NxmStatusText),
        nameof(NxmActionLabel),
        nameof(NxmActionTooltip),
    };

    /// <summary>
    /// The status line embeds a localized format; re-resolve it by re-applying
    /// the current state. Fire-and-forget: a culture flip mid-flight is rare,
    /// and the next state-resolve will pick up the new culture.
    /// </summary>
    protected override void OnCultureChanged() => _ = RefreshAsync();

    /// <summary>
    /// Navigation-away operation: cancels + disposes the current login/API-key
    /// validation CTS so the OAuth loopback listener releases promptly instead
    /// of waiting out the flow timeout. Idempotent and safe after construction
    /// and on repeated cancellation paths. Does not unsubscribe localization:
    /// this VM is an application-lifetime singleton that stays responsive to
    /// culture changes across navigation, and a later <see cref="RefreshAsync"/>
    /// + auth attempt works normally after this.
    /// </summary>
    public void Deactivate()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = null;
    }
}
