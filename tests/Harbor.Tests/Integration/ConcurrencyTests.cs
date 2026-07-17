using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests.Integration;

/// <summary>
/// What happens when two people touch the same row at once — and, just as
/// deliberately, what does not.
///
/// Conversations carry a concurrency token because two agents grabbing the same
/// one is real, frequent, and destructive: a lost update there throws away
/// someone's work. The round-robin pointer carries no token because a lost
/// update there costs nothing but a slightly uneven rotation, and failing a
/// customer's conversation to protect rotation fairness would be a bad trade.
/// Both halves are asserted here, because "we chose not to" is only a decision
/// if something proves it is still true.
///
/// The races are staged through explicit DbContexts rather than parallel HTTP:
/// interleaving is then exact and repeatable, instead of depending on which
/// request happened to win a scheduler race.
/// </summary>
public class ConcurrencyTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(Guid WorkspaceId, Guid InboxId, ConversationDetailResponse Convo)>
        SetUpAsync(bool autoAssign = false)
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, autoAssign: autoAssign);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        return (workspace.Id, inbox.Id, convo);
    }

    // --- Conversations are protected --------------------------------------

    [Fact]
    public async Task TwoAgentsGrabbingTheSameConversation_TheStaleOneIsRefused()
    {
        var (workspaceId, _, convo) = await SetUpAsync();
        var alice = await CreateTeammateAsync(workspaceId, "Alice");
        var bob = await CreateTeammateAsync(workspaceId, "Bob");

        Factory.WithDb(hers =>
        {
            // Alice opens the conversation and starts deciding.
            var aliceCopy = hers.Conversations.Single(c => c.Id == convo.Id);

            // Bob grabs it while she is still looking at her copy.
            Factory.WithDb(his =>
            {
                var bobCopy = his.Conversations.Single(c => c.Id == convo.Id);
                bobCopy.AssignToTeammate(bob.Id, DateTimeOffset.UtcNow);
                his.SaveChanges();
            });

            aliceCopy.AssignToTeammate(alice.Id, DateTimeOffset.UtcNow);

            // Her UPDATE carries the token she read, which no longer matches
            // any row — so it changes nothing instead of erasing Bob's claim.
            Assert.Throws<DbUpdateConcurrencyException>(() => hers.SaveChanges());
        });

        Factory.WithDb(db =>
            Assert.Equal(bob.Id, db.Conversations.Single(c => c.Id == convo.Id).AssignedTeammateId));
    }

    [Fact]
    public async Task AStaleClose_LosesToAConcurrentReopen()
    {
        var (_, _, convo) = await SetUpAsync();

        Factory.WithDb(mine =>
        {
            var myCopy = mine.Conversations.Single(c => c.Id == convo.Id);

            Factory.WithDb(theirs =>
            {
                var theirCopy = theirs.Conversations.Single(c => c.Id == convo.Id);
                theirCopy.Snooze(DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow);
                theirs.SaveChanges();
            });

            myCopy.Close(DateTimeOffset.UtcNow);
            Assert.Throws<DbUpdateConcurrencyException>(() => mine.SaveChanges());
        });

        // The snooze stands; nobody's state change was silently overwritten.
        Factory.WithDb(db =>
            Assert.Equal(
                ConversationState.Snoozed, db.Conversations.Single(c => c.Id == convo.Id).State));
    }

    [Fact]
    public async Task TheTokenMoves_OnEverySuccessfulWrite_SoSerialWorkIsUnaffected()
    {
        var (workspaceId, _, convo) = await SetUpAsync();
        var teammate = await CreateTeammateAsync(workspaceId);
        var versions = new List<Guid>();

        // Ordinary back-to-back API calls each re-read, so the token never gets
        // in the way of one person working normally.
        foreach (var priority in new[] { ConversationPriority.High, ConversationPriority.Urgent })
        {
            await SetPriorityAsync(convo.Id, priority);
            versions.Add(VersionOf(convo.Id));
        }

        var assigned = await AssignAsync(convo.Id, teammate.Id);
        versions.Add(VersionOf(convo.Id));

        Assert.Equal(teammate.Id, assigned.AssignedTeammateId);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public async Task ARefusedWrite_LeavesNoPartialTrace()
    {
        var (workspaceId, _, convo) = await SetUpAsync();
        var teammate = await CreateTeammateAsync(workspaceId);

        Factory.WithDb(mine =>
        {
            var myCopy = mine.Conversations.Single(c => c.Id == convo.Id);
            mine.AssignmentEvents.Add(new AssignmentEvent
            {
                ConversationId = convo.Id,
                Kind = AssignmentKind.Manual,
                ToTeammateId = teammate.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Factory.WithDb(theirs =>
            {
                var theirCopy = theirs.Conversations.Single(c => c.Id == convo.Id);
                theirCopy.Subject = "Taken over";
                theirs.SaveChanges();
            });

            myCopy.AssignToTeammate(teammate.Id, DateTimeOffset.UtcNow);
            Assert.Throws<DbUpdateConcurrencyException>(() => mine.SaveChanges());
        });

        // SaveChanges is one transaction: the audit row queued alongside the
        // refused update must not survive on its own.
        Factory.WithDb(db =>
            Assert.Empty(db.AssignmentEvents.Where(a => a.ConversationId == convo.Id)));
    }

    // --- The rotation pointer deliberately is not ---------------------------

    [Fact]
    public async Task TheRoundRobinPointer_HasNoToken_SoALostUpdateIsSilent()
    {
        var (workspaceId, inboxId, _) = await SetUpAsync(autoAssign: true);
        var first = await CreateTeammateAsync(workspaceId, "First");
        var second = await CreateTeammateAsync(workspaceId, "Second");

        Factory.WithDb(mine =>
        {
            var myCopy = mine.Inboxes.Single(i => i.Id == inboxId);

            Factory.WithDb(theirs =>
            {
                var theirCopy = theirs.Inboxes.Single(i => i.Id == inboxId);
                theirCopy.LastAssignedTeammateId = first.Id;
                theirs.SaveChanges();
            });

            myCopy.LastAssignedTeammateId = second.Id;

            // No throw. This is the decision, made executable: the pointer is a
            // hint about whose turn it is, and two conversations arriving at
            // once may leave it pointing at either. The cost is that someone
            // gets an extra conversation — not that a customer's message fails.
            mine.SaveChanges();
        });

        Factory.WithDb(db =>
            Assert.Equal(second.Id, db.Inboxes.Single(i => i.Id == inboxId).LastAssignedTeammateId));
    }

    [Fact]
    public async Task ALostPointerUpdate_CostsFairness_NotCorrectness()
    {
        var (workspaceId, inboxId, _) = await SetUpAsync(autoAssign: true);
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspaceId}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        var first = await CreateTeammateAsync(workspaceId, "First");
        var second = await CreateTeammateAsync(workspaceId, "Second");
        var contact = await CreateContactAsync(workspaceId);

        // Rewind the pointer behind the rotation's back, as a lost update would.
        Factory.WithDb(db =>
        {
            db.Inboxes.Single(i => i.Id == inboxId).LastAssignedTeammateId = null;
            db.SaveChanges();
        });

        var convo = await StartConversationAsync(workspaceId, inboxId, contact.Id, "after the race");

        // The worst case is that the rotation repeats itself — every
        // conversation still reaches a real, available teammate.
        Assert.Contains(convo.AssignedTeammateId!.Value, new[] { first.Id, second.Id });
        Assert.Equal(ConversationState.Open, convo.State);
    }

    // --- Deliveries are protected -------------------------------------------

    [Fact]
    public async Task TwoDispatchers_CannotBothClaimTheSameDelivery()
    {
        var workspace = await CreateWorkspaceAsync();
        await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/webhooks",
                new CreateWebhookRequest(
                    "https://example.test/hooks", [WebhookEventType.ConversationCreated]), Json));
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Factory.WithDb(mine =>
        {
            var myCopy = mine.WebhookDeliveries.First(d => d.WorkspaceId == workspace.Id);

            Factory.WithDb(theirs =>
            {
                var theirCopy = theirs.WebhookDeliveries.First(d => d.WorkspaceId == workspace.Id);
                theirCopy.Succeed(200, DateTimeOffset.UtcNow);
                theirs.SaveChanges();
            });

            // Without the token both drains would mark it sent — and the
            // subscriber would have been posted the same event twice.
            myCopy.Succeed(200, DateTimeOffset.UtcNow);
            Assert.Throws<DbUpdateConcurrencyException>(() => mine.SaveChanges());
        });

        Factory.WithDb(db =>
            Assert.Equal(
                1,
                db.WebhookDeliveries.Count(d =>
                    d.WorkspaceId == workspace.Id
                    && d.Status == WebhookDeliveryStatus.Succeeded)));
    }

    [Fact]
    public async Task ARetriedDelivery_TracksAttempts_WithoutLosingCount()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/webhooks",
                new CreateWebhookRequest(
                    "https://example.test/hooks", [WebhookEventType.ConversationCreated]), Json));
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        Factory.WebhookSender.Respond = _ => WebhookSendResult.Rejected(500);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        try
        {
            await Client.PostAsync($"/api/workspaces/{workspace.Id}/webhooks/dispatch", null);
            Factory.WithDb(db =>
            {
                foreach (var delivery in db.WebhookDeliveries
                             .Where(d => d.WorkspaceId == workspace.Id
                                 && d.Status == WebhookDeliveryStatus.Pending))
                {
                    delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }

                db.SaveChanges();
            });
            await Client.PostAsync($"/api/workspaces/{workspace.Id}/webhooks/dispatch", null);

            var deliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
                await Client.GetAsync($"/api/webhooks/{subscription.Id}/deliveries"));
            // The token rolls on every attempt; the count must still be exact.
            Assert.Equal(2, Assert.Single(deliveries).AttemptCount);
        }
        finally
        {
            Factory.WebhookSender.Reset();
        }
    }

    // --- The API surface ----------------------------------------------------

    [Fact]
    public async Task RepeatedAssignment_ThroughTheApi_NeverConflicts()
    {
        var (workspaceId, _, convo) = await SetUpAsync();
        var teammates = new List<Guid>();
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            teammates.Add((await CreateTeammateAsync(workspaceId, name)).Id);
        }

        // Each request re-reads, so hand-offs in quick succession are ordinary
        // work rather than a fight.
        foreach (var teammateId in teammates)
        {
            var response = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
                new AssignConversationRequest(teammateId, null), Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Factory.WithDb(db =>
        {
            var stored = db.Conversations.Single(c => c.Id == convo.Id);
            Assert.Equal(teammates[^1], stored.AssignedTeammateId);
            Assert.Null(stored.AssignedTeamId);
        });
        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));
        Assert.Equal(3, events.Count);
    }

    private Guid VersionOf(Guid conversationId)
    {
        var version = Guid.Empty;
        Factory.WithDb(db =>
            version = db.Conversations.AsNoTracking().Single(c => c.Id == conversationId).Version);
        return version;
    }
}
