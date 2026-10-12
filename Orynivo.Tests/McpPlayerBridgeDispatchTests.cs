using Orynivo.Mcp;
using Xunit;

namespace Orynivo.Tests;

/// <summary>Checks command cancellation without starting an Avalonia dispatcher.</summary>
public sealed class McpPlayerBridgeDispatchTests
{
    /// <summary>A cancelled asynchronous command never enters the UI dispatch or its action.</summary>
    /// <returns>The asynchronous regression test.</returns>
    [Fact]
    public async Task CancelledAction_IsRejectedBeforeDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        var bridge = new McpPlayerBridge();
        var operation = bridge.OnUiAsync((Func<Task>)(() =>
        {
            called = true;
            return Task.CompletedTask;
        }), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(called);
    }
}
