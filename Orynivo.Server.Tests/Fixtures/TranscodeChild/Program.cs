namespace TranscodeChild;

/// <summary>Produces synthetic stdout for deterministic process lifetime tests, never audio.</summary>
internal static class Program
{
    /// <summary>Writes a marker and either exits or waits for parent-controlled input.</summary>
    /// <param name="args">One fixed fixture mode.</param>
    /// <returns>The synthetic child lifetime task.</returns>
    private static async Task Main(string[] args)
    {
        using var output = Console.OpenStandardOutput();
        await output.WriteAsync(new byte[] { 1, 2, 3, 4 });
        await output.FlushAsync();
        if (args.Single() == "complete") return;
        await Console.In.ReadLineAsync();
    }
}