using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Hosting;
using OpenFeature.Model;

namespace Bunting.Sdk.Tests;

public class TelemetryAndPrivacyTests
{
    private sealed class CapturingLogger : ILogger<BuntingProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception) + exception);
            }
        }
    }

    [Fact]
    public async Task Evaluations_are_counted_by_flag_and_reason()
    {
        var key = "metric-" + Guid.NewGuid().ToString("N")[..8];
        var counted = new List<(string Flag, string Reason, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BuntingTelemetry.SourceName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "bunting.sdk.evaluations")
            {
                return;
            }

            string? flag = null, reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "feature_flag.key")
                {
                    flag = tag.Value as string;
                }
                else if (tag.Key == "feature_flag.result.reason")
                {
                    reason = tag.Value as string;
                }
            }

            if (flag == key)
            {
                lock (counted)
                {
                    counted.Add((flag, reason!, value));
                }
            }
        });
        listener.Start();

        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool(key, true));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server));

        await harness.Client.GetBooleanValueAsync(key, false);
        await harness.Client.GetStringValueAsync(key, "x"); // type mismatch

        lock (counted)
        {
            Assert.Contains(counted, c => c.Reason == "default");
            Assert.Contains(counted, c => c.Reason == "error");
        }
    }

    [Fact]
    public async Task Ruleset_age_and_connection_state_are_observable()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("f", true));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server));
        await Harness.Eventually(() => harness.Provider.ConnectionState == ConnectionState.Streaming);

        var seen = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BuntingTelemetry.SourceName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => seen.Add(instrument.Name));
        listener.SetMeasurementEventCallback<int>((instrument, _, _, _) => seen.Add(instrument.Name));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains("bunting.sdk.ruleset.age", seen);
        Assert.Contains("bunting.sdk.connection.state", seen);
    }

    [Fact]
    public async Task Context_data_and_the_api_key_never_appear_in_logs()
    {
        var logger = new CapturingLogger();
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("f", true, Flags.Rule("r", "email", "secret@example.com", "on")));
        await server.StartAsync();
        var provider = new BuntingProvider(Harness.FastOptions(server), logger);
        await using var _ = provider;
        await Api.Instance.SetProviderAsync("privacy-" + Guid.NewGuid().ToString("N"), provider);

        var context = EvaluationContext.Builder().SetTargetingKey("user-secret-42").Set("email", "secret@example.com").Build();
        await provider.ResolveBooleanValueAsync("f", false, context);
        await provider.ResolveStringValueAsync("f", "x", context); // error path
        await provider.ResolveBooleanValueAsync("missing", false, context);

        lock (logger.Messages)
        {
            Assert.NotEmpty(logger.Messages);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("user-secret-42") || m.Contains("secret@example.com") || m.Contains(FakeBuntingServer.ApiKey));
        }
    }

    [Fact]
    public async Task The_provider_can_be_registered_through_dependency_injection()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("di-flag", true));
        await server.StartAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddOpenFeature(o => o.AddBuntingProvider("di-domain", options =>
        {
            options.ServerUrl = server.Url;
            options.ApiKey = FakeBuntingServer.ApiKey;
        }));
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredKeyedService<IFeatureClient>("di-domain");
            await Harness.Eventually(async () => await client.GetBooleanValueAsync("di-flag", false));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void Missing_configuration_is_reported_when_the_provider_is_created()
    {
        Assert.Throws<ArgumentException>(() => new BuntingProvider(new BuntingOptions { ApiKey = "bnt_srv_x" }));
        Assert.Throws<ArgumentException>(() => new BuntingProvider(new BuntingOptions { ServerUrl = new Uri("http://localhost") }));
    }
}
