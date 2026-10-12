namespace Orynivo.Library;

/// <summary>Serializes in-process image mutations per canonical artist-cache identity.</summary>
internal static class ArtistImageMutationGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Gets the number of identities with active or waiting mutations.</summary>
    internal static int ActiveIdentityCount { get { lock (Sync) return Entries.Count; } }

    /// <summary>Reserves exclusive mutation ownership without retaining idle identities.</summary>
    /// <param name="identity">Canonical data-directory and artist identity; never logged or persisted.</param>
    /// <param name="cancellationToken">Cancellation while waiting for mutation admission.</param>
    /// <returns>A disposable exclusive lease.</returns>
    internal static async Task<IDisposable> EnterAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(identity, out entry!)) Entries.Add(identity, entry = new Entry());
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(identity, entry);
        }
        catch
        {
            Release(identity, entry, false);
            throw;
        }
    }

    /// <summary>Releases a reservation and removes the idle gate under the registry lock.</summary>
    /// <param name="identity">The reserved identity.</param>
    /// <param name="entry">The reserved gate.</param>
    /// <param name="ownsGate">Whether the reservation acquired exclusive ownership.</param>
    private static void Release(string identity, Entry entry, bool ownsGate)
    {
        lock (Sync)
        {
            if (ownsGate) entry.Gate.Release();
            if (--entry.Users != 0) return;
            Entries.Remove(identity);
            entry.Gate.Dispose();
        }
    }

    /// <summary>Retains one gate and its active/waiting reservation count.</summary>
    private sealed class Entry
    {
        /// <summary>Owns exclusive mutation admission for this identity.</summary>
        internal readonly SemaphoreSlim Gate = new(1, 1);
        /// <summary>Counts active and waiting reservations under the registry lock.</summary>
        internal int Users;
    }

    /// <summary>Releases exclusive ownership exactly once.</summary>
    /// <param name="identity">The canonical reserved identity.</param>
    /// <param name="entry">The gate owned by this lease.</param>
    private sealed class Lease(string identity, Entry entry) : IDisposable
    {
        private Entry? _entry = entry;

        /// <summary>Releases the mutation and removes its final reservation.</summary>
        public void Dispose()
        {
            var reserved = Interlocked.Exchange(ref _entry, null);
            if (reserved is not null) Release(identity, reserved, true);
        }
    }
}
