namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="ModSourceParser"/>: URL → canonical <see cref="ModSource"/>
/// parsing for the inline import card. Covers the accepted shapes (Nexus URL/id) +
/// the malformed rejections (wrong host, too few segments, non-numeric id).
/// Never throws: every malformed case is <c>false</c>.
/// </summary>
public sealed class ModSourceParserTests
{
    // ---- Nexus parsing -----------------------------------------------------

    [Theory]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345", 12345)]
    [InlineData("https://nexusmods.com/warhammer40kdarktide/mods/12345", 12345)]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345/", 12345)]      // trailing slash
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345?tab=files", 12345)] // query string
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345/#content", 12345)] // fragment
    [InlineData("HTTPS://WWW.NEXUSMODS.COM/Warhammer40kDarktide/mods/12345", 12345)]        // host + slug case
    public void TryParseNexus_accepts_valid_url_variants(string url, int expectedModId)
    {
        Assert.True(ModSourceParser.TryParseNexus(url, out var source));
        Assert.Equal(expectedModId, source.ModId);
    }

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("  12345  ", 12345)]     // whitespace trimmed
    [InlineData("999999999", 999999999)]
    public void TryParseNexus_accepts_plain_integer_id(string input, int expectedModId)
    {
        Assert.True(ModSourceParser.TryParseNexus(input, out var source));
        Assert.Equal(expectedModId, source.ModId);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/skyrim/mods/12345", "wrong game slug")]
    [InlineData("https://example.com/warhammer40kdarktide/mods/12345", "wrong host")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/", "missing id segment")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/12345", "missing mods segment")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/abc", "non-numeric id")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/0", "zero id")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/-1", "negative id")]
    [InlineData("not a url", "garbage")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    [InlineData("42abc", "integer prefix then garbage")]
    public void TryParseNexus_rejects_malformed_input(string input, string reason)
    {
        Assert.False(ModSourceParser.TryParseNexus(input, out _), $"should reject ({reason}): {input}");
    }

    [Fact]
    public void TryParseNexus_does_not_throw_on_garbage()
    {
        // The contract is "never throws"; a malformed input returns false.
        foreach (var malformed in new[] { "://", "ht!tp://x", null!, "\0\0\0" })
        {
            Assert.False(ModSourceParser.TryParseNexus(malformed ?? "", out _));
        }
    }
}
