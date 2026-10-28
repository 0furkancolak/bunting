using System.Net;

namespace Bunting.Server.Tests;

[Collection("server")]
public class AuthorizationTests(ServerFixture fixture)
{
    internal sealed record Endpoint(string Name, HttpMethod Method, Func<string, string> Url, Func<object?> Body, string RequiredRole, bool NeedsIfMatch = false);

    internal static readonly Endpoint[] Endpoints =
    [
        new("list projects", HttpMethod.Get, _ => "/api/v1/projects", () => null, "Viewer"),
        new("get project", HttpMethod.Get, p => $"/api/v1/projects/{p}", () => null, "Viewer"),
        new("create project", HttpMethod.Post, _ => "/api/v1/projects", () => new { key = "auth-" + Guid.NewGuid().ToString("N")[..10], name = "x" }, "Owner"),
        new("rename project", HttpMethod.Patch, p => $"/api/v1/projects/{p}", () => new { name = "renamed" }, "Owner"),
        new("list environments", HttpMethod.Get, p => $"/api/v1/projects/{p}/environments", () => null, "Viewer"),
        new("create environment", HttpMethod.Post, p => $"/api/v1/projects/{p}/environments", () => new { key = "e" + Guid.NewGuid().ToString("N")[..8], name = "x" }, "Owner"),
        new("rename environment", HttpMethod.Patch, p => $"/api/v1/projects/{p}/environments/staging", () => new { name = "Stage" }, "Owner"),
        new("list flags", HttpMethod.Get, p => $"/api/v1/projects/{p}/flags", () => null, "Viewer"),
        new("get flag", HttpMethod.Get, p => $"/api/v1/projects/{p}/flags/seed", () => null, "Viewer"),
        new("create flag", HttpMethod.Post, p => $"/api/v1/projects/{p}/flags", () => new { key = "n" + Guid.NewGuid().ToString("N")[..8], name = "x", type = "Boolean", variants = new { on = true, off = false } }, "Editor"),
        new("update flag", HttpMethod.Put, p => $"/api/v1/projects/{p}/flags/seed", () => new { name = "y", variants = new { on = true, off = false }, clientSideAvailable = false, archived = false }, "Editor", true),
        new("get flag config", HttpMethod.Get, p => $"/api/v1/projects/{p}/environments/production/flags/seed", () => null, "Viewer"),
        new("put flag config", HttpMethod.Put, p => $"/api/v1/projects/{p}/environments/production/flags/seed", () => new { enabled = true, offVariant = "off", rules = Array.Empty<object>(), fallthrough = new { variant = "off" } }, "Editor", true),
        new("kill switch", HttpMethod.Patch, p => $"/api/v1/projects/{p}/environments/production/flags/seed", () => new { enabled = true }, "Editor", true),
        new("list segments", HttpMethod.Get, p => $"/api/v1/projects/{p}/environments/production/segments", () => null, "Viewer"),
        new("create segment", HttpMethod.Post, p => $"/api/v1/projects/{p}/environments/production/segments", () => new { key = "s" + Guid.NewGuid().ToString("N")[..8] }, "Editor"),
        new("query audit", HttpMethod.Get, p => $"/api/v1/projects/{p}/audit", () => null, "Viewer"),
        new("delete flag (not archived)", HttpMethod.Delete, p => $"/api/v1/projects/{p}/flags/seed", () => null, "Editor", true),
        new("delete environment", HttpMethod.Delete, p => $"/api/v1/projects/{p}/environments/staging", () => null, "Owner"),
        new("delete project", HttpMethod.Delete, p => $"/api/v1/projects/{p}", () => null, "Owner"),
    ];

    public static TheoryData<string, string, string> Matrix()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var endpoint in Endpoints)
        {
            foreach (var role in new[] { "none", "invalid", "Viewer", "Editor", "Owner" })
            {
                data.Add(endpoint.Name, role, role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Role_by_endpoint_matrix(string endpointName, string caller, string _)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var owner = fixture.Client();
        var project = await owner.NewProject();
        var seedVersion = await owner.NewBoolFlag(project, "seed");
        long? ifMatch = null;
        if (endpoint.NeedsIfMatch)
        {
            ifMatch = endpoint.Url(project).Contains("/environments/")
                ? Api.ETag(await owner.Get(endpoint.Url(project)))
                : seedVersion;
        }

        var api = caller switch
        {
            "none" => new Api(fixture.Factory.CreateClient()),
            "invalid" => fixture.Client("wrong-token"),
            "Viewer" => fixture.Client(ServerFixture.ViewerToken),
            "Editor" => fixture.Client(ServerFixture.EditorToken),
            _ => fixture.Client(ServerFixture.OwnerToken),
        };

        var response = await api.SendAsync(endpoint.Method, endpoint.Url(project), endpoint.Body(), ifMatch);

        var rank = new[] { "Viewer", "Editor", "Owner" };
        if (caller is "none" or "invalid")
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        else if (Array.IndexOf(rank, caller) < Array.IndexOf(rank, endpoint.RequiredRole))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        else
        {
            // Allowed: anything except an auth failure (some allowed calls legitimately return 2xx/4xx business results).
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True((int)response.StatusCode is < 500, $"{endpoint.Name}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Viewer_cannot_change_anything_even_with_a_valid_etag()
    {
        var owner = fixture.Client();
        var project = await owner.NewProject();
        await owner.NewBoolFlag(project, "seed");
        var url = $"/api/v1/projects/{project}/environments/production/flags/seed";
        var version = Api.ETag(await owner.Get(url));

        var viewer = fixture.Client(ServerFixture.ViewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Patch(url, new { enabled = true }, version)).StatusCode);
        Assert.False((await Api.Body(await owner.Get(url))).GetProperty("enabled").GetBoolean());
    }
}
