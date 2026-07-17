using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;

namespace Harbor.Tests.Integration;

public class WorkspaceEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Create_ReturnsCreated_WithLocation()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces", new CreateWorkspaceRequest("Acme"), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await ReadAsync<WorkspaceResponse>(response);
        Assert.Equal("Acme", body.Name);
        Assert.NotEqual(Guid.Empty, body.Id);
    }

    [Fact]
    public async Task Create_WithBlankName_Returns400()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces", new CreateWorkspaceRequest(""), Json);

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
    public async Task GetById_Unknown_Returns404()
    {
        var response = await Client.GetAsync($"/api/workspaces/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ContainsCreatedWorkspace()
    {
        var created = await CreateWorkspaceAsync("Listed");

        var all = await ReadAsync<List<WorkspaceResponse>>(await Client.GetAsync("/api/workspaces"));

        Assert.Contains(all, w => w.Id == created.Id);
    }
}
