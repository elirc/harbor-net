using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

public class ConversationEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Start_CreatesOpenConversation_WithOpeningMessage()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id, "Mario");

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(inbox.Id, contact.Id, "Login broken", "I cannot log in."), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var convo = await ReadAsync<ConversationDetailResponse>(response);
        Assert.Equal(ConversationState.Open, convo.State);
        Assert.Equal("Login broken", convo.Subject);
        var message = Assert.Single(convo.Messages);
        Assert.Equal(AuthorType.Contact, message.AuthorType);
        Assert.Equal(contact.Id, message.AuthorContactId);
        Assert.Equal(MessageKind.Reply, message.Kind);
        Assert.Equal("I cannot log in.", message.Body);
    }

    [Fact]
    public async Task Start_InInboxWithSla_SetsFirstResponseDueAt()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: 60);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.NotNull(convo.FirstResponseDueAt);
        var expected = convo.CreatedAt.AddMinutes(60);
        Assert.Equal(expected, convo.FirstResponseDueAt!.Value);
    }

    [Fact]
    public async Task Start_InInboxWithoutSla_LeavesFirstResponseDueAtNull()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Null(convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task Start_WithInboxFromOtherWorkspace_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var other = await CreateWorkspaceAsync("Other");
        var foreignInbox = await CreateInboxAsync(other.Id);
        ActAsAdminOf(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(foreignInbox.Id, contact.Id, null, "hi"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Start_WithUnknownContact_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(inbox.Id, Guid.NewGuid(), null, "hi"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsNewestActivityFirst()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "first");
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "second");

        var list = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations"));

        Assert.Equal(2, list.Count);
        var firstIndex = list.FindIndex(c => c.Id == first.Id);
        var secondIndex = list.FindIndex(c => c.Id == second.Id);
        Assert.True(secondIndex < firstIndex, "most recent conversation should come first");
    }

    [Fact]
    public async Task GetById_Unknown_Returns404()
    {
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
