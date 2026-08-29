namespace Modificus.Curator.Mods;

/// <summary>
/// Thrown by <see cref="IModRepository.EditImportDetails"/> when an identity
/// change would remove older versions and the caller did not pass
/// <c>removeOlderVersions: true</c>. A distinct type so programmatic callers
/// (the edit dialog's recover path, the load-order association batch) can
/// catch exactly this guard while the other refusals (the FileId lock, the
/// duplicate-identity guard, the tag collision) stay plain
/// <see cref="InvalidOperationException"/>s. Derives from
/// <see cref="InvalidOperationException"/> so existing coarse catches keep
/// working.
/// </summary>
public sealed class RemovalConfirmationRequiredException : InvalidOperationException
{
    /// <param name="message">The refusal detail (surfaced inline by the edit
    /// dialog when it is not recovered).</param>
    public RemovalConfirmationRequiredException(string message) : base(message)
    {
    }
}
