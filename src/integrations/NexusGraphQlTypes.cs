using System.Text.Json.Serialization;
using System.Text.Json;

namespace Modificus.Curator.Integrations;

/// <summary>
/// One mod's update status from the v2 GraphQL <c>modsByUid</c> query. The
/// server-computed <see cref="ViewerUpdateAvailable"/> field is the update
/// signal: true if the mod has been updated since the viewer (current user) last
/// downloaded it, false otherwise. Eliminates the client-side timestamp
/// comparison the v1 Month-endpoint approach required.
/// </summary>
/// <remarks>
/// <see cref="Uid"/> is the Nexus mod UID (<c>game_id * 2^32 + mod_id</c>).
/// GraphQL serializes the <c>ID</c> scalar as a string, so
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> lets the deserializer
/// accept both <c>"21233675571276"</c> (string, per the GraphQL spec) and
/// <c>21233675571276</c> (number) without a custom converter.
/// </remarks>
public sealed record ModUpdateStatus
{
    /// <summary>
    /// The mod's UID (<c>game_id * 2^32 + mod_id</c>). Used to match the
    /// response node back to the checkable mod.
    /// </summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonPropertyName("uid")]
    public long Uid { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// The mod's last update time on Nexus (ISO 8601). Null when the server
    /// does not report it. Surfaced to the UI as display context.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Server-computed: true if the mod has been updated since the viewer
    /// (current user) last downloaded it. Null when the server has no download
    /// record for the user (e.g. a manually imported mod the API never tracked);
    /// treated as false (not flagged) by the update check.
    /// </summary>
    [JsonPropertyName("viewerUpdateAvailable")]
    public bool? ViewerUpdateAvailable { get; init; }

    /// <summary>
    /// The last time the user downloaded this mod (server-tracked). Null when
    /// the server has no download record. Informational; not used by the update
    /// check (which relies on <see cref="ViewerUpdateAvailable"/>).
    /// </summary>
    [JsonPropertyName("viewerDownloaded")]
    public DateTimeOffset? ViewerDownloaded { get; init; }
}

/// <summary>
/// The GraphQL request body: the query string + the variables object. Serialized
/// to <c>{ "query": "...", "variables": { "uids": [...] } }</c>.
/// </summary>
internal sealed class GraphQlRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public GraphQlVariables Variables { get; set; } = new();
}

/// <summary>
/// The variables for the <c>modsByUid</c> query. <see cref="Uids"/> is a string
/// array because the GraphQL variable type is <c>[ID!]!</c> and the <c>ID</c>
/// scalar is serialized as a string.
/// </summary>
internal sealed class GraphQlVariables
{
    [JsonPropertyName("uids")]
    public string[] Uids { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The wrapper for a GraphQL response: either a <see cref="Data"/> payload (on
/// success) or an <see cref="Errors"/> array (on failure), or both. A 200 OK
/// HTTP status can still carry GraphQL errors in the body.
/// </summary>
internal sealed class GraphQlResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public GraphQlError[]? Errors { get; set; }
}

/// <summary>
/// One GraphQL error entry. Only <see cref="Message"/> is bound (the only field
/// Curator surfaces).
/// </summary>
internal sealed class GraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The <c>data</c> object for the <c>modsByUid</c> query, carrying the
/// <see cref="ModsByUid"/> result.
/// </summary>
internal sealed class ModsByUidData
{
    [JsonPropertyName("modsByUid")]
    public ModsByUidResult? ModsByUid { get; set; }
}

/// <summary>
/// The <c>modsByUid</c> result: the matching <see cref="Nodes"/> + the total
/// count of matches. Mods whose UID did not resolve (invalid id, removed mod)
/// are simply absent from <see cref="Nodes"/>; the update check treats them as
/// not flagged (conservative).
/// </summary>
internal sealed class ModsByUidResult
{
    [JsonPropertyName("nodes")]
    public ModUpdateStatus[] Nodes { get; set; } = Array.Empty<ModUpdateStatus>();

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// The <c>data</c> object for the anonymous <c>mods</c> search query.
/// </summary>
internal sealed class ModsSearchData
{
    [JsonPropertyName("mods")]
    public ModsSearchResult? Mods { get; set; }
}

/// <summary>
/// The <c>mods</c> search result: the matching nodes. The filter runs
/// server-side; an empty result is a valid (no-match) answer.
/// </summary>
internal sealed class ModsSearchResult
{
    [JsonPropertyName("nodes")]
    public ModsSearchNode[] Nodes { get; set; } = Array.Empty<ModsSearchNode>();
}

/// <summary>
/// One search node: the mod's id + name + UID. The <c>ID</c> scalars serialize
/// as strings per the GraphQL spec; <paramref name="ModId"/> is an int and
/// accepts both the string and number forms (the
/// <see cref="ModUpdateStatus"/> precedent), while <see cref="Uid"/> stays a
/// string (informational only; the resolver keys on
/// <paramref name="ModId"/>).
/// </summary>
internal sealed class ModsSearchNode
{
    [JsonPropertyName("modId")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ModId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }
}
