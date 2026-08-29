namespace Modificus.Curator.Profiles;

/// <summary>
/// Clones a persisted profile into an independent copy.
/// </summary>
public interface IProfileCloner
{
    /// <summary>
    /// Persists and returns a copy of the profile identified by
    /// <paramref name="sourceProfileId"/>. The copy gets a new id, a new
    /// creation timestamp, and a generated name: the source's copy-family base
    /// name (its name minus a trailing canonical <c> (Copy N)</c> suffix, if
    /// any; recognition is case-insensitive) followed by <c> (Copy N)</c>,
    /// where N is one above the highest existing copy number in that family
    /// (never reusing a gap while a higher number exists) and the result never
    /// equals an existing readable profile name (case-insensitive). The copy
    /// carries the source's description, complete mod membership (enabled
    /// state, order, order locks, and version policies including pinned
    /// version ids), and launch settings, and is independently editable;
    /// generated state such as the staged tree is rebuilt by ordinary use
    /// rather than copied.
    /// </summary>
    /// <remarks>
    /// Does not raise <see cref="IProfileService.ProfileCreated"/>: that event
    /// is the blank-profile creation signal, and a clone is not a blank
    /// profile.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="sourceProfileId"/>
    /// is unknown; nothing is created.</exception>
    Profile CloneProfile(Guid sourceProfileId);
}
