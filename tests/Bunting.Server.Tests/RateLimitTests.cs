using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Bunting.Server.Tests;

[Collection("server")]
public class RateLimitTests(ServerFixture fixture)
{
    [Fact]
    public async Task Requests_beyond_the_per_key_limit_get_429_with_retry_after_and_other_keys_are_unaffected()
    {
        var api = fixture.Client();
        var project = await api.NewProject();
        var (_, keyA) = await api.NewKey(project, "production", "Server");
        var (_, keyB) = await api.NewKey(project, "production", "Server");

        using var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            OptionsServiceCollectionExtensions.Configure<Bunting.Server.Infrastructure.RateLimitOptions>(s, o =>
            {
                o.PermitLimit = 5;
                o.WindowSeconds = 60;
            })));

        HttpClient Client(string key)
        {
            var http = factory.CreateClient();
            http.DefaultRequestHeaders.Add("X-API-Key", key);
            return http;
        }

        var a = Client(keyA);
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 8; i++)
        {
            var response = await a.Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal("60", limited!.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.OK, (await Client(keyB).Ofrep("/ofrep/v1/evaluate/flags", new { targetingKey = "u" })).StatusCode);
    }
}
