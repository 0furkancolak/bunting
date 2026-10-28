using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunting.Evaluation;
using Bunting.Server.Features.Rulesets;
using Bunting.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace Bunting.Server.Features.Ofrep;

public sealed class OfrepOptions
{
    /// <summary>When true and the request has no <c>country</c>, it is taken from Cloudflare's <c>CF-IPCountry</c> header.</summary>
    public bool UseCloudflareCountry { get; set; }
}

/// <summary>Successful evaluation (OFREP <c>evaluationSuccess</c>).</summary>
public sealed record OfrepSuccess(
    string Key,
    JsonElement Value,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Variant,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, object>? Metadata);

/// <summary>Failed evaluation (OFREP <c>evaluationFailure</c> / <c>flagNotFound</c>).</summary>
public sealed record OfrepFailure(
    string Key,
    string ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorDetails);

public sealed record OfrepBulkFailure(string ErrorCode, string ErrorDetails);

public sealed record OfrepBulkResponse(IReadOnlyList<object> Flags, Dictionary<string, object> Metadata);

/// <summary>
/// OFREP v0.4.0 evaluation endpoints (see docs/ofrep-openapi.yaml). Clients authenticate with a server or client API key.
/// Client keys only ever see flags marked <c>ClientSideAvailable</c>, and never rule or segment details.
/// </summary>
public static class OfrepEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/ofrep/v1/evaluate")
            .RequireAuthorization(ApiKeyPolicies.AnyKey)
            .RequireRateLimiting(ApiKeyPolicies.RateLimit);
        g.MapPost("/flags/{key}", EvaluateOne);
        g.MapPost("/flags", EvaluateAll);
    }

    private static bool IsClient(HttpContext http) =>
        http.User.FindFirst(ApiKeyPolicies.KeyTypeClaim)?.Value == nameof(Domain.ApiKeyType.Client);

    private static Guid EnvironmentId(HttpContext http) => Guid.Parse(http.User.FindFirst(ApiKeyPolicies.EnvironmentClaim)!.Value);

    private static async Task<IResult> EvaluateOne(
        string key, HttpContext http, RulesetCache cache, IOptions<OfrepOptions> options, CancellationToken ct)
    {
        if (!TryReadContext(await ReadBodyAsync(http, ct), http, options.Value, out var context, out var details))
        {
            return Results.Json(new OfrepFailure(key, "INVALID_CONTEXT", details), BuntingJson.Options, statusCode: StatusCodes.Status400BadRequest);
        }

        var cached = await cache.GetAsync(EnvironmentId(http), ct);
        var client = IsClient(http);

        // A client key cannot tell "hidden" from "missing": both are FLAG_NOT_FOUND.
        var visible = cached.Ruleset.Flags.FirstOrDefault(f => f.Key == key) is { } info && (!client || info.ClientSideAvailable);
        if (!visible)
        {
            return Results.Json(
                new OfrepFailure(key, "FLAG_NOT_FOUND", $"Flag '{key}' was not found"),
                BuntingJson.Options,
                statusCode: StatusCodes.Status404NotFound);
        }

        var item = Map(cached.Ruleset.Evaluate(key, context), client);
        return item is OfrepSuccess
            ? Results.Json(item, BuntingJson.Options)
            : Results.Json(item, BuntingJson.Options, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> EvaluateAll(
        HttpContext http, RulesetCache cache, IOptions<OfrepOptions> options, CancellationToken ct)
    {
        if (!TryReadContext(await ReadBodyAsync(http, ct), http, options.Value, out var context, out var details))
        {
            return Results.Json(new OfrepBulkFailure("INVALID_CONTEXT", details!), BuntingJson.Options, statusCode: StatusCodes.Status400BadRequest);
        }

        var cached = await cache.GetAsync(EnvironmentId(http), ct);
        var client = IsClient(http);

        // The result depends on ruleset version, key type and context, so the ETag covers all three.
        var etag = ComputeETag(cached.Version, client, context);
        http.Response.Headers.ETag = etag;
        if (MatchesIfNoneMatch(http.Request, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var items = new List<object>();
        foreach (var flag in cached.Ruleset.Flags)
        {
            if (flag.Archived || (client && !flag.ClientSideAvailable))
            {
                continue;
            }

            items.Add(Map(cached.Ruleset.Evaluate(flag.Key, context), client));
        }

        return Results.Json(
            new OfrepBulkResponse(items, new Dictionary<string, object> { ["version"] = $"v{cached.Version}" }),
            BuntingJson.Options);
    }

    private static async Task<JsonElement?> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var limited = new MemoryStream();
            await http.Request.Body.CopyToAsync(limited, ct);
            if (limited.Length > MaxBodyBytes || limited.Length == 0)
            {
                return null;
            }

            limited.Position = 0;
            return (await JsonDocument.ParseAsync(limited, cancellationToken: ct)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts the request context into an <see cref="EvaluationContext"/>. Context values are never logged or stored.
    /// Supported values: strings, numbers, booleans (null is ignored); nested objects/arrays are rejected.
    /// </summary>
    internal static bool TryReadContext(
        JsonElement? body, HttpContext http, OfrepOptions options, out EvaluationContext context, out string? details)
    {
        context = EvaluationContext.Empty;
        details = null;
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Object)
        {
            details = "The request body must be a JSON object with a 'context' object.";
            return false;
        }

        string? targetingKey = null;
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in ctx.EnumerateObject())
        {
            if (property.Name == EvaluationContext.TargetingKeyAttribute)
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    targetingKey = property.Value.GetString();
                }
                else if (property.Value.ValueKind != JsonValueKind.Null)
                {
                    details = "Context property 'targetingKey' must be a string";
                    return false;
                }

                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    attributes[property.Name] = property.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    attributes[property.Name] = property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    attributes[property.Name] = property.Value.GetBoolean();
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    details = $"Context property '{property.Name}' must be a string, number or boolean";
                    return false;
            }
        }

        if (options.UseCloudflareCountry
            && !attributes.ContainsKey("country")
            && http.Request.Headers["CF-IPCountry"].ToString() is { Length: 2 } country
            && country is not ("XX" or "T1"))
        {
            attributes["country"] = country;
        }

        context = new EvaluationContext(targetingKey, attributes);
        return true;
    }

    /// <summary>Maps an engine result to the OFREP wire shape. Client keys never receive rule metadata.</summary>
    internal static object Map(EvaluationResult result, bool client)
    {
        switch (result.ErrorCode)
        {
            case EvaluationErrorCode.None:
                var metadata = !client && result.RuleId is not null ? new Dictionary<string, object> { ["ruleId"] = result.RuleId } : null;
                return new OfrepSuccess(result.FlagKey, result.Value, result.Reason.ToWireString(), result.Variant, metadata);
            case EvaluationErrorCode.FlagNotFound:
                return new OfrepFailure(result.FlagKey, "FLAG_NOT_FOUND", $"Flag '{result.FlagKey}' was not found");
            case EvaluationErrorCode.TargetingKeyMissing:
                return new OfrepFailure(result.FlagKey, "TARGETING_KEY_MISSING", "The flag requires a targeting key");
            case EvaluationErrorCode.ParseError:
                return new OfrepFailure(result.FlagKey, "PARSE_ERROR", "The flag configuration is invalid");
            default:
                return new OfrepFailure(result.FlagKey, "GENERAL", "The flag could not be evaluated");
        }
    }

    internal static string ComputeETag(long version, bool client, EvaluationContext context)
    {
        var text = new StringBuilder();
        text.Append(version).Append('|').Append(client ? 'c' : 's').Append('|').Append(context.TargetingKey).Append('|');
        foreach (var (name, value) in context.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value?.GetType().Name).Append(':').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return "\"" + Convert.ToBase64String(hash, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "\"";
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, string etag)
    {
        foreach (var candidate in request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var value = candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;
            if (value == "*" || value == etag || value.Trim('"') == etag.Trim('"'))
            {
                return true;
            }
        }

        return false;
    }
}
