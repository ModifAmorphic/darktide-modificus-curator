using System.Collections.Immutable;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Immutable description of a child process to spawn: the executable path,
/// the argv-form arguments, and the requested environment mutations. Passed
/// to <see cref="IProcessLauncher.Start"/> as a single value so callers can
/// express both additions (overrides) and removals (keys inherited from the
/// parent environment that must not reach the child) without expanding the
/// launcher's parameter list.
/// </summary>
/// <remarks>
/// <para>
/// The collections are snapshotted at construction time into genuinely
/// immutable containers (<see cref="ImmutableArray{T}"/>,
/// <see cref="ImmutableDictionary{TKey, TValue}"/>,
/// <see cref="ImmutableHashSet{T}"/>), so a request cannot be mutated after
/// the caller hands it over (or after a fake records it). Empty collections
/// are exposed instead of <c>null</c> so consumers never null-check. The
/// snapshot containers themselves use ordinal key comparers.</para>
/// <para>
/// <see cref="EnvironmentOverrides"/> are applied AFTER
/// <see cref="EnvironmentVariablesToRemove"/>, so a key listed in both is
/// first dropped from the inherited block and then re-added with the
/// override's value (the override intentionally wins). The snapshot's
/// ordinal matching governs only the request containers; once
/// <see cref="ProcessLauncher"/> writes keys into
/// <see cref="ProcessStartInfo.Environment"/>, that dictionary supplies the
/// platform's own semantics (case-sensitive on Linux, case-insensitive on
/// Windows). The AppImage cleanup request is issued only by
/// <c>LinuxLaunchStrategy</c>, while <c>WindowsLaunchStrategy</c> requests no
/// removals, so that platform comparer difference does not affect this
/// fix.</para>
/// </remarks>
public sealed class ProcessLaunchRequest
{
    /// <summary>The executable to start (path or resolved name).</summary>
    public string FilePath { get; }

    /// <summary>
    /// The full argument list, already in argv form. Each entry is added
    /// verbatim to <see cref="ProcessStartInfo.ArgumentList"/> (no re-shelling,
    /// no concatenation) so paths and values containing spaces or shell
    /// metacharacters survive unchanged. Never <c>null</c>.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Environment-variable overrides applied to the child after removals.
    /// Never <c>null</c>; empty when the child inherits the parent's
    /// environment untouched.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; }

    /// <summary>
    /// Environment-variable names to remove from the inherited parent block
    /// before <see cref="EnvironmentOverrides"/> are applied. Never
    /// <c>null</c>; empty when nothing is removed. Use this to stop an
    /// inherited identity (e.g. AppImage runtime variables) from leaking into
    /// a child that must not inherit it.
    /// </summary>
    public IReadOnlySet<string> EnvironmentVariablesToRemove { get; }

    /// <summary>
    /// Whether to suppress the child's console window. Mirrors
    /// <see cref="ProcessStartInfo.CreateNoWindow"/> (only honored when
    /// <see cref="ProcessStartInfo.UseShellExecute"/> is <c>false</c>, which the
    /// launcher always sets). <c>true</c> hides the window; <c>false</c> leaves
    /// the platform default (a console child shows its console). Has no effect
    /// on the game's own window.
    /// </summary>
    public bool CreateNoWindow { get; }

    /// <summary>
    /// Creates a request. All collections are snapshotted into immutable
    /// containers; <c>null</c> inputs become empty collections.
    /// </summary>
    /// <param name="filePath">The executable to start. Must be non-empty.</param>
    /// <param name="arguments">
    /// The argv-form arguments. May be <c>null</c> (treated as no arguments).
    /// Each entry is added verbatim; a <c>null</c> entry inside the sequence
    /// is coerced to an empty string at spawn time so the argv layout is
    /// preserved.
    /// </param>
    /// <param name="environmentOverrides">
    /// Key/value overrides applied to the child's environment block after
    /// removals. May be <c>null</c> (no overrides).
    /// </param>
    /// <param name="environmentVariablesToRemove">
    /// Inherited environment-variable names to strip before overrides apply.
    /// May be <c>null</c> (nothing removed).
    /// </param>
    /// <param name="createNoWindow">
    /// When <c>true</c>, suppresses the child's console window
    /// (<see cref="CreateNoWindow"/>). Defaults to <c>false</c> so existing
    /// callers keep the prior behavior unless they opt in.
    /// </param>
    public ProcessLaunchRequest(
        string filePath,
        IEnumerable<string>? arguments = null,
        IEnumerable<KeyValuePair<string, string>>? environmentOverrides = null,
        IEnumerable<string>? environmentVariablesToRemove = null,
        bool createNoWindow = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
        Arguments = arguments?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        EnvironmentOverrides = environmentOverrides is null
            ? ImmutableDictionary<string, string>.Empty
            : environmentOverrides.ToImmutableDictionary(StringComparer.Ordinal);
        EnvironmentVariablesToRemove = environmentVariablesToRemove is null
            ? ImmutableHashSet<string>.Empty
            : environmentVariablesToRemove.ToImmutableHashSet(StringComparer.Ordinal);
        CreateNoWindow = createNoWindow;
    }
}
