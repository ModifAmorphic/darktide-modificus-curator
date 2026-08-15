using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The shell's guarded navigation surface, implemented by
/// <see cref="ViewModels.ShellViewModel"/> and consumed by UI-layer services
/// (the first-run onboarding coordinator) that need to direct the user to a
/// destination without depending on the whole shell view model. The
/// composition root registers it as a lazy forward to the shell singleton, so
/// neither side holds a construction-time cycle.
/// </summary>
public interface IShellNavigation
{
    /// <summary>
    /// Navigates to <paramref name="destination"/>, running the shell's
    /// leave/enter lifecycle (a same-destination call is a strict no-op).
    /// </summary>
    Task NavigateAsync(ShellDestination destination);
}
