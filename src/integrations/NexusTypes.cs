using System.Text.Json.Serialization;

namespace Modificus.Curator.Integrations;

// ---- response wrapper -------------------------------------------------------

/// <summary>
/// A Nexus API response: the parsed payload plus the rate-limit metadata
/// extracted from the response headers. Mirrors NMA's
/// <c>Response&lt;T&gt;</c> shape so callers (the update-check service) can back off
/// based on <see cref="RateLimits"/> before the next request.
/// </summary>
/// <typeparam name="T">The parsed payload type.</typeparam>
/// <param name="Data">The parsed response payload.</param>
/// <param name="RateLimits">The rate-limit headers (<c>x-rl-*</c>), parsed. May
/// carry zeros when the headers were absent (e.g. a non-rate-limited endpoint or
/// a test stub).</param>
public sealed record Response<T>(T Data, NexusRateLimits RateLimits);

/// <summary>
/// The Nexus rate-limit window counters, parsed from the <c>x-rl-*</c> response
/// headers. <c>0</c> for any field the server did not report. NMA parses the
/// same six values via <c>ResponseMetadata.FromHttpHeaders</c>.
/// </summary>
/// <param name="DailyLimit">The total daily quota (reset to this when the window
/// rolls over).</param>
/// <param name="DailyRemaining">Requests remaining in the current daily
/// window.</param>
/// <param name="DailyReset">When the daily window resets (server-reported
/// <c>x-rl-daily-reset</c>), or <c>null</c> if absent.</param>
/// <param name="HourlyLimit">The total hourly quota.</param>
/// <param name="HourlyRemaining">Requests remaining in the current hourly
/// window.</param>
/// <param name="HourlyReset">When the hourly window resets (server-reported
/// <c>x-rl-hourly-reset</c>), or <c>null</c> if absent.</param>
public sealed record NexusRateLimits(
    int DailyLimit,
    int DailyRemaining,
    DateTimeOffset? DailyReset,
    int HourlyLimit,
    int HourlyRemaining,
    DateTimeOffset? HourlyReset)
{
    /// <summary>An all-zero fallback for responses that did not carry the
    /// rate-limit headers (or carried unparseable values).</summary>
    public static NexusRateLimits Unknown { get; } = new(0, 0, null, 0, 0, null);
}

// ---- time period ------------------------------------------------------------

/// <summary>
/// The aggregation window for <c>ModUpdatesAsync</c>. Nexus caches each window
/// server-side, so only these three values are accepted.
/// </summary>
public enum NexusPeriod
{
    /// <summary>Updated in the past day.</summary>
    Day,

    /// <summary>Updated in the past week.</summary>
    Week,

    /// <summary>Updated in the past month.</summary>
    Month,
}

// ---- DTOs (snake_case JSON, mirrors NMA's wire schema) ----------------------

// NOTE: DTO property names are bound to the Nexus v1 wire schema (snake_case).
// They are intentionally close to NMA's DTO shapes (validated against NMA's
// source, not copied) so anyone reading NMA's API docs maps directly to these.

/// <summary>
/// The response from <c>GET /v1/users/validate.json</c> (API-key validation).
/// Carries the user's identity + premium state. The <c>?</c>-suffixed
/// <c>is_premium</c>/<c>is_supporter</c> variants the v1 endpoint also emits are
/// NOT bound here; the modern endpoint emits the bare names.
/// </summary>
public sealed class ValidateInfo
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("profile_url")]
    public Uri? ProfileUrl { get; set; }
}

/// <summary>
/// A Nexus membership role, as embedded in the access token's JWT
/// <c>user.membership_roles</c> claim. Lowercase snake-case wire form
/// (member, supporter, premium, lifetimepremium). The
/// <see cref="JsonStringEnumConverter{TEnum}"/> binds the lowercase wire forms
/// case-insensitively should a future caller deserialize the array; the access-
/// token parser (<see cref="NexusAccessTokenClaims"/>) compares the raw role
/// strings directly so an unexpected value cannot fail the parse.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NexusMembershipRole>))]
public enum NexusMembershipRole
{
    /// <summary>A regular member.</summary>
    Member,

    /// <summary>The mini-premium "supporter" tier.</summary>
    Supporter,

    /// <summary>A premium subscription.</summary>
    Premium,

    /// <summary>Lifetime premium (legacy or redeemed from Donation Points).</summary>
    LifetimePremium,
}

/// <summary>
/// One entry in the <c>GET /v1/games/{domain}/mods/updated.json</c> response.
/// Carries the mod id + two activity timestamps (server-side Unix seconds).
/// </summary>
public sealed class ModUpdate
{
    [JsonPropertyName("mod_id")]
    public long ModId { get; set; }

    /// <summary>The last file-update time, as Unix seconds.</summary>
    [JsonPropertyName("latest_file_update")]
    public long LatestFileUpdate { get; set; }

