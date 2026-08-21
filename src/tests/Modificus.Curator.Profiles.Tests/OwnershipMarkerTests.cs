using System.Text.Json;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// The staging ownership marker contract: <see cref="IProfileService.PrepareModRoot"/>
/// writes <c>.curator.json</c> into the staged <c>mods/</c> on every pass,
/// carrying the projected profile's identity + a projection timestamp. The
/// marker is what proves a game-dir hosting link aimed at the staged tree is
/// Curator's, so it must survive + refresh across the idempotent rebuilds.
/// </summary>
public sealed class OwnershipMarkerTests
{
    [Fact]
    public void PrepareModRoot_writes_the_marker_into_the_staged_mods_root()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("My Profile", string.Empty, new LaunchSettings());

        fx.Service.PrepareModRoot(profile.Id);

        var markerPath = Path.Combine(fx.StagedDir(profile.Id), "mods", StagingOwnership.MarkerFileName);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void Marker_carries_schema_profile_identity_and_projection_timestamp()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("My Profile", string.Empty, new LaunchSettings());
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        fx.Service.PrepareModRoot(profile.Id);

        var marker = ReadMarker(fx, profile.Id);
        Assert.Equal(1, marker.GetProperty("Schema").GetInt32());
        Assert.Equal(profile.Id, marker.GetProperty("ProfileId").GetGuid());
        Assert.Equal("My Profile", marker.GetProperty("ProfileName").GetString());
        var projected = marker.GetProperty("ProjectedAtUtc").GetDateTimeOffset();
        Assert.True(projected >= before && projected <= DateTimeOffset.UtcNow.AddSeconds(5),
            "projectedAtUtc must be the projection time in UTC");
    }

    [Fact]
    public async Task Marker_is_rewritten_with_a_fresh_timestamp_on_every_pass()
    {
        // The pass clears + rebuilds staged/, so the marker must be rewritten
        // (a stale marker surviving a rebuild would misattribute the tree).
        // Timestamps tick at 100ns but file I/O can complete within one tick,
        // so prove the rewrite through content replacement + a real time gap.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        fx.Service.PrepareModRoot(profile.Id);
        var first = ReadMarker(fx, profile.Id);
        var firstWrite = File.GetLastWriteTimeUtc(
            Path.Combine(fx.StagedDir(profile.Id), "mods", StagingOwnership.MarkerFileName));

        await Task.Delay(1100); // cross a filesystem timestamp granularity boundary
        fx.Service.PrepareModRoot(profile.Id);

        var second = ReadMarker(fx, profile.Id);
        Assert.Equal(profile.Id, second.GetProperty("ProfileId").GetGuid());
        Assert.True(
            second.GetProperty("ProjectedAtUtc").GetDateTimeOffset()
                > first.GetProperty("ProjectedAtUtc").GetDateTimeOffset(),
            "the rewritten marker must carry a later projection timestamp");
        var secondWrite = File.GetLastWriteTimeUtc(
            Path.Combine(fx.StagedDir(profile.Id), "mods", StagingOwnership.MarkerFileName));
        Assert.True(secondWrite > firstWrite, "the marker file must be rewritten, not left in place");
    }

    [Fact]
    public void Marker_reflects_a_renamed_profile_on_the_next_pass()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("Old Name", string.Empty, new LaunchSettings());
        fx.Service.PrepareModRoot(profile.Id);

        fx.Service.UpdateProfile(profile.Id, "New Name", string.Empty, new LaunchSettings());
        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal("New Name", ReadMarker(fx, profile.Id).GetProperty("ProfileName").GetString());
    }

    [Fact]
    public void Marker_content_survives_a_reload_of_the_same_projection()
    {
        // Two passes with no changes in between rewrite the marker but keep the
        // same identity: the marker always describes the CURRENT projection,
        // never accumulates history inside staged/.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());

        fx.Service.PrepareModRoot(profile.Id);
        fx.Service.PrepareModRoot(profile.Id);

        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(fx.StagedDir(profile.Id), "mods", StagingOwnership.MarkerFileName)));
        Assert.Equal(4, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void ProfilesRoot_returns_the_configured_base_folder_ensured_to_exist()
    {
        using var fx = new ProfileServiceFixture();

        var root = fx.Service.ProfilesRoot;

        Assert.Equal(fx.BaseFolder, root);
        Assert.True(Directory.Exists(root));
    }

    private static JsonElement ReadMarker(ProfileServiceFixture fx, Guid profileId)
    {
        var path = Path.Combine(fx.StagedDir(profileId), "mods", StagingOwnership.MarkerFileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
}
