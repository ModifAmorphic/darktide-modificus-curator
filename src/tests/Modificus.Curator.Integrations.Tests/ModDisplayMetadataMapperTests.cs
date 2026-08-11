using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// <see cref="ModDisplayMetadataMapper"/>: the single normalization path from
/// a Nexus v1 <see cref="ModInfo"/> into the source-agnostic
/// <see cref="ModDisplayMetadata"/> value object. Covers summary trimming +
/// empty-to-empty, the HTTPS-only thumbnail rule (rejects empty, malformed,
/// http, scheme-less, relative), and the verbatim adult-content copy. The
/// mapper is internal + shared by acquisition and the later backfill, so these
/// rules are the canonical statement of how a Nexus payload becomes display
/// metadata.
/// </summary>
public sealed class ModDisplayMetadataMapperTests
{
    // ---- summary normalization --------------------------------------------

    [Fact]
    public void ToDisplayMetadata_trims_a_non_empty_summary()
    {
        var info = new ModInfo { Summary = "  A short summary.  " };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Equal("A short summary.", metadata.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDisplayMetadata_normalizes_null_or_whitespace_summary_to_empty(string? summary)
    {
        var info = new ModInfo { Summary = summary! };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Equal(string.Empty, metadata.Summary);
    }

    // ---- thumbnail URL normalization --------------------------------------

    [Fact]
    public void ToDisplayMetadata_trims_a_valid_https_thumbnail_url()
    {
        var info = new ModInfo { PictureUrl = "  https://example.com/thumb.png  " };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Equal("https://example.com/thumb.png", metadata.ThumbnailUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDisplayMetadata_normalizes_null_or_empty_picture_url_to_null(string? pictureUrl)
    {
        var info = new ModInfo { PictureUrl = pictureUrl };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Null(metadata.ThumbnailUrl);
    }

    [Theory]
    // Non-HTTPS schemes are rejected (the thumbnail cache downloads HTTPS only).
    [InlineData("http://example.com/thumb.png")]
    [InlineData("ftp://example.com/thumb.png")]
    // Scheme-less + relative values cannot be cached (no host, no scheme).
    [InlineData("example.com/thumb.png")]
    [InlineData("/thumb.png")]
    [InlineData("thumb.png")]
    public void ToDisplayMetadata_rejects_non_https_or_non_absolute_picture_url(string pictureUrl)
    {
        var info = new ModInfo { PictureUrl = pictureUrl };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Null(metadata.ThumbnailUrl);
    }

    [Fact]
    public void ToDisplayMetadata_rejects_a_malformed_picture_url()
    {
        // Uri.TryCreate returns false for a malformed URL; the mapper does not
        // throw, it downgrades to null.
        var info = new ModInfo { PictureUrl = "https://not a valid url with spaces" };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Null(metadata.ThumbnailUrl);
    }

    [Fact]
    public void ToDisplayMetadata_accepts_an_https_url_with_query_and_path()
    {
        // Real Nexus picture URLs carry a path + a query string; the trimmed
        // absolute HTTPS URL is kept verbatim.
        var info = new ModInfo { PictureUrl = "https://staticdelivery.nexusmods.com/mods/1234/images/1-123.jpg" };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.Equal("https://staticdelivery.nexusmods.com/mods/1234/images/1-123.jpg", metadata.ThumbnailUrl);
    }

    // ---- adult flag -------------------------------------------------------

    [Fact]
    public void ToDisplayMetadata_copies_ContainsAdultContent_verbatim()
    {
        var info = new ModInfo { ContainsAdultContent = true };

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.True(metadata.IsAdultContent);
    }

    [Fact]
    public void ToDisplayMetadata_defaults_IsAdultContent_to_false_when_absent()
    {
        // The wire default for an absent contains_adult_content is false (STJ
        // bool default); the mapper copies that through.
        var info = new ModInfo();

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.False(metadata.IsAdultContent);
    }

    // ---- empty-but-fetched contract --------------------------------------

    [Fact]
    public void ToDisplayMetadata_returns_non_null_for_an_empty_payload()
    {
        // A fetched result with no content is a non-null object with an empty
        // summary + null thumbnail. The container can then distinguish
        // fetched-but-empty (this object) from not-fetched (null at the
        // container level), which is what the backfill candidate selection
        // relies on.
        var info = new ModInfo();

        var metadata = ModDisplayMetadataMapper.ToDisplayMetadata(info);

        Assert.NotNull(metadata);
        Assert.Equal(string.Empty, metadata.Summary);
        Assert.Null(metadata.ThumbnailUrl);
        Assert.False(metadata.IsAdultContent);
    }

    // ---- argument guard ---------------------------------------------------

    [Fact]
    public void ToDisplayMetadata_rejects_null_info()
    {
        Assert.Throws<ArgumentNullException>(() => ModDisplayMetadataMapper.ToDisplayMetadata(null!));
    }
}
