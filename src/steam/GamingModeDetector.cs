namespace Modificus.Curator.Steam;

/// <summary>
/// Detects a Steam Deck Gaming Mode session from the process environment:
/// the complete signature is <c>SteamOS=1</c> + <c>SteamGamepadUI=1</c> +
/// <c>XDG_CURRENT_DESKTOP=gamescope</c>, all three required, ordinal and
/// case-sensitive. Any missing or mismatched variable means not Gaming Mode.
/// </summary>
/// <remarks>
/// Independent of <see cref="SteamDeckDetector"/>, which detects Deck
/// hardware from OS release metadata: a Deck can be in Desktop Mode (the
/// signature variables are absent there), and another compositor's
/// gamescope session is not SteamOS Gaming Mode because the signature
/// requires the SteamOS pair as well.
/// </remarks>
public static class GamingModeDetector
{
    /// <summary>Runs the production detection against the real environment.</summary>
    public static bool IsGamingMode() => IsGamingMode(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Environment-injectable form for tests. Each variable is read through
    /// <paramref name="getEnvironmentVariable"/>; a null read (missing
    /// variable) never matches.
    /// </summary>
    public static bool IsGamingMode(Func<string, string?> getEnvironmentVariable) =>
        getEnvironmentVariable("SteamOS") == "1"
        && getEnvironmentVariable("SteamGamepadUI") == "1"
        && getEnvironmentVariable("XDG_CURRENT_DESKTOP") == "gamescope";
}
