using System.Threading.Channels;

namespace Bunting.Server.Tests;

public sealed record SseEvent(string Name, string Data, DateTimeOffset ReceivedAt);

/// <summary>Minimal text/event-stream reader for tests.</summary>
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SseEvent> _events = Channel.CreateUnbounded<SseEvent>();
    private readonly Task _pump;

    private SseClient(HttpResponseMessage response, Stream stream)
    {
        _response = response;
        _pump = Task.Run(() => PumpAsync(stream));
    }

    public static async Task<SseClient> ConnectAsync(HttpClient http, string path)
    {
        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return new SseClient(response, await response.Content.ReadAsStreamAsync());
    }

    public async Task<SseEvent?> NextAsync(TimeSpan timeout, Func<SseEvent, bool>? accept = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var e = await _events.Reader.ReadAsync(cts.Token);
                if (accept?.Invoke(e) ?? true)
                {
                    return e;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static long VersionOf(SseEvent e) => System.Text.Json.JsonDocument.Parse(e.Data).RootElement.GetProperty("version").GetInt64();

    private async Task PumpAsync(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream);
            string name = "message";
            var data = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        _events.Writer.TryWrite(new SseEvent(name, data.ToString(), DateTimeOffset.UtcNow));
                    }

                    name = "message";
                    data.Clear();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    name = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line["data:".Length..].Trim());
                }
            }
        }
        catch (Exception)
        {
            // disposed or connection closed
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _response.Dispose();
        await _pump;
        _cts.Dispose();
    }
}
