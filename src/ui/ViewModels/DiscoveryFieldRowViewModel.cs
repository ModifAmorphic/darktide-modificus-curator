using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One discovery-path row shared by the Settings destination and the discovery
/// escape-hatch. Carries the immutable <see cref="Field"/> metadata (from
/// <see cref="Settings.DiscoveryField"/>), the localized <see cref="Label"/>
/// (which refreshes on a culture change), the editable <see cref="Value"/>
/// string the TextBox two-way binds, and <see cref="IsEditable"/> which drives
/// the TextBox's read-only state (the Browse button binds to
/// <see cref="IsBrowseEnabled"/> instead, which also gates on Gaming Mode).
/// The browse button (folder / file picker) lives in the view code-behind and
/// sets <see cref="Value"/> directly after a pick; the parent VM decides what a
/// change means: Settings writes through immediately when in manual mode, the
/// escape-hatch stages the values and writes them all on submit.
/// </summary>
/// <remarks>
/// <para><b>Optional change callback:</b> when supplied, the row invokes it on
/// every genuine Value change (after the initial restore). The Settings VM uses
/// it for its write-through (it guards it on manual mode itself); the
/// escape-hatch VM passes <c>null</c> and reads <see cref="Value"/> at submit
/// time. Either is fine; the row has no opinion about persistence.</para>
/// <para><b>IsEditable is owned by the parent VM:</b> the row never sets it. It
/// reflects the current discovery mode (manual on, automatic off) and is pushed
/// down by the parent whenever the mode toggles or the rows refresh.</para>
/// <para><b>Localized label is live:</b> <see cref="Label"/> resolves through
/// the <see cref="LocalizationService"/> and re-fires on a culture change so a
/// language switch mid-dialog refreshes the field labels alongside the rest of
/// the UI.</para>
/// </remarks>
public sealed partial class DiscoveryFieldRowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Action<DiscoveryFieldRowViewModel>? _onValueChanged;
    private string _value;

    /// <param name="field">The immutable discovery-field metadata.</param>
    /// <param name="initialValue">The pre-filled value (the current override
    /// from config, or empty when none is set). Null is treated as empty.</param>
    /// <param name="localization">The localization service; the label resolves
    /// through it and refreshes on a culture change.</param>
    /// <param name="onValueChanged">Optional callback invoked on every genuine
    /// Value change. Not invoked for the initial value. The Settings VM uses it
    /// for write-through; the escape-hatch VM passes <c>null</c>.</param>
    public DiscoveryFieldRowViewModel(
        Settings.DiscoveryField field,
        string initialValue,
        LocalizationService localization,
        Action<DiscoveryFieldRowViewModel>? onValueChanged = null)
    {
        Field = field;
        _value = initialValue ?? string.Empty;
        _localization = localization;
        _onValueChanged = onValueChanged;
        _localization.PropertyChanged += OnCultureChanged;
    }

    /// <summary>
    /// The immutable field metadata (canonical name, label resx key, browse
    /// kind). Bound by the view to drive the Browse button's picker kind.
    /// </summary>
    public Settings.DiscoveryField Field { get; }

    /// <summary>
    /// The localized human-readable label for the field. Re-resolves on a
    /// culture change (the row subscribes to the localization service).
    /// </summary>
    public string Label => _localization[Field.LabelResxKey];

    /// <summary>
    /// The TextBox value. An empty / whitespace string clears the field (the
    /// parent VM maps empty back to <c>null</c> on the matching
    /// <see cref="Config.DiscoveryConfig"/> property). Setting to a new value
    /// invokes the optional change callback after the property-changed event
    /// fires.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                _onValueChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Whether this row's TextBox is editable and its Browse button enabled.
    /// Reflects the current discovery mode (manual on, automatic off); the
    /// parent VM pushes it down whenever the mode toggles or the rows refresh.
    /// Views bind <c>TextBox.IsReadOnly</c> to <c>!IsEditable</c> (read-only
    /// paths stay selectable for copying); the Browse button binds to
    /// <see cref="IsBrowseEnabled"/> instead (editability plus the Gaming
    /// Mode gate).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseEnabled))]
    [NotifyPropertyChangedFor(nameof(BrowseTooltip))]
    private bool _isEditable;

    /// <summary>
    /// Whether the app runs inside a Steam Deck Gaming Mode session (where
    /// file/folder pickers are unusable). Pushed down by the parent VM
    /// alongside <see cref="IsEditable"/>; constant for the process lifetime,
    /// but settable so parents share one push path with the mode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseEnabled))]
    [NotifyPropertyChangedFor(nameof(BrowseTooltip))]
    private bool _isGamingMode;

    /// <summary>
    /// Whether this row's Browse button is enabled: the row must be editable
    /// (manual discovery mode) AND the app must not be in a Steam Deck Gaming
    /// Mode session (pickers are unusable there). The TextBox stays editable
    /// either way; manual path entry keeps working in Gaming Mode.
    /// </summary>
    public bool IsBrowseEnabled => IsEditable && !IsGamingMode;

    /// <summary>
    /// The Browse button's tooltip: the localized Gaming Mode guidance while
    /// gaming (shown on the disabled button), or <c>null</c> in normal mode so
    /// an ordinary working button carries no tooltip. Re-resolves on a culture
    /// change.
    /// </summary>
    public string? BrowseTooltip => IsGamingMode
        ? _localization["GamingMode_PickerGuidance"]
        : null;

    /// <summary>
    /// Detaches the culture-change subscription so this short-lived row is
    /// collectable after its window closes (the localization service is a
    /// singleton that outlives any dialog). The owning VM should call this on
    /// window close for each row.
    /// </summary>
    public void Detach() => _localization.PropertyChanged -= OnCultureChanged;

    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalizationService.Culture) or "Item[]")
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(BrowseTooltip));
        }
    }
}
