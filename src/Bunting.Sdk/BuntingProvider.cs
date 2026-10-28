using System.Text.Json;
using Bunting.Domain;
using Bunting.Evaluation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using BuntingContext = Bunting.Evaluation.EvaluationContext;
using FeatureContext = OpenFeature.Model.EvaluationContext;

namespace Bunting.Sdk;

/// <summary>
/// OpenFeature provider that evaluates flags locally against a ruleset downloaded from a Bunting server.
/// It never throws from evaluation: any problem yields the caller's default value plus a reason.
/// </summary>
public sealed partial class BuntingProvider : FeatureProvider, IAsyncDisposable
{
    private const string ProviderName = "Bunting";

    private readonly BuntingOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _logger;
    private readonly RulesetSynchronizer _synchronizer;

    public BuntingProvider(BuntingOptions options, ILogger<BuntingProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        if (options.HttpClient is { } supplied)
        {
            _http = supplied;
            _http.BaseAddress ??= options.ServerUrl;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = options.ServerUrl,
                Timeout = Timeout.InfiniteTimeSpan, // the stream is long-lived; requests carry their own timeout
            };
            _ownsHttp = true;
        }

        _http.DefaultRequestHeaders.Remove("X-API-Key");
        _http.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
        _synchronizer = new RulesetSynchronizer(options, _http, _logger, OnSignal);
    }

    /// <summary>Version of the ruleset in use, or <see langword="null"/> when none is loaded.</summary>
    public long? RulesetVersion => _synchronizer.Ruleset?.Version;

    public ConnectionState ConnectionState => _synchronizer.State;

    public override Metadata GetMetadata() => new(ProviderName);

    public override async Task InitializeAsync(FeatureContext context, CancellationToken cancellationToken = default)
    {
        _synchronizer.Start();
        await _synchronizer.WaitForConfirmationAsync(_options.InitializeTimeout, cancellationToken).ConfigureAwait(false);
        // Without any ruleset the provider still starts: evaluations return defaults with PROVIDER_NOT_READY and
        // a Ready event follows as soon as the server becomes reachable.
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken = default) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await _synchronizer.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, FeatureContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, FlagType.Boolean, context, static (JsonElement v, out bool r) => TryBool(v, out r)));

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, FeatureContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, FlagType.String, context, static (JsonElement v, out string r) => TryString(v, out r)));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, FeatureContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, FlagType.Integer, context, static (JsonElement v, out int r) => TryInt(v, out r)));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, FeatureContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, FlagType.Double, context, static (JsonElement v, out double r) => TryDouble(v, out r)));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, FeatureContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, FlagType.Object, context, static (JsonElement v, out Value r) => TryStructure(v, out r)));

    private delegate bool Convert<T>(JsonElement element, out T value);

    private ResolutionDetails<T> Resolve<T>(string flagKey, T defaultValue, FlagType type, FeatureContext? context, Convert<T> convert)
    {
        try
        {
            var ruleset = _synchronizer.Ruleset;
            if (ruleset is null)
            {
                return Failure(flagKey, defaultValue, ErrorType.ProviderNotReady, "The ruleset has not been loaded yet");
            }

            var result = ruleset.Evaluate(flagKey, ToEngineContext(context), type);
            if (result.IsError && result.ErrorCode != EvaluationErrorCode.None)
            {
                return Failure(flagKey, defaultValue, ToErrorType(result.ErrorCode), result.ErrorCode.ToWireString() ?? "GENERAL");
            }

            if (!convert(result.Value, out var value))
            {
                return Failure(flagKey, defaultValue, ErrorType.TypeMismatch, "The flag value does not have the requested type");
            }

            var reason = _synchronizer.IsStale ? EvaluationReason.Stale.ToWireString() : result.Reason.ToWireString();
            BuntingTelemetry.Evaluations.Add(1, Tags(flagKey, reason));
            return new ResolutionDetails<T>(
                flagKey,
                value,
                variant: result.Variant,
                reason: reason,
                flagMetadata: result.RuleId is null ? null : new ImmutableMetadata(new Dictionary<string, object> { ["ruleId"] = result.RuleId }));
        }
        catch (Exception ex)
        {
            LogEvaluationFailed(ex, flagKey);
            return Failure(flagKey, defaultValue, ErrorType.General, "Unexpected evaluation failure");
        }
    }

    private static ResolutionDetails<T> Failure<T>(string flagKey, T defaultValue, ErrorType type, string message)
    {
        BuntingTelemetry.Evaluations.Add(1, Tags(flagKey, Reason.Error));
        return new ResolutionDetails<T>(flagKey, defaultValue, errorType: type, reason: Reason.Error, errorMessage: message);
    }

    private static KeyValuePair<string, object?>[] Tags(string flagKey, string reason) =>
    [
        new("feature_flag.key", flagKey),
        new("feature_flag.result.reason", reason.ToLowerInvariant()),
    ];

    private void OnSignal(SyncSignal signal, string? message)
    {
        var payload = signal switch
        {
            SyncSignal.Ready => new ProviderEventPayload { Type = ProviderEventTypes.ProviderReady, ProviderName = ProviderName, Message = "Ruleset confirmed" },
            SyncSignal.ConfigurationChanged => new ProviderEventPayload { Type = ProviderEventTypes.ProviderConfigurationChanged, ProviderName = ProviderName, Message = "Ruleset updated" },
            SyncSignal.Stale => new ProviderEventPayload { Type = ProviderEventTypes.ProviderStale, ProviderName = ProviderName, Message = message },
            _ => new ProviderEventPayload { Type = ProviderEventTypes.ProviderError, ProviderName = ProviderName, Message = message },
        };

        EventChannel.Writer.TryWrite(payload);
    }

    private static ErrorType ToErrorType(EvaluationErrorCode code) => code switch
    {
        EvaluationErrorCode.ProviderNotReady => ErrorType.ProviderNotReady,
        EvaluationErrorCode.FlagNotFound => ErrorType.FlagNotFound,
        EvaluationErrorCode.ParseError => ErrorType.ParseError,
        EvaluationErrorCode.TypeMismatch => ErrorType.TypeMismatch,
        EvaluationErrorCode.TargetingKeyMissing => ErrorType.TargetingKeyMissing,
        EvaluationErrorCode.InvalidContext => ErrorType.InvalidContext,
        _ => ErrorType.General,
    };

    /// <summary>Copies the supported attribute kinds; structures and lists cannot be targeted and are skipped.</summary>
    private static BuntingContext ToEngineContext(FeatureContext? context)
    {
        if (context is null || context.Count == 0)
        {
            return BuntingContext.Empty;
        }

        var attributes = new Dictionary<string, object?>(context.Count);
        foreach (var (name, value) in context)
        {
            if (value.IsString)
            {
                attributes[name] = value.AsString;
            }
            else if (value.IsBoolean)
            {
                attributes[name] = value.AsBoolean;
            }
            else if (value.IsNumber)
            {
                attributes[name] = value.AsDouble;
            }
            else if (value.IsDateTime)
            {
                attributes[name] = value.AsDateTime;
            }
        }

        return new BuntingContext(context.TargetingKey, attributes);
    }

    private static bool TryBool(JsonElement v, out bool r)
    {
        r = v.ValueKind == JsonValueKind.True;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryString(JsonElement v, out string r)
    {
        r = v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
        return v.ValueKind == JsonValueKind.String;
    }

    private static bool TryInt(JsonElement v, out int r)
    {
        r = 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out r);
    }

    private static bool TryDouble(JsonElement v, out double r)
    {
        r = 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out r);
    }

    private static bool TryStructure(JsonElement v, out Value r)
    {
        r = ToValue(v);
        return v.ValueKind != JsonValueKind.Undefined;
    }

    private static Structure ToStructure(JsonElement element)
    {
        var builder = Structure.Builder();
        foreach (var property in element.EnumerateObject())
        {
            builder.Set(property.Name, ToValue(property.Value));
        }

        return builder.Build();
    }

    private static Value ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => new Value(true),
        JsonValueKind.False => new Value(false),
        JsonValueKind.String => new Value(element.GetString()!),
        JsonValueKind.Number => new Value(element.GetDouble()),
        JsonValueKind.Array => new Value(element.EnumerateArray().Select(ToValue).ToList()),
        JsonValueKind.Object => new Value(ToStructure(element)),
        _ => new Value(),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Evaluating flag {FlagKey} failed unexpectedly")]
    private partial void LogEvaluationFailed(Exception exception, string flagKey);
}
