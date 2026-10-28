using System.Runtime.CompilerServices;
using System.Text;

namespace Bunting.Sdk;

internal readonly record struct SseMessage(string Name, string Data);

/// <summary>Minimal text/event-stream parser. A stream that stays silent for longer than the idle timeout fails.</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseMessage> ReadAsync(
        Stream stream,
        TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var name = "message";
        var data = new StringBuilder();

        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The stream was silent for {idleTimeout}.");
            }

            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseMessage(name, data.ToString());
                }

                name = "message";
                data.Clear();
            }
            else if (line[0] == ':')
            {
                // comment
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }
}
