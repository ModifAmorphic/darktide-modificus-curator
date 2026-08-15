using Microsoft.Extensions.Logging;
using Modificus.Curator.Nxm;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The shared, last-known state of the OS <c>nxm://</c> handler registration
/// consumed by every UI surface (shell status strip, Mods empty-state hint,
/// Nexus destination, DMF prompt). The OS association is inherently racy (any
/// other manager can claim it at any time), so consumers read last-known state
/// and accept staleness. Only deliberate points refresh it: one seed probe at
/// shell construction, one probe on entering the Nexus destination, and one
/// probe after each register/release action, each publishing
/// <see cref="Changed"/> so every surface updates together.
/// </summary>
public interface INxmRegistrationState
{
    /// <summary>Whether a platform registrar exists (Windows or Linux).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Last-known registration; false when unknown or no registrar exists.
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>Raised on the UI thread after any refresh.</summary>
    event Action? Changed;

    /// <summary>
    /// Synchronous bounded probe of the OS registration; the only writer.
    /// A probe throw is caught, logged, and treated as not-registered. Even
    /// with no registrar (or a failing probe) the refresh still publishes
    /// <see cref="Changed"/> (marshaled to the UI thread), so every consumer
    /// re-syncs after each refresh request.
    /// </summary>
    void RefreshFromOs();
}

/// <summary>
/// The production <see cref="INxmRegistrationState"/>: an application-lifetime
/// singleton wrapping the optional platform <see cref="INxmHandlerRegistrar"/>.
/// All callers are on the UI thread and synchronous, so the marshal seam below
/// is defensive only (it mirrors the other session services' event publishing).
/// </summary>
public sealed class NxmRegistrationState : INxmRegistrationState
{
    private readonly INxmHandlerRegistrar? _registrar;
    private readonly Action<Action> _invokeOnUi;
    private readonly ILogger<NxmRegistrationState> _logger;

    public NxmRegistrationState(
        INxmHandlerRegistrar? nxmRegistrar,
        Action<Action> invokeOnUi,
        ILogger<NxmRegistrationState> logger)
    {
        _registrar = nxmRegistrar;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsAvailable => _registrar is not null;

    /// <inheritdoc />
    public bool IsRegistered { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void RefreshFromOs()
    {
        if (_registrar is not null)
        {
            try
            {
                IsRegistered = _registrar.IsRegistered();
            }
            catch (Exception ex)
            {
                // The platform registrars catch their own probe exceptions;
                // this is defensive. Treat a throw as not-registered so the
                // user can retry the register path.
                _logger.LogWarning(ex, "nxm IsRegistered probe threw; treating as not registered.");
                IsRegistered = false;
            }
        }

        _invokeOnUi(() => Changed?.Invoke());
    }
}
