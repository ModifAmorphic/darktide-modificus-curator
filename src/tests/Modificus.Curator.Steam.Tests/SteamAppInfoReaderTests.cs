using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="SteamAppInfoReader"/>: parses compact
/// synthetic binary appinfo fixtures (v41 with string table) plus a realistic
/// multi-entry fixture matching the live appinfo shape, extracts the
/// compat_tools map by canonical key + alias and Darktide's recommended runtime
/// in either entry order, and degrades gracefully on missing/corrupt input.
/// </summary>
public sealed class SteamAppInfoReaderTests
{
    private static SteamAppInfoReader Reader => new(NullLogger.Instance);

    // An app id absent from the single-app fixtures, so the requested-app
    // lookup stays out of the way of the compat_tools assertions.
    private const uint UnusedAppId = 1361210;

    [Fact]
    public void Parses_compat_tools_from_synthetic_v41_fixture()
    {
        var bytes = AppInfoFixture.Build(
            appId: 891390,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            displayName: "Proton Experimental");

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        var map = snapshot!.CompatTools;
        Assert.NotNull(map);
        Assert.True(map!.ContainsKey("proton_experimental"));
        var entry = map["proton_experimental"];
        Assert.Equal(1493710, entry.AppId);
        Assert.Equal("Proton Experimental", entry.DisplayName);
    }

    [Fact]
    public void Parses_aliases_from_compat_tools()
    {
        var bytes = AppInfoFixture.Build(
            appId: 891390,
            toolName: "proton_9",
            protonAppId: 2808980,
            displayName: "Proton 9.0",
            aliases: "proton_9_alt,another_alias");

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        var entry = snapshot!.CompatTools!["proton_9"];
        Assert.Contains("proton_9_alt", entry.Aliases);
        Assert.Contains("another_alias", entry.Aliases);
    }

