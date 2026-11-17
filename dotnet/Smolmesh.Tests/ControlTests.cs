using Smolmesh;
using System.Net;
using System.Net.Sockets;
using Xunit;

public class ControlTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task StatusReturnsOk()
    {
        int port = FreePort();
        using var stop = new CancellationTokenSource();
        Task server = Control.ListenAsync(port, () => new[] { new { name = "a" } }, _ => Task.FromResult(""), stop.Token);
        Assert.Equal("Ok.", await Control.StatusAsync(port, default));
        Assert.Equal("[{\"name\":\"a\"}]", await Control.PeersAsync(port, default));
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/nope")).StatusCode);
        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task StatusAnswersWhileATraceIsWaiting()
    {
        int port = FreePort();
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource<string>();
        Task server = Control.ListenAsync(port, () => Array.Empty<object>(), _ => release.Task, stop.Token);
        using var http = new HttpClient();
        Task<string> slow = http.GetStringAsync($"http://127.0.0.1:{port}/trace/x");
        Assert.Equal("Ok.", await Control.StatusAsync(port, default));
        Assert.False(slow.IsCompleted);
        release.SetResult("done");
        Assert.Equal("\"done\"", await slow);
        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task StatusFailsWhenNodeIsDown() =>
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Control.StatusAsync(FreePort(), default));
}
