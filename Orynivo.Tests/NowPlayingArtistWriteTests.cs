using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Orynivo.Library;
using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Verifies source-owned Now Playing profile writes with offline transport and download adapters.</summary>
public sealed class NowPlayingArtistWriteTests
{
    /// <summary>Downloads use source-owned cache IDs and preserve authoritative manual-image policy.</summary>
    /// <param name="manual">Whether the server retains a manual image.</param>
    /// <returns>A task completing policy and cache-identity verification.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadUsesSourceIdentityAndManualPolicy(bool manual)
    {
        using var handler = new ProfileHandler { Manual = manual };
        using var client = CreateClient(handler);
        var server = Source();
        var provider = new OrynivoServerNowPlayingMetadataProvider(server, client);
        long? cacheId = null;
        bool? policy = null;
        var result = await provider.GetArtistProfileForViewAsync(new(701, "Artist"), "en", true,
            default, () => true, () => true, write => write(), (id, allow) =>
            {
                cacheId = id; policy = allow;
                return Task.FromResult<ArtistProfileDownload?>(new("Downloaded biography", null, "https://fixture.invalid/biography", "en"));
            }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerArtistProfileCacheIdentity.Create(server.Id, 701), cacheId);
        Assert.True(cacheId < 0);
        Assert.Equal(!manual, policy);
        Assert.Equal("Downloaded biography", result?.Biography);
        Assert.Equal(manual, result?.ImageIsManual);
        Assert.Equal(1, handler.Uploads);
    }

    /// <summary>Queued profile writes refresh manual-image state after the preceding batch finishes.</summary>
    /// <returns>A task completing queued authoritative-state verification.</returns>
    [Fact]
    public async Task WaitingProviderRefreshesManualImageState()
    {
        using var handler = new ProfileHandler();
        using var client = CreateClient(handler);
        var provider = new OrynivoServerNowPlayingMetadataProvider(Source(), client);
        var queue = new ArtistImageWriteQueue();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync("artist", () => true, async () => { await held.Task; return true; });
        bool? policy = null;
        var pending = provider.GetArtistProfileForViewAsync(new(701, "Artist"), "en", true,
            default, () => true, () => true, write =>
            {
                var task = queue.RunAsync("artist", () => true, async () => { await write(); return true; });
                queued.SetResult(); return task;
            }, (_, allow) => { policy = allow; return Task.FromResult<ArtistProfileDownload?>(new("Biography", null, "https://fixture.invalid/biography", "en")); });
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(policy);
        handler.Manual = true;
        held.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(policy);
        Assert.True(result?.ImageIsManual);
        Assert.Equal(2, handler.Reads);
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>Superseded cached images preserve biography without invoking a write adapter or image transport.</summary>
    /// <returns>A task completing metadata-only publication verification.</returns>
    [Fact]
    public async Task SupersededImageRetainsCachedBiographyWithoutWrites()
    {
        using var handler = new ProfileHandler { HasImage = true };
        using var client = CreateClient(handler);
        var provider = new OrynivoServerNowPlayingMetadataProvider(Source(), client);
        var result = await provider.GetArtistProfileForViewAsync(new(701, "Artist"), "en", false,
            default, () => true, () => false, _ => throw new Exception("Unexpected image write admission"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Cached biography", result?.Biography);
        Assert.Null(result?.ImagePath);
        Assert.Equal(1, handler.Reads);
        Assert.Equal(0, handler.Uploads);
    }

    /// <summary>An admitted primary upload completes before a later manual batch starts.</summary>
    /// <returns>A task completing actual provider/upload ordering verification.</returns>
    [Fact]
    public async Task AdmittedUploadPrecedesLaterManualBatch()
    {
        using var handler = new ProfileHandler { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = CreateClient(handler);
        var provider = new OrynivoServerNowPlayingMetadataProvider(Source(), client);
        var queue = new ArtistImageWriteQueue();
        var pending = provider.GetArtistProfileForViewAsync(new(701, "Artist"), "en", true,
            default, () => true, () => true,
            write => queue.RunAsync("artist", () => true, async () => { await write(); return true; }),
            (_, _) => Task.FromResult<ArtistProfileDownload?>(new("Biography", null, "https://fixture.invalid/biography", "en")));
        await handler.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manualRan = false;
        var manual = queue.RunAsync("artist", () => true, () => { manualRan = true; return Task.FromResult(true); });
        Assert.False(manualRan);
        Assert.False(manual.IsCompleted);
        handler.UploadGate.SetResult();
        Assert.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await manual.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(manualRan);
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>Abandoned queued work never starts a download, fresh lookup or profile upload.</summary>
    /// <param name="change">The ownership change while waiting.</param>
    /// <returns>A task completing rejected admission verification.</returns>
    [Theory]
    [InlineData("view")]
    [InlineData("source")]
    [InlineData("cancel")]
    public async Task WaitingProviderRejectsAbandonedOwnership(string change)
    {
        using var handler = new ProfileHandler();
        using var client = CreateClient(handler);
        var server = Source();
        var provider = new OrynivoServerNowPlayingMetadataProvider(server, client);
        var queue = new ArtistImageWriteQueue();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync("artist", () => true, async () => { await held.Task; return true; });
        var owner = true;
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetArtistProfileForViewAsync(new(701, "Artist"), "en", true,
            cancellation.Token, () => owner, () => true, write =>
            {
                var task = queue.RunAsync("artist", () => owner && !cancellation.IsCancellationRequested,
                    async () => { await write(); return true; });
                queued.SetResult(); return task;
            }, (_, _) => throw new Exception("Obsolete download admitted"));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "view") owner = false;
        if (change == "source") server.ProfileId = "replacement";
        if (change == "cancel") cancellation.Cancel();
        held.SetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Reads);
        Assert.Equal(0, handler.Uploads);
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>Creates a credential-free synthetic source.</summary>
    /// <returns>The source configuration.</returns>
    private static OrynivoServerSettings Source() => new() { Id = "fixture", BaseUrl = "https://fixture.invalid", ProfileId = "standard" };

    /// <summary>Creates the shared client using its internal offline-transport constructor.</summary>
    /// <param name="handler">Synthetic HTTP transport.</param>
    /// <returns>The caller-owned client.</returns>
    private static OrynivoServerClient CreateClient(HttpMessageHandler handler)
        => (OrynivoServerClient)Activator.CreateInstance(typeof(OrynivoServerClient),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [new HttpClient(handler)], null)!;

    /// <summary>Returns cached profile DTOs and controllable upload completion without network access.</summary>
    private sealed class ProfileHandler : HttpMessageHandler
    {
        /// <summary>Gets or sets authoritative manual-image state.</summary>
        internal bool Manual { get; set; }
        /// <summary>Gets or sets whether cached artwork exists.</summary>
        internal bool HasImage { get; init; }
        /// <summary>Counts profile reads.</summary>
        internal int Reads { get; private set; }
        /// <summary>Counts profile uploads.</summary>
        internal int Uploads { get; private set; }
        /// <summary>Controls admitted upload completion.</summary>
        internal TaskCompletionSource? UploadGate { get; init; }
        /// <summary>Signals upload admission.</summary>
        internal TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Returns a cached profile or awaits the controlled upload.</summary>
        /// <param name="request">The synthetic client request.</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>The synthetic response.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Uploads++; UploadStarted.TrySetResult();
                if (UploadGate is not null) await UploadGate.Task.WaitAsync(cancellationToken);
            }
            else Reads++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OrynivoArtistInfo(701, "Artist", false,
                "Cached biography", null, "en", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, HasImage, Manual)) };
        }
    }
}
