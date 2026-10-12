using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orynivo.Library;
using Orynivo.Server.Endpoints;
using Orynivo.Server.Middleware;
using Xunit;

namespace Orynivo.Server.Tests;

/// <summary>Exercises production audio stream routes with real middleware and isolated temporary SQLite.</summary>
public sealed class StreamEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orynivo-stream-api-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _trackId;
    private int _connections;
    private readonly byte[] _bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    private bool _blockWrite;
    private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _serverCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken _serverCancellation;
    private bool _writeCancelled;
    private string MediaPath => Path.Combine(_root, "track-0.flac");
    private string DatabasePath => Path.Combine(_root, "library.db");

    /// <summary>Seeds synthetic tracks and starts only the production route/middleware subset in memory.</summary>
    /// <returns>The host startup task.</returns>
    public async Task InitializeAsync()
    {
        using (var db = new AudioDatabase(DatabasePath))
        {
            for (var index = 0; index < 1; index++)
                db.Upsert(new TrackRecord
                {
                    Path = Path.Combine(_root, $"track-{index}.flac"),
                    FileName = $"track-{index}.flac",
                    Title = $"Track {index:D2}",
                    Artist = "Synthetic artist",
                    Album = "Synthetic album",
                    Format = "flac"
                });
            _trackId = db.GetTrackListPage(0, 1).Single().Id;
        }
        await File.WriteAllBytesAsync(MediaPath, _bytes);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ServerSettings
        {
            ApiKey = "synthetic-stream-key",
            Profiles = [new ServerProfile(), new ServerProfile { Id = "alpha", Name = "Alpha" },
                new ServerProfile { Id = "beta", Name = "Beta" }]
        });
        _app = builder.Build();
        _app.UseMiddleware<ApiKeyMiddleware>();
        _app.UseMiddleware<ProfileContextMiddleware>();
        _app.Use(async (context, next) =>
        {
            if (!_blockWrite) { await next(context); return; }
            _serverCancellation = context.RequestAborted;
            var original = context.Response.Body;
            using var blocked = new BlockedResponseStream(_writeStarted, () => _writeCancelled = true);
            context.Response.Body = blocked;
            try { await next(context); }
            finally { context.Response.Body = original; _serverCompleted.TrySetResult(); }
        });
        _app.MapAudioStreamEndpoints(() => { Interlocked.Increment(ref _connections); return new AudioDatabase(DatabasePath); });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    /// <summary>Stops the in-memory host and deletes only this fixture's temporary database after releasing its pool.</summary>
    /// <returns>The host shutdown task.</returns>
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Full original streaming returns exact bytes with the production media headers.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task FullStream_ReturnsExactBytes()
    {
        using var request = Request();
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(256, response.Content.Headers.ContentLength);
        Assert.Equal(_bytes, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Closed, open-ended and suffix ranges return exactly the requested original bytes.</summary>
    /// <param name="range">Range header.</param>
    /// <param name="first">First returned byte index.</param>
    /// <param name="last">Last returned byte index.</param>
    /// <returns>The endpoint assertion task.</returns>
    [Theory]
    [InlineData("bytes=10-19", 10, 19)]
    [InlineData("bytes=200-", 200, 255)]
    [InlineData("bytes=-8", 248, 255)]
    public async Task Range_ReturnsExactSlice(string range, int first, int last)
    {
        using var request = Request(); request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {first}-{last}/256", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(last - first + 1, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(_bytes[first..(last + 1)], await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>An unsatisfiable range returns 416, the complete length and no bytes.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task Range_RejectsUnsatisfiableOffset()
    {
        using var request = Request(); request.Headers.TryAddWithoutValidation("Range", "bytes=300-");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */256", response.Content.Headers.ContentRange!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Invalid or unsupported multipart ranges preserve the framework's full-response fallback.</summary>
    /// <param name="range">Unsupported Range header.</param>
    /// <returns>The endpoint assertion task.</returns>
    [Theory]
    [InlineData("bytes=invalid")]
    [InlineData("bytes=0-1,4-5")]
    public async Task Range_FallsBackToFullResponse(string range)
    {
        using var request = Request(); request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_bytes, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Unknown tracks and removed source files return 404 rather than an empty success.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task MissingTrackOrFile_ReturnsNotFound()
    {
        using var unknown = Request("/api/stream/9223372036854775807");
        using var unknownResponse = await _client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
        File.Delete(MediaPath);
        using var missing = Request(); using var response = await _client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Invalid transcode options reject before any FFmpeg process is needed.</summary>
    /// <param name="query">Invalid option query.</param>
    /// <returns>The endpoint assertion task.</returns>
    [Theory]
    [InlineData("format=wav")]
    [InlineData("format=opus&bitrate=63")]
    [InlineData("format=aac&bitrate=321")]
    public async Task InvalidTranscode_ReturnsBadRequest(string query)
    {
        using var request = Request($"/api/stream/{_trackId}?{query}"); using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Authentication and profile rejection occur before indexed-track database access.</summary>
    /// <returns>The endpoint assertion task.</returns>
    [Fact]
    public async Task RejectedRequests_DoNotOpenDatabase()
    {
        using var anonymous = await _client.GetAsync($"/api/stream/{_trackId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var unknown = Request(); unknown.Headers.Add("X-Orynivo-Profile", "unknown");
        using var response = await _client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _connections);
    }

    /// <summary>Client cancellation reaches the actual file-response write and unwinds the server request.</summary>
    /// <returns>The deterministic cancellation assertion task.</returns>
    [Fact]
    public async Task ClientAbort_CancelsBlockedFileWrite()
    {
        _blockWrite = true;
        using var cancellation = new CancellationTokenSource();
        using var request = Request();
        var send = _client.SendAsync(request, cancellation.Token);
        try
        {
            await _writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await send; });
            await _serverCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_serverCancellation.IsCancellationRequested);
            Assert.True(_writeCancelled);
        }
        finally { cancellation.Cancel(); }
    }

    /// <summary>Creates a fixed synthetic authenticated stream request.</summary>
    /// <param name="path">Optional relative route.</param>
    /// <returns>The caller-owned request.</returns>
    private HttpRequestMessage Request(string? path = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path ?? $"/api/stream/{_trackId}");
        request.Headers.Add("X-Api-Key", "synthetic-stream-key");
        return request;
    }
}

/// <summary>Holds an actual response write until its request token cancels, without arbitrary sleeps.</summary>
/// <param name="started">Signals admission of the actual file-response write.</param>
/// <param name="cancelled">Records observed cancellation of that write.</param>
internal sealed class BlockedResponseStream(TaskCompletionSource started, Action cancelled) : Stream
{
    /// <summary>Gets whether reads are supported.</summary>
    public override bool CanRead => false;
    /// <summary>Gets whether seeking is supported.</summary>
    public override bool CanSeek => false;
    /// <summary>Gets whether writes are supported.</summary>
    public override bool CanWrite => true;
    /// <summary>Gets the unsupported stream length.</summary>
    public override long Length => throw new NotSupportedException();
    /// <summary>Gets or sets the unsupported position.</summary>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <summary>Flushes no buffered data.</summary>
    public override void Flush() { }
    /// <summary>Rejects reads.</summary>
    /// <param name="buffer">Unused buffer.</param><param name="offset">Unused offset.</param><param name="count">Unused count.</param>
    /// <returns>No value; reading is unsupported.</returns>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <summary>Rejects seeking.</summary>
    /// <param name="offset">Unused offset.</param><param name="origin">Unused origin.</param>
    /// <returns>No value; seeking is unsupported.</returns>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>Rejects length changes.</summary>
    /// <param name="value">Unused length.</param>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>Rejects synchronous writes so the fixture cannot silently bypass cancellation.</summary>
    /// <param name="buffer">Unused buffer.</param><param name="offset">Unused offset.</param><param name="count">Unused count.</param>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <summary>Holds a legacy asynchronous write until cancellation.</summary>
    /// <param name="buffer">Synthetic bytes.</param><param name="offset">Buffer offset.</param><param name="count">Byte count.</param>
    /// <param name="cancellationToken">Actual request cancellation.</param>
    /// <returns>The cancellation-observing write task.</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <summary>Signals that the file response started writing and observes its actual cancellation token.</summary>
    /// <param name="buffer">Synthetic bytes.</param><param name="cancellationToken">Actual request cancellation.</param>
    /// <returns>The cancellation-observing write operation.</returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { cancelled(); throw; }
    }
}
