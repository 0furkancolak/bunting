using OpenFeature.Constant;

namespace Bunting.Sdk.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bunting-sdk-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SnapshotPath => Path.Combine(_directory, "nested", "snapshot.json");

    [Fact]
    public async Task The_last_ruleset_is_written_to_disk()
    {
        await using var server = new FakeBuntingServer();
        server.Publish(Flags.Bool("promo", true));
        await server.StartAsync();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server, o => o.SnapshotPath = SnapshotPath));

        await Harness.Eventually(() => File.Exists(SnapshotPath));

        Assert.Contains("\"promo\"", await File.ReadAllTextAsync(SnapshotPath));
        Assert.False(File.Exists(SnapshotPath + ".tmp"));
    }

    [Fact]
    public async Task An_application_starts_with_the_snapshot_while_the_server_is_unreachable_and_recovers_later()
    {
        await using var first = new FakeBuntingServer();
        first.Publish(Flags.Bool("promo", true));
        await first.StartAsync();
        await using (var warm = await Harness.StartAsync(Harness.FastOptions(first, o => o.SnapshotPath = SnapshotPath)))
        {
            await Harness.Eventually(() => File.Exists(SnapshotPath));
        }

        await using var down = new FakeBuntingServer(); // never started: unreachable
        down.Publish(Flags.Bool("promo", false));
        down.Publish(Flags.Bool("promo", false)); // a version different from the snapshot's
        await using var harness = await Harness.StartAsync(Harness.FastOptions(down, o =>
        {
            o.SnapshotPath = SnapshotPath;
            o.InitializeTimeout = TimeSpan.FromMilliseconds(300);
        }));

        var details = await harness.Client.GetBooleanDetailsAsync("promo", false);
        Assert.True(details.Value);
        Assert.Null(details.ErrorType is ErrorType.None ? null : details.ErrorType.ToString());
        await Harness.Eventually(() => harness.Events.Contains(ProviderEventTypes.ProviderStale));

        await down.StartAsync();

        await Harness.Eventually(async () => !await harness.Client.GetBooleanValueAsync("promo", true), because: "fresh ruleset replaces the snapshot");
        Assert.Contains(ProviderEventTypes.ProviderReady, harness.Events);
    }

    [Fact]
    public async Task A_corrupt_snapshot_is_ignored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        await File.WriteAllTextAsync(SnapshotPath, "{ not json");
        await using var server = new FakeBuntingServer();
        await using var harness = await Harness.StartAsync(Harness.FastOptions(server, o =>
        {
            o.SnapshotPath = SnapshotPath;
            o.InitializeTimeout = TimeSpan.FromMilliseconds(200);
        }));

        var details = await harness.Client.GetBooleanDetailsAsync("promo", false);

        Assert.False(details.Value);
        Assert.Equal(ErrorType.ProviderNotReady, details.ErrorType);
    }
}
