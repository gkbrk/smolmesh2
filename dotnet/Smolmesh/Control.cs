using System.Net;
using System.Text;
using System.Text.Json;

namespace Smolmesh;

/// <summary>Localhost-only JSON control interface: HTTP on 127.0.0.1, one JSON value per response.</summary>
internal static class Control
{
    internal static async Task ListenAsync(int port, Func<object> peers, Func<string, Task<string>> trace, CancellationToken token)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        await using var registration = token.Register(listener.Stop);
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            // Not awaited: a 3 second /trace must not hold up /status.
            _ = RespondAsync(context, peers, trace);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, Func<object> peers, Func<string, Task<string>> trace)
    {
        // Nobody awaits this task, so a client that hung up must not become an unobserved exception.
        try { await RespondCoreAsync(context, peers, trace); }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
    }

    private static async Task RespondCoreAsync(HttpListenerContext context, Func<object> peers, Func<string, Task<string>> trace)
    {
        using var response = context.Response;
        string? path = context.Request is { HttpMethod: "GET" } request ? request.Url?.AbsolutePath : null;
        object? result = path switch
        {
            "/status" => "Ok.",
            "/peers" => peers(),
            _ when path?.StartsWith("/trace/") == true => await trace(Uri.UnescapeDataString(path[7..])),
            _ => null,
        };
        response.StatusCode = result is null ? 404 : 200;
        response.ContentType = "application/json";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result ?? "Not found.");
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
    }

    /// <summary>Asks a running node for its status; throws unless it answers "Ok.".</summary>
    internal static async Task<string> StatusAsync(int port, CancellationToken token)
    {
        string text = await GetAsync(port, "status", 5, token);
        string? status = JsonSerializer.Deserialize<string>(text);
        return status == "Ok." ? status : throw new InvalidDataException($"Unexpected status response: {text}");
    }

    /// <summary>Fetches the peer list JSON from a running node.</summary>
    internal static Task<string> PeersAsync(int port, CancellationToken token) => GetAsync(port, "peers", 5, token);

    /// <summary>Asks a running node to trace the route to a node name; returns the plain-text hop table.</summary>
    internal static async Task<string> TraceAsync(int port, string name, CancellationToken token) =>
        JsonSerializer.Deserialize<string>(await GetAsync(port, $"trace/{Uri.EscapeDataString(name)}", 10, token))!;

    private static async Task<string> GetAsync(int port, string path, int seconds, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        return await http.GetStringAsync($"http://127.0.0.1:{port}/{path}", token);
    }
}
