using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>Inbound email ingestion, threading, and outbound reply rendering.</summary>
public class EmailChannelTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private const string InboxAddress = "support@acme.test";

    private async Task<InboxResponse> CreateEmailInboxAsync(
        Guid workspaceId, string address = InboxAddress, bool autoAssign = false)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/inboxes",
            new CreateInboxRequest("Support", null, autoAssign, address), Json);
        return await ReadAsync<InboxResponse>(response);
    }

    private async Task<HttpResponseMessage> PostInboundAsync(
        Guid workspaceId, string from = "jane@example.test", string? fromName = "Jane Doe",
        string to = InboxAddress, string? subject = "Cannot log in",
        string body = "I cannot log in to my account.",
        string? messageId = "<first@mail.test>", string? inReplyTo = null,
        IReadOnlyList<string>? references = null) =>
        await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/email/inbound",
            new InboundEmailRequest(from, fromName, to, subject, body, messageId, inReplyTo, references),
            Json);

    private async Task<InboundEmailResponse> IngestAsync(Guid workspaceId, params object[] _) =>
        await ReadAsync<InboundEmailResponse>(await PostInboundAsync(workspaceId));

    private async Task<ConversationDetailResponse> GetConversationAsync(Guid id) =>
        await ReadAsync<ConversationDetailResponse>(await Client.GetAsync($"/api/conversations/{id}"));

    [Fact]
    public async Task Inbound_StartsAConversation_AndCreatesTheContact()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        var result = await IngestAsync(workspace.Id);

        Assert.True(result.StartedNewConversation);
        Assert.True(result.CreatedContact);

        var convo = await GetConversationAsync(result.ConversationId);
        Assert.Equal("Cannot log in", convo.Subject);
        Assert.Equal(MessageChannel.Email, convo.Channel);
        var message = Assert.Single(convo.Messages);
        Assert.Equal(MessageChannel.Email, message.Channel);
        Assert.Equal("<first@mail.test>", message.EmailMessageId);
        Assert.Equal(AuthorType.Contact, message.AuthorType);

        var contact = await ReadAsync<ContactResponse>(
            await Client.GetAsync($"/api/contacts/{result.ContactId}"));
        Assert.Equal("Jane Doe", contact.Name);
        Assert.Equal("jane@example.test", contact.Email);
    }

    [Fact]
    public async Task Inbound_ReusesAnExistingContact_ByEmail()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var existing = await CreateContactAsync(workspace.Id, "Jane", "jane@example.test");

        var result = await IngestAsync(workspace.Id);

        Assert.False(result.CreatedContact);
        Assert.Equal(existing.Id, result.ContactId);
    }

    [Fact]
    public async Task Inbound_MatchesAddressesCaseInsensitively()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        var response = await PostInboundAsync(
            workspace.Id, from: "Jane@Example.TEST", to: "SUPPORT@ACME.TEST");

        var result = await ReadAsync<InboundEmailResponse>(response);
        Assert.True(result.StartedNewConversation);
    }

    [Fact]
    public async Task Inbound_FallsBackToTheAddress_WhenNoNameIsGiven()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        var result = await ReadAsync<InboundEmailResponse>(
            await PostInboundAsync(workspace.Id, fromName: null));

        var contact = await ReadAsync<ContactResponse>(
            await Client.GetAsync($"/api/contacts/{result.ContactId}"));
        Assert.Equal("jane@example.test", contact.Name);
    }

    [Fact]
    public async Task Inbound_ToAnUnknownAddress_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        var response = await PostInboundAsync(workspace.Id, to: "nobody@acme.test");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Inbound_ThreadsOntoTheConversation_ByInReplyTo()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var first = await IngestAsync(workspace.Id);

        var second = await ReadAsync<InboundEmailResponse>(await PostInboundAsync(
            workspace.Id, subject: "Re: Cannot log in", body: "Still broken.",
            messageId: "<second@mail.test>", inReplyTo: "<first@mail.test>"));

        Assert.False(second.StartedNewConversation);
        Assert.Equal(first.ConversationId, second.ConversationId);
        var convo = await GetConversationAsync(first.ConversationId);
        Assert.Equal(2, convo.Messages.Count);
    }

    [Fact]
    public async Task Inbound_ThreadsByReferences_WhenInReplyToIsUnknown()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var first = await IngestAsync(workspace.Id);

        var second = await ReadAsync<InboundEmailResponse>(await PostInboundAsync(
            workspace.Id, body: "Adding my colleague.", messageId: "<third@mail.test>",
            inReplyTo: "<never-seen@mail.test>",
            references: ["<first@mail.test>", "<never-seen@mail.test>"]));

        Assert.False(second.StartedNewConversation);
        Assert.Equal(first.ConversationId, second.ConversationId);
    }

    [Fact]
    public async Task Inbound_WithUnknownThreadHeaders_StartsANewConversation()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var first = await IngestAsync(workspace.Id);

        var second = await ReadAsync<InboundEmailResponse>(await PostInboundAsync(
            workspace.Id, subject: "A different problem", messageId: "<other@mail.test>",
            inReplyTo: "<unknown@mail.test>"));

        Assert.True(second.StartedNewConversation);
        Assert.NotEqual(first.ConversationId, second.ConversationId);
    }

    [Fact]
    public async Task Inbound_DoesNotThreadAcrossWorkspaces()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateEmailInboxAsync(other.Id, "support@other.test");
        await ReadAsync<InboundEmailResponse>(
            await PostInboundAsync(other.Id, to: "support@other.test"));

        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        // Same In-Reply-To, but that Message-ID lives in another workspace.
        var result = await ReadAsync<InboundEmailResponse>(await PostInboundAsync(
            workspace.Id, messageId: "<mine@mail.test>", inReplyTo: "<first@mail.test>"));

        Assert.True(result.StartedNewConversation);
    }

    [Fact]
    public async Task Inbound_ReopensAClosedConversation()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var first = await IngestAsync(workspace.Id);
        await Client.PostAsJsonAsync($"/api/conversations/{first.ConversationId}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        await PostInboundAsync(
            workspace.Id, body: "It happened again.", messageId: "<again@mail.test>",
            inReplyTo: "<first@mail.test>");

        var convo = await GetConversationAsync(first.ConversationId);
        Assert.Equal(ConversationState.Open, convo.State);
    }

    [Fact]
    public async Task Inbound_ComposesWithAutoAssignmentAndSla()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateEmailInboxAsync(workspace.Id, autoAssign: true);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 30);

        var result = await IngestAsync(workspace.Id);

        var convo = await GetConversationAsync(result.ConversationId);
        // The same start-time rules a chat conversation gets.
        Assert.NotNull(convo.AssignedTeammateId);
        Assert.NotNull(convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(30), convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task Inbound_FiresWebhooks()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var subscription = await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/webhooks",
                new CreateWebhookRequest("https://example.test/hooks",
                    [WebhookEventType.ConversationCreated, WebhookEventType.MessageCreated]),
                Json));

        var first = await IngestAsync(workspace.Id);
        await PostInboundAsync(workspace.Id, body: "Still broken.",
            messageId: "<second@mail.test>", inReplyTo: "<first@mail.test>");

        var deliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscription.Id}/deliveries"));

        Assert.Contains(deliveries, d => d.EventType == WebhookEventType.ConversationCreated);
        Assert.Contains(deliveries, d => d.EventType == WebhookEventType.MessageCreated);
        Assert.NotEqual(Guid.Empty, first.ConversationId);
    }

    [Fact]
    public async Task TeammateReply_OnAnEmailConversation_IsStampedForEmail()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var result = await IngestAsync(workspace.Id);

        var reply = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply,
                    "Try resetting your password."),
                Json));

        Assert.Equal(MessageChannel.Email, reply.Channel);
        Assert.Equal($"<{reply.Id:D}@harbor.local>", reply.EmailMessageId);
    }

    [Fact]
    public async Task InternalNote_OnAnEmailConversation_StaysOnChat()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var result = await IngestAsync(workspace.Id);

        var note = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Note,
                    "VIP customer."),
                Json));

        Assert.Equal(MessageChannel.Chat, note.Channel);
        Assert.Null(note.EmailMessageId);
    }

    [Fact]
    public async Task Render_ProducesTheOutboundEmailWithThreadingHeaders()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var result = await IngestAsync(workspace.Id);
        var reply = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply,
                    "Try resetting your password."),
                Json));

        var email = await ReadAsync<RenderedEmailResponse>(
            await Client.GetAsync($"/api/messages/{reply.Id}/email"));

        Assert.Equal(InboxAddress, email.From);
        Assert.Equal("jane@example.test", email.To);
        Assert.Equal("Re: Cannot log in", email.Subject);
        Assert.Equal("<first@mail.test>", email.InReplyTo);
        Assert.Equal(["<first@mail.test>"], email.References);
        Assert.Equal("Try resetting your password.", email.Body);
        Assert.Equal(reply.EmailMessageId, email.EmailMessageId);
    }

    [Fact]
    public async Task Render_ThenTheCustomerReplies_ThreadsBackOntoTheConversation()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var result = await IngestAsync(workspace.Id);
        var reply = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "Try this."),
                Json));
        var email = await ReadAsync<RenderedEmailResponse>(
            await Client.GetAsync($"/api/messages/{reply.Id}/email"));

        // The customer replies to the address we sent from, quoting our id.
        var back = await ReadAsync<InboundEmailResponse>(await PostInboundAsync(
            workspace.Id, body: "That worked, thanks!", messageId: "<thanks@mail.test>",
            inReplyTo: email.EmailMessageId));

        Assert.False(back.StartedNewConversation);
        Assert.Equal(result.ConversationId, back.ConversationId);
        var convo = await GetConversationAsync(result.ConversationId);
        Assert.Equal(3, convo.Messages.Count);
    }

    [Fact]
    public async Task Render_OfANote_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var result = await IngestAsync(workspace.Id);
        var note = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Note, "Internal"),
                Json));

        var response = await Client.GetAsync($"/api/messages/{note.Id}/email");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Render_OnAChatOnlyInbox_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id, "Jane", "jane@example.test");
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var reply = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "Hi"), Json));

        var response = await Client.GetAsync($"/api/messages/{reply.Id}/email");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Render_ForForeignMessage_Returns404()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateEmailInboxAsync(other.Id, "support@other.test");
        var teammate = await CreateTeammateAsync(other.Id);
        var result = await ReadAsync<InboundEmailResponse>(
            await PostInboundAsync(other.Id, to: "support@other.test"));
        var reply = await ReadAsync<MessageResponse>(
            await Client.PostAsJsonAsync($"/api/conversations/{result.ConversationId}/messages",
                new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "Hi"), Json));
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/messages/{reply.Id}/email");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChannelFilter_SeparatesEmailFromChat()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);
        var chatInbox = await CreateInboxAsync(workspace.Id, "Chat");
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, chatInbox.Id, contact.Id, "chat one");
        var emailed = await IngestAsync(workspace.Id);

        var byEmail = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?channel=Email"));
        var byChat = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?channel=Chat"));

        Assert.Equal(emailed.ConversationId, Assert.Single(byEmail).Id);
        Assert.Equal("chat one", Assert.Single(byChat).Subject);
    }

    [Fact]
    public async Task Inbox_WithADuplicateAddress_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateEmailInboxAsync(workspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/inboxes",
            new CreateInboxRequest("Second", null, false, "SUPPORT@acme.test"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Inbox_WithAnInvalidAddress_Returns400()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/inboxes",
            new CreateInboxRequest("Bad", null, false, "not-an-address"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatOnlyInboxes_CanCoexist()
    {
        var workspace = await CreateWorkspaceAsync();

        var first = await CreateInboxAsync(workspace.Id, "Chat one");
        var second = await CreateInboxAsync(workspace.Id, "Chat two");

        // Null addresses must not collide with each other.
        Assert.Null(first.EmailAddress);
        Assert.Null(second.EmailAddress);
    }

    [Fact]
    public async Task Inbound_ForForeignWorkspace_Returns403()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await PostInboundAsync(other.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
