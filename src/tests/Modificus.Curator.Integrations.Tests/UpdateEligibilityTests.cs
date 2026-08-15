using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// <see cref="UpdateEligibility"/> unit tests: the four rules (membership /
/// policy / source / version) evaluated directly, including every rejection
/// reason and the case-insensitive version match. The end-to-end consumers
/// (the state store's hydration self-heal + the install-time revalidation)
/// are covered in their own test files.
/// </summary>
public sealed class UpdateEligibilityTests
{
    private static ModContainer Container(ModSource source, string version) =>
        new()
        {
            Id = Guid.NewGuid(),
            Source = source,
            Name = "Mod",
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = "v",
                    VersionString = version,
                    IsLatest = true,
                    ImportedAt = DateTimeOffset.UtcNow,
                },
            },
        };

    [Fact]
    public void Eligible_when_all_four_rules_hold()
    {
        var candidate = new ModListCandidate(Guid.NewGuid(), new LatestPolicy());
        var container = Container(new NexusSource { ModId = 8 }, "1.0");

        var eligible = UpdateEligibility.IsEligible(
            candidate, container, expectedModId: 8, expectedVersion: "1.0", out var reason);

        Assert.True(eligible);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Null_candidate_means_removed_from_profile()
    {
        var container = Container(new NexusSource { ModId = 8 }, "1.0");

        var eligible = UpdateEligibility.IsEligible(
            null, container, 8, "1.0", out var reason);

        Assert.False(eligible);
        Assert.Equal("removed from profile", reason);
    }

    [Fact]
    public void Pinned_candidate_means_re_pinned()
    {
        var candidate = new ModListCandidate(Guid.NewGuid(), new PinnedPolicy("v"));
        var container = Container(new NexusSource { ModId = 8 }, "1.0");

        var eligible = UpdateEligibility.IsEligible(
            candidate, container, 8, "1.0", out var reason);

        Assert.False(eligible);
        Assert.Equal("re-pinned", reason);
    }

    [Fact]
    public void Null_container_means_container_gone()
    {
        var candidate = new ModListCandidate(Guid.NewGuid(), new LatestPolicy());

        var eligible = UpdateEligibility.IsEligible(
            candidate, null, 8, "1.0", out var reason);

        Assert.False(eligible);
        Assert.Equal("container gone", reason);
    }

    [Theory]
    [InlineData(9)]          // different Nexus mod id
    [InlineData(8)]          // right id, wrong source type below
    public void Non_matching_nexus_identity_means_source_changed(int expectedModId)
    {
        var candidate = new ModListCandidate(Guid.NewGuid(), new LatestPolicy());
        // The mod-id-8 container: expectedModId 9 misses by id; the untracked
        // variant (built below) misses by source type.
        var container = expectedModId == 9
            ? Container(new NexusSource { ModId = 8 }, "1.0")
            : Container(new UntrackedSource(), "1.0");

        var eligible = UpdateEligibility.IsEligible(
            candidate, container, expectedModId, "1.0", out var reason);

        Assert.False(eligible);
        Assert.Equal("source changed", reason);
    }

    [Fact]
    public void Different_installed_version_means_version_changed()
    {
        var candidate = new ModListCandidate(Guid.NewGuid(), new LatestPolicy());
        var container = Container(new NexusSource { ModId = 8 }, "2.0");

        var eligible = UpdateEligibility.IsEligible(
            candidate, container, 8, "1.0", out var reason);

        Assert.False(eligible);
        Assert.Equal("version changed", reason);
    }

    [Fact]
    public void Version_match_is_ordinal_ignore_case()
    {
        // "1.0" vs "1.0-BETA" differs only by case: still the same version
        // (Nexus version tags are compared case-insensitively everywhere in
        // the update family).
        var candidate = new ModListCandidate(Guid.NewGuid(), new LatestPolicy());
        var container = Container(new NexusSource { ModId = 8 }, "1.0-beta");

        var eligible = UpdateEligibility.IsEligible(
            candidate, container, 8, "1.0-BETA", out var reason);

        Assert.True(eligible);
        Assert.Equal(string.Empty, reason);
    }
}
