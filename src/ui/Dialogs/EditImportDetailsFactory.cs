using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// Constructs the edit-import-details dialog's view model from a container
/// id, the per-dialog-factory precedent of
/// <see cref="IDiscoveryEscapeHatchFactory"/>. Keeps
/// <see cref="DialogService"/> free of VM construction (and of the repository
/// dependency that exists solely to build it) while the service keeps its
/// actual job: showing the dialog over the owner window.
/// </summary>
/// <remarks>
/// Deliberately narrow: one factory for the one dialog that needs it (the
/// other dialog VMs need no service dependencies). Returns the VM (not the
/// Window) because the dialog's result lives on the VM and the VM-to-Window
/// pairing belongs to the code that shows the Window. Returns <c>null</c>
/// when the container no longer exists or is linked (never editable), so the
/// service can skip the modal entirely.
/// </remarks>
public interface IEditImportDetailsFactory
{
    /// <summary>
    /// Creates the edit-details VM loaded from the container's current facts,
    /// or <c>null</c> when the id is unknown or the container is linked (the
    /// caller skips the modal).
    /// </summary>
    EditImportDetailsViewModel? Create(Guid containerId);
}

/// <summary>
/// The production factory over the live repository (registered in
/// composition).
/// </summary>
public sealed class EditImportDetailsFactory : IEditImportDetailsFactory
{
    private readonly IModRepository _repo;
    private readonly LocalizationService _localization;

    public EditImportDetailsFactory(IModRepository repo, LocalizationService localization)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <inheritdoc />
    public EditImportDetailsViewModel? Create(Guid containerId)
    {
        var container = _repo.Get(containerId);
        if (container is null || container.Source is LinkedSource)
        {
            return null;
        }

        return new EditImportDetailsViewModel(container, _repo, _localization);
    }
}
