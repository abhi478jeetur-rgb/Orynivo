using System.Net;
using System.Text;
using System.Text.Json;
using Orynivo.Streaming;
using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Verifies strict feature-page reads without contacting a server.</summary>
public sealed class SimilarityFeatureClientTests
{
    /// <summary>Strict callers distinguish empty success from malformed or missing arrays.</summary>
    /// <param name="payload">Synthetic response payload.</param>
    /// <param name="valid">Whether the response is a successful feature array.</param>
    [Theory]
    [InlineData("[]", true)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("invalid", false)]
    public async Task StrictRead_ValidatesArray(string payload, bool valid)
    {
        using var client = CreateClient(payload);
        if (valid) Assert.Empty(await client.GetSimilarityFeaturesAsync(Server(), requireComplete: true));
        else await Assert.ThrowsAsync<JsonException>(() => client.GetSimilarityFeaturesAsync(Server(), requireComplete: true));
    }

    /// <summary>HTTP failures propagate for strict callers and remain tolerant by default.</summary>
    [Fact]
    public async Task HttpFailure_StrictThrowsAndDefaultRemainsCompatible()
    {
        using var client = CreateClient("[]", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSimilarityFeaturesAsync(Server(), requireComplete: true));
        Assert.Empty(await client.GetSimilarityFeaturesAsync(Server()));
    }

    /// <summary>Cancelled strict reads never masquerade as successful empty pages.</summary>
    [Fact]
    public async Task StrictRead_PropagatesCancellation()
    {
        using var client = CreateClient("[]");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSimilarityFeaturesAsync(Server(), cancellationToken: cts.Token, requireComplete: true));
    }

    /// <summary>Returned vectors use the owning provider identity rather than a payload-supplied identity.</summary>
    [Fact]
    public async Task StrictRead_RebasesProvider()
    {
        using var client = CreateClient("""[{"SourceKey":"local","TrackId":7}]""");
        var vectors = await client.GetSimilarityFeaturesAsync(Server(), requireComplete: true);
        Assert.Equal("orynivo:fixture", Assert.Single(vectors).SourceKey);
        Assert.Equal(7, vectors[0].TrackId);
    }

    private static OrynivoServerSettings Server() => new() { Id = "fixture", BaseUrl = "http://fixture.invalid", ApiKey = "synthetic-only" };
    private static OrynivoServerClient CreateClient(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new FixtureHandler(payload, status)));

    /// <summary>Returns synthetic feature responses without network access.</summary>
    private sealed class FixtureHandler(string payload, HttpStatusCode status) : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }
}