    /// <summary>Convenience UTC conversion of <see cref="LatestFileUpdate"/>.</summary>
    [JsonIgnore]
    public DateTimeOffset LatestFileUpdateUtc =>
        DateTimeOffset.FromUnixTimeSeconds(LatestFileUpdate);

    /// <summary>The last mod-page activity, as Unix seconds.</summary>
    [JsonPropertyName("latest_mod_activity")]
    public long LatestModActivity { get; set; }

    /// <summary>Convenience UTC conversion of <see cref="LatestModActivity"/>.</summary>
    [JsonIgnore]
    public DateTimeOffset LatestModActivityUtc =>
        DateTimeOffset.FromUnixTimeSeconds(LatestModActivity);
}

/// <summary>
/// One CDN download link from <c>download_link.json</c>. <see cref="Uri"/> is the
/// actual CDN URL the client fetches (consumed by the acquisition service).
/// </summary>
public sealed class DownloadLink
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("short_name")]
    public string ShortName { get; set; } = string.Empty;

    [JsonPropertyName("URI")]
    public Uri Uri { get; set; } = new("about:blank");
}

/// <summary>
/// The mod-page metadata from <c>GET /v1/games/{domain}/mods/{modId}.json</c>.
/// Only the fields Curator consumes are bound; the v1 endpoint emits many more,
/// which deserialize safely into the unbound extras.
/// </summary>
public sealed class ModInfo
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("game_id")]
    public int GameId { get; set; }

    [JsonPropertyName("domain_name")]
    public string DomainName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("picture_url")]
    public string PictureUrl { get; set; } = string.Empty;

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCount { get; set; }

    [JsonPropertyName("created_timestamp")]
    public long CreatedTimestamp { get; set; }

    [JsonPropertyName("updated_timestamp")]
    public long UpdatedTimestamp { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("uploaded_by")]
    public string UploadedBy { get; set; } = string.Empty;

    [JsonPropertyName("contains_adult_content")]
    public bool ContainsAdultContent { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// One file entry from <c>GET /v1/games/{domain}/mods/{modId}/files.json</c>.
/// </summary>
public sealed class ModFile
{
    [JsonPropertyName("file_id")]
    public long FileId { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonPropertyName("uploaded_timestamp")]
    public long UploadedTimestamp { get; set; }

    // Whether the file has been archived by the mod author. The Nexus v1
    // files.json endpoint includes archived entries (marked true); the
    // latest-file resolution excludes them so an update targets a current
    // release, not a historical one the author has superseded. Defaults to
    // false when the field is absent (older API responses / partial payloads).
    [JsonPropertyName("archived")]
    public bool IsArchived { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// The wrapper shape of the <c>files.json</c> endpoint (an object with a single
/// <c>files</c> array). Internal: <see cref="INexusClient.ListModFilesAsync"/>
/// unwraps it to <c>ModFile[]</c> for callers.
/// </summary>
internal sealed class ModFilesResponse
{
    [JsonPropertyName("files")]
    public ModFile[] Files { get; set; } = Array.Empty<ModFile>();
}

/// <summary>
/// Shared helpers over a mod's <see cref="ModFile"/> list. Internal: the only
/// callers are the acquisition service (resolves the latest release to download)
/// + the update-check service's thorough pass (resolves the latest release to
/// compare against the imported version). Keeping the filter in one place
/// ensures both call sites agree on what "latest MAIN release" means.
/// </summary>
internal static class NexusModFiles
{
    /// <summary>
    /// The Nexus <c>category_id</c> for a mod's MAIN file. Universal across
    /// every game on Nexus (the category tree is per-game, but id 1 is always
    /// the primary/main bucket); mirrors how MO2, NMA, and Vortex pick the
    /// default download. Optional / miscellaneous / archived files are excluded
    /// by <see cref="LatestMain"/>.
    /// </summary>
    public const int MainFileCategory = 1;

    /// <summary>
    /// Picks the newest non-archived MAIN file (<see cref="MainFileCategory"/>)
    /// from <paramref name="files"/> by <see cref="ModFile.UploadedTimestamp"/>.
    /// Returns <c>null</c> when <paramref name="files"/> is null/empty or no
    /// entry qualifies (every file is optional, archived, or in another
    /// category). The filter both call sites use to resolve "the current
    /// release" of a mod.
    /// </summary>
    public static ModFile? LatestMain(IReadOnlyList<ModFile>? files)
    {
        if (files is null || files.Count == 0)
        {
            return null;
        }

        ModFile? best = null;
        foreach (var f in files)
        {
            if (f.CategoryId != MainFileCategory || f.IsArchived)
            {
                continue;
            }
            if (best is null || f.UploadedTimestamp > best.UploadedTimestamp)
            {
                best = f;
            }
        }
        return best;
    }
}
