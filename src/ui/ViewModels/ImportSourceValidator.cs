using Modificus.Curator.Mods;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The source provenance choices offered by the mod-source field: the inline
/// import card's editing form and the edit-import-details dialog. Untracked is
/// a local import (no remote identity, no version); Nexus collects a URL or
/// bare mod id parsed to a canonical identity.
/// </summary>
public enum ImportSource
{
    /// <summary>Untracked import: no remote identity, no version.</summary>
    Untracked,

    /// <summary>Nexus Mods: collects a URL or bare mod id parsed to a mod id.</summary>
    Nexus,
}

/// <summary>
/// The shared validation rules for the mod-source form fields (the import
/// card + the edit-import-details dialog): URL/id parsing and the
/// Nexus-requires-a-version rule. One definition so the two surfaces validate
/// identically; pure + static, no localization (the surfaces own their
/// localized messages).
/// </summary>
public static class ImportSourceValidator
{
    /// <summary>
    /// Parses the URL/id for the chosen source into a canonical
    /// <see cref="ModSource"/>. Never throws. Nexus accepts a bare positive
    /// integer or a Darktide Nexus mod URL (see <see cref="ModSourceParser"/>);
    /// Untracked carries no remote field and never parses here.
    /// </summary>
    public static bool TryParseUrl(ImportSource source, string url, out ModSource parsed)
    {
        parsed = new UntrackedSource();
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        switch (source)
        {
            case ImportSource.Nexus:
                if (ModSourceParser.TryParseNexus(url, out var nexus))
                {
                    parsed = nexus;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the remote-source fields are valid for saving: a Nexus choice
    /// needs a non-whitespace version AND a URL/id that parses. Untracked is
    /// always valid (it carries no remote fields).
    /// </summary>
    public static bool IsRemoteSourceValid(ImportSource source, string url, string version)
    {
        if (source != ImportSource.Nexus)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(version)
            && TryParseUrl(source, url ?? string.Empty, out _);
    }
}
