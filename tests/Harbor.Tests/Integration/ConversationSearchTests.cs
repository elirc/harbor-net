using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

public class ConversationSearchTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<List<ConversationSummaryResponse>> SearchAsync(Guid workspaceId, string queryString) =>
        await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspaceId}/conversations?{queryString}"));

    [Fact]
    public async Task FilterByState_ReturnsOnlyMatching()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var open = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "open one");
        var closed = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "closed one");
        await Client.PostAsJsonAsync(
            $"/api/conversations/{closed.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var openOnly = await SearchAsync(workspace.Id, "state=Open");
        var closedOnly = await SearchAsync(workspace.Id, "state=Closed");

        Assert.Equal([open.Id], openOnly.Select(c => c.Id).ToArray());
        Assert.Equal([closed.Id], closedOnly.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task FilterByInbox_AndContact()
    {
        var workspace = await CreateWorkspaceAsync();
        var inboxA = await CreateInboxAsync(workspace.Id, "A");
        var inboxB = await CreateInboxAsync(workspace.Id, "B");
        var contactX = await CreateContactAsync(workspace.Id, "X");
        var contactY = await CreateContactAsync(workspace.Id, "Y");
        var inA = await StartConversationAsync(workspace.Id, inboxA.Id, contactX.Id);
        var inB = await StartConversationAsync(workspace.Id, inboxB.Id, contactY.Id);

        var byInbox = await SearchAsync(workspace.Id, $"inboxId={inboxA.Id}");
        var byContact = await SearchAsync(workspace.Id, $"contactId={contactY.Id}");

        Assert.Equal([inA.Id], byInbox.Select(c => c.Id).ToArray());
        Assert.Equal([inB.Id], byContact.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task FilterByAssignee_AndUnassigned()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var team = await CreateTeamAsync(workspace.Id, "Squad");
        var mine = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "mine");
        var teams = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "team's");
        var nobodys = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "nobody's");
        await Client.PostAsJsonAsync($"/api/conversations/{mine.Id}/assignment",
            new AssignConversationRequest(teammate.Id, null), Json);
        await Client.PostAsJsonAsync($"/api/conversations/{teams.Id}/assignment",
            new AssignConversationRequest(null, team.Id), Json);

        var byTeammate = await SearchAsync(workspace.Id, $"assignedTeammateId={teammate.Id}");
        var byTeam = await SearchAsync(workspace.Id, $"assignedTeamId={team.Id}");
        var unassigned = await SearchAsync(workspace.Id, "unassigned=true");

        Assert.Equal([mine.Id], byTeammate.Select(c => c.Id).ToArray());
        Assert.Equal([teams.Id], byTeam.Select(c => c.Id).ToArray());
        Assert.Equal([nobodys.Id], unassigned.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task FilterByTag_MatchesCaseInsensitively()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var tagged = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "tagged");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "untagged");
        var tag = await ReadAsync<TagResponse>(await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/tags", new CreateTagRequest("billing"), Json));
        await Client.PutAsync($"/api/conversations/{tagged.Id}/tags/{tag.Id}", null);

        var results = await SearchAsync(workspace.Id, "tag=BILLING");

        Assert.Equal([tagged.Id], results.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task Search_MatchesSubject_AndMessageBodies_ButNotNotes()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var bySubject = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "Invoice overdue", "hello there");
        var byBody = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "other", "my invoice is wrong");
        var byNoteOnly = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "unrelated", "different topic");
        await Client.PostAsJsonAsync($"/api/conversations/{byNoteOnly.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Note, "invoice mentioned in note"), Json);

        var results = await SearchAsync(workspace.Id, "q=invoice");

        var ids = results.Select(c => c.Id).ToHashSet();
        Assert.Contains(bySubject.Id, ids);
        Assert.Contains(byBody.Id, ids);
        Assert.DoesNotContain(byNoteOnly.Id, ids);
    }

    [Fact]
    public async Task FilterBySlaBreached_FindsOverdue_AndLateResponses()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: 60);
        var contact = await CreateContactAsync(workspace.Id);
        var overdue = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "overdue");
        var late = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "late response");
        var onTime = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "on time");
        var fresh = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "fresh");

        var now = DateTimeOffset.UtcNow;
        Factory.WithDb(db =>
        {
            var overdueEntity = db.Conversations.Single(c => c.Id == overdue.Id);
            overdueEntity.FirstResponseDueAt = now.AddMinutes(-30);

            var lateEntity = db.Conversations.Single(c => c.Id == late.Id);
            lateEntity.FirstResponseDueAt = now.AddMinutes(-60);
            lateEntity.FirstRespondedAt = now.AddMinutes(-10);

            var onTimeEntity = db.Conversations.Single(c => c.Id == onTime.Id);
            onTimeEntity.FirstResponseDueAt = now.AddMinutes(-60);
            onTimeEntity.FirstRespondedAt = now.AddMinutes(-90);

            db.SaveChanges();
        });

        var results = await SearchAsync(workspace.Id, "slaBreached=true");

        var ids = results.Select(c => c.Id).ToHashSet();
        Assert.Contains(overdue.Id, ids);
        Assert.Contains(late.Id, ids);
        Assert.DoesNotContain(onTime.Id, ids);
        Assert.DoesNotContain(fresh.Id, ids);
    }

    [Fact]
    public async Task CombinedFilters_Intersect()
    {
        var workspace = await CreateWorkspaceAsync();
        var inboxA = await CreateInboxAsync(workspace.Id, "A");
        var inboxB = await CreateInboxAsync(workspace.Id, "B");
        var contact = await CreateContactAsync(workspace.Id);
        var match = await StartConversationAsync(workspace.Id, inboxA.Id, contact.Id, "target");
        var wrongInbox = await StartConversationAsync(workspace.Id, inboxB.Id, contact.Id, "target");
        var wrongState = await StartConversationAsync(workspace.Id, inboxA.Id, contact.Id, "target");
        await Client.PostAsJsonAsync($"/api/conversations/{wrongState.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var results = await SearchAsync(workspace.Id, $"state=Open&inboxId={inboxA.Id}&q=target");

        Assert.Equal([match.Id], results.Select(c => c.Id).ToArray());
    }
}
