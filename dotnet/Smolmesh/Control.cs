using System.Net;
using System.Text;
using System.Text.Json;

namespace Smolmesh;

/// <summary>Localhost-only JSON control interface: HTTP on 127.0.0.1, one JSON value per response.</summary>
internal static class Control
{
    internal static async Task ListenAsync(int port, CancellationToken token)
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
            await RespondAsync(context);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context)
    {
        using var response = context.Response;
        bool ok = context.Request is { HttpMethod: "GET", Url.AbsolutePath: "/status" };
        response.StatusCode = ok ? 200 : 404;
        response.ContentType = "application/json";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(ok ? "Ok." : "Not found.");
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
}