    [Fact]
    public void Missing_display_name_falls_back_to_internal_name()
    {
        // Build a fixture with an empty display name.
        var appData = KVObject.ListCollection();
        var compatToolsWrapper = KVObject.ListCollection();
        var compatToolsInner = KVObject.ListCollection();
        var entry = KVObject.ListCollection();
        entry.Add("appid", 12345);
        // No display_name.
        compatToolsInner.Add("bare-tool", entry);
        compatToolsWrapper.Add("compat_tools", compatToolsInner);
        appData.Add("common", compatToolsWrapper);

        var bytes = AppInfoFixture.BuildFromAppData(891390, appData);

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        Assert.Equal("bare-tool", snapshot!.CompatTools!["bare-tool"].DisplayName);
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        var result = Reader.ReadSnapshot(
            Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".vdf"), UnusedAppId);
        Assert.Null(result);
    }

    [Fact]
    public void Corrupt_magic_returns_null()
    {
        using var ms = new MemoryStream(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Empty_stream_returns_null_without_throwing()
    {
        using var ms = new MemoryStream();
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void App_entry_without_compat_tools_is_skipped()
    {
        // First app has no compat_tools; second app does. The reader scans past
        // the first and finds the collection in the second.
        var firstApp = KVObject.ListCollection();
        firstApp.Add("common", KVObject.ListCollection()); // no compat_tools

        var secondApp = KVObject.ListCollection();
        var compatToolsWrapper = KVObject.ListCollection();
        var compatToolsInner = KVObject.ListCollection();
        var entry = KVObject.ListCollection();
        entry.Add("appid", 1493710);
        entry.Add("display_name", "Proton Experimental");
        compatToolsInner.Add("proton_experimental", entry);
        compatToolsWrapper.Add("compat_tools", compatToolsInner);
        secondApp.Add("common", compatToolsWrapper);

        var bytes = AppInfoFixture.BuildMultiApp((891390, firstApp), (891391, secondApp));

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.CompatTools!.ContainsKey("proton_experimental"));
    }

    [Fact]
    public void Pathological_compat_tools_skips_entry_and_continues_to_valid()
    {
        // First app carries a compat_tools whose value is a SCALAR (not a
        // collection), a pathological shape. FindCompatTools locates the key but
        // BuildCompatToolMap produces an empty map (a scalar has no children to
        // enumerate). The reader treats the empty result as "not found" and
        // continues to the second app, which carries a valid collection.
        var firstApp = KVObject.ListCollection();
        var firstCommon = KVObject.ListCollection();
        // compat_tools mapped to a bare integer, not a collection of tool entries.
        firstCommon.Add("compat_tools", 42);
        firstApp.Add("common", firstCommon);

        var secondApp = KVObject.ListCollection();
        var compatToolsWrapper = KVObject.ListCollection();
        var compatToolsInner = KVObject.ListCollection();
        var entry = KVObject.ListCollection();
        entry.Add("appid", 1493710);
        entry.Add("display_name", "Proton Experimental");
        compatToolsInner.Add("proton_experimental", entry);
        compatToolsWrapper.Add("compat_tools", compatToolsInner);
        secondApp.Add("common", compatToolsWrapper);

        var bytes = AppInfoFixture.BuildMultiApp((891390, firstApp), (891391, secondApp));

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.CompatTools!.ContainsKey("proton_experimental"));
    }

    // ---- realistic multi-entry appinfo fixture ---------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_shape_fixture_yields_compat_tools_and_runtime_in_either_order(bool darktideFirst)
    {
        var bytes = AppInfoFixture.BuildRecommendedRuntimeAppInfo(darktideFirst: darktideFirst);

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, AppInfoFixture.DarktideAppId);

        Assert.NotNull(snapshot);
        Assert.Equal(AppInfoFixture.RecommendedRuntime, snapshot!.RecommendedRuntime);

        // The exact live shape: proton_11 maps app id 4628710, display name
        // "Proton 11.0", and aliases covering the recommended runtime name.
        var proton11 = snapshot.CompatTools![AppInfoFixture.Proton11ToolKey];
        Assert.Equal(AppInfoFixture.Proton11AppId, proton11.AppId);
        Assert.Equal("Proton 11.0", proton11.DisplayName);
        Assert.Contains("proton-11.0-beta", proton11.Aliases);
        Assert.Contains("proton-11.0", proton11.Aliases);
    }

    [Fact]
    public void Requested_app_absent_yields_null_runtime_but_compat_tools()
    {
        // The scan finds the compat_tools registry but never the requested
        // app's entry; it continues to EOF and reports the one piece found.
        var bytes = AppInfoFixture.BuildRecommendedRuntimeAppInfo();
        var missingAppId = AppInfoFixture.DarktideAppId + 1;

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, (uint)missingAppId);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.RecommendedRuntime);
        Assert.NotNull(snapshot.CompatTools);
    }

    [Fact]
    public void Zero_requested_app_id_skips_runtime_lookup()
    {
        // requestedAppId 0 asks only for the compat_tools registry; the scan
        // does not wait for a recommended runtime.
        var bytes = AppInfoFixture.BuildRecommendedRuntimeAppInfo();

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, 0);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.CompatTools);
        Assert.Null(snapshot.RecommendedRuntime);
    }

    [Fact]
    public void Non_string_recommended_runtime_yields_null_runtime()
    {
        var appData = KVObject.ListCollection();
        var configuration = KVObject.ListCollection();
        configuration.Add("recommended_runtime", 3);
        var deckCompatibility = KVObject.ListCollection();
        deckCompatibility.Add("configuration", configuration);
        var common = KVObject.ListCollection();
        common.Add("steam_deck_compatibility", deckCompatibility);
        appData.Add("common", common);

        var bytes = AppInfoFixture.BuildFromAppData(AppInfoFixture.DarktideAppId, appData);

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, AppInfoFixture.DarktideAppId);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.RecommendedRuntime);
    }

    [Fact]
    public void Missing_terminator_stops_at_string_table_offset()
    {
        // A v41 file missing its terminating 0 app id: with the requested app
        // absent, the scan runs to the region end, which must be the
        // string-table offset (not the stream end), so the table region is
        // never misparsed as entries and the found compat_tools survive.
        var bytes = AppInfoFixture.BuildRecommendedRuntimeAppInfo();
        var tableOffset = BitConverter.ToInt64(bytes, 8);

        var stripped = new byte[bytes.Length - sizeof(uint)];
        Array.Copy(bytes, stripped, tableOffset - sizeof(uint));
        Array.Copy(bytes, tableOffset, stripped, tableOffset - sizeof(uint), bytes.Length - tableOffset);
        BitConverter.GetBytes(tableOffset - sizeof(uint)).CopyTo(stripped, 8);

        using var ms = new MemoryStream(stripped);
        var snapshot = Reader.ReadSnapshot(ms, AppInfoFixture.DarktideAppId + 1);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.CompatTools);
        Assert.Null(snapshot.RecommendedRuntime);
    }

    // ---- hardening: corrupt / truncated input degrades to null ---------------

    /// <summary>Builds a raw v41 byte stream from explicit sections.</summary>
    private static byte[] BuildRawV41(long stringTableOffset, Action<BinaryWriter> writeBody)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(0x07564429U); // v41 magic
        writer.Write(1U);          // universe
        writer.Write(stringTableOffset);
        writeBody(writer);
        writer.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Invalid_string_table_offset_past_stream_returns_null()
    {
        // The offset points well past the end of the stream.
        var bytes = BuildRawV41(stringTableOffset: 999_999, _ => { });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Negative_string_table_offset_returns_null()
    {
        // int64 with the high bit set = negative when read as signed.
        var bytes = BuildRawV41(stringTableOffset: -1, _ => { });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Impossible_string_count_returns_null()
    {
        // String table has count = 999 but only a few bytes of data remain. The
        // table offset points at the count field (the footer precedes it).
        var bytes = BuildRawV41(stringTableOffset: 20, writer =>
        {
            writer.Write(0U); // footer (no app entries before the string table)
            // String table at offset 20:
            writer.Write(999U); // count
            writer.Write(new byte[] { 65, 66 }); // only 2 bytes (not 999 strings)
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Unterminated_string_table_string_returns_null()
    {
        // count = 1 but the single string has no null terminator before EOF.
        var bytes = BuildRawV41(stringTableOffset: 20, writer =>
        {
            writer.Write(0U); // footer
            // String table:
            writer.Write(1U); // count = 1
            writer.Write(Encoding.UTF8.GetBytes("abc")); // no null terminator
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Truncated_entry_header_returns_null()
    {
        // A valid string table (count=0) at offset 32, then one entry whose
        // declared size is smaller than the fixed header (60 bytes for v40+).
        var bytes = BuildRawV41(stringTableOffset: 32, writer =>
        {
            // App entry at position 16 (right after the 16-byte file header).
            writer.Write(999U);    // appId (non-zero)
            writer.Write(10U);     // size = 10 (less than the 60-byte header)
            writer.Write(new byte[6]); // padding to reach offset 32
            // String table at offset 32.
            writer.Write(0U);      // count = 0
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Entry_size_beyond_stream_returns_null()
    {
        // A valid string table, then one entry whose size extends past EOF.
        var bytes = BuildRawV41(stringTableOffset: 32, writer =>
        {
            writer.Write(999U);        // appId
            writer.Write(999_999U);    // size = way past stream end
            writer.Write(new byte[4]); // padding
            // String table at offset 32.
            writer.Write(0U);          // count = 0
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadSnapshot(ms, UnusedAppId));
    }

    [Fact]
    public void Corrupt_binary_kv_payload_yields_no_compat_tools()
    {
        // A valid container whose binary KV1 payload is garbage. The per-entry
        // catch handles the deserialization failure; the reader finds no
        // compat_tools and returns an empty snapshot without crashing.
        var bytes = BuildRawV41(stringTableOffset: 32, writer =>
        {
            writer.Write(891390U);  // appId
            writer.Write(70U);      // size = 60 (header) + 10 (payload)
            // Fixed header (60 bytes).
            writer.Write(2U);              // info state
            writer.Write(0U);              // last updated
            writer.Write(0UL);             // PICS token
            writer.Write(new byte[20]);    // SHA-1
            writer.Write(0U);              // change number
            writer.Write(new byte[20]);    // binary SHA-1
            // Garbage KV1 payload (10 bytes).
            writer.Write(new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8, 0xF7, 0xF6 });
            // String table at offset 32 (we're already past that; but the
            // reader reads the table before entries, so pad to reach it).
            while (writer.BaseStream.Position < 32) writer.Write((byte)0);
            writer.Write(0U); // count = 0
        });

        using var ms = new MemoryStream(bytes);
        var snapshot = Reader.ReadSnapshot(ms, UnusedAppId);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.CompatTools);
        Assert.Null(snapshot.RecommendedRuntime);
    }

    [Fact]
    public void Non_seekable_stream_returns_null()
    {
        // A readable but non-seekable stream cannot be parsed (v41 requires
        // seeking to the string table).
        var bytes = AppInfoFixture.Build(
            appId: 891390,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            displayName: "Proton Experimental");

        using var ms = new MemoryStream(bytes);
        using var nonSeekable = new NonSeekableStream(ms);

        Assert.Null(Reader.ReadSnapshot(nonSeekable, UnusedAppId));
    }

    /// <summary>A stream wrapper that reports CanSeek = false.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
