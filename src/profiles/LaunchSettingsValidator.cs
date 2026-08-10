namespace Modificus.Curator.Profiles;

/// <summary>
/// The kind of launch-settings validation error a single environment-variable
/// entry can carry. Part of the structured, machine-readable result returned by
/// <see cref="LaunchSettingsValidator.Validate"/>; the field (Name vs Value) is
/// encoded in the kind prefix and exposed via
/// <see cref="LaunchSettingsValidationError.Field"/>.
/// </summary>
/// <remarks>
/// Precedence, when more than one applies to a single entry, matches the prior
/// service + UI checks: NameEmpty, then NameInvalid, then NameReserved, then
/// NameDuplicate, then ValueNul (the first applicable kind wins per entry).
/// </remarks>
public enum LaunchSettingsValidationErrorKind
{
    /// <summary>The name is empty or whitespace after trim.</summary>
    NameEmpty,

    /// <summary>The name contains <c>=</c> or a NUL character.</summary>
    NameInvalid,

    /// <summary>The name is in the reserved set
    /// (<see cref="LaunchSettings.ReservedEnvironmentNames"/>, case-insensitive):
    /// Curator sets or supplies it itself.</summary>
    NameReserved,

    /// <summary>The name is a case-insensitive duplicate of another entry (a
    /// profile may not carry two env vars that differ only in case, for
    /// portability between Windows and Linux env semantics).</summary>
    NameDuplicate,

    /// <summary>The value contains a NUL character (it cannot be carried by an
    /// environment block; spaces + empty values are otherwise legal).</summary>
    ValueNul,
}

/// <summary>
/// Which field of an environment-variable entry a
/// <see cref="LaunchSettingsValidationError"/> pertains to. Derived from the
/// kind (Name* kinds -> <see cref="Name"/>; ValueNul -> <see cref="Value"/>).
/// </summary>
public enum LaunchSettingsErrorField
{
    /// <summary>The error is about the entry's name.</summary>
    Name,

    /// <summary>The error is about the entry's value.</summary>
    Value,
}

/// <summary>
/// One structured validation error on a single environment-variable entry,
/// produced by <see cref="LaunchSettingsValidator.Validate"/>. Carries the entry
/// index, the field, the kind, and the offending name (empty for
/// <see cref="LaunchSettingsValidationErrorKind.NameEmpty"/>). Machine-readable
/// so each consumer localizes / reports it its own way; never carries a
/// localized string itself (the Profiles library is backend-only).
/// </summary>
public sealed record LaunchSettingsValidationError(
    int Index,
    LaunchSettingsValidationErrorKind Kind,
    string Name)
{
    /// <summary>Which field the error pertains to, derived from <see cref="Kind"/>.</summary>
    public LaunchSettingsErrorField Field => Kind switch
    {
        LaunchSettingsValidationErrorKind.ValueNul => LaunchSettingsErrorField.Value,
        _ => LaunchSettingsErrorField.Name,
    };
}

/// <summary>
/// The single source of truth for launch-settings validation, shared by the
/// authoritative <see cref="IProfileService.UpdateProfile"/> (the trust
/// boundary) and the launch-settings editor (inline per-field feedback). Pure:
/// no localization, no I/O, no side effects. Returns structured errors, not
/// localized strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules:</b> per entry, name non-empty after trim; name contains neither
/// <c>=</c> nor NUL; name not in the reserved set
/// (<see cref="LaunchSettings.ReservedEnvironmentNames"/>, case-insensitive);
/// name not a case-insensitive duplicate of another entry; value contains no
/// NUL. Values are otherwise stored exactly (spaces + empty values preserved).
/// Game arguments are not validated (any string is a legal argv value, and Relay
/// owns the final quoting).</para>
/// <para>
/// <b>Duplicates are reported on every colliding entry</b> (a name that appears
/// more than once case-insensitively), not just the later one, so the UI can
/// flag every row involved in the conflict. The service, which throws on the
/// first error in entry order, surfaces the first colliding entry.</para>
/// <para>
/// <b>Precedence</b> within an entry: NameEmpty, NameInvalid, NameReserved,
/// NameDuplicate, ValueNul (the first applicable kind wins; at most one error
/// per entry). This matches the prior service + UI ordering.</para>
/// </remarks>
public static class LaunchSettingsValidator
{
    /// <summary>
    /// Validates the launch settings and returns every error, one per offending
    /// environment-variable entry, in entry order. An empty result means the
    /// settings are valid.
    /// </summary>
    /// <param name="settings">The settings to validate. Game arguments are
    /// ignored (any string is legal); only the environment-variable entries are
    /// checked.</param>
    /// <returns>Every per-entry error, in entry order; empty when valid. Never
    /// <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is
    /// <c>null</c>.</exception>
    public static IReadOnlyList<LaunchSettingsValidationError> Validate(LaunchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Count non-empty names (trimmed) case-insensitively for duplicate
        // detection across entries. A name collides if it appears more than once;
        // every colliding entry is flagged (see the class remarks).
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.EnvironmentVariables)
        {
            var n = (entry.Name ?? string.Empty).Trim();
            if (n.Length == 0)
            {
                continue;
            }
            nameCounts.TryGetValue(n, out var c);
            nameCounts[n] = c + 1;
        }

        var errors = new List<LaunchSettingsValidationError>();
        for (var i = 0; i < settings.EnvironmentVariables.Count; i++)
        {
            var entry = settings.EnvironmentVariables[i];
            var name = entry.Name ?? string.Empty;

            // Precedence: empty -> invalid (= or NUL) -> reserved -> duplicate ->
            // value-NUL. The first applicable kind wins for this entry.
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new LaunchSettingsValidationError(i, LaunchSettingsValidationErrorKind.NameEmpty, string.Empty));
                continue;
            }

            if (name.IndexOf('=') >= 0 || name.IndexOf('\0') >= 0)
            {
                errors.Add(new LaunchSettingsValidationError(i, LaunchSettingsValidationErrorKind.NameInvalid, name));
                continue;
            }

            if (LaunchSettings.ReservedEnvironmentNames.Contains(name))
            {
                errors.Add(new LaunchSettingsValidationError(i, LaunchSettingsValidationErrorKind.NameReserved, name));
                continue;
            }

            if (nameCounts.TryGetValue(name.Trim(), out var count) && count > 1)
            {
                errors.Add(new LaunchSettingsValidationError(i, LaunchSettingsValidationErrorKind.NameDuplicate, name));
                continue;
            }

            var value = entry.Value ?? string.Empty;
            if (value.IndexOf('\0') >= 0)
            {
                errors.Add(new LaunchSettingsValidationError(i, LaunchSettingsValidationErrorKind.ValueNul, name));
                continue;
            }
        }

        return errors;
    }

    /// <summary>
    /// Whether <paramref name="settings"/> is valid (no errors). Convenience
    /// over <c>Validate(settings).Count == 0</c> for callers that only need the
    /// verdict (e.g. an agreement assertion).
    /// </summary>
    public static bool IsValid(LaunchSettings settings)
        => Validate(settings).Count == 0;
}
