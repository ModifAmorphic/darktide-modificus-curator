namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The five hosted shell destinations, in navigation-rail order. The shell owns
/// one <see cref="ShellViewModel.CurrentDestination"/> of this type; selecting
/// the current value is a strict no-op.
/// </summary>
public enum ShellDestination
{
    Profiles,
    Mods,
    NexusIntegrations,
    Preferences,
    Settings,
}
