using Modificus.Curator.General;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// Constructs the discovery escape-hatch dialog's view model, the one modal
/// VM with service dependencies. Keeps <see cref="DialogService"/> free of VM
/// construction (and of the Steam / config / gaming-mode dependencies that
/// exist solely to build it) while the service keeps its actual job: showing
/// the dialog over the owner window.
/// </summary>
/// <remarks>
/// Deliberately narrow: a per-dialog factory for the one dialog that needs
/// one, not a generalized all-dialogs factory (the other dialogs need no VM
/// dependencies, so a general seam would be speculative). Returns the VM (not
/// the Window) because the dialog's result lives on the VM and the
/// VM-to-Window pairing belongs to the code that shows the Window.
/// </remarks>
public interface IDiscoveryEscapeHatchFactory
{
    /// <summary>
    /// Creates the escape-hatch VM focused on the given missing discovery
    /// fields (inputs are shown only for those fields).
    /// </summary>
    DiscoveryEscapeHatchViewModel Create(IReadOnlyList<string> missingFields);
}

/// <summary>
/// The production factory over the live services (registered in composition).
/// </summary>
public sealed class DiscoveryEscapeHatchFactory : IDiscoveryEscapeHatchFactory
{
    private readonly IConfigLoader _configLoader;
    private readonly ISteamService _steam;
    private readonly LocalizationService _localization;
    private readonly IGamingModeState _gamingMode;

    public DiscoveryEscapeHatchFactory(
        IConfigLoader configLoader,
        ISteamService steam,
        LocalizationService localization,
        IGamingModeState gamingMode)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _gamingMode = gamingMode ?? throw new ArgumentNullException(nameof(gamingMode));
    }

    /// <inheritdoc />
    public DiscoveryEscapeHatchViewModel Create(IReadOnlyList<string> missingFields) =>
        new(missingFields, _configLoader, _steam, _localization, _gamingMode);
}
