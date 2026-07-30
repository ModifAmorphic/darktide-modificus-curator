using System.Text;
using System.Text.Json;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The user's display name + Premium state extracted from a Nexus OAuth access
/// token's JWT payload. Nexus embeds the user's username + membership roles in
/// the access token (under a <c>user</c> claim); the <c>/oauth/userinfo</c>
/// endpoint returns only <c>sub</c> for this client, so the Integrations status
/// line + the Premium-gated behaviors read these claims from the token rather
/// than from a separate API call. The typed <see cref="NexusMembershipRole"/>
/// enum names the role wire-values this parser recognizes as Premium
/// indicators.
/// </summary>
/// <remarks>
/// <b>No signature verification.</b> The token was obtained over TLS via the
/// authenticated exchange; these claims are for UI display only (the status
/// line + the Premium badge). Curator makes no authorization decision on them
/// (the bearer itself is validated server-side by Nexus on each API call), so
/// verifying the signature here would add cost without changing any behavior.
/// </remarks>
internal sealed record NexusAccessTokenClaims(string? Username, bool? IsPremium)
{
    /// <summary>
    /// Parses the access token's JWT payload for the user's display name +
    /// Premium state. Returns <c>null</c> when the token is missing, not a JWT
    /// (fewer or more than three dot-separated segments), or unparseable; the
    /// caller surfaces the unverified state rather than throwing.
    /// </summary>
    /// <param name="accessToken">The raw Nexus OAuth access token (a JWT).</param>
    /// <returns>
    /// The parsed claims, or <c>null</c> when the input is not a parseable
    /// Nexus access token. <see cref="IsPremium"/> is <c>null</c> only when the
    /// <c>membership_roles</c> property itself is absent; <c>false</c> when the
    /// array is present but contains no Premium-indicating role.
    /// </returns>
    public static NexusAccessTokenClaims? TryParse(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var segments = accessToken.Split('.');
        if (segments.Length != 3)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(segments[1]);
            using var doc = JsonDocument.Parse(payloadBytes);

            if (!doc.RootElement.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? username = null;
            if (user.TryGetProperty("username", out var usernameEl)
                && usernameEl.ValueKind == JsonValueKind.String)
            {
                username = usernameEl.GetString();
            }

            bool? isPremium = null;
            if (user.TryGetProperty("membership_roles", out var rolesEl)
                && rolesEl.ValueKind == JsonValueKind.Array)
            {
                isPremium = ContainsPremiumRole(rolesEl);
            }

            return new NexusAccessTokenClaims(username, isPremium);
        }
        catch
        {
            // Defensive: any decode or JSON parse failure yields null so the
            // caller surfaces the unverified state rather than throwing. The
            // caller treats a null parse as "signed in, unverified".
            return null;
        }
    }

    /// <summary>
    /// Whether the <c>membership_roles</c> array contains a Premium-indicating
    /// role (<c>premium</c> or <c>lifetimepremium</c>, case-insensitive).
    /// Returns <c>false</c> when the array is present but contains neither.
    /// Raw-string comparison (not enum deserialization) so an unexpected role
    /// value cannot fail the whole parse.
    /// </summary>
    private static bool ContainsPremiumRole(JsonElement rolesEl)
    {
        foreach (var role in rolesEl.EnumerateArray())
        {
            if (role.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var value = role.GetString();
            if (value is not null
                && (value.Equals("premium", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("lifetimepremium", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Base64url-decodes <paramref name="value"/> (translates <c>-</c> to
    /// <c>+</c> and <c>_</c> to <c>/</c>, pads to a multiple of 4 with
    /// <c>=</c>). Throws <see cref="FormatException"/> on invalid input; the
    /// caller wraps that in a <c>null</c> return.
    /// </summary>
    private static byte[] Base64UrlDecode(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }

        // Pad to a multiple of 4 with '=' so Convert.FromBase64String accepts it.
        var pad = (4 - (value.Length % 4)) % 4;
        if (pad > 0)
        {
            builder.Append('=', pad);
        }

        return Convert.FromBase64String(builder.ToString());
    }
}
