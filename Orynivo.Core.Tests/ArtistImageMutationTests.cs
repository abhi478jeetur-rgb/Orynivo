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

    /// <summary>Automatic content retains provider MIME and URL-extension fallback through staging.</summary>
    /// <param name="mediaType">Provider content type.</param>
    /// <param name="url">Synthetic format fallback URL.</param>
    /// <param name="extension">Expected cached extension.</param>
    /// <returns>A task completing format verification.</returns>
    [Theory]
    [InlineData("image/png", "https://fixture.invalid/a.jpg", ".png")]
    [InlineData("image/webp", "https://fixture.invalid/a.jpg", ".webp")]
    [InlineData("image/jpeg", "https://fixture.invalid/a.png", ".jpg")]
    [InlineData("image/gif", "https://fixture.invalid/a.png", ".jpg")]
    [InlineData("application/octet-stream", "https://fixture.invalid/a.webp?ignored=1", ".webp")]
    [InlineData(null, "https://fixture.invalid/a.PNG#ignored", ".png")]
    [InlineData(null, "https://fixture.invalid/a.jpeg", ".jpg")]
    public async Task DownloadedContentRetainsFormatFallback(string? mediaType, string url, string extension)
    {
        var id = Interlocked.Add(ref _nextId, 2);
        using var content = new ByteArrayContent([4, 5, 6]);
        if (mediaType is not null) content.Headers.ContentType = new(mediaType);
        var path = await ArtistProfileService.SaveDownloadedImageAsync(id, content, url, default);
        Assert.Equal(extension, Path.GetExtension(path));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(path!));
        await ArtistImageSearchService.DeleteImageAsync(id);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Unsupported automatic content never changes the existing image.</summary>
    /// <returns>A task completing rejection verification.</returns>
    [Fact]
    public async Task UnsupportedDownloadedContentPreservesExistingImage()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var path = await ArtistImageSearchService.SaveImageAsync(id, [7, 8], "image/png");
        using var content = new ByteArrayContent([9]);
        content.Headers.ContentType = new("application/octet-stream");
        Assert.Null(await ArtistProfileService.SaveDownloadedImageAsync(id, content, "https://fixture.invalid/no-format", default));
        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(path));
        await ArtistImageSearchService.DeleteImageAsync(id);
    }

    /// <summary>Held automatic streaming shares admission with subsequent manual save and deletion.</summary>
    /// <param name="delete">Whether the subsequent mutation deletes instead of replaces the image.</param>
    /// <returns>A task completing file ordering verification.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticStreamingSharesManualMutationAdmission(bool delete)
    {
        var id = Interlocked.Add(ref _nextId, 2);
        using var source = new ControlledImageStream();
        using var content = new StreamContent(source);
        content.Headers.ContentType = new("image/jpeg");
        var automatic = ArtistProfileService.SaveDownloadedImageAsync(id, content, "https://fixture.invalid/image", default);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task subsequent = delete ? ArtistImageSearchService.DeleteImageAsync(id)
            : ArtistImageSearchService.SaveImageAsync(id, [9, 8], "image/png");
        Assert.False(subsequent.IsCompleted);
        source.Finish.SetResult();
        var automaticPath = await automatic.WaitAsync(TimeSpan.FromSeconds(5));
        await subsequent.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(automaticPath));
        var variants = Directory.GetFiles(Path.GetDirectoryName(automaticPath!)!, id + ".*");
        if (delete) Assert.Empty(variants);
        else
        {
            var finalPath = Assert.Single(variants);
            Assert.Equal(".png", Path.GetExtension(finalPath));
            Assert.Equal(new byte[] { 9, 8 }, await File.ReadAllBytesAsync(finalPath));
        }
        await ArtistImageSearchService.DeleteImageAsync(id);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Partial copies preserve the previous image on cancellation or transfer failure.</summary>
    /// <param name="cancel">Whether the partial transfer cancels rather than fails.</param>
    /// <returns>A task completing preservation and staging cleanup verification.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialAutomaticCopyPreservesPreviousImage(bool cancel)
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var path = await ArtistImageSearchService.SaveImageAsync(id, [7, 8, 9], "image/png");
        using var source = new ControlledImageStream { Fail = !cancel };
        using var content = new StreamContent(source);
        content.Headers.ContentType = new("image/png");
        using var cancellation = new CancellationTokenSource();
        var automatic = ArtistProfileService.SaveDownloadedImageAsync(id, content, "https://fixture.invalid/image", cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
        if (cancel) cancellation.Cancel(); else source.Finish.SetResult();
        var error = await Record.ExceptionAsync(() => automatic);
        Assert.True(cancel ? error is OperationCanceledException : error is IOException);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!, id + ".*"));
        await ArtistImageSearchService.DeleteImageAsync(id);
        Assert.Equal(0, ArtistImageMutationGate.ActiveIdentityCount);
    }

    /// <summary>Empty automatic streams leave the current image and no staging file.</summary>
    /// <returns>A task completing empty-content rejection verification.</returns>
    [Fact]
    public async Task EmptyAutomaticContentPreservesExistingImage()
    {
        var id = Interlocked.Add(ref _nextId, 2);
        var path = await ArtistImageSearchService.SaveImageAsync(id, [7, 8], "image/png");
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new("image/png");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArtistProfileService.SaveDownloadedImageAsync(id, content, "https://fixture.invalid/image", default));
        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!, id + ".*"));
        await ArtistImageSearchService.DeleteImageAsync(id);
    }

    /// <summary>Stages partial bytes before a deterministic completion, failure or cancellation.</summary>
    private sealed class ControlledImageStream : MemoryStream
    {
        /// <summary>Signals partial staging.</summary>
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Controls transfer completion without sleeping.</summary>
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets whether completion fails after partial staging.</summary>
        internal bool Fail { get; init; }
        /// <summary>Copies partial bytes and waits for the controlled outcome.</summary>
        /// <param name="destination">The actual staging file stream.</param>
        /// <param name="bufferSize">Unused copy buffer size.</param>
        /// <param name="cancellationToken">Cancellation during transfer.</param>
        /// <returns>A task completing the synthetic transfer.</returns>
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2 }, cancellationToken);
            Started.SetResult();
            await Finish.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("Synthetic partial image transfer failure");
            await destination.WriteAsync(new byte[] { 3 }, cancellationToken);
        }
    }
}
