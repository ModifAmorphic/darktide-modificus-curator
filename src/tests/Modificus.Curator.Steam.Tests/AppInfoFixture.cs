using System.IO;
using System.Text;
using ValveKeyValue;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Builds compact synthetic binary <c>appinfo.vdf</c> fixtures (version 41 with
/// string table) for testing the Valve-managed Proton resolution path + the
/// no-user-mapping recommended-runtime fallback. The binary KV1 blobs are
/// produced by ValveKeyValue so the fixtures match what the reader expects, and
/// the outer container (magic, version, string table, app entries) follows the
/// appinfo format documented in the SteamAppInfo reference parser.
/// </summary>
internal static class AppInfoFixture
{
    public const int SteamPlayManifestAppId = 891390;
    public const int DarktideAppId = 1361210;
    public const int Proton11AppId = 4628710;
    public const string Proton11ToolKey = "proton_11";
    public const string RecommendedRuntime = "proton-11.0-beta";

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

    /// <summary>
    /// Builds a realistic multi-entry v41 appinfo.vdf matching the live shape:
    /// the Steam Play manifest app carrying <c>compat_tools</c> under
    /// <c>extended</c> (including the <c>proton_11</c> entry with app id
    /// 4628710, display name <c>Proton 11.0</c>, and aliases
    /// <c>proton-11.0-beta,proton-11.0</c>), and Darktide carrying its
    /// recommended runtime under
    /// <c>common/steam_deck_compatibility/configuration/recommended_runtime</c>.
    /// <paramref name="darktideFirst"/> controls the entry order so both scan
    /// directions are covered.
    /// </summary>
    public static byte[] BuildRecommendedRuntimeAppInfo(string recommendedRuntime = RecommendedRuntime, bool darktideFirst = false)
    {
        var entries = darktideFirst
            ? new[] { (DarktideAppId, BuildDarktideAppData(recommendedRuntime)), (SteamPlayManifestAppId, BuildSteamPlayAppData()) }
            : new[] { (SteamPlayManifestAppId, BuildSteamPlayAppData()), (DarktideAppId, BuildDarktideAppData(recommendedRuntime)) };
        return BuildMultiApp(entries);
    }

    /// <summary>Builds an appinfo.vdf from a fully-formed app-data KVObject.</summary>
    public static byte[] BuildFromAppData(int appId, KVObject appData) =>
        BuildMultiApp((appId, appData));

    /// <summary>
    /// Builds an appinfo.vdf with multiple app entries sharing one string table.
    /// </summary>
    public static byte[] BuildMultiApp(params (int AppId, KVObject AppData)[] apps)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // v41 magic: 0x07564429.
        writer.Write(0x07564429U);
        writer.Write(1U); // universe

        // String-table offset placeholder (filled after the entries are written).
        var stringTableOffsetPos = ms.Position;
        writer.Write(0L);

        // Serialize the binary KV1 payloads, letting ValveKeyValue populate the
        // shared string table so the indices it writes match the table contents.
        var stringTable = new StringTable();
        var options = new KVSerializerOptions { StringTable = stringTable };
        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

        foreach (var (appId, appData) in apps)
        {
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

            // Patch the size field: the size covers everything from after the
            // size field to the end of the binary KV1 payload (header + payload).
            var afterPayload = ms.Position;
            var dataSize = (uint)(afterPayload - sizePos - sizeof(uint));
            ms.Position = sizePos;
            writer.Write(dataSize);
            ms.Position = afterPayload;
        }

        // Footer: app id = 0 terminates the entry list, then the string table
        // ends the file (the real v41 layout: the terminator bounds the entry
        // scan before the table region).
        writer.Write(0U);

        // Write the string table at the current offset, then patch the header.
        var stringTableOffset = ms.Position;
        WriteStringTable(writer, stringTable.ToArray());

        ms.Position = stringTableOffsetPos;
        writer.Write(stringTableOffset);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds the Steam Play manifest app data: <c>compat_tools</c> nested under
    /// <c>extended</c>, as in the live appinfo.vdf.
    /// </summary>
    private static KVObject BuildSteamPlayAppData()
    {
        var compatTools = KVObject.ListCollection();
        compatTools.Add("proton_experimental", BuildToolEntry(1493710, "Proton Experimental", "proton-experimental,proton-11.0-2RC"));
        compatTools.Add(Proton11ToolKey, BuildToolEntry(Proton11AppId, "Proton 11.0", "proton-11.0-beta,proton-11.0"));
        compatTools.Add("proton_10", BuildToolEntry(3658110, "Proton 10.0-4", "proton-10,proton-10.0-beta"));

        var extended = KVObject.ListCollection();
        extended.Add("compat_tools", compatTools);

        var appData = KVObject.ListCollection();
        appData.Add("extended", extended);
        return appData;
    }

    /// <summary>
    /// Builds Darktide's app data with its recommended runtime at
    /// <c>common/steam_deck_compatibility/configuration/recommended_runtime</c>.
    /// </summary>
    private static KVObject BuildDarktideAppData(string recommendedRuntime)
    {
        var configuration = KVObject.ListCollection();
        configuration.Add("recommended_runtime", recommendedRuntime);

        var deckCompatibility = KVObject.ListCollection();
        deckCompatibility.Add("configuration", configuration);

        var common = KVObject.ListCollection();
        common.Add("steam_deck_compatibility", deckCompatibility);
        common.Add("name", "Warhammer 40,000: Darktide");

        var appData = KVObject.ListCollection();
        appData.Add("common", common);
        return appData;
    }

    private static KVObject BuildToolEntry(int protonAppId, string displayName, string aliases)
    {
        var entry = KVObject.ListCollection();
        entry.Add("appid", protonAppId);
        entry.Add("display_name", displayName);
        entry.Add("aliases", aliases);
        return entry;
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
