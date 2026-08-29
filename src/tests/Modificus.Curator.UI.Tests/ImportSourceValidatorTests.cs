using Modificus.Curator.Mods;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="ImportSourceValidator"/>: the URL/id parsing + remote-field
/// rules shared by the import card's two modes (the batch per-item form and
/// the edit mode), extracted so both surfaces validate identically.
/// </summary>
public sealed class ImportSourceValidatorTests
{
    [Fact]
    public void Nexus_parses_a_bare_id_or_url_into_a_nexus_source()
    {
        Assert.True(ImportSourceValidator.TryParseUrl(
            ImportSource.Nexus, "42", out var bare));
        Assert.Equal(42, Assert.IsType<NexusSource>(bare).ModId);

        Assert.True(ImportSourceValidator.TryParseUrl(
            ImportSource.Nexus,
            "https://www.nexusmods.com/warhammer40kdarktide/mods/42/",
            out var url));
        Assert.Equal(42, Assert.IsType<NexusSource>(url).ModId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("https://example.com/mods/42")]
    public void Nexus_rejects_missing_or_malformed_input(string url)
    {
        Assert.False(ImportSourceValidator.TryParseUrl(ImportSource.Nexus, url, out _));
    }

    [Fact]
    public void Untracked_never_parses_a_remote_field()
    {
        Assert.False(ImportSourceValidator.TryParseUrl(
            ImportSource.Untracked, "42", out var parsed));
        Assert.IsType<UntrackedSource>(parsed);
    }

    [Fact]
    public void Remote_fields_are_valid_only_with_a_version_and_a_parsable_id()
    {
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "42", ""));
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "", "1.0"));
        Assert.False(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "garbage", "1.0"));
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Nexus, "42", "1.0"));
    }

    [Fact]
    public void Untracked_fields_are_always_valid()
    {
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Untracked, "", ""));
        Assert.True(ImportSourceValidator.IsRemoteSourceValid(
            ImportSource.Untracked, "anything", "anything"));
    }
}
