namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="SteamDeckDetector"/>: quoted + unquoted
/// SteamOS Deck OS release values, the host-before-etc file order, and
/// non-Deck or missing input.
/// </summary>
public sealed class SteamDeckDetectorTests : IDisposable
{
    private string? _tempDir;

    private string TempDir
    {
        get
        {
            if (_tempDir is null)
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "deck-detect-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }
            return _tempDir;
        }
    }

    private string WriteOsRelease(string fileName, string content)
    {
        var path = Path.Combine(TempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string MissingPath(string fileName) => Path.Combine(TempDir, fileName);

    [Fact]
    public void Unquoted_steamos_steamdeck_values_match()
    {
        var host = WriteOsRelease("host-unquoted", """
            NAME=SteamOS
            ID=steamos
            ID_LIKE=arch
            VARIANT_ID=steamdeck
            """);
        var etc = MissingPath("etc-unquoted");

        Assert.True(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Quoted_steamos_steamdeck_values_match()
    {
        var host = MissingPath("host-quoted");
        var etc = WriteOsRelease("etc-quoted", """
            NAME="SteamOS"
            ID="steamos"
            ID_LIKE="arch"
            BUILD_ID="20240212.1"
            VARIANT_ID="steamdeck"
            """);

        Assert.True(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Steamos_without_steamdeck_variant_does_not_match()
    {
        // Desktop SteamOS (or a non-Deck variant) is not a Deck.
        var host = MissingPath("host-desktop-steamos");
        var etc = WriteOsRelease("etc-desktop-steamos", """
            NAME="SteamOS"
            ID=steamos
            VARIANT_ID=arch
            """);

        Assert.False(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Non_steamos_release_does_not_match()
    {
        var host = MissingPath("host-ubuntu");
        var etc = WriteOsRelease("etc-ubuntu", """
            NAME="Ubuntu"
            ID=ubuntu
            VARIANT_ID=steamdeck
            """);

        Assert.False(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Missing_variant_id_does_not_match()
    {
        var host = MissingPath("host-no-variant");
        var etc = WriteOsRelease("etc-no-variant", """
            NAME=SteamOS
            ID=steamos
            """);

        Assert.False(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Host_file_is_consulted_first()
    {
        // The host file identifies the Deck; the etc file does not. The host
        // file wins because it is checked first.
        var host = WriteOsRelease("host-deck", """
            ID=steamos
            VARIANT_ID=steamdeck
            """);
        var etc = WriteOsRelease("etc-not-deck", """
            ID=ubuntu
            """);

        Assert.True(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Etc_file_matches_when_host_file_does_not()
    {
        var host = WriteOsRelease("host-not-deck", """
            ID=fedora
            """);
        var etc = WriteOsRelease("etc-deck", """
            ID=steamos
            VARIANT_ID=steamdeck
            """);

        Assert.True(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var host = WriteOsRelease("host-comments", """
            # OS release data
            NAME="SteamOS"

            # the variant below identifies the Deck
            ID=steamos
            VARIANT_ID=steamdeck
            """);
        var etc = MissingPath("etc-comments");

        Assert.True(SteamDeckDetector.IsSteamDeck(host, etc));
    }

    [Fact]
    public void Missing_files_do_not_match()
    {
        Assert.False(SteamDeckDetector.IsSteamDeck(MissingPath("host-none"), MissingPath("etc-none")));
    }

    public void Dispose()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }
}
