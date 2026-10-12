using System.ComponentModel;
using System.Diagnostics;
using Orynivo.Server.Services;
using Xunit;

namespace Orynivo.Server.Tests;

/// <summary>Checks the production transcode lifetime runner using a real synthetic .NET child process.</summary>
public sealed class TranscodeProcessRunnerTests
{
    /// <summary>Cancellation during output copying returns only after the child has exited.</summary>
    /// <returns>The process lifetime assertion task.</returns>
    [Fact]
    public async Task CancelledCopy_KillsAndAwaitsChild()
    {
        using var process = CreateChild("pump");
        using var cancellation = new CancellationTokenSource();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var output = new BlockedResponseStream(writeStarted, () => cancelled = true);
        var pipe = TranscodeProcessRunner.PipeAsync(process, output, cancellation.Token);
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(cancelled);
            Assert.True(process.HasExited);
        }
        finally { cancellation.Cancel(); await StopChildAsync(process); }
    }

    /// <summary>Natural completion preserves all output bytes and leaves the borrowed response stream open.</summary>
    /// <returns>The process lifetime assertion task.</returns>
    [Fact]
    public async Task CompletedChild_ReturnsBytesAndLeavesOutputOpen()
    {
        using var process = CreateChild("complete");
        using var output = new MemoryStream();
        try
        {
            await TranscodeProcessRunner.PipeAsync(process, output, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
            Assert.True(output.CanWrite);
        }
        finally { await StopChildAsync(process); }
    }

    /// <summary>A response-write failure cannot leave the started child alive.</summary>
    /// <returns>The process lifetime assertion task.</returns>
    [Fact]
    public async Task FailedOutput_KillsAndAwaitsChild()
    {
        using var process = CreateChild("pump");
        using var output = new FailingOutput();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => TranscodeProcessRunner.PipeAsync(process, output, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(process.HasExited);
        }
        finally { await StopChildAsync(process); }
    }

    /// <summary>Already cancelled requests do not start a child.</summary>
    /// <returns>The process lifetime assertion task.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotStartChild()
    {
        using var process = CreateChild("pump"); using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranscodeProcessRunner.PipeAsync(process, output, cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => process.Id);
    }

    /// <summary>Startup failure propagates without fabricating a running child.</summary>
    /// <returns>The process startup assertion task.</returns>
    [Fact]
    public async Task FailedStartup_Propagates()
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "orynivo-nonexistent-child-" + Guid.NewGuid().ToString("N"), UseShellExecute = false, CreateNoWindow = true } };
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<Win32Exception>(() => TranscodeProcessRunner.PipeAsync(process, output, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => process.Id);
    }

    /// <summary>Creates a managed fixture child without shell, network, credentials or audio work.</summary>
    /// <param name="mode">Fixed synthetic fixture mode.</param>
    /// <returns>The unstarted caller-owned process.</returns>
    private static Process CreateChild(string mode)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TranscodeChild", "TranscodeChild.dll"));
        start.ArgumentList.Add(mode);
        return new Process { StartInfo = start };
    }

    /// <summary>Stops only the exact fixture-owned child if an assertion or diagnostic timeout fails.</summary>
    /// <param name="process">Fixture child, possibly unstarted or already exited.</param>
    /// <returns>The cleanup task.</returns>
    private static async Task StopChildAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>Fails an actual output copy after the child has started producing bytes.</summary>
internal sealed class FailingOutput : MemoryStream
{
    /// <summary>Rejects response output deterministically.</summary>
    /// <param name="buffer">Synthetic child bytes.</param><param name="cancellationToken">Request cancellation.</param>
    /// <returns>A failed output operation.</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromException(new IOException("Synthetic response failure."));
    /// <summary>Rejects legacy asynchronous writes as well.</summary>
    /// <param name="buffer">Synthetic bytes.</param><param name="offset">Buffer offset.</param><param name="count">Byte count.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>A failed output operation.</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromException(new IOException("Synthetic response failure."));
}
