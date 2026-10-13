using System.Net;
using System.Net.Http.Json;
using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Verifies selected album loading and older-server compatibility with synthetic transport.</summary>
public sealed class AlbumBatchClientTests
{
    /// <summary>Modern servers receive only distinct selected IDs with the requested profile.</summary>
    /// <returns>The transport assertion task.</returns>
    [Fact]
    public async Task Batch_IsSelectedAndProfileAware()
    {
        using var handler = new AlbumHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new OrynivoServerClient(http);
        var albums = await client.GetAlbumsByIdsAsync(Server(), [7, 7, 9]);
        Assert.Equal(new long[] { 7, 9 }, handler.Ids);
        Assert.Equal("alpha", handler.Profile);
        Assert.Equal(7, Assert.Single(albums).Id);
        Assert.Equal(0, handler.Details);
    }

    /// <summary>Older servers resolve selected details without requesting a full album catalog.</summary>
    /// <param name="status">Unsupported batch response.</param>
    /// <returns>The compatibility assertion task.</returns>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task OlderServer_UsesSelectedDetails(HttpStatusCode status)
    {
        using var handler = new AlbumHandler(status);
        using var http = new HttpClient(handler);
        var client = new OrynivoServerClient(http);
        var albums = await client.GetAlbumsByIdsAsync(Server(), [7, 9]);
        Assert.Equal(7, Assert.Single(albums).Id);
        Assert.Equal(2, handler.Details);
    }

    /// <summary>Unavailable batch responses cannot be cached as successful empty results.</summary>
    /// <returns>The failure assertion task.</returns>
    [Fact]
    public async Task Failure_PropagatesWithoutFallback()
    {
        using var handler = new AlbumHandler(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var client = new OrynivoServerClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAlbumsByIdsAsync(Server(), [7]));
        Assert.Equal(0, handler.Details);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetAlbumsByIdsAsync(Server(), Enumerable.Range(1, 101).Select(id => (long)id).ToArray()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAlbumsByIdsAsync(Server(), [7], cancelled.Token));
    }

    /// <summary>Creates synthetic source settings with a non-default profile.</summary>
    /// <returns>In-memory settings.</returns>
    private static OrynivoServerSettings Server() => new()
    { Id = "batch", BaseUrl = "http://batch.invalid", ApiKey = "synthetic", ProfileId = "alpha" };

    /// <summary>Rejects any complete-catalog request and supplies selected synthetic albums.</summary>
    /// <param name="status">Batch status.</param>
    private sealed class AlbumHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _details;
        /// <summary>Gets selected identifiers sent to the batch endpoint.</summary>
        internal long[] Ids { get; private set; } = [];
        /// <summary>Gets the transmitted profile header.</summary>
        internal string? Profile { get; private set; }
        /// <summary>Gets the number of bounded detail requests.</summary>
        internal int Details => Volatile.Read(ref _details);
        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Profile = request.Headers.GetValues("X-Orynivo-Profile").Single();
            var path = request.RequestUri!.AbsolutePath;
            Assert.NotEqual("/api/albums", path);
            if (path == "/api/albums/by-ids")
            {
                Ids = (await request.Content!.ReadFromJsonAsync<long[]>(cancellationToken))!;
                return new HttpResponseMessage(status)
                { Content = new StringContent("[{\"id\":7,\"album\":\"Selected\"}]") };
            }
            Interlocked.Increment(ref _details);
            return new HttpResponseMessage(path == "/api/albums/7" ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            { Content = new StringContent("{\"id\":7,\"album\":\"Selected\"}") };
        }
    }
}
