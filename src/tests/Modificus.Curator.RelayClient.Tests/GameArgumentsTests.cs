using Modificus.Curator.Profiles;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Game-argument emission via Relay's bare-<c>--</c> contract, exercised through
/// the pure <see cref="LinuxLaunchStrategy.BuildLauncherArgs"/> /
/// <see cref="WindowsLaunchStrategy.BuildLauncherArgs"/> seams (no process is
/// spawned). The two strategies share <see cref="LinuxLaunchStrategy.AppendGameArguments"/>,
/// so the contract is identical on both. These tests assert the contract
/// relationally: Relay's own flags precede the <c>--</c> separator; a
/// value-taking flag is immediately followed by its value; a bare flag is
/// present iff its toggle is on; game args follow <c>--</c> verbatim, in order.
/// They deliberately do NOT pin absolute argv layout (no fixed indices, no total
/// element count, no adjacency between independent flags), so the suite stays
/// resilient to flag additions and reordering that Relay fully accepts.
/// </summary>
public sealed class GameArgumentsTests
{
    private const string GameBinary = "/opt/Darktide.exe";
    private const string ModPath = "/curator/profile/mods";
    private const string LogFile = "/curator/curator.log";

    // ---- empty game args: no -- (legacy launch) ---------------------------

    [Fact]
    public void Linux_empty_game_args_emit_no_separator()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings());

        AssertSeparator(args, present: false);
    }

    [Fact]
    public void Windows_empty_game_args_emit_no_separator()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings());

        AssertSeparator(args, present: false);
    }

    [Fact]
    public void Null_game_args_emit_no_separator()
    {
        // Defense: a null list (LaunchSettings stores non-null, but the seam is
        // robust) is treated as empty.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = null! });

        AssertSeparator(args, present: false);
    }

    // ---- non-empty: one -- then each arg as its own element, in order ------

    [Fact]
    public void Linux_multiple_args_emit_one_separator_then_each_arg_in_order()
    {
        var gameArgs = new[] { "-windowed", "-borderless", "-width" };

        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = gameArgs });

        AssertSeparator(args, present: true);
        AssertGameArgsAfterSeparator(args, gameArgs);
    }

    [Fact]
    public void Windows_multiple_args_emit_one_separator_then_each_arg_in_order()
    {
        var gameArgs = new[] { "-one", "-two", "-three" };

        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = gameArgs });

        AssertSeparator(args, present: true);
        AssertGameArgsAfterSeparator(args, gameArgs);
    }

    [Fact]
    public void A_single_arg_emits_one_separator_then_the_arg()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = new[] { "-solo" } });

        AssertSeparator(args, present: true);
        AssertGameArgsAfterSeparator(args, new[] { "-solo" });
    }

    // ---- duplicate game args preserved -------------------------------------

    [Fact]
    public void Duplicate_game_args_are_each_emitted_as_their_own_element()
    {
        // Each entry is a distinct argv value; duplicates are not collapsed.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = new[] { "-x", "-x", "-y" } });

        AssertGameArgsAfterSeparator(args, new[] { "-x", "-x", "-y" });
    }

    // ---- values with spaces + quotes stay one element ----------------------

    [Fact]
    public void Values_with_spaces_stay_one_element()
    {
        // Relay owns the final CreateProcess quoting. Curator adds each arg
        // verbatim to ArgumentList; a value with spaces survives as a single
        // argv entry (no prequoting / joining on Curator's side).
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new LaunchSettings { GameArguments = new[] { "an arg with spaces", "-plain" } });

        AssertGameArgsAfterSeparator(args, new[] { "an arg with spaces", "-plain" });
    }

    [Fact]
    public void Values_with_quotes_stay_one_element()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new LaunchSettings { GameArguments = new[] { """a "quoted" arg""", "-plain" } });

        AssertGameArgsAfterSeparator(args, new[] { """a "quoted" arg""", "-plain" });
    }

    [Fact]
    public void An_empty_string_arg_is_emitted_as_an_empty_element()
    {
        // An empty game arg is a distinct (empty) argv entry, not dropped.
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new LaunchSettings { GameArguments = new[] { "-a", "", "-b" } });

        AssertGameArgsAfterSeparator(args, new[] { "-a", "", "-b" });
    }

    // ---- launcher flags precede -- -----------------------------------------

    [Fact]
    public void Launcher_flags_precede_the_separator_on_linux()
    {
        // The launcher's own value-taking flags precede the -- separator; their
        // values are Z:\-translated (the launcher runs under Wine). Relay flags
        // precede --, game args follow.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = new[] { "-g" } });

        AssertFlagFollowedByValue(args, "--game-binary", WinePath.ToWine(GameBinary));
        AssertFlagFollowedByValue(args, "--mod-path", WinePath.ToWine(ModPath));
        AssertFlagFollowedByValue(args, "--log-file", WinePath.ToWine(LogFile));
        AssertPrecedesSeparator(args, "--game-binary");
        AssertPrecedesSeparator(args, "--mod-path");
        AssertPrecedesSeparator(args, "--log-file");
        AssertGameArgsAfterSeparator(args, new[] { "-g" });
    }

    [Fact]
    public void Launcher_flags_precede_the_separator_on_windows()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { GameArguments = new[] { "-g" } });

        AssertFlagFollowedByValue(args, "--game-binary", GameBinary);
        AssertFlagFollowedByValue(args, "--mod-path", ModPath);
        AssertFlagFollowedByValue(args, "--log-file", LogFile);
        AssertPrecedesSeparator(args, "--game-binary");
        AssertPrecedesSeparator(args, "--mod-path");
        AssertPrecedesSeparator(args, "--log-file");
        AssertGameArgsAfterSeparator(args, new[] { "-g" });
    }

    // ---- --lua-logs: bare flag emitted when the profile opts in ------------

    [Fact]
    public void Windows_enable_lua_logs_emits_the_bare_flag()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { EnableLuaLogs = true });

        AssertBareFlag(args, "--lua-logs", present: true);
        AssertSeparator(args, present: false);
    }

    [Fact]
    public void Linux_enable_lua_logs_emits_the_bare_flag()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { EnableLuaLogs = true });

        AssertBareFlag(args, "--lua-logs", present: true);
        AssertSeparator(args, present: false);

        // Guards against accidentally routing the bare flag through WinePath: it
        // carries no Z:\ prefix (a path-translated "--lua-logs" would be corrupt).
        Assert.DoesNotContain("Z:", args[IndexOf(args, "--lua-logs")]);
    }

    [Fact]
    public void Enable_lua_logs_flag_precedes_the_separator()
    {
        // With both a lua-log toggle and a game arg, the bare --lua-logs flag
        // precedes the -- separator, and the game arg follows --. Covers both
        // Windows and Linux.
        var settings = new LaunchSettings { EnableLuaLogs = true, GameArguments = new[] { "-g" } };
        var win = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);
        var lin = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);

        AssertBareFlag(win, "--lua-logs", present: true);
        AssertPrecedesSeparator(win, "--lua-logs");
        AssertGameArgsAfterSeparator(win, new[] { "-g" });

        AssertBareFlag(lin, "--lua-logs", present: true);
        AssertPrecedesSeparator(lin, "--lua-logs");
        AssertGameArgsAfterSeparator(lin, new[] { "-g" });
    }

    [Fact]
    public void Disable_lua_logs_emits_no_flag()
    {
        // Explicit false: --lua-logs is absent (one platform is enough; the
        // signature is shared).
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings());

        AssertBareFlag(args, "--lua-logs", present: false);
    }

    // ---- --skip-splash: bare flag emitted when the profile opts in ---------

    [Fact]
    public void Windows_skip_splash_emits_the_bare_flag()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { SkipSplash = true });

        AssertBareFlag(args, "--skip-splash", present: true);
        AssertSeparator(args, present: false);
    }

    [Fact]
    public void Linux_skip_splash_emits_the_bare_flag()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings { SkipSplash = true });

        AssertBareFlag(args, "--skip-splash", present: true);
        AssertSeparator(args, present: false);

        // Guards against accidentally routing the bare flag through WinePath: it
        // carries no Z:\ prefix (a path-translated "--skip-splash" would be corrupt).
        Assert.DoesNotContain("Z:", args[IndexOf(args, "--skip-splash")]);
    }

    [Fact]
    public void Skip_splash_flag_precedes_the_separator()
    {
        // With both a skip-splash toggle and a game arg, the bare --skip-splash
        // flag precedes the -- separator, and the game arg follows --. Covers
        // both Windows and Linux.
        var settings = new LaunchSettings { SkipSplash = true, GameArguments = new[] { "-g" } };
        var win = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);
        var lin = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);

        AssertBareFlag(win, "--skip-splash", present: true);
        AssertPrecedesSeparator(win, "--skip-splash");
        AssertGameArgsAfterSeparator(win, new[] { "-g" });

        AssertBareFlag(lin, "--skip-splash", present: true);
        AssertPrecedesSeparator(lin, "--skip-splash");
        AssertGameArgsAfterSeparator(lin, new[] { "-g" });
    }

    [Fact]
    public void Disable_skip_splash_emits_no_flag()
    {
        // Explicit false (default): --skip-splash is absent (one platform is
        // enough; the signature is shared).
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new LaunchSettings());

        AssertBareFlag(args, "--skip-splash", present: false);
    }

    // ---- both bare flags: present and precede the -- separator when both on ----

    [Fact]
    public void Both_bare_flags_precede_separator_when_both_toggles_on()
    {
        // With both toggles on, each bare flag is present and precedes the --
        // separator (game args follow --). The relative order of the two bare
        // flags is not a Relay contract, so it is not asserted. Covers both
        // Windows and Linux.
        var settings = new LaunchSettings { EnableLuaLogs = true, SkipSplash = true, GameArguments = new[] { "-g" } };
        var win = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);
        var lin = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, settings);

        AssertBareFlag(win, "--lua-logs", present: true);
        AssertPrecedesSeparator(win, "--lua-logs");
        AssertBareFlag(win, "--skip-splash", present: true);
        AssertPrecedesSeparator(win, "--skip-splash");
        AssertGameArgsAfterSeparator(win, new[] { "-g" });

        AssertBareFlag(lin, "--lua-logs", present: true);
        AssertPrecedesSeparator(lin, "--lua-logs");
        AssertBareFlag(lin, "--skip-splash", present: true);
        AssertPrecedesSeparator(lin, "--skip-splash");
        AssertGameArgsAfterSeparator(lin, new[] { "-g" });

        // Linux: the path-valued flag IS Z:\-translated; the bare flags are not.
        AssertFlagFollowedByValue(lin, "--log-file", WinePath.ToWine(LogFile));
        Assert.DoesNotContain("Z:", lin[IndexOf(lin, "--lua-logs")]);
        Assert.DoesNotContain("Z:", lin[IndexOf(lin, "--skip-splash")]);
    }

    // ---- relational contract helpers ---------------------------------------
    // These assert Relay's launcher-arg contract without pinning absolute argv
    // layout: flags precede --, a value-taking flag is immediately followed by
    // its value, a bare flag is present iff its toggle is on, and game args
    // follow the first -- verbatim. No fixed indices, no total-count checks.
    // Takes IReadOnlyList<string> so the same helpers apply to the List<string>
    // from BuildLauncherArgs and the IReadOnlyList<string> the fake launcher
    // exposes, without forcing a ToList copy.

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    // A value-taking flag is present and its value IMMEDIATELY follows it (Relay contract #2).
    private static void AssertFlagFollowedByValue(IReadOnlyList<string> args, string flag, string expectedValue)
    {
        var i = IndexOf(args, flag);
        Assert.True(i >= 0, $"expected flag {flag} to be present");
        Assert.True(i + 1 < args.Count, $"expected {flag} to be immediately followed by its value");
        Assert.Equal(expectedValue, args[i + 1]);
    }

    // A bare (value-less) flag is present exactly once when on, absent when off (the toggle contract).
    private static void AssertBareFlag(IReadOnlyList<string> args, string flag, bool present)
    {
        if (present) Assert.Single(args, a => a == flag);
        else Assert.DoesNotContain(flag, args);
    }

    // A Relay flag precedes the -- separator (Relay contract #1). No-op when the separator is absent.
    private static void AssertPrecedesSeparator(IReadOnlyList<string> args, string flag)
    {
        var i = IndexOf(args, flag);
        var sep = IndexOf(args, "--");
        Assert.True(i >= 0, $"expected flag {flag} to be present");
        Assert.True(sep < 0 || i < sep, $"expected {flag} to precede the -- separator");
    }

    // Exactly one -- separator when game args are non-empty; none when empty.
    private static void AssertSeparator(IReadOnlyList<string> args, bool present)
    {
        if (present) Assert.Single(args, a => a == "--");
        else Assert.DoesNotContain("--", args);
    }

    // The tokens after the FIRST -- separator equal the expected game args, in order.
    private static void AssertGameArgsAfterSeparator(IReadOnlyList<string> args, IReadOnlyList<string> expected)
    {
        var sep = IndexOf(args, "--");
        Assert.True(sep >= 0, "expected a -- separator");
        Assert.Equal(expected, args.Skip(sep + 1).ToArray());
    }
}
