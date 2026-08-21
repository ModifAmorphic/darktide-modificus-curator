using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The row-facing projection of one queued <see cref="DownloadItem"/>: the
/// narrow download view both hosts render (the in-place morph on a
/// <see cref="ModItemViewModel"/> and the appended row below the profile
/// rows). Owns no download state: every phase, byte, name, and pulse change
/// arrives through the item's own UI-thread property notifications and is
/// re-fired here as the derived bindables the shared template consumes.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle:</b> created, cached, and dropped by
/// <see cref="ModListViewModel"/> per queue item (the wrapper lives exactly
/// as long as its item sits in the coordinator's collection), so the
/// subscription below is bounded by the source it watches and a row dropped
/// by a reload never receives further notifications through it.</para>
/// <para><b>Commands:</b> Cancel, Dismiss, and Retry forward straight to
/// <see cref="IModDownloadQueue"/> with the wrapped item. The coordinator
/// owns every semantic (cancel is token-authoritative, dismiss is
/// Failed-only, retry re-enqueues the identical request); this wrapper adds
/// only the <see cref="CanCancel"/> render gate.</para>
/// <para><b>Join pulse:</b> a dedupe join increments the item's Pulse; the
/// wrapper surfaces that as <see cref="JoinPulse"/> plus the time-decayed
/// <see cref="IsPulsed"/> flag the row roots bind their one-shot flash class
/// to.</para>
/// <para><b>Localized text is live:</b> the status word, the target-profile
/// label, the tooltips, and the automation string re-resolve from
/// <see cref="LocalizationService"/>; the parent list VM refreshes every
/// wrapper on a culture change (via <see cref="Refresh"/>).</para>
/// </remarks>
public partial class DownloadRowViewModel : ObservableObject
{
    /// <summary>How long the join-pulse flash stays lit before decaying.</summary>
    private static readonly TimeSpan DefaultPulseDecay = TimeSpan.FromMilliseconds(900);

    private readonly LocalizationService _localization;
    private readonly IModDownloadQueue _queue;
    private readonly TimeSpan _pulseDecay;
    private int _pulseVersion;

