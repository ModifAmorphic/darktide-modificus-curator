using System.ComponentModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// A small curated palette of dark, visually distinct avatar backgrounds with
/// adequate contrast against the alt-high/white letter foreground used by the
/// profile banner + picker rows. Selection is deterministic from the profile id
/// (see <see cref="ProfileChoice"/>) so a profile keeps its color across
/// reloads, sorting, and app restarts. Palette collisions are acceptable: the
/// goal is variety, not uniqueness.
/// </summary>
/// <remarks>
/// <see cref="ImmutableSolidColorBrush"/> instances: these are immutable Avalonia
/// brush values suitable for direct XAML binding (Avalonia 12 distinguishes
/// mutable brushes from immutable ones via <see cref="IImmutableBrush"/>; the
/// immutable form is safe to share across binding targets and never triggers
/// spurious change notifications). Lives in the UI assembly: color is a UI
/// concern, not a Profiles-domain concern.
/// </remarks>
internal static class ProfileAvatarPalette
{
    private static readonly IReadOnlyList<IBrush> _brushes = new IBrush[]
    {
        new ImmutableSolidColorBrush(0xFF4A6FA5), // deep blue
        new ImmutableSolidColorBrush(0xFF5B4A8C), // deep purple
        new ImmutableSolidColorBrush(0xFF2C7A7B), // deep teal
        new ImmutableSolidColorBrush(0xFF8C5B2F), // deep amber-brown
        new ImmutableSolidColorBrush(0xFF8C3A5B), // deep magenta
        new ImmutableSolidColorBrush(0xFF3A7D44), // deep green
        new ImmutableSolidColorBrush(0xFFB05B36), // rust
        new ImmutableSolidColorBrush(0xFF5C6BC0), // indigo
    };

    /// <summary>
    /// Resolves a stable avatar background for <paramref name="id"/>. The
    /// selection is a pure function of the id's byte representation (not the
    /// hash-code, which can vary by runtime; not the list index, which varies
    /// with sort + reload), so the same id always maps to the same color.
    /// </summary>
    public static IBrush For(Guid id)
    {
        // Sum the 16 bytes (a cheap, stable reduction; the goal is variety +
        // determinism, not cryptographic distribution). Modulo the palette size
        // keeps the result in range. Summing rather than XOR avoids the
        // all-same-byte symmetry of XOR (e.g. alternating words would XOR to 0).
        var bytes = id.ToByteArray();
        var sum = 0;
        foreach (var b in bytes)
        {
            sum = unchecked(sum + b);
        }
        return _brushes[sum % _brushes.Count];
    }
}

/// <summary>
/// One persisted profile projected for the banner / picker card: the stable id,
/// the display name, the trimmed description, the uppercased first letter
/// (or "?" when the name has no usable first character), and the deterministic
/// avatar background from <see cref="ProfileAvatarPalette"/>. Stateless; the
/// parent <see cref="ProfilesViewModel"/> rebuilds these from
/// <see cref="IProfileService.ListProfiles"/> on every authoritative reload.
/// </summary>
public sealed record ProfileChoice(Guid Id, string Name, string Description, string FirstLetter, IBrush AvatarBackground);

