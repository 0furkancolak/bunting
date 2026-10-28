using System.Text.Json;
using Bunting.Evaluation;
using Microsoft.Extensions.Logging;

namespace Bunting.Sdk;

/// <summary>Reads and writes the optional on-disk copy of the last ruleset.</summary>
internal static partial class SnapshotStore
{
    public static RulesetSnapshot? TryLoad(string path, JsonSerializerOptions json, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<RulesetSnapshot>(File.ReadAllBytes(path), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(logger, ex, path);
            return null;
        }
    }

    /// <summary>Writes atomically (temp file + rename) so a crash never leaves a half-written snapshot.</summary>
    public static async Task TrySaveAsync(string path, byte[] content, ILogger logger)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, content).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                // The ruleset can contain segment member lists.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the Bunting snapshot at {Path}; ignoring it")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the Bunting snapshot to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
