using ValveKeyValue;

namespace Modificus.Curator.Steam;

/// <summary>
/// Deserializes Steam text KeyValues1 files (<c>config.vdf</c>,
/// <c>compatibilitytool.vdf</c>, <c>appmanifest_*.acf</c>) with the correct
/// Steam escape semantics. ValveKeyValue defaults
/// <see cref="KVSerializerOptions.HasEscapeSequences"/> to <c>false</c>, but
/// Steam files routinely contain C-style escapes (<c>\"</c>, <c>\\</c>), so
/// Curator always enables them.
/// </summary>
internal static class SteamTextVdf
{
    /// <summary>
    /// Deserializes a Steam text KV1 stream with escape-sequence translation
    /// always enabled. The caller owns the stream's lifetime.
    /// </summary>
    public static KVDocument Deserialize(Stream stream)
    {
        // Options are created per call: KVSerializerOptions is a mutable object
        // (Conditions list etc.), so sharing a static instance is unsafe.
        var options = new KVSerializerOptions { HasEscapeSequences = true };
        return KVSerializer.Create(KVSerializationFormat.KeyValues1Text)
            .Deserialize(stream, options);
    }
}