/// <summary>
/// The Profiles destination view model. Owns the full profile draft workflow:
/// the active profile editor (name + description + composed launch-settings
/// editor), the persisted-profile banner + picker, new-draft creation, atomic
/// save/cancel/delete, the running-state gates, the dirty-navigation guard, and
/// the application-lifetime session + localization subscriptions. The page edits
/// only <see cref="IProfileSession.ActiveProfileId"/>; every voluntary change
/// routes through the session's gate.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> <see cref="IProfileSession.ActiveProfileId"/> is the
/// single source of truth for "which profile is active." This VM never caches an
/// active id beyond what the session reports; on every reload it re-reads the
/// session id, then deep-loads the full <see cref="Profile"/> (metadata +
/// <see cref="LaunchSettings"/>) from <see cref="IProfileService.GetProfile"/>
/// for editing. The persisted profile is never mutated until Save.</para>
/// <para><b>Draft vs. persisted boundary:</b> editable state (<see cref="Name"/>,
/// <see cref="Description"/>, <see cref="Editor"/>) holds staged values. A new
/// draft starts from empty name + empty description + default launch settings
/// and is not itself dirty until the user types. <see cref="IsDirty"/> compares
/// the staged values against the persisted baseline captured at Load.</para>
/// <para><b>Running-state gates:</b> switching, adding, and deleting profiles
/// are disabled while Darktide runs (and re-checked defense-in-depth in the
/// command body). Editing and saving the active profile's metadata + launch
/// settings stays enabled while the game runs (a profile.json write that does
/// not touch the running process). A new draft's Save additionally requires the
/// game stopped, since saving activates the new profile.</para>
/// <para><b>DMF + mod-list reload owned by the shell:</b> this VM is narrowly
/// coupled to profile workflow. The DMF (Darktide Mod Framework) install-prompt
/// coordinator subscribes to <see cref="IProfileService.ProfileCreated"/> at
/// construction (resolved eagerly when the shell is built, before this VM can
/// create a profile), records the trigger, and the shell awaits
/// <see cref="DmfPromptService.ProcessPendingAsync"/> on the next navigation
/// into Mods. After a successful create-and-activate this VM does no DMF or
/// mod-list work; the shell's post-DMF reload surfaces an accepted install.</para>
/// <para><b>Application-lifetime subscriptions:</b> subscribes to
/// <see cref="IProfileSession.PropertyChanged"/> (active-id + IsRunning) +
/// <see cref="LocalizationService.PropertyChanged"/> (culture) exactly once at
/// construction. There is no per-navigation subscribe/detach; this VM is a
/// hosted singleton for the application lifetime.</para>
/// <para><b>No <c>ConfigureAwait(false)</c></b> anywhere: the UI layer stays on
/// the captured UI context (repo convention).</para>
/// </remarks>
public partial class ProfilesViewModel : LocalizedViewModel
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ProfilesViewModel> _logger;

    // Persisted baseline captured at Load so IsDirty compares staged values
    // against the last reload, not against whatever the user happens to have
    // typed. Empty for a new draft (so an untouched draft is not dirty).
    private string _persistedName = string.Empty;
    private string _persistedDescription = string.Empty;

    // The active id at the last reload. Tracked separately from the session's
    // current id so a programmatic change (session fires PropertyChanged) is
    // detectable: a divergence means an outside authority displaced our draft.
    private Guid? _activeId;

    // True while this VM is driving the session through one of its own guarded
    // commands (Save new, SelectProfile). Suppresses the session event handler's
    // stale-draft displacement branch for the active-id change the command
    // itself just caused, so it does not log a spurious warning + race with the
    // command's own authoritative reload.
    private bool _syncing;

    public ProfilesViewModel(
        IProfileService profiles,
        IProfileSession session,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<ProfilesViewModel> logger)
        : base(localization)
    {
        _profiles = profiles;
        _session = session;
        _dialogs = dialogs;
        _logger = logger;

        // Snapshot the session's running state BEFORE subscribing + before any
        // command can be observed. Without this, a VM constructed while Darktide
        // is running would have IsRunning=false until the next PropertyChanged,
        // so Add/Select/new-Save gates would briefly evaluate against stale
        // "stopped" state. Initializing here makes the first observation honest.
        _isRunning = _session.IsRunning;

        Editor = new LaunchSettingsEditorViewModel(localization);
        Editor.Changed += OnEditorChanged;

        _session.PropertyChanged += OnSessionPropertyChanged;

        // First authoritative load mirrors what every later reload does.
        ReloadFromActive();
    }

    /// <summary>
    /// The composed launch-settings editor. Owns env-var + game-arg rows, the
    /// Enable Lua Logs + Skip Splash toggles, inline localized validation, and
    /// structural dirty tracking against the last <see cref="Editor.Load"/>
    /// baseline. This VM owns the final Save; the editor never persists.
    /// </summary>
    public LaunchSettingsEditorViewModel Editor { get; }

    /// <summary>
    /// The editable profile name (staged). Trimmed by the service on Save.
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// The editable single-line profile description (staged), at most
    /// <see cref="Profile.DescriptionMaxLength"/> characters. The XAML caps
    /// length; this VM additionally rejects CR/LF as a defense-in-depth gate on
    /// paste. Trimmed by the service on Save.
    /// </summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>True while a new (unsaved) draft is open; the banner is hidden
    /// and Save creates + activates rather than updates.</summary>
    [ObservableProperty]
    private bool _isDraft;

    /// <summary>True while a Save is in flight. Save is synchronous (the
    /// service writes synchronously to disk), so this flag is set + cleared
    /// within one call; it serves as a reentrancy guard and a command gate so
    /// Save + Add + Select + Delete stay disabled for the duration of the
    /// atomic create/update + authoritative reload and a second click cannot
    /// reenter the same path.</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>
    /// Mirrors <see cref="IProfileSession.IsRunning"/>. Kept here so the XAML
    /// + CanExecute stay in sync with the session's live polling-timer state.
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// A top-level save error from the authoritative Save call (empty when
    /// there is nothing to show). Cleared on any editor or metadata edit so a
    /// stale error does not linger after the user fixes the input.
    /// </summary>
    [ObservableProperty]
    private string _saveError = string.Empty;

    /// <summary>
    /// Sorted persisted profiles offered as switch targets in the picker. The
    /// active profile is excluded while one is active (it is not a switch
    /// target); when no profile is active, every profile is selectable.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<ProfileChoice> _profileChoices = Array.Empty<ProfileChoice>();

    /// <summary>
    /// The persisted active profile's banner card (first-letter, name,
    /// description), or null when no profile is active or a draft is open. Bound
    /// to the banner button that hosts the picker flyout.
    /// </summary>
    [ObservableProperty]
    private ProfileChoice? _activeProfileBanner;

    /// <summary>True when at least one persisted profile exists.</summary>
    [ObservableProperty]
    private bool _hasProfiles;

    // ---- derived visibility (raised explicitly in ReloadFromActive / StartDraft) ----

    /// <summary>True when the persisted profile banner should show (an active
    /// profile exists and no draft is open).</summary>
    public bool IsBannerVisible => _activeId is not null && !IsDraft;

    /// <summary>True when the editor + footer should show (an active profile is
    /// loaded or a draft is open).</summary>
    public bool IsEditorVisible => _activeId is not null || IsDraft;

    /// <summary>True when the no-profiles call-to-action should show (no profile
    /// is active, none exist, and no draft is open).</summary>
    public bool ShowNoProfilesCta => _activeId is null && !HasProfiles && !IsDraft;

    /// <summary>True when the Select-a-profile affordance should show (no profile
    /// is active but at least one exists, and no draft is open).</summary>
    public bool ShowSelectAffordance => _activeId is null && HasProfiles && !IsDraft;

    /// <summary>True when the shared Add/Delete action row beneath the banner or
    /// Select affordance should show. Hidden while a draft is open (Save +
    /// Cancel are the only draft-completion controls) and hidden when no profile
    /// is active and none exist (the no-profiles CTA replaces it). Defense in
    /// depth: <see cref="CanAddProfile"/> also disables Add while a draft is
    /// open, so the row's visibility + the command gate both block Add.</summary>
    public bool ShowProfileActions => !IsDraft && (_activeId is not null || HasProfiles);

    /// <summary>True when the picker flyout has at least one switch target.</summary>
    public bool HasPickerChoices => ProfileChoices.Count > 0;

    // ---- inline metadata validation (derived; re-resolves on edits + culture) ----

    /// <summary>
    /// The localized inline error shown under the Name field (empty when the
    /// name is valid). A blank/whitespace name explains that a profile name is
    /// required; anything else is valid (the service trims). Recomputed on each
    /// Name edit and on a culture change; the XAML shows it with the existing
    /// error brush + compact error-text pattern (matching the editor's env-var
    /// errors).
    /// </summary>
    public string NameError => string.IsNullOrWhiteSpace(Name)
        ? _localization["Profiles_ErrNameRequired"]
        : string.Empty;

    /// <summary>
    /// The localized inline error shown under the Description field (empty when
    /// the description is valid). Explains that the description must be
    /// single-line and at most <see cref="Profile.DescriptionMaxLength"/>
    /// characters; surfaces only on multiline paste or length-overflow (the XAML
    /// caps typing length but a paste can still carry newlines).
    /// </summary>
    public string DescriptionError => IsDescriptionValid
        ? string.Empty
        : _localization["Profiles_ErrDescriptionInvalid"];

    // ---- running-aware command tooltips (derived; re-resolve on running + culture) ----

    /// <summary>
    /// The Add button tooltip: the normal action label, or the lock explanation
    /// while Darktide runs (switching + creating are blocked). Bound from XAML
    /// so the disabled state's tooltip carries the reason. Re-resolves on a
    /// running-state flip and on a culture change.
    /// </summary>
    public string AddTooltip => IsRunning
        ? _localization["Profiles_AddLockedTooltip"]
        : _localization["Profiles_AddTooltip"];

    /// <summary>
    /// The Delete button tooltip: the normal action label, or the lock
    /// explanation while Darktide runs (delete-of-active is blocked). Bound from
    /// XAML; re-resolves on a running-state flip and on a culture change.
    /// </summary>
    public string DeleteTooltip => IsRunning
        ? _localization["Profiles_DeleteLockedTooltip"]
        : _localization["Profiles_DeleteTooltip"];

    // ---- CanExecute --------------------------------------------------------

    /// <summary>
    /// Whether the Save button may be enabled: not currently saving, no top-level
    /// error, a nonblank trimmed name, a valid single-line description, valid
    /// launch settings, actual staged changes, and (for a new draft) the game
    /// stopped. Existing-profile save stays enabled while Darktide runs.
    /// </summary>
    public bool CanSave =>
        !IsSaving &&
        string.IsNullOrEmpty(SaveError) &&
        !string.IsNullOrWhiteSpace(Name) &&
        IsDescriptionValid &&
        Editor.IsValid &&
        IsDirty &&
        (!IsDraft || !IsRunning);

    /// <summary>
    /// True when the description is a valid single line at or under the limit
    /// after trim: no CR/LF anywhere (defense-in-depth on paste; the XAML caps
    /// length but a paste can still carry newlines) and within
    /// <see cref="Profile.DescriptionMaxLength"/>.
    /// </summary>
    private bool IsDescriptionValid
    {
        get
        {
            if (Description.IndexOf('\r') >= 0 || Description.IndexOf('\n') >= 0)
            {
                return false;
            }
            return Description.Trim().Length <= Profile.DescriptionMaxLength;
        }
    }

    /// <summary>Add Profile: disabled while Darktide runs, while a Save is in
    /// flight, and (defense in depth) while a draft is already open. The draft
    /// case is also hidden via <see cref="ShowProfileActions"/>; CanExecute is
    /// the secondary guard so a programmatic call cannot start a second draft
    /// either.</summary>
    private bool CanAddProfile => !IsRunning && !IsSaving && !IsDraft;

    /// <summary>Delete Profile: requires an active persisted profile, not a
    /// draft, not saving, and the session gate. The button also hides for a
    /// draft (see <c>DeleteIsVisible</c>); CanExecute is the secondary guard.
    /// </summary>
    private bool CanDeleteProfile =>
        _activeId is not null && !IsDraft && !IsSaving && _session.CanDeleteProfile(_activeId.Value);

    /// <summary>Delete button visibility: hidden for a new draft and when there
    /// is no active profile to delete.</summary>
    public bool DeleteIsVisible => _activeId is not null && !IsDraft;

    /// <summary>Selecting a profile in the picker: requires switch targets and
    /// the game stopped. Defense-in-depth re-check in the command body.</summary>
    private bool CanSelectProfile => HasPickerChoices && !IsRunning && !IsSaving;

    // ---- change plumbing ---------------------------------------------------

    partial void OnNameChanged(string value)
    {
        SaveError = string.Empty;
        OnPropertyChanged(nameof(NameError));
        RefreshCanSave();
    }

    partial void OnDescriptionChanged(string value)
    {
        SaveError = string.Empty;
        OnPropertyChanged(nameof(DescriptionError));
        RefreshCanSave();
    }

    partial void OnIsSavingChanged(bool value) => RefreshCommandCanExecute();

    partial void OnIsRunningChanged(bool value)
    {
        RefreshCommandCanExecute();
        // The running-aware tooltips re-resolve with the running flip.
        OnPropertyChanged(nameof(AddTooltip));
        OnPropertyChanged(nameof(DeleteTooltip));
    }

    partial void OnIsDraftChanged(bool value)
    {
        // Draft toggles all the derived visibility flags + every command's gate.
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(ShowNoProfilesCta));
        OnPropertyChanged(nameof(ShowSelectAffordance));
        OnPropertyChanged(nameof(ShowProfileActions));
        OnPropertyChanged(nameof(DeleteIsVisible));
        RefreshCommandCanExecute();
    }

    /// <summary>
    /// Any editor edit clears a stale top-level save error (the inline pass
    /// supersedes it) and re-evaluates CanSave.
    /// </summary>
    private void OnEditorChanged(object? sender, EventArgs e)
    {
        SaveError = string.Empty;
        RefreshCanSave();
    }

    private void RefreshCanSave()
    {
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommandCanExecute()
    {
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AddProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SelectProfileCommand.NotifyCanExecuteChanged();
    }
    // ---- commands ----------------------------------------------------------

    /// <summary>
    /// Starts a new (unsaved) draft. Disabled while Darktide runs, while a Save
    /// is in flight, or while a draft is already open (the latter also hides the
    /// Add affordance via <see cref="ShowProfileActions"/>; CanExecute is the
    /// secondary guard). If the current form is dirty, asks the unsaved-changes
    /// three-choice prompt; Cancel/ESC/X preserves the current draft, Save tries
    /// the atomic write and proceeds only on success, Don't save reloads
    /// authority and proceeds. A new draft has empty name + empty description +
    /// default launch settings and is not itself dirty.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddProfile))]
    private async Task AddProfileAsync()
    {
        if (IsRunning || IsSaving || IsDraft)
        {
            return;
        }

        if (!await ResolveDirtyTransitionAsync())
        {
            return;
        }

        StartDraft();
    }

    /// <summary>
    /// Saves the staged profile. Synchronous: there is no async DMF or mod-list
    /// work in profile Save (the shell owns DMF prompting + the mod-list reload
    /// on Mods entry), so no artificial await is preserved. For a new draft:
    /// creates via the atomic
    /// <see cref="IProfileService.CreateProfile(string, string, LaunchSettings)"/>
    /// and requests the new id active through the session (synchronous through
    /// RequestActive so the running-state polling timer cannot interleave
    /// between create and activation on the UI thread), then reloads the
    /// authoritative active profile + choices. For an existing profile: one
    /// atomic <see cref="IProfileService.UpdateProfile"/> call with the staged
    /// fields, then reloads. An authoritative <see cref="ArgumentException"/>
    /// surfaces a localized generic error without exposing raw service text;
    /// edits clear the stale error. The reusable core
    /// <see cref="TrySaveCore"/> returns success/failure so the dirty-transition
    /// helper can run the same atomic write.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        _ = TrySaveCore();
    }

    /// <summary>
    /// The single honest save core, reused by the Save button
    /// (<see cref="Save"/>) and the dirty-transition helper
    /// (<see cref="ResolveDirtyTransitionAsync"/>). Returns <c>true</c> on a
    /// successful atomic create/update + reload; <c>false</c> on a gate failure
    /// or a service rejection (the localized save error is set, the staged state
    /// is preserved so the user can fix + retry). Never requests active / reloads
    /// after a rejected create, mirroring the original contract.
    /// </summary>
    /// <remarks>
    /// Synchronous through the service calls (the profile service writes
    /// synchronously to disk). The <paramref name="simulateSavingFlag"/> hook
    /// flips the <see cref="IsSaving"/> flag for the Save-button path only; the
    /// dirty-transition path calls this core without the flag (no UI affordance
    /// needs to disable for an instant atomic write that has its own modal
    /// already up).
    /// </remarks>
    /// <param name="simulateSavingFlag">When <c>true</c> (the Save button path),
    /// sets <see cref="IsSaving"/> around the work so Add/Select/Delete stay
    /// disabled + Cancel is gated off for the duration. When <c>false</c> (the
    /// dirty-transition path), the unsaved-changes modal is already up + the
    /// gates are irrelevant.</param>
    private bool TrySaveCore(bool simulateSavingFlag = true)
    {
        if (!CanSave)
        {
            return false;
        }

        if (simulateSavingFlag)
        {
            IsSaving = true;
        }
        try
        {
            var name = Name.Trim();
            var description = Description;
            var settings = Editor.BuildSettings();

            if (IsDraft)
            {
                // Defense-in-depth: the gate is in CanSave, but a programmatic
                // call after the game started externally still bails before the
                // create (which would require activation).
                if (IsRunning)
                {
                    return false;
                }

                Guid createdId;
                try
                {
                    createdId = _profiles.CreateProfile(name, description, settings).Id;
                }
                catch (ArgumentException ex)
                {
                    // Same defense-in-depth contract as the existing-profile
                    // UpdateProfile path: the inline pass should have caught
                    // any violation, so reaching here means the validator and
                    // the service diverged. Surface the localized generic error
                    // and keep the draft open; never request active / reload
                    // after a rejected create.
                    _logger.LogWarning(ex,
                        "Atomic CreateProfile rejected an edit the inline pass allowed");
                    SaveError = _localization["Profiles_ErrSaveFailed"];
                    return false;
                }

                // Suppress the session event handler's stale-draft displacement
                // branch for the active-id change this command is driving. The
                // operations through RequestActive are synchronous, so the
                // running-state polling timer cannot interleave between create
                // and activation on the UI thread.
                _syncing = true;
                try
                {
                    _session.RequestActive(createdId);
                    ReloadFromActive();
                }
                finally
                {
                    _syncing = false;
                }
                // No DMF await + no mod-list reload here: the shell owns DMF
                // prompting + the post-DMF reload (both fire on the next Mods
                // entry). DmfPromptService's eagerly established
                // ProfileCreated subscription recorded the trigger; this VM's
                // part ends at the atomic create + activate + authoritative
                // reload, and the shell consumes the trigger on the next real
                // navigation into Mods.
                return true;
            }

            var id = _activeId!.Value;
            try
            {
                _profiles.UpdateProfile(id, name, description, settings);
            }
            catch (ArgumentException ex)
            {
                // Defense-in-depth: the inline pass should have caught any
                // violation, so reaching here means the inline validator and
                // the service diverged. Keep the page open with a generic,
                // localized error; never surface the raw (non-localized)
                // service text. The offending row's inline message (if any)
                // is already shown by the editor.
                _logger.LogWarning(ex,
                    "Atomic UpdateProfile rejected an edit the inline pass allowed for profile {Id}", id);
                SaveError = _localization["Profiles_ErrSaveFailed"];
                return false;
            }

            ReloadFromActive();
            return true;
        }
        finally
        {
            if (simulateSavingFlag)
            {
                IsSaving = false;
            }
        }
    }

    /// <summary>
    /// Cancel: reloads the persisted active profile (or the no-active state)
    /// without writing. Cancelling a new draft creates nothing; the editor
    /// reverts to the persisted baseline. Disabled while a Save is in flight so
    /// a Cancel click cannot reenter the synchronous create/update + reload
    /// path.
    /// </summary>
    private bool CanCancel => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ReloadFromActive();
    }

    /// <summary>
    /// Deletes the active profile after a localized confirmation. Defense-in-depth
    /// on <see cref="IProfileSession.CanDeleteProfile"/> (the button's enabled
    /// state also binds to it, but a programmatic call could bypass that). On
    /// confirm: deletes the active profile, calls
    /// <see cref="IProfileSession.ReconcileActive"/> (clears the active id when
    /// the deleted profile was active), reloads choices + no-active state. Does
    /// not auto-select another profile (the user explicitly picks the next).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (_activeId is not Guid id || IsDraft || IsSaving)
        {
            return;
        }

        // Defense-in-depth: the button binds IsEnabled to the session gate, but
        // a programmatic call would bypass that binding.
        if (!_session.CanDeleteProfile(id))
        {
            return;
        }

        var name = _persistedName;
        var title = _localization["Profiles_DeleteTitle"];
        var message = _localization.Format("Profiles_DeleteMessage", name);

        if (!await _dialogs.ConfirmAsync(title, message))
        {
            return;
        }

        _profiles.DeleteProfile(id);

        _syncing = true;
        try
        {
            _session.ReconcileActive();
            ReloadFromActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Selects a persisted profile from the picker. Blocked while Darktide runs
    /// (defense-in-depth re-check in the body). When the current form is dirty,
    /// asks the unsaved-changes three-choice prompt; Cancel/ESC/X preserves the
    /// draft, Save tries the atomic write and proceeds only on success, Don't
    /// save reloads authority and proceeds. On proceed: requests the id active
    /// through the session, then reloads the authoritative active id. The view
    /// closes the flyout after the command runs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectProfile))]
    private async Task SelectProfileAsync(ProfileChoice? choice)
    {
        if (choice is null || IsRunning || IsSaving)
        {
            return;
        }

        if (!await ResolveDirtyTransitionAsync())
        {
            return;
        }

        _syncing = true;
        try
        {
            _session.RequestActive(choice.Id);
            ReloadFromActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The shell navigation guard. Clean proceeds without a dialog; dirty asks
    /// the unsaved-changes three-choice prompt. Cancel/ESC/X preserves the draft
    /// and returns <c>false</c>; Save tries the same atomic create/update used
    /// by the Save button and returns <c>true</c> only on success (the
    /// localized save error surfaces in the editor on a service rejection);
    /// Don't save reloads authority and returns <c>true</c>. Shell navigation,
    /// switching profiles, and starting another draft all reuse this core.
    /// </summary>
    public async Task<bool> ConfirmCanNavigateAwayAsync() => await ResolveDirtyTransitionAsync();

    // ---- dirty -------------------------------------------------------------

    /// <summary>
    /// Structural dirty state: true when the staged name (trimmed), description,
    /// or launch settings (via <see cref="Editor.IsDirty"/>) differ from the
    /// persisted baseline captured at Load. A new untouched draft is not dirty
    /// (its baseline is empty name + empty description + default settings, which
    /// is exactly what StartDraft stages).
    /// </summary>
    public bool IsDirty =>
        !string.Equals(Name.Trim(), _persistedName, StringComparison.Ordinal) ||
        !string.Equals(Description, _persistedDescription, StringComparison.Ordinal) ||
        Editor.IsDirty;

    /// <summary>
    /// The shared dirty-transition core for navigate-away, switch, and
    /// start-another-draft. Clean is a no-op (proceeds). Dirty asks the
    /// unsaved-changes three-choice prompt:
    /// <list type="bullet">
    /// <item><term><see cref="UnsavedChangesChoice.Cancel"/></term>
    /// <description>preserve the staged state, return <c>false</c> (the
    /// attempted transition stops).</description></item>
    /// <item><term><see cref="UnsavedChangesChoice.Save"/></term>
    /// <description>run <see cref="TrySaveCore(bool)"/> without the
    /// <see cref="IsSaving"/> flag (the modal is already up); return
    /// <c>true</c> on success, <c>false</c> on a service rejection (the
    /// localized save error is left in place so the user sees why the
    /// transition stopped).</description></item>
    /// <item><term><see cref="UnsavedChangesChoice.DontSave"/></term>
    /// <description>reload the authoritative state, return
    /// <c>true</c>.</description></item>
    /// </list>
    /// When <see cref="CanSave"/> is <c>false</c> the prompt is opened with
    /// <c>canSave=false</c> so the Save button is disabled and the concise
    /// unavailable explanation shows (the user can still Cancel or Don't save).
    /// </summary>
    private async Task<bool> ResolveDirtyTransitionAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        var title = _localization["Unsaved_Title"];
        var message = _localization["Unsaved_Message"];
        var choice = await _dialogs.ShowUnsavedChangesAsync(title, message, canSave: CanSave);

        switch (choice)
        {
            case UnsavedChangesChoice.Save:
                // The modal is up; do not flip IsSaving (it would disable the
                // wrong affordances while the unsaved-changes modal is still
                // closing). The core's own gate (CanSave) re-checks.
                return TrySaveCore(simulateSavingFlag: false);
            case UnsavedChangesChoice.DontSave:
                ReloadFromActive();
                return true;
            default:
                return false;
        }
    }

    // ---- draft + reload ----------------------------------------------------

    /// <summary>
    /// Starts a new unsaved draft: empty name + empty description + default
    /// launch settings, IsDraft = true (hides the banner). The baseline is empty
    /// so an untouched draft is not dirty.
    /// </summary>
    private void StartDraft()
    {
        _persistedName = string.Empty;
        _persistedDescription = string.Empty;

        // Load default settings before flipping IsDraft so the editor is already
        // clean when the view reveals it (no transient dirty flicker).
        Editor.Load(new LaunchSettings());

        Name = string.Empty;
        Description = string.Empty;
        SaveError = string.Empty;
        IsDraft = true;

        RefreshCanSave();
    }

    /// <summary>
    /// Authoritative reload from the session's current active id. Reads the full
    /// <see cref="Profile"/> (metadata + launch settings), deep-loads the editor,
    /// rebuilds the picker choices, refreshes the banner, and re-raises every
    /// derived visibility flag. For no active id: clears the editor's baseline
    /// to empty + hides the banner; the editor content is not meaningful and
    /// <see cref="IsEditorVisible"/> is false. Exits a draft if one was open.
    /// </summary>
    /// <remarks>
    /// <para>If the session reports an active id but that profile is missing or
    /// unreadable (KeyNotFoundException / IOException / JsonException /
    /// UnauthorizedAccessException from <see cref="IProfileService.GetProfile"/>),
    /// the VM falls back to a genuine no-active state: <c>_activeId</c> is set
    /// to null so no banner/editor/Delete can target the stale id, while the
    /// Select-a-profile affordance + Add button remain usable. The session's
    /// reported id is still excluded from the picker (the user picks a different
    /// profile, not the one that won't load).</para>
    /// <para>Centralizes every "reload from authority" path (initial load,
    /// post-Save, post-Delete, post-Select, programmatic active-id change,
    /// Cancel). All paths see the same reconstructed state, so the view never
    /// observes a half-rebuilt VM.</para>
    /// </remarks>
    private void ReloadFromActive()
    {
        var id = _session.ActiveProfileId;

        // Try to deep-load the active profile. On any unreadable/missing
        // condition, treat the active id as unrecoverable: _activeId becomes
        // null so banner/editor/Delete cannot target a stale id. The session's
        // reported id is still excluded from the picker below, so the user
        // picks a *different* profile rather than re-selecting one that won't
        // load.
        Profile? loaded = null;
        if (id is Guid activeId)
        {
            try
            {
                loaded = _profiles.GetProfile(activeId);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "Active profile {Id} is missing or unreadable; loading no-active state", activeId);
            }
        }

        // _activeId reflects what actually loaded, not what the session claims.
        // null here on the unreadable path gates IsBannerVisible / DeleteIsVisible.
        _activeId = loaded is not null ? id : null;
        IsDraft = false;
        SaveError = string.Empty;

        var summaries = _profiles.ListProfiles();
        HasProfiles = summaries.Count > 0;

        // Rebuild the picker choices: exclude the session's reported active id
        // (it is not a switch target, even when unreadable). When the session
        // reports no active, every profile is selectable.
        ProfileChoices = summaries
            .Where(s => s.Id != id)
            .Select(s => ToChoice(s))
            .ToArray();

        if (loaded is Profile profile)
        {
            _persistedName = profile.Name;
            _persistedDescription = profile.Description ?? string.Empty;
            Name = profile.Name;
            Description = profile.Description ?? string.Empty;
            ActiveProfileBanner = ToChoice(summaries.First(s => s.Id == profile.Id));
            Editor.Load(profile.LaunchSettings ?? new LaunchSettings());
        }
        else
        {
            LoadNoActiveState();
        }

        // Raise derived visibility + inline validation + command gates together
        // so the view sees a consistent snapshot, not intermediate states. This
        // block always runs (no early return), so the unreadable path also gets
        // a full notification set.
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(ShowNoProfilesCta));
        OnPropertyChanged(nameof(ShowSelectAffordance));
        OnPropertyChanged(nameof(ShowProfileActions));
        OnPropertyChanged(nameof(HasPickerChoices));
        OnPropertyChanged(nameof(DeleteIsVisible));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(DescriptionError));
        RefreshCommandCanExecute();
    }

    /// <summary>
    /// The no-active-state branch of <see cref="ReloadFromActive"/>: empties the
    /// editor baseline + banner. The editor content itself is left in whatever
    /// state the last Load put it in; <see cref="IsEditorVisible"/> is false so
    /// it is not shown.
    /// </summary>
    private void LoadNoActiveState()
    {
        _persistedName = string.Empty;
        _persistedDescription = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
        ActiveProfileBanner = null;
        Editor.Load(new LaunchSettings());
    }

    /// <summary>
    /// Builds a <see cref="ProfileChoice"/> from a summary, deriving the
    /// uppercased first letter (or "?" when the name has no usable first
    /// character) + the deterministic avatar background from
    /// <see cref="ProfileAvatarPalette"/>. Used for both the banner + every
    /// picker row.
    /// </summary>
    private static ProfileChoice ToChoice(ProfileSummary summary)
    {
        var first = summary.Name.Length > 0
            ? char.ToUpperInvariant(summary.Name[0]).ToString()
            : "?";
        return new ProfileChoice(
            summary.Id,
            summary.Name,
            summary.Description ?? string.Empty,
            first,
            ProfileAvatarPalette.For(summary.Id));
    }

    // ---- application-lifetime subscriptions --------------------------------

    /// <summary>
    /// Reacts to live session state changes. Active-id changes reload the
    /// authoritative profile (a programmatic change while dirty outside this
    /// VM's own guarded command logs + discards the stale draft rather than
    /// attempting an async dialog from an event handler). IsRunning changes
    /// refresh the command gates (and re-evaluate CanSave for a new draft).
    /// Suppressed while <see cref="_syncing"/> is set (one of this VM's own
    /// commands is driving the change and will perform its own reload).
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (e.PropertyName == nameof(IProfileSession.ActiveProfileId))
        {
            var newId = _session.ActiveProfileId;
            if (newId == _activeId)
            {
                return;
            }

            // A programmatic active-id change arrived while our staged draft is
            // dirty. We cannot ask the user from inside an event handler (an
            // async dialog here would race with whatever drove this change), so
            // log it and discard the stale draft rather than risk a confusing
            // prompt. The authoritative reload below establishes the new truth.
            if (IsDirty)
            {
                _logger.LogInformation(
                    "Active profile changed to {Id} outside the Profiles view while a dirty draft was open; discarding the draft",
                    newId);
            }

            ReloadFromActive();
        }
        else if (e.PropertyName == nameof(IProfileSession.IsRunning))
        {
            IsRunning = _session.IsRunning;
        }
    }

    /// <summary>
    /// Reacts to UI culture changes by re-mapping the editor's current
    /// validation errors AND this VM's inline metadata errors + running-aware
    /// tooltips to the new language. Does not change values, baselines, dirty
    /// state, or raise the editor's user-edit Changed event. The XAML's indexer
    /// bindings refresh the static labels automatically.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(NameError),
        nameof(DescriptionError),
        nameof(AddTooltip),
        nameof(DeleteTooltip),
    };

    /// <summary>The non-list culture work: refresh the launch-settings
    /// editor's localized validation strings.</summary>
    protected override void OnCultureChanged() => Editor.RefreshLocalizedValidation();
}
