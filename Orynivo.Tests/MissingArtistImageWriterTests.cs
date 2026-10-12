using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks missing-image admission with authoritative synthetic remote state and no external services.</summary>
public sealed class MissingArtistImageWriterTests
{
    /// <summary>Only current missing artists admit an image mutation and notify catalog consumers.</summary>
    /// <param name="state">Authoritative server state.</param>
    /// <returns>A task completing eligibility checks.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("manual")]
    [InlineData("present")]
    [InlineData("unknown")]
    [InlineData("renamed")]
    [InlineData("failure")]
    public async Task AuthoritativeStateControlsMutation(string state)
    {
        using var handler = new Handler { State = state };
        using var client = Client(handler);
        var queue = new ArtistImageWriteQueue();
        var admitted = 0; var changed = 0;
        var writer = new MissingArtistImageWriter(queue, (_, _, _) => admitted++);
        var result = await writer.StoreAsync(701, "Artist", Source(), [1], "image/png",
            client, default, () => true, () => changed++);
        var eligible = state is "missing" or "failure";
        Assert.Equal(eligible ? 1 : 0, handler.Uploads);
        Assert.Equal(eligible ? 1 : 0, admitted);
        Assert.Equal(state == "missing" ? 1 : 0, changed);
        Assert.Equal(state == "missing" ? true : state == "failure" ? false : (bool?)null, result);
        Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>A waiting candidate reads manual state after the preceding mutation completes.</summary>
    /// <returns>A task completing ordered state validation.</returns>
    [Fact]
    public async Task WaitingCandidateRefreshesManualState()
    {
        using var handler = new Handler(); using var client = Client(handler);
        var queue = new ArtistImageWriteQueue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = queue.RunAsync("artist", () => true, async () => { await gate.Task; handler.State = "manual"; return true; });
        var pending = new MissingArtistImageWriter(queue).StoreAsync(701, "Artist", Source(), [1], null, client, default, () => true);
        Assert.False(pending.IsCompleted); Assert.Equal(0, handler.Reads);
        gate.SetResult(); await prior;
        Assert.Null(await pending); Assert.Equal(1, handler.Reads); Assert.Equal(0, handler.Uploads);
    }

    /// <summary>Lost source ownership after the authoritative read prevents upload and image intent.</summary>
    /// <returns>A task completing post-read ownership rejection.</returns>
    [Fact]
    public async Task SourceChangeAfterReadRejectsUpload()
    {
        using var handler = new Handler { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(handler);
        var current = true; var admitted = false;
        var writer = new MissingArtistImageWriter(new(), (_, _, _) => admitted = true);
        var task = writer.StoreAsync(701, "Artist", Source(), [1], null, client, default, () => current);
        await handler.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current = false; handler.ReadGate.SetResult();
        Assert.Null(await task); Assert.False(admitted); Assert.Equal(0, handler.Uploads);
    }

    /// <summary>Rejected waiting ownership or cancellation performs no source read.</summary>
    /// <param name="cancel">Whether cancellation rather than source ownership is lost.</param>
    /// <returns>A task completing queue admission rejection.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedWaiterDoesNotRead(bool cancel)
    {
        using var handler = new Handler(); using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        var queue = new ArtistImageWriteQueue(); var current = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = queue.RunAsync("artist", () => true, async () => { await gate.Task; return true; });
        var pending = new MissingArtistImageWriter(queue).StoreAsync(701, "Artist", Source(), [1], null,
            client, cancellation.Token, () => current);
        if (cancel) cancellation.Cancel(); else current = false;
        gate.SetResult(); await prior;
        Assert.Null(await pending); Assert.Equal(0, handler.Reads); Assert.Equal(0, queue.ActiveArtistCount);
    }

    /// <summary>An admitted server upload completes before later manual work, retaining its copied profile.</summary>
    /// <returns>A task completing complete-batch ordering verification.</returns>
    [Fact]
    public async Task UploadRetainsOrderingAndSourceProfile()
    {
        using var handler = new Handler { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(handler); var queue = new ArtistImageWriteQueue();
        var changed = false; var laterRan = false;
        var pending = new MissingArtistImageWriter(queue).StoreAsync(701, "Artist", Source(), [1], null,
            client, default, () => true, () => changed = true);
        await handler.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var later = queue.RunAsync("artist", () => true, () => { Assert.True(changed); laterRan = true; return Task.FromResult(true); });
        Assert.False(laterRan); handler.UploadGate.SetResult();
        Assert.True(await pending); await later; Assert.True(laterRan);
        Assert.Equal("profile-fixture", handler.Profile);
    }

    /// <summary>Creates a synthetic source with a non-default personal profile.</summary>
    /// <returns>The credential-free source.</returns>
    private static OrynivoServerSettings Source() => new() { Id = "fixture", BaseUrl = "https://fixture.invalid", ProfileId = "profile-fixture" };

    /// <summary>Creates the caller-owned client with offline transport.</summary>
    /// <param name="handler">Synthetic transport.</param>
    /// <returns>The client.</returns>
    private static OrynivoServerClient Client(HttpMessageHandler handler) =>
        (OrynivoServerClient)Activator.CreateInstance(typeof(OrynivoServerClient),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [new HttpClient(handler)], null)!;

    /// <summary>Controls authoritative metadata and uploads without network access.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        /// <summary>Gets or sets synthetic authoritative state.</summary>
        internal string State { get; set; } = "missing";
        /// <summary>Gets the read count.</summary>
        internal int Reads { get; private set; }
        /// <summary>Gets the upload count.</summary>
        internal int Uploads { get; private set; }
        /// <summary>Gets the transmitted profile identity.</summary>
        internal string? Profile { get; private set; }
        /// <summary>Gets or initializes the optional metadata gate.</summary>
        internal TaskCompletionSource? ReadGate { get; init; }
        /// <summary>Gets or initializes the optional upload gate.</summary>
        internal TaskCompletionSource? UploadGate { get; init; }
        /// <summary>Signals metadata admission.</summary>
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Signals upload admission.</summary>
        internal TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Returns controlled source responses.</summary>
        /// <param name="request">Synthetic HTTP request.</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>The authoritative metadata or upload response.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Profile = request.Headers.TryGetValues("X-Orynivo-Profile", out var values) ? values.Single() : null;
            if (request.Method == HttpMethod.Get)
            {
                Reads++; ReadStarted.TrySetResult();
                if (ReadGate is not null) await ReadGate.Task.WaitAsync(cancellationToken);
                if (State == "unknown") return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OrynivoArtistInfo(
                    701, State == "renamed" ? "Other" : "Artist", false, null, null, null, null, true,
                    State == "present", State == "manual")) };
            }
            Uploads++; UploadStarted.TrySetResult();
            if (UploadGate is not null) await UploadGate.Task.WaitAsync(cancellationToken);
            return new(State == "failure" ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent);
        }
    }
}
