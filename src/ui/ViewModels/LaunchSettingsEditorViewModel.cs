using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One editable environment-variable row in the launch-settings editor: a name +
/// value pair plus the inline localized validation message derived from the
/// current state of all rows (so a duplicate or reserved-name error is reported
/// live as the user types). The parent
/// <see cref="LaunchSettingsEditorViewModel"/> owns the validation pass; this
/// row carries state only.
/// </summary>
public partial class EnvVarRow : ObservableObject
{
    /// <summary>The environment-variable name (editable). Validated by the
    /// parent VM (non-empty, no <c>=</c>/NUL, not reserved, unique
    /// case-insensitively).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The environment-variable value (editable, stored exactly).
    /// Validated by the parent VM (no NUL).</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>
    /// The localized inline validation message for this row (empty when valid).
    /// Computed + pushed by the parent
    /// <see cref="LaunchSettingsEditorViewModel"/> on every edit, so it tracks
    /// duplicate/reserved-name state across rows live.
    /// </summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public EnvVarRow(string name, string value)
    {
        _name = name;
        _value = value;
    }
}

/// <summary>
/// One editable game-argument row in the launch-settings editor: a single exact
/// argv value (any string is legal; Relay owns the final quoting). No
/// validation, a game argument is opaque to Curator.
/// </summary>
public partial class GameArgRow : ObservableObject
{
    /// <summary>The game argument (editable, stored exactly verbatim).</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    public GameArgRow(string value) => _value = value;
}

