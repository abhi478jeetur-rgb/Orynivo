using System.Diagnostics;

namespace Orynivo.Server.Services;

/// <summary>Owns the started transcode process lifetime through output copy, exit or failure.</summary>
public static class TranscodeProcessRunner
{
    /// <summary>Starts and pipes a caller-owned process, terminating and awaiting it before returning on every started path.</summary>
    /// <param name="process">Unstarted process with redirected standard output; the caller disposes it.</param>
    /// <param name="output">Borrowed response stream, never disposed here.</param>
    /// <param name="cancellationToken">Request cancellation for both copying and natural exit.</param>
    /// <returns>A task completing only after the started process has exited.</returns>
    public static async Task PipeAsync(Process process, Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("The transcode process did not start.");
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}