namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="GamingModeDetector"/>: the complete
/// three-variable signature matches, each single missing variable fails,
/// wrong or decorated values fail, and the empty environment fails.
/// </summary>
public sealed class GamingModeDetectorTests
{
    private static Dictionary<string, string> CompleteSignature() => new()
    {
        ["SteamOS"] = "1",
        ["SteamGamepadUI"] = "1",
        ["XDG_CURRENT_DESKTOP"] = "gamescope",
    };

    private static bool Detect(IReadOnlyDictionary<string, string> environment) =>
        GamingModeDetector.IsGamingMode(
            name => environment.TryGetValue(name, out var value) ? value : null);

    [Fact]
    public void Complete_signature_matches()
    {
        Assert.True(Detect(CompleteSignature()));
    }

    [Theory]
    [InlineData("SteamOS")]
    [InlineData("SteamGamepadUI")]
    [InlineData("XDG_CURRENT_DESKTOP")]
    public void Each_single_missing_variable_does_not_match(string missing)
    {
        var environment = CompleteSignature();
        environment.Remove(missing);

        Assert.False(Detect(environment));
    }

    [Theory]
    [InlineData("SteamOS", "0")]
    [InlineData("SteamOS", "true")]
    [InlineData("SteamOS", "")]
    [InlineData("SteamGamepadUI", "0")]
    [InlineData("XDG_CURRENT_DESKTOP", "GNOME")]
    [InlineData("XDG_CURRENT_DESKTOP", "Gamescope")]
    [InlineData("XDG_CURRENT_DESKTOP", "\"gamescope\"")]
    [InlineData("XDG_CURRENT_DESKTOP", " gamescope")]
    [InlineData("XDG_CURRENT_DESKTOP", "gamescope ")]
    public void Wrong_or_decorated_values_do_not_match(string name, string value)
    {
        // Values are ordinal + case-sensitive: near-miss casing, quoting,
        // and surrounding whitespace must all miss.
        var environment = CompleteSignature();
        environment[name] = value;

        Assert.False(Detect(environment));
    }

    [Fact]
    public void Empty_environment_does_not_match()
    {
        Assert.False(Detect(new Dictionary<string, string>()));
    }

    [Fact]
    public void Null_variable_read_does_not_match()
    {
        // A null read (the production lookup's missing-variable result)
        // must behave like a mismatched value.
        Assert.False(GamingModeDetector.IsGamingMode(_ => null));
    }
}
