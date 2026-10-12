using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Orynivo.Audio;

namespace Orynivo.Library;

/// <summary>Technical WavPack source properties missing from TagLib's high-rate reader.</summary>
/// <param name="Duration">Complete physical source duration in seconds.</param>
/// <param name="SampleRate">Source sample rate in hertz.</param>
/// <param name="BitDepth">Source bit depth.</param>
/// <param name="Channels">Source channel count.</param>
/// <param name="Bitrate">Encoded bitrate in kilobits per second.</param>
internal sealed record WavPackTechnicalMetadata(double? Duration, int? SampleRate, int? BitDepth, int? Channels, int? Bitrate)
{
    /// <summary>Reads selected technical fields only, with bounded process lifetime and no playback.</summary>
    /// <param name="path">Physical WavPack source file.</param>
    /// <param name="cancellationToken">Scan cancellation.</param>
    /// <returns>Source properties, or null when probing is unavailable or invalid.</returns>
    internal static WavPackTechnicalMetadata? Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "ffprobe", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = FfmpegLocator.GetSafeWorkingDirectory()
        } };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries",
                     "stream=codec_name,sample_rate,channels,bits_per_raw_sample,bits_per_sample,duration:format=duration,bit_rate",
                     "-of", "json", path })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            var json = output.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0 && json.Length <= 16_384 ? Parse(json) : null;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Parses positive technical fields without guessing unavailable values.</summary>
    /// <param name="json">Selected FFprobe fields; never retained.</param>
    /// <returns>WavPack properties, or null for an invalid or unrelated payload.</returns>
    internal static WavPackTechnicalMetadata? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array ||
                streams.GetArrayLength() == 0) return null;
            var stream = streams[0];
            if (!stream.TryGetProperty("codec_name", out var codec) || codec.GetString() != "wavpack") return null;
            var format = root.TryGetProperty("format", out var container) ? container : default;
            var duration = PositiveNumber(stream, "duration") ?? PositiveNumber(format, "duration");
            var bitrate = PositiveNumber(format, "bit_rate") / 1000;
            return new(duration, PositiveInt(stream, "sample_rate"),
                PositiveInt(stream, "bits_per_raw_sample") ?? PositiveInt(stream, "bits_per_sample"),
                PositiveInt(stream, "channels"), bitrate is > 0 and <= int.MaxValue ? (int)Math.Round(bitrate.Value) : null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Fills missing source fields without overwriting valid tags or existing technical values.</summary>
    /// <param name="record">Physical source metadata before CUE segment projection.</param>
    internal void Apply(TrackRecord record)
    {
        record.Duration ??= Duration;
        record.SampleRate ??= SampleRate;
        record.BitDepth ??= BitDepth;
        record.Channels ??= Channels;
        record.Bitrate ??= Bitrate;
    }

    /// <summary>Reads a positive integral property within the supported integer range.</summary>
    /// <param name="element">Stream or format object.</param>
    /// <param name="name">Technical property name.</param>
    /// <returns>A positive integer, or null.</returns>
    private static int? PositiveInt(JsonElement element, string name)
    {
        var number = PositiveNumber(element, name);
        return number is > 0 and <= int.MaxValue && number == Math.Truncate(number.Value) ? (int)number.Value : null;
    }

    /// <summary>Reads a finite positive numeric or numeric-string property.</summary>
    /// <param name="element">Stream or format object.</param>
    /// <param name="name">Technical property name.</param>
    /// <returns>A finite positive number, or null.</returns>
    private static double? PositiveNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
               double.IsFinite(number) && number > 0 ? number : null;
    }
}
