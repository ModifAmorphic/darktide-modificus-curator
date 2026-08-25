using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the edit-import-details modal
/// (<see cref="Views.EditImportDetailsDialog"/>): the universal correction
/// surface for one mod container's import details (display name, source
/// association, release tag). Loaded from the container's current facts; Save
/// applies them through <see cref="IModRepository.EditImportDetails"/> in one
/// atomic write (the container id, and with it every profile reference,
/// survives the edit).
/// </summary>
/// <remarks>
/// <para><b>FileId degradation:</b> when any version on the container carries
/// a <see cref="ModVersion.FileId"/> the identity is grounded by a download:
/// the mod id field is read-only, the source switch is disabled, and a
/// localized hint explains why. The dialog degrades to name-only editing (the
/// primitive enforces the same lock; this is affordance, not the
/// guard).</para>
/// <para><b>The dialog can never create an unknown state:</b> a version is
/// required when saving as Nexus (the same shared
/// <see cref="ImportSourceValidator"/> rules the import card enforces); only
/// the programmatic association path records an empty tag.</para>
/// <para><b>Identity-change confirm:</b> changing the identity of a
/// multi-version container removes the older versions (a destructive, flagged
/// operation at the primitive). The confirm is an inline visibility-swapped
/// warning panel inside the same dialog (never a nested modal): the first
/// Save presents the plain-language removal notice, and only the explicit
/// confirm button proceeds.</para>
/// <para><b>Failure handling:</b> a refused save (the duplicate-identity
/// guard, the tag-collision guard, the FileId lock reached anyway) surfaces
/// as an inline localized failure message carrying the exception's detail;
/// the dialog stays open for correction or cancel. Never a crash.</para>
/// <para><b>Lifecycle:</b> a transient dialog VM; <see cref="Detach"/> drops
/// the culture subscription on close so the VM is collectable.</para>
/// </remarks>
public partial class EditImportDetailsViewModel : LocalizedViewModel
{
    private readonly IModRepository _repo;
    private readonly Guid _containerId;
    private readonly ModSource _originalSource;
    private readonly int _originalVersionCount;
    private readonly bool _identityLocked;

    /// <summary>The raw failure detail of the last refused save, or null.</summary>
    private string? _failureDetail;

    /// <param name="container">The container being edited (non-null,
    /// non-linked; the factory screens both).</param>
    /// <param name="repo">The repository; Save applies through
    /// <see cref="IModRepository.EditImportDetails"/>.</param>
    /// <param name="localization">The localization service (derived strings
    /// re-resolve on a culture change until the dialog closes).</param>
    public EditImportDetailsViewModel(
        ModContainer container,
        IModRepository repo,
        LocalizationService localization)
        : base(localization)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        ArgumentNullException.ThrowIfNull(container);

        _containerId = container.Id;
        _originalSource = container.Source;
        _originalVersionCount = container.Versions.Count;
        _identityLocked = container.Versions.Any(v => v.FileId is not null);

