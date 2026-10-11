namespace Orynivo;

/// <summary>Distinguishes nested Back work from independent navigation without owning history or loaders.</summary>
internal sealed class NavigationRestoreOwnership
{
    private readonly NavigationLoadOwnership _loads = new();
    private readonly AsyncLocal<NavigationLoadOwnership.Request?> _context = new();

    /// <summary>Gets or sets the restore context flowing through the current asynchronous call chain.</summary>
    internal NavigationLoadOwnership.Request? Context
    {
        get => _context.Value;
        set => _context.Value = value;
    }

    /// <summary>Gets whether the latest restore is still executing.</summary>
    internal bool IsRestoring { get; private set; }

    /// <summary>Gets whether this call belongs to the current restore and must avoid capturing history.</summary>
    internal bool ShouldSuppressHistory => Context is { } request && IsCurrent(request);

    /// <summary>Admits a replacement restore before any nested loader is invoked.</summary>
    /// <returns>Identity retained by cleanup and delayed UI restoration.</returns>
    internal NavigationLoadOwnership.Request Begin()
    {
        IsRestoring = true;
        return _loads.Begin(CancellationToken.None);
    }

    /// <summary>Checks whether a captured restore still owns delayed work.</summary>
    /// <param name="request">Captured restore identity.</param>
    /// <returns>Whether no replacement or independent navigation has abandoned it.</returns>
    internal bool IsCurrent(NavigationLoadOwnership.Request request) => _loads.IsCurrent(request);

    /// <summary>Ends only the executing flag of the current restore, retaining ownership for layout callbacks.</summary>
    /// <param name="request">Restore whose asynchronous execution has completed.</param>
    internal void Complete(NavigationLoadOwnership.Request request)
    {
        if (IsCurrent(request)) IsRestoring = false;
    }

    /// <summary>Abandons restore publication and clears its executing flag.</summary>
    internal void Invalidate()
    {
        _loads.Invalidate();
        IsRestoring = false;
    }

    /// <summary>Preserves current nested restore work, rejects obsolete continuations, and admits independent navigation.</summary>
    /// <returns>Whether the caller may start navigation work.</returns>
    internal bool TryAdmitNavigation()
    {
        if (Context is { } request) return IsCurrent(request);
        Invalidate();
        return true;
    }
}
