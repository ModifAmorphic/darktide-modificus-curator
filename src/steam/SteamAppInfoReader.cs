using System.Text;
using Microsoft.Extensions.Logging;
using ValveKeyValue;

namespace Modificus.Curator.Steam;

/// <summary>
/// One Valve-managed compatibility-tool entry parsed from
/// <c>appinfo.vdf</c>: the tool's own Steam app id (locates its
/// <c>appmanifest_&lt;appid&gt;.acf</c>), its display name, and its aliases.
/// </summary>
internal sealed record CompatToolEntry(int AppId, string DisplayName, IReadOnlyList<string> Aliases);

/// <summary>
/// Reads Steam's binary <c>appinfo.vdf</c> container (versions 39-41) and
/// extracts the first app entry carrying a nested <c>compat_tools</c> collection.
/// This is the narrow format mechanic for Valve-managed Proton resolution; the
/// binary KV1 blobs inside the container are delegated to ValveKeyValue.
/// </summary>
/// <remarks>
/// <para>
/// The outer container is: a uint32 magic (low byte = version, upper bytes =
/// <c>0x075644</c>), a uint32 universe, and for v41 an int64 string-table
/// offset followed by a string table (uint32 count + null-terminated UTF-8
/// strings). App entries follow: a uint32 app id (0 terminates), a uint32 size,
/// a fixed header (info state, last updated, PICS token, SHA hash, change
/// number, and a binary Sha hash for v40+), then the binary KV1 blob.</para>
/// <para>
/// Best-effort: a missing or unreadable file, or any parse failure, degrades to
/// a null result (warning + unresolved Proton), never an app crash.</para>
/// </remarks>
internal sealed class SteamAppInfoReader
{
    private const uint MagicVersionMask = 0x07564400;
    private const int MinSupportedVersion = 39;
    private const int MaxSupportedVersion = 41;

    private readonly ILogger _logger;

