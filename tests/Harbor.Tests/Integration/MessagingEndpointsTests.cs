using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

public class MessagingEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(Guid WorkspaceId, Guid InboxId, Guid ContactId, Guid TeammateId, ConversationDetailResponse Convo)>
        SetUpConversationAsync(int? slaMinutes = null)
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: slaMinutes);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        return (workspace.Id, inbox.Id, contact.Id, teammate.Id, convo);
    }

    private async Task<HttpResponseMessage> PostMessageAsync(
        Guid conversationId, AuthorType authorType, Guid authorId,
        MessageKind kind = MessageKind.Reply, string body = "hello")
    {
        return await Client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new AddMessageRequest(authorType, authorId, kind, body), Json);
    }

    private async Task<ConversationDetailResponse> GetConversationAsync(Guid id) =>
        await ReadAsync<ConversationDetailResponse>(await Client.GetAsync($"/api/conversations/{id}"));

    [Fact]
    public async Task TeammateReply_AppendsToThread_AndSetsFirstResponse()
    {
        var (_, _, _, teammateId, convo) = await SetUpConversationAsync(slaMinutes: 60);

        var response = await PostMessageAsync(convo.Id, AuthorType.Teammate, teammateId, body: "On it!");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var detail = await GetConversationAsync(convo.Id);
        Assert.Equal(2, detail.Messages.Count);
        Assert.NotNull(detail.FirstRespondedAt);
        var reply = detail.Messages[^1];
        Assert.Equal(teammateId, reply.AuthorTeammateId);
        Assert.Equal("On it!", reply.Body);
    }

    [Fact]
    public async Task SecondTeammateReply_DoesNotMoveFirstResponse()
    {
        var (_, _, _, teammateId, convo) = await SetUpConversationAsync(slaMinutes: 60);
        await PostMessageAsync(convo.Id, AuthorType.Teammate, teammateId);
        var afterFirst = await GetConversationAsync(convo.Id);

        await PostMessageAsync(convo.Id, AuthorType.Teammate, teammateId, body: "follow-up");
        var afterSecond = await GetConversationAsync(convo.Id);

        Assert.Equal(afterFirst.FirstRespondedAt, afterSecond.FirstRespondedAt);
    }

    [Fact]
    public async Task ContactReply_OnClosedConversation_ReopensIt()
    {
        var (_, _, contactId, _, convo) = await SetUpConversationAsync();
        await Client.PostAsJsonAsync(
            $"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        await PostMessageAsync(convo.Id, AuthorType.Contact, contactId, body: "still broken");

        var detail = await GetConversationAsync(convo.Id);
        Assert.Equal(ConversationState.Open, detail.State);
        Assert.Null(detail.ClosedAt);
    }

    [Fact]
    public async Task Note_DoesNotReopen_OrSetFirstResponse()
    {
        var (_, _, _, teammateId, convo) = await SetUpConversationAsync(slaMinutes: 60);
        await Client.PostAsJsonAsync(
            $"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var response = await PostMessageAsync(
            convo.Id, AuthorType.Teammate, teammateId, MessageKind.Note, "internal context");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var detail = await GetConversationAsync(convo.Id);
        Assert.Equal(ConversationState.Closed, detail.State);
        Assert.Null(detail.FirstRespondedAt);
        Assert.Equal(MessageKind.Note, detail.Messages[^1].Kind);
    }

    [Fact]
    public async Task Note_AuthoredByContact_Returns422()
    {
        var (_, _, contactId, _, convo) = await SetUpConversationAsync();

        var response = await PostMessageAsync(convo.Id, AuthorType.Contact, contactId, MessageKind.Note);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ContactMessage_FromDifferentContact_Returns422()
    {
        var (workspaceId, _, _, _, convo) = await SetUpConversationAsync();
        var stranger = await CreateContactAsync(workspaceId, "Stranger");

        var response = await PostMessageAsync(convo.Id, AuthorType.Contact, stranger.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TeammateMessage_FromOtherWorkspace_Returns422()
    {
        var (_, _, _, _, convo) = await SetUpConversationAsync();
        var otherWorkspace = await CreateWorkspaceAsync("Other");
        var outsider = await CreateTeammateAsync(otherWorkspace.Id);

        var response = await PostMessageAsync(convo.Id, AuthorType.Teammate, outsider.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Message_OnUnknownConversation_Returns404()
    {
        var response = await PostMessageAsync(Guid.NewGuid(), AuthorType.Contact, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Thread_IsOrderedChronologically()
    {
        var (_, _, contactId, teammateId, convo) = await SetUpConversationAsync();
        await PostMessageAsync(convo.Id, AuthorType.Teammate, teammateId, body: "first reply");
        await PostMessageAsync(convo.Id, AuthorType.Contact, contactId, body: "thanks");

        var detail = await GetConversationAsync(convo.Id);

        Assert.Equal(3, detail.Messages.Count);
        Assert.True(detail.Messages
            .Zip(detail.Messages.Skip(1))
            .All(pair => pair.First.CreatedAt <= pair.Second.CreatedAt));
        Assert.Equal("thanks", detail.Messages[^1].Body);
    }
}
