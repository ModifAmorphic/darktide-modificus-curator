using Modificus.Curator.Steam;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// Whether the app is running inside a Steam Deck Gaming Mode session.
/// The answer is fixed for the process lifetime: a session cannot change
/// without restarting Curator, so consumers read it directly with no
/// change notification.
/// </summary>
public interface IGamingModeState
{
    /// <summary>Whether the app runs inside a Steam Deck Gaming Mode session.</summary>
    bool IsGamingMode { get; }
}

/// <summary>
/// The production <see cref="IGamingModeState"/>: an application-lifetime
/// singleton that captures <see cref="GamingModeDetector.IsGamingMode"/> once
/// at construction and serves the captured answer thereafter.
/// </summary>
public sealed class GamingModeState : IGamingModeState
{
    private readonly bool _isGamingMode;

    /// <summary>Captures the detection once from the real environment.</summary>
    public GamingModeState() => _isGamingMode = GamingModeDetector.IsGamingMode();

    /// <summary>Fixed-result constructor for tests.</summary>
    internal GamingModeState(bool isGamingMode) => _isGamingMode = isGamingMode;

    /// <inheritdoc />
    public bool IsGamingMode => _isGamingMode;
}
