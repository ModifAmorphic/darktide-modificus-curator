namespace Modificus.Curator.UI.Session;

/// <summary>
/// The narrow "reload the mod list now" seam consumed by out-of-band mod-list
/// writers (the nxm download handler) and implemented by the mod-list view
/// model. Exists so those writers depend on a one-member interface the
/// composition root can forward to the list VM lazily, instead of a delegate
/// closure or the whole view-model surface.
/// </summary>
public interface IModListRefresh
{
    /// <summary>Rebuilds the mod list from the active profile.</summary>
    void Reload();
}
