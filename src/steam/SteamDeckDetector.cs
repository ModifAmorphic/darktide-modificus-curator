namespace Modificus.Curator.Steam;

/// <summary>
/// Detects Steam Deck hardware from OS release metadata: the host
/// <c>/run/host/os-release</c> is checked before <c>/etc/os-release</c>, and a
/// match requires <c>ID=steamos</c> + <c>VARIANT_ID=steamdeck</c> (the values
/// SteamOS ships on the Deck). Quoted values are tolerated; IO and access
/// failures degrade to "not a Steam Deck".
/// </summary>
/// <remarks>
/// <see cref="SteamDiscoveryOptions.IsSteamDeck"/> is the injectable seam;
/// this helper is only the production detection.
/// </remarks>
internal static class SteamDeckDetector
{
    private const string HostOsReleasePath = "/run/host/os-release";
    private const string EtcOsReleasePath = "/etc/os-release";

    /// <summary>Runs the production detection against the real OS release files.</summary>
    public static bool IsSteamDeck() => IsSteamDeck(HostOsReleasePath, EtcOsReleasePath);

    /// <summary>
    /// Path-injectable form for tests. Each file is parsed when present; the
    /// host file is consulted first, and either file identifying the Deck wins.
    /// </summary>
    internal static bool IsSteamDeck(string hostOsReleasePath, string etcOsReleasePath) =>
        IdentifiesSteamDeck(hostOsReleasePath) || IdentifiesSteamDeck(etcOsReleasePath);

    private static bool IdentifiesSteamDeck(string osReleasePath)
    {
        string? id = null;
        string? variantId = null;
        try
        {
            if (!File.Exists(osReleasePath))
            {
                return false;
            }

            foreach (var line in File.ReadLines(osReleasePath))
            {
                if (TryReadAssignment(line, out var key, out var value))
                {
                    if (key == "ID")
                    {
                        id = value;
                    }
                    else if (key == "VARIANT_ID")
                    {
                        variantId = value;
                    }
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return id == "steamos" && variantId == "steamdeck";
    }

    /// <summary>
    /// Parses one <c>KEY=value</c> assignment, skipping comments and blank
    /// lines and stripping matching single or double quotes.
    /// </summary>
    private static bool TryReadAssignment(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return false;
        }

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        key = trimmed[..separator].Trim();
        value = Unquote(trimmed[(separator + 1)..].Trim());
        return true;
    }

    private static string Unquote(string value) =>
        value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
