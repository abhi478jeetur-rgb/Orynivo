namespace Orynivo;

/// <summary>Tracks dispatcher-owned view load publication without owning navigation history or cancellation sources.</summary>
internal sealed class NavigationLoadOwnership
{
    private int _version;

    /// <summary>Admits a load before cancellation callbacks can admit a replacement.</summary>
    /// <param name="token">Cancellation owned by the window adapter.</param>
    /// <returns>Captured ownership for asynchronous publication checks.</returns>
    internal Request Begin(CancellationToken token) => new(this, ++_version, token);

    /// <summary>Observes current ownership without replacing an admitted load.</summary>
    /// <returns>A non-cancelling observation rejected by later admission or invalidation.</returns>
    internal Request CaptureCurrent() => new(this, _version, CancellationToken.None);

    /// <summary>Rejects previous loads when navigation leaves their view.</summary>
    internal void Invalidate() => ++_version;

    /// <summary>Checks request identity and cancellation before UI publication.</summary>
    /// <param name="request">Captured load ownership.</param>
    /// <returns>Whether the uncancelled request still owns this view loader.</returns>
    internal bool IsCurrent(Request request) => ReferenceEquals(request.Owner, this) &&
        request.Version == _version && !request.Token.IsCancellationRequested;

    /// <summary>Captures only load identity and cancellation, never rows or source credentials.</summary>
    /// <param name="Owner">Owner that admitted the request.</param>
    /// <param name="Version">Admitted view load version.</param>
    /// <param name="Token">Window-owned cancellation.</param>
    internal sealed record Request(NavigationLoadOwnership Owner, int Version, CancellationToken Token);
}
