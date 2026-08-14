using System.IO;
using System.Text;
using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Builds a compact synthetic binary <c>appinfo.vdf</c> (version 41 with string
/// table) for testing the Valve-managed Proton resolution path. The binary KV1
/// blobs are produced by ValveKeyValue so the fixture matches what the reader
/// expects, and the outer container (magic, version, string table, app entries)
/// follows the appinfo format documented in the SteamAppInfo reference parser.
/// </summary>
internal static class AppInfoFixture
{
    /// <summary>
    /// Builds an appinfo.vdf carrying one app whose data includes a
    /// <c>compat_tools</c> collection with the given tool entry.
    /// </summary>
    public static byte[] Build(
        int appId,
        string toolName,
        int protonAppId,
        string displayName,
        string? aliases = null)
    {
        // Build the compat_tools KV1 structure.
        var compatTools = KVObject.ListCollection();
        compatTools.Add("compat_tools", BuildCompatToolsCollection(toolName, protonAppId, displayName, aliases));

        var appData = KVObject.ListCollection();
        appData.Add("common", compatTools);

        return BuildFromAppData(appId, appData);
    }

    /// <summary>Builds an appinfo.vdf from a fully-formed app-data KVObject.</summary>
    public static byte[] BuildFromAppData(int appId, KVObject appData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // v41 magic: 0x07564429.
        writer.Write(0x07564429U);
        writer.Write(1U); // universe

        // String-table offset placeholder (filled after the entry is written).
        var stringTableOffsetPos = ms.Position;
        writer.Write(0L);

        // Serialize the binary KV1 payload, letting ValveKeyValue populate the
        // shared string table so the indices it writes match the table contents.
        var stringTable = new StringTable();
        var options = new KVSerializerOptions { StringTable = stringTable };
        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

        writer.Write((uint)appId); // app id
        var sizePos = ms.Position;
        writer.Write(0U); // size placeholder

        // Fixed app-entry header.
        writer.Write(2U);             // info state
        writer.Write(0U);             // last updated
        writer.Write(0UL);            // PICS token
        writer.Write(new byte[20]);   // SHA-1 hash
        writer.Write(0U);             // change number
        writer.Write(new byte[20]);   // binary SHA-1 hash (v40+)

        serializer.Serialize(ms, appData, "app", options);

        // Patch the size field: the size covers everything from after the size
        // field to the end of the binary KV1 payload (header + payload).
        var afterPayload = ms.Position;
        var dataSize = (uint)(afterPayload - sizePos - sizeof(uint));
        ms.Position = sizePos;
        writer.Write(dataSize);
        ms.Position = afterPayload;

        // Write the string table at the current offset, then patch the header.
        var stringTableOffset = ms.Position;
        WriteStringTable(writer, stringTable.ToArray());

        // Footer: app id = 0 terminates the entry list.
        writer.Write(0U);

        ms.Position = stringTableOffsetPos;
        writer.Write(stringTableOffset);

        return ms.ToArray();
    }

    private static KVObject BuildCompatToolsCollection(
        string toolName, int protonAppId, string displayName, string? aliases)
    {
        var entry = KVObject.ListCollection();
        entry.Add("appid", protonAppId);
        entry.Add("display_name", displayName);
        if (!string.IsNullOrEmpty(aliases))
        {
            entry.Add("aliases", aliases);
        }

        var tools = KVObject.ListCollection();
        tools.Add(toolName, entry);
        return tools;
    }

    private static void WriteStringTable(BinaryWriter writer, string[] strings)
    {
        writer.Write((uint)strings.Length);
        foreach (var s in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            writer.Write(bytes);
            writer.Write((byte)0);
        }
    }
}