/// <summary>
/// Reusable launch-settings editor state: ordered environment-variable rows +
/// ordered game-argument rows (each add/remove) with inline localized
/// validation, the Enable Lua Logs + Skip Splash toggles, structural dirty
/// tracking against the last <see cref="Load"/> baseline, and a value builder
/// (<see cref="BuildSettings"/>). Owns edit state, validation, and value
/// construction only; it never persists. Composed by the transitional
/// <see cref="LaunchSettingsViewModel"/> modal host and, later, the Profiles
/// destination.
/// </summary>
/// <remarks>
/// <para><b>Validation never throws:</b> the inline pass recomputes each env
/// row's <see cref="EnvVarRow.ErrorMessage"/> on every edit (name + value +
/// duplicate + reserved), and <see cref="IsValid"/> is false while any row is
/// invalid. Validation policy lives in the shared <see cref="LaunchSettingsValidator"/>
/// (the single source of truth, also used by the service); this VM only maps
/// each structured error to the row's localized message.</para>
/// <para><b>Change notification:</b> every add, remove, row edit, and toggle
/// recomputes validation + dirty state and raises <see cref="Changed"/> so a
/// consumer (the modal host, the future Profiles VM) can recompute its
/// aggregate CanSave/dirty state. Notifications are suppressed during
/// <see cref="Load"/> so a programmatic reload does not fire as a user edit.</para>
/// <para><b>Row handler lifetime:</b> each row is subscribed via a named method
/// so it can be detached on removal or reload. A discarded row stops
/// influencing editor state and is collectable.</para>
/// </remarks>
public partial class LaunchSettingsEditorViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    // Structural dirty baseline captured at Load. Compared value-by-value so
    // order, duplicates, and exact strings survive (not collection identity).
    private (string Name, string Value)[] _baselineEnv = Array.Empty<(string, string)>();
    private string[] _baselineArgs = Array.Empty<string>();
    private bool _baselineLua;
    private bool _baselineSplash;

    // Suppresses outward change notification (Changed + IsDirty recompute) during
    // a programmatic Load so a reload does not look like a burst of user edits.
    private bool _suppressChanges;

    public LaunchSettingsEditorViewModel(LocalizationService localization)
    {
        _localization = localization;
    }

    /// <summary>The editable environment-variable rows.</summary>
    public ObservableCollection<EnvVarRow> EnvironmentVariables { get; } = new();

    /// <summary>The editable game-argument rows (one exact argv value each).</summary>
    public ObservableCollection<GameArgRow> GameArguments { get; } = new();

    /// <summary>
    /// Whether Relay's <c>--log-lua</c> flag is emitted at launch (tees Lua
    /// print output into the log file). No validation (a boolean toggle).
    /// </summary>
    [ObservableProperty]
    private bool _enableLuaLogs;

    /// <summary>
    /// Whether Relay's <c>--skip-splash</c> flag is emitted at launch (skips the
    /// intro splash state). No validation (a boolean toggle).
    /// </summary>
    [ObservableProperty]
    private bool _skipSplash;

    partial void OnEnableLuaLogsChanged(bool value) => OnEdit();

    partial void OnSkipSplashChanged(bool value) => OnEdit();

    /// <summary>
    /// True when no env row carries an inline error. Pure over the current row
    /// messages; recomputed (and notified) whenever validation reruns.
    /// </summary>
    public bool IsValid
    {
        get
        {
            foreach (var row in EnvironmentVariables)
            {
                if (!string.IsNullOrEmpty(row.ErrorMessage))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// True when the current rows differ structurally from the last
    /// <see cref="Load"/> baseline: ordered env names + values, ordered game
    /// arguments (duplicates count), or either boolean. Structural, not
    /// collection-identity.
    /// </summary>
    public bool IsDirty => ComputeIsDirty();

    /// <summary>
    /// Raised on any add, remove, row edit, or toggle change (suppressed during
    /// <see cref="Load"/>). A consumer recomputes its aggregate dirty/CanSave
    /// state in response.
    /// </summary>
    public event EventHandler? Changed;

    // ---- load ---------------------------------------------------------------

    /// <summary>
    /// Deep-copies <paramref name="settings"/> into fresh, subscribed rows +
    /// toggles, captures the dirty baseline, and recomputes validation. Resets
    /// <see cref="IsDirty"/> to false. Outward change notification is suppressed
    /// for the duration, so a reload never looks like a burst of user edits;
    /// only the final <see cref="IsDirty"/> reset is signaled.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is
    /// <c>null</c>.</exception>
    public void Load(LaunchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            _suppressChanges = true;

            DetachAllRowHandlers();

            EnvironmentVariables.Clear();
            foreach (var ev in settings.EnvironmentVariables)
            {
                var row = new EnvVarRow(ev.Name, ev.Value);
                Watch(row);
                EnvironmentVariables.Add(row);
            }

            GameArguments.Clear();
            foreach (var arg in settings.GameArguments)
            {
                var row = new GameArgRow(arg);
                WatchArg(row);
                GameArguments.Add(row);
            }

            EnableLuaLogs = settings.EnableLuaLogs;
            SkipSplash = settings.SkipSplash;

            CaptureBaseline();
            RecomputeValidation();
        }
        finally
        {
            _suppressChanges = false;
        }

        // Signal the reset observably. Load itself never raises Changed.
        OnPropertyChanged(nameof(IsDirty));
    }

    // ---- culture refresh ----------------------------------------------------

    /// <summary>
    /// Re-maps each row's localized validation message after a UI culture change,
    /// without touching row values, the dirty baseline, <see cref="IsDirty"/>, or
    /// raising the user-edit <see cref="Changed"/> event. A host with an
    /// application-lifetime localization subscription calls this so the inline
    /// messages re-resolve in the new language live. Safe to call at any time.
    /// </summary>
    /// <remarks>
    /// <see cref="RecomputeValidation"/> only writes each row's
    /// <see cref="EnvVarRow.ErrorMessage"/> (a programmatic field filtered out of
    /// <see cref="OnEnvRowPropertyChanged"/> so it cannot re-enter as a user edit)
    /// and notifies <see cref="IsValid"/>. The row values and
    /// <see cref="CaptureBaseline"/> snapshot are untouched, so dirty state is
    /// unchanged; only the displayed text re-resolves.
    /// </remarks>
    public void RefreshLocalizedValidation() => RecomputeValidation();

    // ---- build --------------------------------------------------------------

    /// <summary>
    /// Builds a new <see cref="LaunchSettings"/> from the current rows +
    /// toggles, preserving row order, duplicate game arguments, and exact
    /// values. A pure value builder; it never persists.
    /// </summary>
    public LaunchSettings BuildSettings() => new()
    {
        EnvironmentVariables = EnvironmentVariables
            .Select(r => new EnvVar(r.Name, r.Value))
            .ToArray(),
        GameArguments = GameArguments
            .Select(r => r.Value)
            .ToArray(),
        EnableLuaLogs = EnableLuaLogs,
        SkipSplash = SkipSplash,
    };

    // ---- add / remove rows --------------------------------------------------

    /// <summary>Adds a new empty environment-variable row (subscribed for live
    /// validation).</summary>
    [RelayCommand]
    private void AddEnvVar()
    {
        var row = new EnvVarRow(string.Empty, string.Empty);
        Watch(row);
        EnvironmentVariables.Add(row);
        OnEdit();
    }

    /// <summary>Removes an environment-variable row and detaches its handler so
    /// the discarded row cannot keep firing changes.</summary>
    [RelayCommand]
    private void RemoveEnvVar(EnvVarRow? row)
    {
        if (row is null)
        {
            return;
        }
        Unwatch(row);
        EnvironmentVariables.Remove(row);
        OnEdit();
    }

    /// <summary>Adds a new empty game-argument row (subscribed so its edits
    /// participate in dirty state).</summary>
    [RelayCommand]
    private void AddGameArg()
    {
        var row = new GameArgRow(string.Empty);
        WatchArg(row);
        GameArguments.Add(row);
        OnEdit();
    }

    /// <summary>Removes a game-argument row and detaches its handler so the
    /// discarded row cannot keep firing changes.</summary>
    [RelayCommand]
    private void RemoveGameArg(GameArgRow? row)
    {
        if (row is null)
        {
            return;
        }
        UnwatchArg(row);
        GameArguments.Remove(row);
        OnEdit();
    }

    // ---- change plumbing ----------------------------------------------------

    /// <summary>
    /// Recomputes validation + dirty state and raises <see cref="Changed"/> for
    /// any edit. A no-op while <see cref="_suppressChanges"/> is set (during
    /// Load), so a programmatic reload does not surface as user edits.
    /// </summary>
    private void OnEdit()
    {
        if (_suppressChanges)
        {
            return;
        }

        RecomputeValidation();
        OnPropertyChanged(nameof(IsDirty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Watch(EnvVarRow row) => row.PropertyChanged += OnEnvRowPropertyChanged;
    private void Unwatch(EnvVarRow row) => row.PropertyChanged -= OnEnvRowPropertyChanged;
    private void WatchArg(GameArgRow row) => row.PropertyChanged += OnGameArgRowPropertyChanged;
    private void UnwatchArg(GameArgRow row) => row.PropertyChanged -= OnGameArgRowPropertyChanged;

    private void DetachAllRowHandlers()
    {
        foreach (var row in EnvironmentVariables)
        {
            Unwatch(row);
        }
        foreach (var row in GameArguments)
        {
            UnwatchArg(row);
        }
    }

    // ErrorMessage changes are programmatic (set by RecomputeValidation) and
    // must not re-enter OnEdit, so each handler filters to the user-edit fields.
    private void OnEnvRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvVarRow.Name) or nameof(EnvVarRow.Value))
        {
            OnEdit();
        }
    }

    private void OnGameArgRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameArgRow.Value))
        {
            OnEdit();
        }
    }

    // ---- validation ---------------------------------------------------------

    /// <summary>
    /// Recomputes every env row's <see cref="EnvVarRow.ErrorMessage"/> from the
    /// shared <see cref="LaunchSettingsValidator"/> (the single source of
    /// truth): builds a <see cref="LaunchSettings"/> from the rows, asks the
    /// validator, and maps each structured error to the corresponding row's
    /// localized message. Notifies <see cref="IsValid"/> so bound consumers
    /// refresh. Game arguments are not validated (any string is legal argv).
    /// </summary>
    private void RecomputeValidation()
    {
        var settings = new LaunchSettings
        {
            EnvironmentVariables = EnvironmentVariables
                .Select(r => new EnvVar(r.Name, r.Value))
                .ToArray(),
        };
        var errors = LaunchSettingsValidator.Validate(settings);

        // Clear every row, then apply the validator's per-entry errors. A row
        // with no error stays clear (the validator reports at most one error per
        // entry, in entry order, so indices line up with the rows).
        foreach (var row in EnvironmentVariables)
        {
            row.ErrorMessage = string.Empty;
        }
        foreach (var error in errors)
        {
            if (error.Index >= 0 && error.Index < EnvironmentVariables.Count)
            {
                EnvironmentVariables[error.Index].ErrorMessage = LocalizeError(error);
            }
        }

        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>
    /// Maps one structured validation error to the localized inline message the
    /// row shows. The structured error carries no localization (the Profiles
    /// library is backend-only); this is the single place the kind -> resx key
    /// mapping lives, so the inline messages track the shared rules exactly.
    /// </summary>
    private string LocalizeError(LaunchSettingsValidationError error) => error.Kind switch
    {
        LaunchSettingsValidationErrorKind.NameEmpty => _localization["LaunchSettings_ErrNameRequired"],
        LaunchSettingsValidationErrorKind.NameInvalid => _localization["LaunchSettings_ErrNameInvalid"],
        LaunchSettingsValidationErrorKind.NameReserved => _localization.Format("LaunchSettings_ErrNameReserved", error.Name),
        LaunchSettingsValidationErrorKind.NameDuplicate => _localization["LaunchSettings_ErrNameDuplicate"],
        LaunchSettingsValidationErrorKind.ValueNul => _localization["LaunchSettings_ErrValueInvalid"],
        _ => string.Empty,
    };

    // ---- dirty --------------------------------------------------------------

    private void CaptureBaseline()
    {
        _baselineEnv = EnvironmentVariables.Select(r => (r.Name, r.Value)).ToArray();
        _baselineArgs = GameArguments.Select(r => r.Value).ToArray();
        _baselineLua = EnableLuaLogs;
        _baselineSplash = SkipSplash;
    }

    private bool ComputeIsDirty()
    {
        if (EnableLuaLogs != _baselineLua || SkipSplash != _baselineSplash)
        {
            return true;
        }

        if (EnvironmentVariables.Count != _baselineEnv.Length)
        {
            return true;
        }
        for (var i = 0; i < EnvironmentVariables.Count; i++)
        {
            var row = EnvironmentVariables[i];
            var b = _baselineEnv[i];
            if (!string.Equals(row.Name, b.Name, StringComparison.Ordinal)
                || !string.Equals(row.Value, b.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (GameArguments.Count != _baselineArgs.Length)
        {
            return true;
        }
        for (var i = 0; i < GameArguments.Count; i++)
        {
            if (!string.Equals(GameArguments[i].Value, _baselineArgs[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
