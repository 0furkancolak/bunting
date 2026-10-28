using System.Diagnostics;
using OpenFeature;
using OpenFeature.Constant;

namespace Bunting.Sdk.Tests;

/// <summary>A provider registered with the OpenFeature API under a unique domain, with its events recorded.</summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly List<ProviderEventTypes> _events = [];

    private Harness(BuntingProvider provider, IFeatureClient client)
    {
        Provider = provider;
        Client = client;
    }

    public BuntingProvider Provider { get; }

    public IFeatureClient Client { get; }

    public IReadOnlyList<ProviderEventTypes> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>Options with timings short enough for tests.</summary>
    public static BuntingOptions FastOptions(FakeBuntingServer server, Action<BuntingOptions>? tweak = null)
    {
        var options = new BuntingOptions
        {
            ServerUrl = server.Url,
            ApiKey = FakeBuntingServer.ApiKey,
            InitializeTimeout = TimeSpan.FromSeconds(3),
            PollingInterval = TimeSpan.FromMilliseconds(200),
            MinReconnectDelay = TimeSpan.FromMilliseconds(50),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
            RequestTimeout = TimeSpan.FromSeconds(2),
            StaleAfter = TimeSpan.FromMilliseconds(600),
            StreamIdleTimeout = TimeSpan.FromSeconds(5),
        };
        tweak?.Invoke(options);
        return options;
    }

    public static async Task<Harness> StartAsync(BuntingOptions options)
    {
        var domain = "sdk-test-" + Guid.NewGuid().ToString("N");
        var provider = new BuntingProvider(options);
        var client = Api.Instance.GetClient(domain);
        var harness = new Harness(provider, client);
        foreach (var type in Enum.GetValues<ProviderEventTypes>())
        {
            client.AddHandler(type, _ =>
            {
                lock (harness._events)
                {
                    harness._events.Add(type);
                }
            });
        }

        await Api.Instance.SetProviderAsync(domain, provider);
        return harness;
    }

    public static async Task Eventually(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        while (deadline.Elapsed < limit)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Condition not met within {limit}. {because}");
    }

    public static Task Eventually(Func<bool> condition, TimeSpan? timeout = null, string? because = null) =>
        Eventually(() => Task.FromResult(condition()), timeout, because);

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
}
