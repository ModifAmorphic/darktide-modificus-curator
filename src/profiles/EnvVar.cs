namespace Modificus.Curator.Profiles;

/// <summary>
/// One profile environment-variable entry: an exact name/value pair that a
/// profile asks Curator to set on the launch environment. The values flow into
/// the process that starts Proton (on Linux) or the Relay launcher (on
/// Windows), and from there into the game.
/// </summary>
/// <remarks>
/// <para>
/// Stored as an ordered entry (not a dictionary key) so duplicate-name
/// detection happens in <see cref="IProfileService.UpdateProfile"/>
/// validation, not as silent dictionary collapse at the storage boundary. The
/// pair is stored exactly: values may carry spaces or be empty, and a name is
/// stored verbatim (case is preserved on save; duplicate detection is
/// case-insensitive for profile portability between Windows and Linux).</para>
/// <para>
/// Profile files are plaintext, so this is not secret storage. Logs must never
/// print environment values; a log line may carry the profile id and counts or
/// names where genuinely useful, never the values.</para>
/// </remarks>
/// <param name="Name">The environment-variable name. Validated (non-empty, no
/// <c>=</c>, no NUL, not a reserved name, unique case-insensitively) at
/// <see cref="IProfileService.UpdateProfile"/>.</param>
/// <param name="Value">The environment-variable value, stored exactly
/// (spaces + empty values preserved; NUL rejected at
/// <see cref="IProfileService.UpdateProfile"/>).</param>
public sealed record EnvVar(string Name, string Value);
