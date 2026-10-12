using Orynivo.Library;
using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Verifies serialized artist-cache mutation, cancellation and staging cleanup.</summary>
public sealed class ArtistImageMutationTests
{
    private static long _nextId = 1_000_000;

    /// <summary>Same identities wait while unrelated identities remain independent and idle gates are removed.</summary>
    [Fact]
    public async Task ReservationsSerializePerIdentityAndReleaseIdleEntries()
    {
        var identity = Guid.NewGuid().ToString();
        using var first = await ArtistImageMutationGate.EnterAsync(identity, default);
        var waiting = ArtistImageMutationGate.EnterAsync(identity, default);
        Assert.False(waiting.IsCompleted);
        using (await ArtistImageMutationGate.EnterAsync(identity + "-other", default).WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(2, ArtistImageMutationGate.ActiveIdentityCount);
        first.Dispose();
        first.Dispose();
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(1, ArtistImageMutationGate.ActiveIdentityCount);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Cancelling a waiter neither releases the active writer nor retains an idle reservation.</summary>
    [Fact]
    public async Task CancelledWaiterPreservesActiveOwnership()
    {
        var identity = Guid.NewGuid().ToString();
        using var first = await ArtistImageMutationGate.EnterAsync(identity, default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = ArtistImageMutationGate.EnterAsync(identity, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = ArtistImageMutationGate.EnterAsync(identity, default);
        Assert.False(next.IsCompleted);
        first.Dispose();
        using (await next.WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Real save and deletion paths share admission and preserve unrelated artist files.</summary>
    [Fact]
    public async Task SaveAndDeleteShareArtistDirectoryAdmission()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var directory = Path.GetFullPath(Orynivo.AppPaths.GetDataPath("artist-images"));
        var identity = Path.Combine(directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var first = await ArtistImageMutationGate.EnterAsync(identity, default);
        var save = ArtistImageSearchService.SaveImageAsync(id, [1, 2, 3], "image/png");
        Assert.False(save.IsCompleted);
        var otherPath = await ArtistImageSearchService.SaveImageAsync(id + 1, [7, 8, 9], "image/webp").WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
        var path = await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        using var deletionOwner = await ArtistImageMutationGate.EnterAsync(identity, default);
        var deletion = ArtistImageSearchService.DeleteImageAsync(id);
        Assert.False(deletion.IsCompleted);
        Assert.True(File.Exists(path));
        deletionOwner.Dispose();
        await deletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(otherPath));
        await ArtistImageSearchService.DeleteImageAsync(id + 1);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Failed publication cleans its unique staging file and preserves the previous variant.</summary>
    [Fact]
    public async Task FailedPublicationPreservesExistingVariantAndCleansStaging()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var previous = await ArtistImageSearchService.SaveImageAsync(id, [4, 5], "image/jpeg");
        var obstructedTarget = Path.ChangeExtension(previous, ".png");
        Directory.CreateDirectory(obstructedTarget);
        try
        {
            var error = await Record.ExceptionAsync(() => ArtistImageSearchService.SaveImageAsync(id, [6], "image/png"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(previous));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(previous)!, id + ".*.tmp"));
        }
        finally { Directory.Delete(obstructedTarget); await ArtistImageSearchService.DeleteImageAsync(id); }
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Cancellation while awaiting save/delete admission preserves the current image.</summary>
    [Fact]
    public async Task CancelledServiceWaitersLeaveCurrentImageUntouched()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var path = await ArtistImageSearchService.SaveImageAsync(id, [8, 9], "image/png");
        var identity = Path.Combine(Path.GetDirectoryName(path)!, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var owner = await ArtistImageMutationGate.EnterAsync(identity, default);
        using var cancellation = new CancellationTokenSource();
        var save = ArtistImageSearchService.SaveImageAsync(id, [1], "image/jpeg", cancellation.Token);
        var deletion = ArtistImageSearchService.DeleteImageAsync(id, cancellation.Token);
        Assert.False(save.IsCompleted);
        Assert.False(deletion.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, id + ".*.tmp"));
        owner.Dispose();
        await ArtistImageSearchService.DeleteImageAsync(id);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Successful format replacement leaves one variant and the synchronous deletion API remains usable.</summary>
    [Fact]
    public async Task FormatReplacementAndLegacyDeletionRetainExistingContract()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var previous = await ArtistImageSearchService.SaveImageAsync(id, [1], "image/jpeg");
        var current = await ArtistImageSearchService.SaveImageAsync(id, [2, 3], "image/png");
        Assert.False(File.Exists(previous));
        Assert.Equal(new byte[] { 2, 3 }, await File.ReadAllBytesAsync(current));
        Assert.Equal([current], Directory.GetFiles(Path.GetDirectoryName(current)!, id + ".*"));
        await Task.Run(() => ArtistImageSearchService.DeleteImage(id));
        Assert.False(File.Exists(current));
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }
}
