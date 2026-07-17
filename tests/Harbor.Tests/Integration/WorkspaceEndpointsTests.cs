using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

public class WorkspaceEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Create_ReturnsCreated_WithBootstrapAdminAndApiKey()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces",
            new CreateWorkspaceRequest("Acme", "Ada", "ada@acme.test"), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await ReadAsync<CreateWorkspaceResponse>(response);
        Assert.Equal("Acme", body.Workspace.Name);
        Assert.NotEqual(Guid.Empty, body.Workspace.Id);
        Assert.Equal(TeammateRole.Admin, body.Admin.Role);
        Assert.Equal("ada@acme.test", body.Admin.Email);
        Assert.StartsWith("hbk_", body.ApiKey);
    }

    [Fact]
    public async Task Create_WithBlankName_Returns400()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces",
            new CreateWorkspaceRequest("", "Ada", "ada@acme.test"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetById_RoundTrips()
    {
        var created = await CreateWorkspaceAsync("Roundtrip");

        var fetched = await ReadAsync<WorkspaceResponse>(
            await Client.GetAsync($"/api/workspaces/{created.Id}"));

        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("Roundtrip", fetched.Name);
    }

    [Fact]
    public async Task GetById_OtherWorkspace_Returns403()
    {
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyCallersWorkspace()
    {
        await CreateWorkspaceAsync("Someone Else's");
        var created = await CreateWorkspaceAsync("Listed");

        var all = await ReadAsync<List<WorkspaceResponse>>(await Client.GetAsync("/api/workspaces"));

        var only = Assert.Single(all);
        Assert.Equal(created.Id, only.Id);
    }
}
