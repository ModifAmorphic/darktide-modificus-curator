namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="ModLoadOrderParser"/>: the DML-exact reader rules (trim, skip
/// empty, skip <c>--</c> comments after trim, first-wins dedupe, BOM
/// tolerance) and the deliberately-absent tolerances (<c>#</c>/<c>//</c>/
/// inline trailing comments stay part of the name).
/// </summary>
public sealed class ModLoadOrderParserTests
{
    [Fact]
    public void Parses_plain_lines_in_order()
    {
        var names = ModLoadOrderParser.Parse("ModA\nModB\nModC\n");

        Assert.Equal(["ModA", "ModB", "ModC"], names);
    }

    [Fact]
    public void Windows_newlines_and_blank_lines_are_handled()
    {
        var names = ModLoadOrderParser.Parse("ModA\r\n\r\nModB\r\n\r\n\r\nModC");

        Assert.Equal(["ModA", "ModB", "ModC"], names);
    }

    [Fact]
    public void Each_line_is_trimmed()
    {
        var names = ModLoadOrderParser.Parse("  ModA  \n\tModB\t\n \nModC");

        Assert.Equal(["ModA", "ModB", "ModC"], names);
    }

    [Fact]
    public void Comment_lines_are_skipped_after_trim()
    {
        // Whitespace before the marker still comments the line: the reader
        // trims first, then checks the prefix.
        var names = ModLoadOrderParser.Parse(
            "-- a header comment\nModA\n   -- indented comment\nModB\n--ModC-is-commented-out");

        Assert.Equal(["ModA", "ModB"], names);
    }

    [Fact]
    public void Duplicate_names_dedupe_first_wins()
    {
        var names = ModLoadOrderParser.Parse("ModA\nModB\nModA\nModA\nModC");

        Assert.Equal(["ModA", "ModB", "ModC"], names);
    }

    [Fact]
    public void Case_variants_are_distinct_names()
    {
        // The parser dedupes exact duplicates only; case-insensitive
        // equivalence is the MATCHER's rule (the planner), not the reader's.
        var names = ModLoadOrderParser.Parse("ModA\nmoda");

        Assert.Equal(["ModA", "moda"], names);
    }

    [Fact]
    public void A_leading_bom_is_tolerated()
    {
        var names = ModLoadOrderParser.Parse("\uFEFFModA\nModB");

        Assert.Equal(["ModA", "ModB"], names);
    }

    [Fact]
    public void Empty_and_comment_only_texts_yield_an_empty_result()
    {
        Assert.Empty(ModLoadOrderParser.Parse(string.Empty));
        Assert.Empty(ModLoadOrderParser.Parse("\n\n"));
        Assert.Empty(ModLoadOrderParser.Parse("-- only comments\n-- nothing else\n"));
        Assert.Empty(ModLoadOrderParser.Parse("\uFEFF-- BOM then comments\n"));
    }

    [Fact]
    public void Hash_and_slash_comments_are_NOT_comments()
    {
        // The loader does not treat these as comments; neither does the
        // parser. A line carrying them is a (strange) folder name.
        var names = ModLoadOrderParser.Parse("# ModA\n// ModB");

        Assert.Equal(["# ModA", "// ModB"], names);
    }

    [Fact]
    public void Inline_trailing_comments_stay_part_of_the_name()
    {
        // No inline-comment stripping: the loader reads whole lines.
        var names = ModLoadOrderParser.Parse("ModA -- note");

        Assert.Equal(["ModA -- note"], names);
    }
}
