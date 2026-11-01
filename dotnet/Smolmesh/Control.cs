using System.Net;
using System.Text;
using System.Text.Json;

namespace Smolmesh;

/// <summary>Localhost-only JSON control interface: HTTP on 127.0.0.1, one JSON value per response.</summary>
internal static class Control
{
    internal static async Task ListenAsync(int port, Func<object> peers, CancellationToken token)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        await using var _ = token.Register(listener.Stop);
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            await RespondAsync(context, peers);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, Func<object> peers)
    {
        using var response = context.Response;
        object? result = context.Request is { HttpMethod: "GET" } request ? request.Url?.AbsolutePath switch
        {
            "/status" => "Ok.",
            "/peers" => peers(),
            _ => null,
        } : null;
        response.StatusCode = result is null ? 404 : 200;
        response.ContentType = "application/json";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result ?? "Not found.");
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
    }

    /// <summary>Asks a running node for its status; throws unless it answers "Ok.".</summary>
    internal static async Task<string> StatusAsync(int port, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string text = await http.GetStringAsync($"http://127.0.0.1:{port}/status", token);
        string? status = JsonSerializer.Deserialize<string>(text);
        return status == "Ok." ? status : throw new InvalidDataException($"Unexpected status response: {text}");
    }

    /// <summary>Fetches the peer list JSON from a running node.</summary>
    internal static async Task<string> PeersAsync(int port, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await http.GetStringAsync($"http://127.0.0.1:{port}/peers", token);
    }
}
