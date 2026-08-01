using System.Diagnostics;
using Modificus.Curator.RelayClient;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Focused tests for <see cref="ProcessLauncher.BuildStartInfo"/>: the
/// deterministic, pure-construction path that production <see cref="ProcessLauncher.Start"/>
/// invokes (no real process is spawned). The tests cover the four environment
/// mutations (a requested inherited key is absent; an unrelated inherited key
/// remains; an override is applied; an override wins after removal) plus the
/// <see cref="ProcessStartInfo.UseShellExecute"/> requirement and the verbatim
/// argv layout. They are reliable on Windows and Linux and never mutate the
/// global process environment: inherited-key tests read existing parent
/// environment state read-only, and override tests use synthetic keys that the
/// parent never carried (the override is written only into the per-instance
/// <see cref="ProcessStartInfo.Environment"/> snapshot, not the global block).
/// </summary>
public sealed class ProcessLauncherTests
{
    [Fact]
    public void BuildStartInfo_sets_UseShellExecute_false()
    {
        // UseShellExecute=false is required both to mutate the environment and
        // to use ArgumentList (no shell). The launcher is an .exe even on Linux.
        var request = new ProcessLaunchRequest("/bin/true");

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void BuildStartInfo_sets_CreateNoWindow_from_request()
    {
        // CreateNoWindow suppresses the child's console window (honored only with
        // UseShellExecute=false, which BuildStartInfo always sets). The request's
        // flag flows straight through to ProcessStartInfo.CreateNoWindow.
        var hidden = new ProcessLaunchRequest("/bin/true", createNoWindow: true);
        var shown = new ProcessLaunchRequest("/bin/true", createNoWindow: false);

        Assert.True(ProcessLauncher.BuildStartInfo(hidden).CreateNoWindow);
        Assert.False(ProcessLauncher.BuildStartInfo(shown).CreateNoWindow);
    }

    [Fact]
    public void BuildStartInfo_defaults_CreateNoWindow_to_false_when_not_requested()
    {
        // The ctor default is false, so a caller that does not opt in keeps the
        // prior behavior (the child console is shown per the platform default).
        var request = new ProcessLaunchRequest("/bin/true");

        Assert.False(ProcessLauncher.BuildStartInfo(request).CreateNoWindow);
    }

    [Fact]
    public void BuildStartInfo_sets_FileName_from_request()
    {
        var request = new ProcessLaunchRequest("/opt/curator/mod_relay.exe");

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.Equal("/opt/curator/mod_relay.exe", startInfo.FileName);
    }

    [Fact]
    public void BuildStartInfo_adds_each_argument_as_a_distinct_ArgumentList_entry()
    {
        // Each argument is added verbatim to ArgumentList -- no re-shelling or
        // concatenation. Values containing spaces and shell metacharacters must
        // survive as a single argv entry so paths resolve at the child.
        var args = new[]
        {
            "run",
            "/opt/relay/mod_relay.exe",
            "--game-binary",
            @"C:\Program Files\Darktide\binaries\Darktide.exe",
            "--mod-path",
            "/home/u/My Mods/profile",
            "--flag",
            "a$b `c` \"d\" |e; f&g",
        };
        var request = new ProcessLaunchRequest("/bin/true", args);

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.Equal(args.Length, startInfo.ArgumentList.Count);
        Assert.Equal(args, startInfo.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_coerces_null_argument_entries_to_empty_string()
    {
        // A null argument entry would corrupt the argv layout; the launcher
        // coerces it to "" defensively so an IProcessLauncher caller passing a
        // sequence that happens to contain a null cannot crash ArgumentList.Add.
        // The null-forgiving operator is intentional: this deliberately violates
        // the non-null element contract to verify the defensive coercion.
        var args = new[] { "a", null!, "b" };
        var request = new ProcessLaunchRequest("/bin/true", args);

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.Equal(new[] { "a", "", "b" }, startInfo.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_removes_a_requested_inherited_key()
    {
        // A key listed for removal that the parent environment carries must be
        // absent from the constructed environment block. The key is chosen
        // dynamically from the actual parent environment (read-only: the global
        // environment is never mutated), so the test exercises a real inherited
        // entry without depending on any specific well-known name.
        var inheritedKey = PickExistingEnvironmentKey();
        var request = new ProcessLaunchRequest(
            "/bin/true",
            environmentVariablesToRemove: new[] { inheritedKey });

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.False(
            startInfo.Environment.ContainsKey(inheritedKey),
            "the requested key must be removed from the inherited block");
    }

    [Fact]
    public void BuildStartInfo_preserves_an_unrelated_inherited_key()
    {
        // A key the request does NOT list for removal must remain in the
        // constructed environment block with the inherited value. Both keys are
        // chosen dynamically from the actual parent environment (read-only); only
        // the first is in the removal set, so the second must survive.
        var (removeKey, keepKey) = PickTwoDistinctExistingEnvironmentKeys();
        var request = new ProcessLaunchRequest(
            "/bin/true",
            environmentVariablesToRemove: new[] { removeKey });

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.False(startInfo.Environment.ContainsKey(removeKey));
        Assert.True(startInfo.Environment.ContainsKey(keepKey));
        Assert.Equal(
            Environment.GetEnvironmentVariable(keepKey),
            startInfo.Environment[keepKey]);
    }

    [Fact]
    public void BuildStartInfo_applies_an_environment_override()
    {
        // An override for a synthetic key the parent never carried is written
        // into the child's environment block. The synthetic key cannot pollute
        // the global environment: the value lands only in the per-instance
        // ProcessStartInfo.Environment snapshot.
        var key = UniqueSyntheticKey();
        var request = new ProcessLaunchRequest(
            "/bin/true",
            environmentOverrides: new[] { new KeyValuePair<string, string>(key, "override-value") });

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.True(startInfo.Environment.ContainsKey(key));
        Assert.Equal("override-value", startInfo.Environment[key]);
    }

    [Fact]
    public void BuildStartInfo_lets_an_override_win_when_the_key_is_in_both_sets()
    {
        // A key listed in both EnvironmentVariablesToRemove and
        // EnvironmentOverrides ends up with the override's value: removals run
        // first, overrides apply second, so the override intentionally wins.
        // The key is synthetic (never inherited), so this also proves ordering:
        // if overrides ran BEFORE removals, the removal would wipe the just-written
        // override and the key would be absent.
        var key = UniqueSyntheticKey();
        var request = new ProcessLaunchRequest(
            "/bin/true",
            environmentOverrides: new[] { new KeyValuePair<string, string>(key, "override-wins") },
            environmentVariablesToRemove: new[] { key });

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        Assert.True(startInfo.Environment.ContainsKey(key));
        Assert.Equal("override-wins", startInfo.Environment[key]);
    }

    [Fact]
    public void BuildStartInfo_removal_is_a_noop_for_a_key_the_parent_does_not_carry()
    {
        // Removing a key the parent environment never carried is a silent no-op:
        // the constructed block is simply the inherited snapshot minus nothing.
        // This is the AppImage-not-present case on Linux (Curator launched as a
        // standalone build, none of APPDIR/APPIMAGE/etc. set).
        var request = new ProcessLaunchRequest(
            "/bin/true",
            environmentVariablesToRemove: new[]
            {
                "APPDIR", "APPIMAGE", "ARGV0", "OWD", "BAMF_DESKTOP_FILE_HINT",
            });

        var startInfo = ProcessLauncher.BuildStartInfo(request);

        // None of those keys should be present (whether they were inherited or
        // not). The constructed block must still be usable.
        foreach (var key in request.EnvironmentVariablesToRemove)
        {
            Assert.False(startInfo.Environment.ContainsKey(key));
        }
    }

    [Fact]
    public void BuildStartInfo_rejects_a_null_request()
    {
        Assert.Throws<ArgumentNullException>(() => ProcessLauncher.BuildStartInfo(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProcessLaunchRequest_constructor_rejects_a_null_or_whitespace_FilePath(string? filePath)
    {
        // FilePath is guaranteed non-blank by the constructor, so BuildStartInfo
        // has no redundant whitespace check. This test exercises the constructor
        // alone (BuildStartInfo is never reached) for each invalid FilePath
        // shape: null, empty, and whitespace-only. ThrowIfNullOrWhiteSpace raises
        // ArgumentNullException for null and ArgumentException for empty /
        // whitespace, so ThrowsAny accepts both.
        Assert.ThrowsAny<ArgumentException>(() => new ProcessLaunchRequest(filePath!));
    }

    /// <summary>
    /// Picks the name of an environment variable that exists in the parent
    /// environment right now. Read-only: the global environment is never
    /// mutated. Used so a removal test exercises a real inherited entry without
    /// depending on any specific well-known name.
    /// </summary>
    private static string PickExistingEnvironmentKey()
    {
        var key = Environment.GetEnvironmentVariables().Keys.Cast<string>().FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(key), "the parent environment must carry at least one variable for this test");
        return key!;
    }

    /// <summary>
    /// Picks the names of two distinct environment variables that both exist in
    /// the parent environment right now. Read-only. Used so a preservation test
    /// can remove one real inherited key while asserting another real inherited
    /// key survives.
    /// </summary>
    private static (string removeKey, string keepKey) PickTwoDistinctExistingEnvironmentKeys()
    {
        var keys = Environment.GetEnvironmentVariables().Keys.Cast<string>().Take(2).ToList();
        Assert.True(
            keys.Count >= 2,
            "the parent environment must carry at least two distinct variables for this test");
        return (keys[0], keys[1]);
    }

    /// <summary>
    /// A synthetic key name extremely unlikely to exist in the parent
    /// environment, used for override tests where seeding the parent env is not
    /// needed (the override is written only into the per-instance snapshot).
    /// </summary>
    private static string UniqueSyntheticKey() =>
        "CURATOR_TEST_SYNTHETIC_" + Guid.NewGuid().ToString("N");
}
