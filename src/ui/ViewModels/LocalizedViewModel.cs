using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The shared culture-refresh mechanism for localized view models: takes the
/// <see cref="LocalizationService"/>, subscribes once, and on a culture
/// change re-fires the derived VM's registered localized property names so
/// their bindings re-resolve. Each VM declares its localized property names
/// in ONE place (<see cref="LocalizedProperties"/>, next to the properties);
/// a getter referencing <c>_localization[...]</c> that is not registered is a
/// red source-scan test (the <c>LocalizedViewModelRegistrationTests</c>),
/// not silently stale UI text.
/// </summary>
/// <remarks>
/// Deliberately tiny: this base owns the subscription + the name re-fire and
/// nothing else (no caching, no string lookup helpers, no static surface). VM
/// specifics stay in the derived class; non-list culture work (per-row
/// refreshes, gate re-renders, state re-resolves) belongs in the
/// <see cref="OnCultureChanged"/> hook.
/// </remarks>
public abstract class LocalizedViewModel : ObservableObject
{
    /// <summary>
    /// The localization service the derived VM resolves strings from. Kept as
    /// the established <c>_localization[...]</c> field shape so derived
    /// getters read exactly as they did when each VM held its own field (and
    /// so the source-scan test's getter detection stays meaningful).
    /// </summary>
    protected LocalizationService _localization;

    private bool _detached;

    /// <param name="localization">The shared localization service (its
    /// culture change drives the re-fire).</param>
    protected LocalizedViewModel(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _localization.PropertyChanged += OnLocalizationPropertyChanged;
    }

    /// <summary>
    /// The derived VM's localized property names, declared once next to the
    /// properties: every property whose getter resolves through
    /// <c>_localization[...]</c> belongs here, or the source-scan test fails.
    /// Re-fired (in declaration order) on every culture change.
    /// </summary>
    protected abstract IReadOnlyList<string> LocalizedProperties { get; }

    /// <summary>
    /// The non-list culture work this VM genuinely does beyond re-firing
    /// <see cref="LocalizedProperties"/> (per-row Refresh calls, gate
    /// re-renders, state re-resolves). Runs after the registered names have
    /// re-fired.
    /// </summary>
    protected virtual void OnCultureChanged()
    {
    }

    /// <summary>
    /// Stops the culture subscription + re-fire for this VM. For
    /// transient VMs (dialog + row VMs) whose lifetime is shorter than the
    /// application-lifetime localization service, so a detached instance is
    /// collectable; idempotent. Application-lifetime VMs never call this.
    /// </summary>
    protected void DetachLocalization()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _localization.PropertyChanged -= OnLocalizationPropertyChanged;
    }

    private void OnLocalizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        foreach (var name in LocalizedProperties)
        {
            OnPropertyChanged(name);
        }

        OnCultureChanged();
    }
}