    /// <param name="localization">The localization service for the derived
    /// status, label, tooltip, and automation strings.</param>
    /// <param name="queue">The coordinator the Cancel / Dismiss / Retry
    /// commands forward to.</param>
    /// <param name="item">The coordinator-owned item this row projects.</param>
    /// <param name="pulseDecay">How long the join-pulse flash stays lit
    /// (tests inject a short window; production uses
    /// <see cref="DefaultPulseDecay"/>).</param>
    public DownloadRowViewModel(
        LocalizationService localization,
        IModDownloadQueue queue,
        DownloadItem item,
        TimeSpan? pulseDecay = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _pulseDecay = pulseDecay ?? DefaultPulseDecay;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>The coordinator-owned item this row projects.</summary>
    public DownloadItem Item { get; }

    /// <summary>
    /// The item's display name (the peeked or fallback name at enqueue, the
    /// resolved repository name once the download resolves it).
    /// </summary>
    public string DisplayName => Item.DisplayName;

    /// <summary>
    /// The always-shown target-profile label on the appended row
    /// ("for profile {name}"). The name is the enqueue-time capture;
    /// the completion verifies the profile through the service, never this
    /// string.
    /// </summary>
    public string ProfileLabel =>
        _localization.Format("ModRow_DownloadForProfile", Item.TargetProfileName);

    /// <summary>Whether the item is waiting for the serial worker.</summary>
    public bool IsQueued => Item.Phase == DownloadPhase.Queued;

    /// <summary>Whether the archive download is in flight.</summary>
    public bool IsDownloading => Item.Phase == DownloadPhase.Downloading;

    /// <summary>Whether the bytes are in and the import is running.</summary>
    public bool IsImporting => Item.Phase == DownloadPhase.Importing;

    /// <summary>Whether the item failed (it stays until dismissed or retried).</summary>
    public bool IsFailed => Item.Phase == DownloadPhase.Failed;

    /// <summary>
    /// Whether the row's Cancel affordance is usable: false once the item is
    /// terminal (a Failed row offers Retry + dismiss instead; Completed and
    /// Canceled rows leave the collection before they could render).
    /// </summary>
    public bool CanCancel => !Item.IsTerminal;

    /// <summary>
    /// Whether the progress bar renders determinate (Downloading with a known
    /// non-zero total).
    /// </summary>
    public bool ShowDeterminateProgress => IsDownloading && Item.TotalBytes is > 0;

    /// <summary>
    /// Whether the progress bar renders indeterminate: Downloading with an
    /// unknown total, or Importing.
    /// </summary>
    public bool ShowIndeterminateProgress =>
        (IsDownloading && Item.TotalBytes is not > 0) || IsImporting;

    /// <summary>
    /// The determinate progress value, 0 to 100 (clamped; 0 when the total
    /// is unknown).
    /// </summary>
    public double ProgressPercent
    {
        get
        {
            if (Item.TotalBytes is not > 0)
            {
                return 0;
            }

            return Math.Clamp(Item.ReceivedBytes * 100.0 / Item.TotalBytes.Value, 0, 100);
        }
    }

    /// <summary>
    /// The whole-number percent label ("45%"). Culture-free manual formatting
    /// (the same posture as the throttle countdown) so the value is
    /// deterministic across locales.
    /// </summary>
    public string PercentText =>
        ((int)Math.Round(ProgressPercent, MidpointRounding.AwayFromZero)).ToString(
            CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The byte-progress label: "12.3 / 27.5 MB" with a known total, the
    /// received bytes only ("12.3 MB") without one. Invariantly formatted
    /// (a file-size pair reads the same everywhere; no locale separators).
    /// </summary>
    public string BytesText => Item.TotalBytes is > 0
        ? string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0} / {1:0.0} MB",
            Item.ReceivedBytes / 1048576.0,
            Item.TotalBytes.Value / 1048576.0)
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0} MB",
            Item.ReceivedBytes / 1048576.0);

    /// <summary>The localized status word for the current phase.</summary>
    public string StatusText => Item.Phase switch
    {
        DownloadPhase.Queued => _localization["ModRow_DownloadQueued"],
        DownloadPhase.Downloading => _localization["ModRow_DownloadDownloading"],
        DownloadPhase.Importing => _localization["ModRow_DownloadImporting"],
        DownloadPhase.Failed => _localization["ModRow_DownloadFailed"],
        _ => string.Empty,
    };

    /// <summary>The failure message (Failed rows only; empty otherwise).</summary>
    public string FailureText => Item.ErrorMessage ?? string.Empty;

    /// <summary>
    /// The row's automation name: name + phase, plus the percent (or the
    /// received bytes when the total is unknown) while downloading, matching
    /// the mod rows' name-first accessibility convention.
    /// </summary>
    public string AutomationText
    {
        get
        {
            var detail = ShowDeterminateProgress
                ? PercentText
                : IsDownloading ? BytesText : string.Empty;
            return detail.Length == 0
                ? _localization.Format("ModRow_DownloadRowAutomation", DisplayName, StatusText)
                : _localization.Format(
                    "ModRow_DownloadRowProgressAutomation", DisplayName, StatusText, detail);
        }
    }

    /// <summary>The localized tooltip + automation name for the Cancel button.</summary>
    public string CancelTooltip => _localization["ModRow_DownloadCancelTooltip"];

    /// <summary>The localized tooltip + automation name for the Retry button.</summary>
    public string RetryTooltip => _localization["ModRow_DownloadRetryTooltip"];

    /// <summary>The localized tooltip + automation name for the dismiss button.</summary>
    public string DismissTooltip => _localization["ModRow_DownloadDismissTooltip"];

    /// <summary>
    /// The forwarded join-pulse counter (the item's ever-incrementing Pulse).
    /// Bindable so a host can react to a join; never reset.
    /// </summary>
    public int JoinPulse => Item.Pulse;

    /// <summary>
    /// Whether the join-pulse flash is lit: set on a pulse, decays to false
    /// after the pulse window. Bound to the row roots' one-shot flash class.
    /// </summary>
    [ObservableProperty]
    private bool _isPulsed;

    /// <summary>
    /// The item's own (coordinator-marshaled, UI-thread) property change:
    /// re-fires the derived bindables. Phase and byte writes can move several
    /// members at once, so one broad re-fire covers the small derived set.
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Pulse))
        {
            OnPropertyChanged(nameof(JoinPulse));
            BeginPulseFlash();
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsImporting));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ShowDeterminateProgress));
        OnPropertyChanged(nameof(ShowIndeterminateProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(BytesText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(AutomationText));
    }

    /// <summary>
    /// Lights the flash and arms its decay. The version counter makes the
    /// newest join own the decay, so the class stays applied for one window
    /// from the last join; the animation runs once per class application,
    /// so a join while the flag is already set extends the window without
    /// re-running the flash (a later join re-lights only after the flag has
    /// decayed).
    /// </summary>
    private void BeginPulseFlash()
    {
        IsPulsed = true;
        var version = ++_pulseVersion;
        _ = DecayPulseFlashAsync(version);
    }

    /// <summary>
    /// Dims the pulse flag after the decay window. Fire-and-forget and purely
    /// cosmetic: the awaited delay resumes on the captured context (the UI
    /// thread in production; never ConfigureAwait(false) in UI code), and the
    /// wrapper is unreachable once its item leaves the queue, so a late decay
    /// on a dropped row observes no listeners.
    /// </summary>
    private async Task DecayPulseFlashAsync(int version)
    {
        try
        {
            await Task.Delay(_pulseDecay);
        }
        catch (Exception)
        {
            // Task.Delay does not throw in practice; the flash is cosmetic.
            return;
        }

        if (version == _pulseVersion)
        {
            IsPulsed = false;
        }
    }

    /// <summary>
    /// Re-fires the localized derived strings so their bindings re-resolve
    /// after a UI culture switch. Called by the parent list VM (which owns
    /// the wrapper cache) on a culture change.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ProfileLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AutomationText));
        OnPropertyChanged(nameof(CancelTooltip));
        OnPropertyChanged(nameof(RetryTooltip));
        OnPropertyChanged(nameof(DismissTooltip));
    }

    /// <summary>Cancels the wrapped item through the coordinator.</summary>
    [RelayCommand]
    private void Cancel() => _queue.Cancel(Item);

    /// <summary>Dismisses the wrapped Failed item through the coordinator.</summary>
    [RelayCommand]
    private void Dismiss() => _queue.Dismiss(Item);

    /// <summary>
    /// Retries the wrapped Failed item through the coordinator (a fresh
    /// enqueue of the identical request).
    /// </summary>
    [RelayCommand]
    private void Retry() => _queue.Retry(Item);
}