    public SteamAppInfoReader(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads <c>appinfo.vdf</c> at <paramref name="appInfoPath"/> and returns the
    /// <c>compat_tools</c> map from the first app entry that has one. Returns
    /// null when the file is missing, malformed, or no compat_tools collection
    /// is found. Opens with <see cref="FileShare.ReadWrite"/> so a running Steam
    /// client's lock does not block the read.
    /// </summary>
    public IReadOnlyDictionary<string, CompatToolEntry>? ReadCompatTools(string appInfoPath)
    {
        try
        {
            using var stream = new FileStream(
                appInfoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadCompatTools(stream);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read appinfo.vdf at {Path}.", appInfoPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied reading appinfo.vdf at {Path}.", appInfoPath);
            return null;
        }
    }

    /// <summary>
    /// Reads the appinfo container from <paramref name="input"/> and returns the
    /// first app's <c>compat_tools</c> map, or null on malformed input. Internal
    /// so tests can feed a synthetic binary fixture.
    /// </summary>
    internal IReadOnlyDictionary<string, CompatToolEntry>? ReadCompatTools(Stream input)
    {
        try
        {
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            return ReadCompatToolsCore(reader);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "appinfo.vdf has an unrecognized format.");
            return null;
        }
        catch (EndOfStreamException ex)
        {
            _logger.LogWarning(ex, "appinfo.vdf ended unexpectedly.");
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error reading appinfo.vdf.");
            return null;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "appinfo.vdf has an out-of-range offset.");
            return null;
        }
    }

    private IReadOnlyDictionary<string, CompatToolEntry>? ReadCompatToolsCore(BinaryReader reader)
    {
        var stream = reader.BaseStream;

        // The v41 string table requires seeking; a non-seekable stream cannot
        // be parsed.
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDataException("appinfo.vdf requires a readable, seekable stream.");
        }

        var streamLength = stream.Length;

        var magic = reader.ReadUInt32();
        var version = (int)(magic & 0xFF);

        if ((magic & 0xFFFFFF00) != MagicVersionMask)
        {
            throw new InvalidDataException($"Unknown appinfo magic: 0x{magic:X8}.");
        }

        if (version < MinSupportedVersion || version > MaxSupportedVersion)
        {
            throw new InvalidDataException($"Unsupported appinfo version: {version}.");
        }

        // universe (unused, always 1)
        _ = reader.ReadUInt32();

        var options = new KVSerializerOptions();
        var headerSize = version >= 40 ? HeaderSizeV40Plus : HeaderSizeV39;

        if (version >= 41)
        {
            var stringTableOffset = reader.ReadInt64();

            // Validate the offset is non-negative and within the stream, leaving
            // room for at least the uint32 string count.
            if (stringTableOffset < 0 || stringTableOffset > streamLength - sizeof(uint))
            {
                throw new InvalidDataException(
                    $"Invalid appinfo string-table offset: {stringTableOffset}.");
            }

            var returnOffset = stream.Position;
            stream.Position = stringTableOffset;
            options.StringTable = new StringTable(ReadStringTable(reader, streamLength));
            stream.Position = returnOffset;
        }

        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

        while (stream.Position < streamLength)
        {
            var appId = reader.ReadUInt32();
            if (appId == 0)
            {
                break;
            }

            var size = reader.ReadUInt32();
            var entryStart = stream.Position;

            // Validate the entry's declared size fits within the stream and is at
            // least large enough for the fixed header.
            if (size < headerSize || size > streamLength - entryStart)
            {
                throw new InvalidDataException(
                    $"appinfo entry size {size} is out of bounds.");
            }

            var end = entryStart + size;

            // Read the fixed header, verifying the fixed-length reads complete.
            ReadAppHeader(reader, version);

            // Deserialize the binary KV1 blob and extract compat_tools within
            // the same recoverable-exception boundary: a corrupt blob, a bad
            // string-table index, or a pathological compat_tools shape all skip
            // this entry and continue scanning. OutOfMemoryException is never
            // caught (it signals a fatal runtime condition).
            IReadOnlyDictionary<string, CompatToolEntry>? entryMap = null;
            try
            {
                var doc = serializer.Deserialize(stream, options);
                var compatTools = FindCompatTools(doc.Root);
                if (compatTools is not null)
                {
                    entryMap = BuildCompatToolMap(compatTools);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A corrupt blob, bad string-table index, or pathological
                // compat_tools shape skips this entry. The declared end is
                // validated, so we can safely skip forward.
            }

            // Ensure the stream is positioned for the next entry regardless of
            // what the deserializer consumed. end is already validated in bounds.
            if (stream.Position != end)
            {
                stream.Position = end;
            }

            if (entryMap is not null && entryMap.Count > 0)
            {
                return entryMap;
            }
        }

        return null;
    }

    private const int HeaderSizeV39 = 4 + 4 + 8 + 20 + 4;         // 40 bytes
    private const int HeaderSizeV40Plus = HeaderSizeV39 + 20;      // 60 bytes (binary SHA)

    private static void ReadAppHeader(BinaryReader reader, int version)
    {
        // info state (uint32), last updated (uint32), PICS token (uint64),
        // SHA-1 hash (20 bytes), change number (uint32).
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        VerifyFixedRead(reader.ReadBytes(20), 20);
        _ = reader.ReadUInt32();

        if (version >= 40)
        {
            VerifyFixedRead(reader.ReadBytes(20), 20); // binary SHA-1 hash
        }
    }

    /// <summary>
    /// Throws <see cref="EndOfStreamException"/> when a fixed-size read returned
    /// fewer bytes than required (a truncated stream or corrupt header).
    /// </summary>
    private static void VerifyFixedRead(byte[] bytes, int expected)
    {
        if (bytes.Length != expected)
        {
            throw new EndOfStreamException(
                $"Expected {expected} bytes but read {bytes.Length}.");
        }
    }

    private static List<string> ReadStringTable(BinaryReader reader, long streamLength)
    {
        var count = reader.ReadUInt32();
        var remaining = streamLength - reader.BaseStream.Position;

        // Each null-terminated string consumes at least one byte (the null
        // terminator), so a count larger than the remaining bytes is impossible.
        if (count > remaining)
        {
            throw new InvalidDataException(
                $"appinfo string-table count {count} exceeds remaining {remaining} bytes.");
        }

        var pool = new List<string>((int)Math.Min(count, 8192));
        for (var i = 0; i < count; i++)
        {
            pool.Add(ReadNullTerminatedUtf8(reader.BaseStream));
        }
        return pool;
    }

    private static string ReadNullTerminatedUtf8(Stream stream)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException(
                    "appinfo string-table string was not null-terminated.");
            }
            if (b == 0)
            {
                break;
            }
            bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Locates a <c>compat_tools</c> collection within an app entry. Steam nests
    /// it under <c>common</c> or <c>extended</c> (or occasionally at the root);
    /// the first one found is used.
    /// </summary>
    private static KVObject? FindCompatTools(KVObject appData)
    {
        if (appData.TryGetValue("compat_tools", out var direct)) return direct;
        if (TryGetChild(appData, "extended") is { } ext && ext.TryGetValue("compat_tools", out var extTools)) return extTools;
        if (TryGetChild(appData, "common") is { } common && common.TryGetValue("compat_tools", out var commonTools)) return commonTools;
        return null;
    }

    private IReadOnlyDictionary<string, CompatToolEntry> BuildCompatToolMap(KVObject compatTools)
    {
        var map = new Dictionary<string, CompatToolEntry>(StringComparer.Ordinal);
        foreach (var (key, child) in compatTools)
        {
            if (string.IsNullOrEmpty(key)) continue;

            var appId = TryGetChild(child, "appid") is { } appidObj ? ToInt32(appidObj) : 0;
            var displayName = TryGetChild(child, "display_name") is { } dnObj ? AsString(dnObj) : null;
            var aliasesStr = TryGetChild(child, "aliases") is { } aliasObj ? AsString(aliasObj) : null;

            var aliases = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(aliasesStr))
            {
                aliases = aliasesStr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }

            map[key] = new CompatToolEntry(
                appId,
                string.IsNullOrWhiteSpace(displayName) ? key : displayName!,
                aliases);
        }

        return map;
    }

    private static KVObject? TryGetChild(KVObject parent, string key) =>
        parent.TryGetValue(key, out var child) ? child : null;

    private static string? AsString(KVObject obj) => obj.ValueType == KVValueType.String ? (string)obj : null;

    private static int ToInt32(KVObject obj) => obj.ValueType switch
    {
        KVValueType.Int32 => (int)obj,
        KVValueType.UInt32 => (int)(uint)obj,
        KVValueType.Int64 => (int)(long)obj,
        KVValueType.UInt64 => (int)(ulong)obj,
        KVValueType.Int16 => (short)obj,
        KVValueType.UInt16 => (ushort)obj,
        _ => 0,
    };
}
