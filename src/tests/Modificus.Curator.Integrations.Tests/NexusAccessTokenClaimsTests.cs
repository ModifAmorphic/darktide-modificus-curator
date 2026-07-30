using System.Text;
using System.Text.Json;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Tests for <see cref="NexusAccessTokenClaims.TryParse"/>: the parser that
/// extracts the user's display name + Premium state from a Nexus OAuth access
/// token's JWT payload. Covers the Premium-indicating roles, the non-Premium
/// case, the missing-user-claim case, non-JWT / opaque tokens, null / empty
/// input, and malformed payloads (the parser never throws).
/// </summary>
public sealed class NexusAccessTokenClaimsTests
{
    [Fact]
    public void TryParse_extracts_username_and_premium_from_premium_role()
    {
        var jwt = BuildJwt(new { user = new { id = 1, username = "ModifAmorphic", membership_roles = new[] { "member", "premium" } } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.Equal("ModifAmorphic", claims!.Username);
        Assert.True(claims.IsPremium);
    }

    [Fact]
    public void TryParse_treats_lifetimepremium_as_premium()
    {
        var jwt = BuildJwt(new { user = new { username = "Lifetime", membership_roles = new[] { "member", "lifetimepremium" } } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.True(claims!.IsPremium);
    }

    [Fact]
    public void TryParse_returns_false_premium_when_roles_lack_premium()
    {
        var jwt = BuildJwt(new { user = new { username = "Regular", membership_roles = new[] { "member", "supporter" } } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.Equal("Regular", claims!.Username);
        Assert.False(claims.IsPremium);
    }

    [Fact]
    public void TryParse_returns_null_premium_when_membership_roles_absent()
    {
        // A user claim without the membership_roles property: Username is set,
        // IsPremium is null (the property itself is absent, not empty).
        var jwt = BuildJwt(new { user = new { id = 1, username = "NoRoles" } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.Equal("NoRoles", claims!.Username);
        Assert.Null(claims.IsPremium);
    }

    [Fact]
    public void TryParse_returns_null_when_user_claim_absent()
    {
        var jwt = BuildJwt(new { sub = "121411413" });

        Assert.Null(NexusAccessTokenClaims.TryParse(jwt));
    }

    [Fact]
    public void TryParse_returns_null_when_user_claim_is_not_an_object()
    {
        // The user claim is present but not an object (e.g. a string). The
        // parser must not throw; it returns null so the caller surfaces the
        // unverified state. Complements TryParse_returns_null_when_user_claim_absent.
        var jwt = BuildJwt(new { user = "not-an-object" });

        Assert.Null(NexusAccessTokenClaims.TryParse(jwt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_returns_null_for_null_or_empty_token(string? token)
    {
        Assert.Null(NexusAccessTokenClaims.TryParse(token));
    }

    [Fact]
    public void TryParse_returns_null_for_non_jwt_opaque_token()
    {
        // An opaque access token (no dots, not a JWT) yields null so the caller
        // surfaces the unverified state.
        Assert.Null(NexusAccessTokenClaims.TryParse("AT"));
    }

    [Fact]
    public void TryParse_returns_null_for_wrong_segment_count()
    {
        // Two segments (missing signature) is not a JWT the parser recognizes.
        Assert.Null(NexusAccessTokenClaims.TryParse("header.payload"));
    }

    [Fact]
    public void TryParse_returns_null_for_malformed_payload_without_throwing()
    {
        // Three segments but the payload is not valid base64url / JSON. The
        // parser must never throw; it returns null.
        var malformed = "header.!!!notbase64!!!.";
        Assert.Null(NexusAccessTokenClaims.TryParse(malformed));
    }

    [Fact]
    public void TryParse_returns_null_premium_when_roles_is_empty_array()
    {
        var jwt = BuildJwt(new { user = new { username = "Empty", membership_roles = Array.Empty<string>() } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.False(claims!.IsPremium);
    }

    [Fact]
    public void TryParse_treats_premium_role_case_insensitively()
    {
        // The wire form is lowercase, but the parser matches case-insensitively
        // so a future casing change does not silently demote a Premium user.
        var jwt = BuildJwt(new { user = new { username = "U", membership_roles = new[] { "Premium" } } });

        var claims = NexusAccessTokenClaims.TryParse(jwt);

        Assert.NotNull(claims);
        Assert.True(claims!.IsPremium);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Builds an unsigned JWT (header.payload.) from the supplied payload
    /// object, serialized to JSON + base64url-encoded. The parser does not
    /// verify the signature, so the signature segment is empty.
    /// </summary>
    private static string BuildJwt(object payload)
    {
        const string HeaderJson = "{\"alg\":\"none\",\"typ\":\"JWT\"}";
        var payloadJson = JsonSerializer.Serialize(payload);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson)) + "."
            + Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson)) + ".";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
