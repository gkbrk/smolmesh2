using System.Net;
using Smolmesh;
using Xunit;

// Some tests redirect Console.Error, so tests must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal sealed class FakeTun : ITun
{
    internal readonly List<byte[]> Written = [];
    public Task<byte[]> ReadAsync(CancellationToken token) => throw new NotSupportedException();
    public Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token) { Written.Add(packet.ToArray()); return Task.CompletedTask; }
    public Task RouteAsync(IPAddress address, bool add, CancellationToken token) => Task.CompletedTask;
    public void Dispose() { }
}

internal static class TestSupport
{
    internal static async Task IgnoreCancellation(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }
}
