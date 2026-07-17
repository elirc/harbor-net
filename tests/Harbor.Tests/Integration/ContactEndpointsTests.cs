using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;

namespace Harbor.Tests.Integration;

public class ContactEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Create_And_GetById_RoundTrips()
    {
        var workspace = await CreateWorkspaceAsync();

        var created = await CreateContactAsync(workspace.Id, "Mario Rossi", "mario@example.com");
        var fetched = await ReadAsync<ContactResponse>(
            await Client.GetAsync($"/api/contacts/{created.Id}"));

        Assert.Equal("Mario Rossi", fetched.Name);
        Assert.Equal("mario@example.com", fetched.Email);
        Assert.Equal(workspace.Id, fetched.WorkspaceId);
    }

    [Fact]
    public async Task Create_InOtherWorkspace_Returns403()
    {
        await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{Guid.NewGuid()}/contacts",
            new CreateContactRequest("Ghost", null, null), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidEmail_Returns400()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/contacts",
            new CreateContactRequest("Bad Email", "not-an-email", null), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_WithSearch_FiltersByNameOrEmail()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactAsync(workspace.Id, "Alice Anderson", "alice@example.com");
        await CreateContactAsync(workspace.Id, "Bob Brown", "bob@sample.org");

        var byName = await ReadAsync<List<ContactResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/contacts?q=alice"));
        var byEmail = await ReadAsync<List<ContactResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/contacts?q=sample.org"));

        Assert.Single(byName);
        Assert.Equal("Alice Anderson", byName[0].Name);
        Assert.Single(byEmail);
        Assert.Equal("Bob Brown", byEmail[0].Name);
    }

    [Fact]
    public async Task Update_ChangesFields()
    {
        var workspace = await CreateWorkspaceAsync();
        var contact = await CreateContactAsync(workspace.Id, "Old Name");

        var response = await Client.PutAsJsonAsync(
            $"/api/contacts/{contact.Id}",
            new UpdateContactRequest("New Name", "new@example.com", "ext-1"), Json);
        var updated = await ReadAsync<ContactResponse>(response);

        Assert.Equal("New Name", updated.Name);
        Assert.Equal("new@example.com", updated.Email);
        Assert.Equal("ext-1", updated.ExternalId);
    }

    [Fact]
    public async Task Delete_WithoutConversations_Returns204_ThenGone()
    {
        var workspace = await CreateWorkspaceAsync();
        var contact = await CreateContactAsync(workspace.Id, "Deletable");

        var delete = await Client.DeleteAsync($"/api/contacts/{contact.Id}");
        var get = await Client.GetAsync($"/api/contacts/{contact.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_WithConversations_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id, "Busy Contact");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var delete = await Client.DeleteAsync($"/api/contacts/{contact.Id}");

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }
}
