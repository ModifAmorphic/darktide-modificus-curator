namespace Modificus.Curator.General;

/// <summary>The persisted main-window geometry: the last valid Normal client
/// size in DIP and whether the last meaningful state was Maximized. Primitives
/// only (no Avalonia type) so this source-agnostic library does not depend on
/// the UI. Stored as one atomic record under
/// <see cref="IAppStateStore.MainWindowState"/> so width, height, and the flag
/// always land together and a partial triple can never be persisted.</summary>
public sealed record AppWindowState(double Width, double Height, bool IsMaximized);
