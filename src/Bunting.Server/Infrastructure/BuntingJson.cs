using System.Text.Json;

namespace Bunting.Server.Infrastructure;

/// <summary>JSON options shared by the HTTP API and the jsonb columns.</summary>
public static class BuntingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
