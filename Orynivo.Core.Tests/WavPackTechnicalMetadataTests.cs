using Orynivo.Library;
using Xunit;

namespace Orynivo.Core.Tests;

/// <summary>Checks high-rate WavPack technical parsing without external processes or personal media.</summary>
public sealed class WavPackTechnicalMetadataTests
{
    /// <summary>Reads float32 high-rate properties and the container duration used by the final CUE track.</summary>
    [Fact]
    public void Parse_HighRateFloatReadsSourceProperties()
    {
        var metadata = WavPackTechnicalMetadata.Parse("""
            {"streams":[{"codec_name":"wavpack","sample_rate":"384000","bits_per_raw_sample":"32","channels":2}],
             "format":{"duration":"10.5","bit_rate":"2048000"}}
            """);
        Assert.NotNull(metadata);
        Assert.Equal(384000, metadata.SampleRate);
        Assert.Equal(32, metadata.BitDepth);
        Assert.Equal(2, metadata.Channels);
        Assert.Equal(10.5, metadata.Duration);
        Assert.Equal(2048, metadata.Bitrate);
        var record = new TrackRecord { Title = "Cue title", Format = "wv" };
        metadata.Apply(record);
        Assert.Equal("Cue title", record.Title);
        Assert.Equal("wv", record.Format);
        Assert.Equal(10.5, record.Duration);
    }

    /// <summary>Existing source values remain authoritative.</summary>
    [Fact]
    public void Apply_PreservesExistingTechnicalValues()
    {
        var record = new TrackRecord { Duration = 20, SampleRate = 96000, BitDepth = 24, Channels = 1, Bitrate = 500 };
        new WavPackTechnicalMetadata(10, 384000, 32, 2, 2000).Apply(record);
        Assert.Equal(20, record.Duration);
        Assert.Equal(96000, record.SampleRate);
        Assert.Equal(24, record.BitDepth);
        Assert.Equal(1, record.Channels);
        Assert.Equal(500, record.Bitrate);
    }

    /// <summary>Unavailable technical fields remain unknown rather than being invented.</summary>
    [Fact]
    public void Parse_InvalidValuesRemainUnknown()
    {
        var metadata = WavPackTechnicalMetadata.Parse("""
            {"streams":[{"codec_name":"wavpack","sample_rate":"NaN","channels":-1,
                          "bits_per_raw_sample":"0","duration":"N/A"}],
             "format":{"duration":"Infinity","bit_rate":"-1"}}
            """);
        Assert.Equal(new WavPackTechnicalMetadata(null, null, null, null, null), metadata);
    }

    /// <summary>Unrelated and malformed payloads cannot populate WavPack metadata.</summary>
    /// <param name="json">Invalid or unrelated technical payload.</param>
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"streams\":[]}")]
    [InlineData("{\"streams\":[{\"codec_name\":\"flac\"}]}")]
    public void Parse_RejectsInvalidPayload(string json) => Assert.Null(WavPackTechnicalMetadata.Parse(json));

    /// <summary>Stream duration wins and an available stored-bit field fills absent raw bits.</summary>
    [Fact]
    public void Parse_UsesStreamDurationAndBitDepthFallback()
    {
        var metadata = WavPackTechnicalMetadata.Parse("""
            {"streams":[{"codec_name":"wavpack","duration":2,"bits_per_sample":24}],"format":{"duration":10}}
            """);
        Assert.Equal(2, metadata!.Duration);
        Assert.Equal(24, metadata.BitDepth);
    }

    /// <summary>Cancellation is observed before attempting to start FFprobe.</summary>
    [Fact]
    public void Read_AlreadyCancelledDoesNotLaunchProbe()
    {
        Assert.Throws<OperationCanceledException>(() => WavPackTechnicalMetadata.Read("unused.wv", new CancellationToken(true)));
    }
}
