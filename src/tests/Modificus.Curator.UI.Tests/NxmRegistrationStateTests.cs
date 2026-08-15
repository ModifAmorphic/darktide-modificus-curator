using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.Nxm;
using Modificus.Curator.UI.Session;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The production <see cref="NxmRegistrationState"/> contract: availability,
/// the registrar read on refresh, a probe throw treated as not-registered, the
/// unconditional publish, and the UI-thread marshal of <see cref="INxmRegistrationState.Changed"/>.
/// </summary>
public sealed class NxmRegistrationStateTests
{
    [Fact]
    public void Unavailable_without_a_registrar_and_refresh_still_publishes()
    {
        var published = 0;
        var state = new NxmRegistrationState(
            null, static action => action(), NullLogger<NxmRegistrationState>.Instance);
        state.Changed += () => published++;

        Assert.False(state.IsAvailable);
        Assert.False(state.IsRegistered);

        state.RefreshFromOs();

        Assert.False(state.IsAvailable);
        Assert.False(state.IsRegistered);
        Assert.Equal(1, published);
    }

    [Fact]
    public void RefreshFromOs_reads_the_registrar_and_publishes()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var published = 0;
        var state = new NxmRegistrationState(
            registrar, static action => action(), NullLogger<NxmRegistrationState>.Instance);
        state.Changed += () => published++;

        Assert.True(state.IsAvailable);
        Assert.False(state.IsRegistered); // last-known starts false

        state.RefreshFromOs();

        Assert.True(state.IsRegistered);
        Assert.Equal(1, registrar.IsRegisteredCalls);
        Assert.Equal(1, published);

        registrar.Registered = false;
        state.RefreshFromOs();

        Assert.False(state.IsRegistered);
        Assert.Equal(2, registrar.IsRegisteredCalls);
        Assert.Equal(2, published);
    }

    [Fact]
    public void RefreshFromOs_treats_a_probe_throw_as_not_registered()
    {
        var published = 0;
        var state = new NxmRegistrationState(
            new ThrowingRegistrar(), static action => action(), NullLogger<NxmRegistrationState>.Instance);
        state.Changed += () => published++;

        state.RefreshFromOs();

        Assert.True(state.IsAvailable);
        Assert.False(state.IsRegistered);
        // The publish still fires so every consumer re-syncs after the probe.
        Assert.Equal(1, published);
    }

    [Fact]
    public void Changed_is_marshaled_through_the_ui_seam()
    {
        var actions = new List<Action>();
        var published = 0;
        var state = new NxmRegistrationState(
            new FakeNxmHandlerRegistrar(), action => actions.Add(action),
            NullLogger<NxmRegistrationState>.Instance);
        state.Changed += () => published++;

        state.RefreshFromOs();

        // The publish is captured, not run: only the seam's invocation lands
        // on the (simulated) UI thread.
        Assert.Equal(0, published);
        var marshaled = Assert.Single(actions);
        marshaled();
        Assert.Equal(1, published);
    }

    /// <summary>
    /// A registrar whose probe always throws (the real platform registrars
    /// catch their own common exceptions; this exercises the state's defensive
    /// catch).
    /// </summary>
    private sealed class ThrowingRegistrar : INxmHandlerRegistrar
    {
        public bool IsRegistered() => throw new InvalidOperationException("probe failed");

        public void Register() => throw new NotImplementedException();

        public void Unregister() => throw new NotImplementedException();

        public void MaintainRegistration() => throw new NotImplementedException();
    }
}
