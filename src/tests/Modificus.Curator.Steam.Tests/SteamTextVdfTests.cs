using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="SteamTextVdf"/> proving that Steam escape
/// sequences (\" and \\) are translated correctly. These would all fail if the
/// helper used ValveKeyValue's default <c>HasEscapeSequences = false</c>.
/// </summary>
public sealed class SteamTextVdfTests
{
    [Fact]
    public void Quoted_json_with_escaped_quotes_deserializes_as_one_string()
    {
        // A string value containing escaped quotes must survive as a single
        // scalar, not be split at the first \".
        var vdf = """
                  "root"
                  {
                      "data"        "{\"version\":\"2\",\"path\":\"/home/test\"}"
                  }
                  """;

        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vdf));
        var doc = SteamTextVdf.Deserialize(ms);

        var data = (string)doc.Root["data"];
        Assert.Equal("{\"version\":\"2\",\"path\":\"/home/test\"}", data);
    }

    [Fact]
    public void Escaped_backslash_in_value_is_translated()
    {
        var vdf = """
                  "root"
                  {
                      "path"        "C:\\Program Files\\Test"
                  }
                  """;

        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vdf));
        var doc = SteamTextVdf.Deserialize(ms);

        var path = (string)doc.Root["path"];
        Assert.Equal(@"C:\Program Files\Test", path);
    }

    [Fact]
    public void Nested_collection_after_escaped_json_string_parses_correctly()
    {
        // Proves the parser fully traverses past a JSON section with many
        // escaped quotes and reaches a later nested collection.
        var vdf = """
                  "root"
                  {
                      "mapping"
                      {
                          "name"        "fixture-proton"
                      }
                      "storage"
                      {
                          "0"        "{\"version\":\"2\",\"data\":\"test\",\"path\":\"/a/b\"}"
                      }
                      "after"
                      {
                          "key"        "reachable"
                      }
                  }
                  """;

        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vdf));
        var doc = SteamTextVdf.Deserialize(ms);

        Assert.Equal("fixture-proton", (string)doc.Root["mapping"]["name"]);
        Assert.Equal("reachable", (string)doc.Root["after"]["key"]);
    }

    [Fact]
    public void Multiple_json_scalars_with_escaped_content_all_parse()
    {
        var vdf = """
                  "root"
                  {
                      "a"        "{\"v\":1}"
                      "b"        "{\"v\":2,\"x\":\"y\"}"
                      "c"        "{\"v\":3,\"list\":[1,2,3]}"
                  }
                  """;

        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vdf));
        var doc = SteamTextVdf.Deserialize(ms);

        Assert.Equal("{\"v\":1}", (string)doc.Root["a"]);
        Assert.Equal("{\"v\":2,\"x\":\"y\"}", (string)doc.Root["b"]);
        Assert.Equal("{\"v\":3,\"list\":[1,2,3]}", (string)doc.Root["c"]);
    }

    [Fact]
    public void Realistic_config_fixture_deserializes_and_yields_compat_mapping()
    {
        // Exercises the checked-in sanitized fixture against the helper. The
        // fixture carries JSON sections with many escaped quotes after the
        // CompatToolMapping, matching the operator's real config.vdf shape.
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "config-escaped.vdf");

        using var stream = File.OpenRead(fixturePath);
        var doc = SteamTextVdf.Deserialize(stream);

        var mapping = doc.Root["Software"]["Valve"]["Steam"]["CompatToolMapping"];

        // App-specific mapping.
        Assert.Equal("fixture-proton", (string)mapping["1361210"]["name"]);
        // Global mapping.
        Assert.Equal("fixture-proton", (string)mapping["0"]["name"]);

        // A section after the JSON-heavy WebStorage block is reachable, proving
        // the parser fully traversed the escaped content.
        var music = doc.Root["Software"]["Valve"]["Steam"]["MusicPlayer"];
        Assert.True(music.ContainsKey("shuffle"));
    }
}