        var nexus = container.Source as NexusSource;
        _name = container.Name;
        _sourceChoice = nexus is null ? ImportSource.Untracked : ImportSource.Nexus;
        _url = nexus is null ? string.Empty : nexus.ModId.ToString();
        _version = container.Versions.FirstOrDefault(v => v.IsLatest)?.VersionString ?? string.Empty;
    }

    // ---- observable editing fields -----------------------------------------

    /// <summary>
    /// The container's display name (always editable; never locked by the
    /// FileId rule).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveCommand))]
    private string _name;

    /// <summary>
    /// The chosen source. Drives which conditional fields show (Nexus: id +
    /// version; Untracked: neither) and which validation applies. Disabled
    /// while the identity is FileId-locked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(SourceChoiceIndex))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyPropertyChangedFor(nameof(RequiresIdentityConfirm))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveCommand))]
    private ImportSource _sourceChoice;

    /// <summary>
    /// The remote identity as typed: a bare Nexus mod id or a Darktide Nexus
    /// mod URL, parsed through the shared validator. Read-only while the
    /// identity is FileId-locked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(RequiresIdentityConfirm))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveCommand))]
    private string _url;

    /// <summary>
    /// The release tag typed by the user (never fetched from the remote: the
    /// user has the files page open when correcting a match anyway). Required
    /// when saving as Nexus; disabled + empty for Untracked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveCommand))]
    private string _version;

    /// <summary>
    /// Whether the inline identity-removal confirm panel is showing (the
    /// visibility-swapped second step of the dialog; never a nested modal).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingStep))]
    private bool _isConfirmStep;

    /// <summary>
    /// Switching to Untracked clears the version field: an untracked mod
    /// carries no release tag, and the field is disabled + empty for that
    /// choice.
    /// </summary>
    partial void OnSourceChoiceChanged(ImportSource value)
    {
        if (value == ImportSource.Untracked)
        {
            Version = string.Empty;
        }
    }

    // ---- ComboBox index adapter ---------------------------------------------

    /// <summary>
    /// Integer adapter for the source ComboBox's <c>SelectedIndex</c>
    /// (0 = Untracked, 1 = Nexus), so the ComboBox binds two-way without a
    /// converter or view code-behind. Maps to/from <see cref="SourceChoice"/>.
    /// </summary>
    public int SourceChoiceIndex
    {
        get => (int)SourceChoice;
        set
        {
            var choice = (ImportSource)value;
            if (choice != SourceChoice)
            {
                SourceChoice = choice;
            }
        }
    }

    // ---- the FileId degradation ----------------------------------------------

    /// <summary>
    /// Whether the identity is grounded by a download (any version on the
    /// container carries a <see cref="ModVersion.FileId"/>): the id field is
    /// read-only, the source switch is disabled, and the localized
    /// "downloaded from Nexus" hint shows. The dialog degrades to name-only
    /// editing.
    /// </summary>
    public bool IsIdentityLocked => _identityLocked;

    /// <summary>Whether the mod id field accepts input.</summary>
    public bool IsIdEditable => !IsIdentityLocked;

    /// <summary>Whether the source switch accepts input.</summary>
    public bool IsSourceEditable => !IsIdentityLocked;

    // ---- derived editing projections -----------------------------------------

    /// <summary>Whether a remote source (Nexus) is chosen.</summary>
    public bool IsRemote => SourceChoice == ImportSource.Nexus;

    /// <summary>Whether the Version field is visible + enabled (Nexus).</summary>
    public bool IsVersionVisible => IsRemote;

    /// <summary>Whether the editing form (vs. the confirm panel) is showing.</summary>
    public bool IsEditingStep => !IsConfirmStep;

    /// <summary>
    /// Whether the identity (the source record) differs from the container's
    /// current one: a different Nexus id, a Nexus/Untracked swap in either
    /// direction. A rename or retag alone is not an identity change.
    /// </summary>
    public bool IsIdentityChange
    {
        get
        {
            ModSource current;
            if (SourceChoice == ImportSource.Untracked)
            {
                current = new UntrackedSource();
            }
            else if (!ImportSourceValidator.TryParseUrl(SourceChoice, Url ?? string.Empty, out var parsed))
            {
                // An unparsable id is not a saveable identity; treat it as a
                // change so a locked multi-version confirm is never skipped on
                // a technicality (CanSave blocks the save itself).
                return true;
            }
            else
            {
                current = parsed;
            }

            return (_originalSource, current) switch
            {
                (NexusSource a, NexusSource b) => a.ModId != b.ModId,
                (UntrackedSource, UntrackedSource) => false,
                _ => true,
            };
        }
    }

    /// <summary>
    /// Whether saving the current fields requires the explicit removal
    /// confirm: an identity change on a multi-version container (the older
    /// versions are claims about the old identity and are removed).
    /// </summary>
    public bool RequiresIdentityConfirm => IsIdentityChange && _originalVersionCount > 1;

    /// <summary>
    /// Whether Save may be enabled: a non-whitespace name, and the shared
    /// remote-source rules when saving as Nexus (a non-empty version + an
    /// id/URL that parses). Untracked needs only the name.
    /// </summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name)
        && ImportSourceValidator.IsRemoteSourceValid(SourceChoice, Url ?? string.Empty, Version ?? string.Empty);

    // ---- localized strings -----------------------------------------------------

    /// <summary>The localized header explaining what the dialog edits.</summary>
    public string HeaderText => _localization["EditDetails_Header"];

    /// <summary>
    /// The localized hint shown while the identity is FileId-locked ("this
    /// mod was downloaded from Nexus; its id is fixed"). Empty otherwise.
    /// </summary>
    public string FileIdLockHint => IsIdentityLocked
        ? _localization["EditDetails_FileIdLockHint"]
        : string.Empty;

    /// <summary>The localized label for the URL/id field (Nexus).</summary>
    public string UrlLabel => _localization["Import_NexusUrlLabel"];

    /// <summary>The localized placeholder for the URL/id field (Nexus).</summary>
    public string UrlPlaceholder => _localization["Import_UrlPlaceholderNexus"];

    /// <summary>
    /// The localized validation message for the id field (required when
    /// empty for Nexus; invalid when non-empty but unparsable). Empty when
    /// there is nothing to show.
    /// </summary>
    public string UrlValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            var url = Url?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                return _localization["Import_UrlRequired"];
            }

            return ImportSourceValidator.TryParseUrl(SourceChoice, url, out _)
                ? string.Empty
                : _localization["Import_UrlInvalid"];
        }
    }

    /// <summary>
    /// The localized validation message for the Version field when it is
    /// empty or whitespace for a Nexus save. Empty when there is nothing to
    /// show.
    /// </summary>
    public string VersionValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(Version)
                ? _localization["Import_VersionRequired"]
                : string.Empty;
        }
    }

    /// <summary>The localized title of the inline removal-confirm panel.</summary>
    public string ConfirmTitle => _localization["EditDetails_ConfirmTitle"];

    /// <summary>
    /// The localized plain-language removal notice, formatted with the number
    /// of older versions the identity change removes.
    /// </summary>
    public string ConfirmMessage => _localization.Format(
        "EditDetails_ConfirmMessage", Math.Max(0, _originalVersionCount - 1));

    /// <summary>
    /// The localized inline failure of the last refused save: the framing
    /// plus the primitive's detail (the duplicate-identity guard, the
    /// tag-collision guard, the vanished container). Empty when the last
    /// attempt succeeded or none ran. The dialog stays open for correction.
    /// </summary>
    public string FailureMessage => _failureDetail is null
        ? string.Empty
        : _localization["EditDetails_FailedMessage"] + " " + _failureDetail;

    /// <summary>
    /// The dialog VM's localized property names, re-fired by the shared
    /// culture-refresh base on a culture change.
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(HeaderText),
        nameof(FileIdLockHint),
        nameof(UrlLabel),
        nameof(UrlPlaceholder),
        nameof(UrlValidationMessage),
        nameof(VersionValidationMessage),
        nameof(ConfirmTitle),
        nameof(ConfirmMessage),
        nameof(FailureMessage),
    };

    // ---- result + lifecycle -----------------------------------------------------

    /// <summary>
    /// The outcome of the dialog: <c>true</c> when a save was applied,
    /// <c>false</c> when cancelled (ESC, title-bar close, window close, or
    /// the Cancel button; the enum-free bool mirrors the escape-hatch
    /// contract). Read by the dialog service after <c>ShowDialog</c> returns.
    /// </summary>
    public bool Result { get; private set; }

    /// <summary>
    /// Stops the culture subscription so the short-lived dialog VM is
    /// collectable after its window closes. Called by the dialog on close.
    /// </summary>
    public void Detach() => DetachLocalization();

    // ---- commands -------------------------------------------------------------

    /// <summary>
    /// Save. When the fields require the removal confirm (an identity change
    /// on a multi-version container) the first click swaps to the inline
    /// confirm panel instead of applying; otherwise it applies through the
    /// primitive. No-op when the fields are invalid (CanSave).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveCore))]
    private void Save()
    {
        if (!CanSave)
        {
            return;
        }

        if (RequiresIdentityConfirm && !IsConfirmStep)
        {
            IsConfirmStep = true;
            return;
        }

        // A single-version identity change removes nothing (there are no
        // older versions), so the confirm flag stays false on this path.
        Apply(removeOlderVersions: IsConfirmStep && RequiresIdentityConfirm);
    }

    private bool CanSaveCore => CanSave;

    /// <summary>
    /// The confirm panel's explicit proceed: applies the save with
    /// older-version removal (the plain-language notice was shown + acted
    /// on). No-op when the fields are invalid.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveCore))]
    private void ConfirmSave()
    {
        if (!CanSave)
        {
            return;
        }

        Apply(removeOlderVersions: true);
    }

    /// <summary>Back from the confirm panel to the editing form (no save).</summary>
    [RelayCommand]
    private void Back() => IsConfirmStep = false;

    /// <summary>Cancel: marks <see cref="Result"/> false so the dialog closes.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// Applies the edited facts through the primitive. Builds the canonical
    /// source from the validated fields (Untracked records an empty tag;
    /// Nexus parses the id/URL), calls
    /// <see cref="IModRepository.EditImportDetails"/>, and on success marks
    /// <see cref="Result"/> true (the dialog closes). A refused save records
    /// the localized inline failure (with the primitive's detail) and stays
    /// open; an unexpected exception is caught the same way, never a crash
    /// through the command's calling context.
    /// </summary>
    private void Apply(bool removeOlderVersions)
    {
        _failureDetail = null;
        OnPropertyChanged(nameof(FailureMessage));

        ModSource source;
        string tag;
        if (SourceChoice == ImportSource.Untracked)
        {
            source = new UntrackedSource();
            tag = string.Empty;
        }
        else
        {
            ImportSourceValidator.TryParseUrl(SourceChoice, Url ?? string.Empty, out source);
            tag = (Version ?? string.Empty).Trim();
        }

        try
        {
            var updated = _repo.EditImportDetails(
                _containerId, (Name ?? string.Empty).Trim(), source, tag, removeOlderVersions);
            if (updated is null)
            {
                // The container vanished between opening the dialog + saving.
                _failureDetail = _localization["EditDetails_ContainerGone"];
                OnPropertyChanged(nameof(FailureMessage));
                return;
            }

            Result = true;
            OnPropertyChanged(nameof(Result));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A refused save (the guards' messages are user-actionable): show
            // the localized framing + the detail inline; the dialog stays
            // open for correction or cancel.
            _failureDetail = ex.Message;
            OnPropertyChanged(nameof(FailureMessage));
        }
    }
}
