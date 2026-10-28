using System.Net;

namespace Bunting.Server.Tests;

/// <summary>The role-by-endpoint matrix again, for users whose role comes from a project membership.</summary>
[Collection("server")]
public class MemberAuthorizationTests(ServerFixture fixture)
{
    public static TheoryData<string, string> Matrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in AuthorizationTests.Endpoints)
        {
            foreach (var role in new[] { "Viewer", "Editor", "Owner", "NonMember" })
            {
                data.Add(endpoint.Name, role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Member_role_by_endpoint_matrix(string endpointName, string caller)
    {
        var endpoint = AuthorizationTests.Endpoints.Single(e => e.Name == endpointName);
        var admin = fixture.Client();
        var project = await admin.NewProject();
        var seedVersion = await admin.NewBoolFlag(project, "seed");
        long? ifMatch = null;
        if (endpoint.NeedsIfMatch)
        {
            ifMatch = endpoint.Url(project).Contains("/environments/")
                ? Api.ETag(await admin.Get(endpoint.Url(project)))
                : seedVersion;
        }

        Api api;
        if (caller == "NonMember")
        {
            var (email, _) = await fixture.NewUser();
            api = await fixture.LoginAsync(email, ServerFixture.UserPassword);
        }
        else
        {
            api = await fixture.MemberClient(project, caller);
        }

        var response = await api.SendAsync(endpoint.Method, endpoint.Url(project), endpoint.Body(), ifMatch);

        // Creating projects is reserved for instance administrators; listing projects is open to any signed-in user.
        var required = endpoint.Name == "create project" ? "Admin" : endpoint.RequiredRole;
        var rank = new[] { "Viewer", "Editor", "Owner", "Admin" };
        var allowed = endpoint.Name == "list projects"
            || (caller != "NonMember" && Array.IndexOf(rank, caller) >= Array.IndexOf(rank, required));
        if (allowed)
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True((int)response.StatusCode < 500, $"{endpoint.Name}: {(int)response.StatusCode}");
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_viewer_member_cannot_change_anything_even_with_a_valid_etag()
    {
        var admin = fixture.Client();
        var project = await admin.NewProject();
        await admin.NewBoolFlag(project, "seed");
        var url = $"/api/v1/projects/{project}/environments/production/flags/seed";
        var version = Api.ETag(await admin.Get(url));
        var viewer = await fixture.MemberClient(project, "Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Patch(url, new { enabled = true }, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/projects/{project}/flags", new { key = "x", name = "x", type = "Boolean", variants = new { on = true, off = false } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/v1/projects/{project}/environments/production/api-keys", new { name = "k", type = "Server" })).StatusCode);
        Assert.False((await Api.Body(await admin.Get(url))).GetProperty("enabled").GetBoolean());
    }
}
