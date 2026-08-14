using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Focused unit tests for <see cref="SteamAppInfoReader"/>: parses a compact
/// synthetic binary appinfo fixture (v41 with string table), extracts the
/// compat_tools map by canonical key + alias, and degrades gracefully on
/// missing/corrupt input.
/// </summary>
public sealed class SteamAppInfoReaderTests
{
    private static SteamAppInfoReader Reader => new(NullLogger.Instance);

    [Fact]
    public void Parses_compat_tools_from_synthetic_v41_fixture()
    {
        var bytes = AppInfoFixture.Build(
            appId: 891390,
            toolName: "proton_experimental",
            protonAppId: 1493710,
            displayName: "Proton Experimental");

        using var ms = new MemoryStream(bytes);
        var map = Reader.ReadCompatTools(ms);

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
        var map = Reader.ReadCompatTools(ms);

        Assert.NotNull(map);
        var entry = map!["proton_9"];
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
        var map = Reader.ReadCompatTools(ms);

        Assert.NotNull(map);
        Assert.Equal("bare-tool", map!["bare-tool"].DisplayName);
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        var result = Reader.ReadCompatTools(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".vdf"));
        Assert.Null(result);
    }

    [Fact]
    public void Corrupt_magic_returns_null()
    {
        using var ms = new MemoryStream(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });
        var result = Reader.ReadCompatTools(ms);
        Assert.Null(result);
    }

    [Fact]
    public void Empty_stream_returns_null_without_throwing()
    {
        using var ms = new MemoryStream();
        var result = Reader.ReadCompatTools(ms);
        Assert.Null(result);
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

        var bytes = BuildMultiAppAppInfo(new[] { (891390, firstApp), (891391, secondApp) });

        using var ms = new MemoryStream(bytes);
        var map = Reader.ReadCompatTools(ms);

        Assert.NotNull(map);
        Assert.True(map!.ContainsKey("proton_experimental"));
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

        var bytes = BuildMultiAppAppInfo(new[] { (891390, firstApp), (891391, secondApp) });

        using var ms = new MemoryStream(bytes);
        var map = Reader.ReadCompatTools(ms);

        Assert.NotNull(map);
        Assert.True(map!.ContainsKey("proton_experimental"));
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
        Assert.Null(Reader.ReadCompatTools(ms));
    }

    [Fact]
    public void Negative_string_table_offset_returns_null()
    {
        // int64 with the high bit set = negative when read as signed.
        var bytes = BuildRawV41(stringTableOffset: -1, _ => { });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadCompatTools(ms));
    }

    [Fact]
    public void Impossible_string_count_returns_null()
    {
        // String table has count = 999 but only a few bytes of data remain.
        var bytes = BuildRawV41(stringTableOffset: 16, writer =>
        {
            writer.Write(0U); // footer (no app entries before the string table)
            // String table at offset 16:
            writer.Write(999U); // count
            writer.Write(new byte[] { 65, 66 }); // only 2 bytes (not 999 strings)
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadCompatTools(ms));
    }

    [Fact]
    public void Unterminated_string_table_string_returns_null()
    {
        // count = 1 but the single string has no null terminator before EOF.
        var bytes = BuildRawV41(stringTableOffset: 16, writer =>
        {
            writer.Write(0U); // footer
            // String table:
            writer.Write(1U); // count = 1
            writer.Write(Encoding.UTF8.GetBytes("abc")); // no null terminator
        });

        using var ms = new MemoryStream(bytes);
        Assert.Null(Reader.ReadCompatTools(ms));
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
        Assert.Null(Reader.ReadCompatTools(ms));
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
        Assert.Null(Reader.ReadCompatTools(ms));
    }

    [Fact]
    public void Corrupt_binary_kv_payload_degrades_to_null()
    {
        // A valid container whose binary KV1 payload is garbage. The per-entry
        // catch handles the deserialization failure; the reader finds no
        // compat_tools and returns null without crashing.
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
        Assert.Null(Reader.ReadCompatTools(ms));
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

        Assert.Null(Reader.ReadCompatTools(nonSeekable));
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

    /// <summary>Builds an appinfo file with multiple app entries.</summary>
    private static byte[] BuildMultiAppAppInfo(IReadOnlyList<(int AppId, KVObject AppData)> apps)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(0x07564429U); // v41 magic
        writer.Write(1U);          // universe

        var stringTableOffsetPos = ms.Position;
        writer.Write(0L); // placeholder

        var stringTable = new StringTable();
        var options = new KVSerializerOptions { StringTable = stringTable };
        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

        foreach (var (appId, appData) in apps)
        {
            writer.Write((uint)appId);
            var sizePos = ms.Position;
            writer.Write(0U);

            // Header
            writer.Write(2U);
            writer.Write(0U);
            writer.Write(0UL);
            writer.Write(new byte[20]);
            writer.Write(0U);
            writer.Write(new byte[20]);

            serializer.Serialize(ms, appData, "app", options);

            // size = header + payload (everything from after the size field).
            var after = ms.Position;
            var dataSize = (uint)(after - sizePos - sizeof(uint));
            ms.Position = sizePos;
            writer.Write(dataSize);
            ms.Position = after;
        }

        // String table.
        var stringTableOffset = ms.Position;
        var strings = stringTable.ToArray();
        writer.Write((uint)strings.Length);
        foreach (var s in strings)
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes(s));
            writer.Write((byte)0);
        }

        // Footer.
        writer.Write(0U);

        ms.Position = stringTableOffsetPos;
        writer.Write(stringTableOffset);

        return ms.ToArray();
    }
}
