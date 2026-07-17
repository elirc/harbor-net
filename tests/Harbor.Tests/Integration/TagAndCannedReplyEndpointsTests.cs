using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;

namespace Harbor.Tests.Integration;

public class TagAndCannedReplyEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<TagResponse> CreateTagAsync(Guid workspaceId, string name)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tags", new CreateTagRequest(name), Json);
        return await ReadAsync<TagResponse>(response);
    }

    [Fact]
    public async Task CreateTag_NormalizesName_AndListsAlphabetically()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTagAsync(workspace.Id, "  Billing ");
        await CreateTagAsync(workspace.Id, "api");

        var tags = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags"));

        Assert.Equal(["api", "billing"], tags.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task CreateTag_Duplicate_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTagAsync(workspace.Id, "vip");

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/tags", new CreateTagRequest("VIP"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TagConversation_ShowsUpInDetail_AndIsIdempotent()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var tag = await CreateTagAsync(workspace.Id, "urgent");

        var first = await Client.PutAsync($"/api/conversations/{convo.Id}/tags/{tag.Id}", null);
        var second = await Client.PutAsync($"/api/conversations/{convo.Id}/tags/{tag.Id}", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.Equal(["urgent"], detail.Tags);
    }

    [Fact]
    public async Task TagConversation_WithTagFromOtherWorkspace_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var other = await CreateWorkspaceAsync("Other");
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var foreignTag = await CreateTagAsync(other.Id, "foreign");

        var response = await Client.PutAsync($"/api/conversations/{convo.Id}/tags/{foreignTag.Id}", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UntagConversation_RemovesTag()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var tag = await CreateTagAsync(workspace.Id, "temp");
        await Client.PutAsync($"/api/conversations/{convo.Id}/tags/{tag.Id}", null);

        var remove = await Client.DeleteAsync($"/api/conversations/{convo.Id}/tags/{tag.Id}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.Empty(detail.Tags);
    }

    [Fact]
    public async Task DeleteTag_CascadesOffConversations()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var tag = await CreateTagAsync(workspace.Id, "doomed");
        await Client.PutAsync($"/api/conversations/{convo.Id}/tags/{tag.Id}", null);

        var delete = await Client.DeleteAsync($"/api/tags/{tag.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.Empty(detail.Tags);
    }

    [Fact]
    public async Task CannedReply_CrudLifecycle()
    {
        var workspace = await CreateWorkspaceAsync();

        var created = await ReadAsync<CannedReplyResponse>(await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/canned-replies",
            new CreateCannedReplyRequest("Refund-Policy", "Refund policy", "30-day refunds."), Json));
        Assert.Equal("refund-policy", created.Shortcut);

        var updated = await ReadAsync<CannedReplyResponse>(await Client.PutAsJsonAsync(
            $"/api/canned-replies/{created.Id}",
            new UpdateCannedReplyRequest("refund-policy", "Refund policy v2", "45-day refunds."), Json));
        Assert.Equal("Refund policy v2", updated.Title);

        var delete = await Client.DeleteAsync($"/api/canned-replies/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await Client.GetAsync($"/api/canned-replies/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task CannedReply_DuplicateShortcut_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/canned-replies",
            new CreateCannedReplyRequest("greet", "Greeting", "Hello!"), Json);

        var duplicate = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/canned-replies",
            new CreateCannedReplyRequest("GREET", "Other", "Hi."), Json);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task CannedReply_Search_MatchesShortcutTitleOrBody()
    {
        var workspace = await CreateWorkspaceAsync();
        await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/canned-replies",
            new CreateCannedReplyRequest("refund", "Refund policy", "We refund within 30 days."), Json);
        await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/canned-replies",
            new CreateCannedReplyRequest("hours", "Office hours", "We answer 9-5 CET."), Json);

        var results = await ReadAsync<List<CannedReplyResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/canned-replies?q=30 days"));

        Assert.Single(results);
        Assert.Equal("refund", results[0].Shortcut);
    }
}
