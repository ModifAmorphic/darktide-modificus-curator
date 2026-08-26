namespace Modificus.Curator.Profiles;

/// <summary>
/// Parses a DML/DMF-world <c>mod_load_order.txt</c> into the ordered list of
/// mod folder names it lists. Pure (text in, names out; no IO), mirroring the
/// format owner's reader exactly rather than inventing tolerance: the
/// Darktide Mod Loader's <c>mod_manager.lua</c> reads the file through DMF's
/// <c>read_content_to_table</c>, which trims each line, skips empty lines,
/// and skips lines starting with <c>--</c> (single-line Lua comments). Nothing
/// else is special.
/// </summary>
/// <remarks>
/// <para><b>Deliberately NOT supported</b> (the loader does not support them
/// either): <c>#</c> comments, <c>//</c> comments, and inline trailing
/// comments. A line like <c>MyMod -- note</c> is a folder name with that
/// literal text, exactly as the loader would treat it.</para>
/// <para><b>Duplicates:</b> deduplicated first-wins. The loader's table read
/// leaves duplicate handling to its caller, and first-wins keeps a repeated
/// line from double-listing a mod without changing which mod it names.</para>
/// <para><b>BOM tolerance:</b> a leading <c>U+FEFF</c> is stripped once, so a
/// file decoded with its BOM retained still yields a clean first name (the
/// BOM code point is not whitespace to <see cref="string.Trim"/>, so it would
/// otherwise prefix the first line forever).</para>
/// <para><b>Empty result is valid:</b> a comment-only or blank-only file
/// imports nothing and reports nothing; the caller decides how to present
/// that.</para>
/// </remarks>
public static class ModLoadOrderParser
{
    /// <summary>
    /// Parses <paramref name="text"/> into the ordered, deduplicated list of
    /// mod folder names it lists.
    /// </summary>
    /// <param name="text">The raw file contents (any newline style; a leading
    /// BOM is tolerated).</param>
    public static IReadOnlyList<string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // BOM tolerance: strip exactly one leading U+FEFF (it is not
        // whitespace, so Trim would leave it on the first name).
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        if (text.Length == 0)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            // The reader trims first, THEN decides: whitespace before a
            // comment marker still comments the line, and a line that is
            // whitespace-only is empty.
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            // Duplicate names: first occurrence wins.
            if (seen.Add(line))
            {
                names.Add(line);
            }
        }

        return names;
    }
}
