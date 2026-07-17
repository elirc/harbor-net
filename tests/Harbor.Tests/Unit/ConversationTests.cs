using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Tests.Unit;

public class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static Conversation NewConversation() => new()
    {
        WorkspaceId = Guid.NewGuid(),
        InboxId = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now.AddHours(-1),
    };

    [Fact]
    public void Snooze_SetsStateAndWakeTime()
    {
        var convo = NewConversation();
        var until = Now.AddHours(4);

        convo.Snooze(until, Now);

        Assert.Equal(ConversationState.Snoozed, convo.State);
        Assert.Equal(until, convo.SnoozedUntil);
        Assert.Equal(Now, convo.UpdatedAt);
    }

    [Fact]
    public void Snooze_InThePast_Throws()
    {
        var convo = NewConversation();

        Assert.Throws<DomainException>(() => convo.Snooze(Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void Close_SetsClosedAt_AndClearsSnooze()
    {
        var convo = NewConversation();
        convo.Snooze(Now.AddHours(1), Now);

        convo.Close(Now.AddMinutes(5));

        Assert.Equal(ConversationState.Closed, convo.State);
        Assert.Equal(Now.AddMinutes(5), convo.ClosedAt);
        Assert.Null(convo.SnoozedUntil);
    }

    [Fact]
    public void Open_ClearsClosedAtAndSnooze()
    {
        var convo = NewConversation();
        convo.Close(Now);

        convo.Open(Now.AddMinutes(1));

        Assert.Equal(ConversationState.Open, convo.State);
        Assert.Null(convo.ClosedAt);
        Assert.Null(convo.SnoozedUntil);
    }

    [Fact]
    public void AssignToTeammate_ClearsTeamAssignment()
    {
        var convo = NewConversation();
        var teamId = Guid.NewGuid();
        var teammateId = Guid.NewGuid();
        convo.AssignToTeam(teamId, Now);

        convo.AssignToTeammate(teammateId, Now.AddMinutes(1));

        Assert.Equal(teammateId, convo.AssignedTeammateId);
        Assert.Null(convo.AssignedTeamId);
    }

    [Fact]
    public void AssignToTeam_ClearsTeammateAssignment()
    {
        var convo = NewConversation();
        convo.AssignToTeammate(Guid.NewGuid(), Now);
        var teamId = Guid.NewGuid();

        convo.AssignToTeam(teamId, Now.AddMinutes(1));

        Assert.Equal(teamId, convo.AssignedTeamId);
        Assert.Null(convo.AssignedTeammateId);
    }

    [Fact]
    public void RegisterMessage_FirstTeammateReply_SetsFirstRespondedAt()
    {
        var convo = NewConversation();
        var reply = new Message
        {
            ConversationId = convo.Id,
            AuthorType = AuthorType.Teammate,
            AuthorTeammateId = Guid.NewGuid(),
            Body = "On it!",
        };

        convo.RegisterMessage(reply, Now);

        Assert.Equal(Now, convo.FirstRespondedAt);
        Assert.Equal(Now, convo.LastMessageAt);
    }

    [Fact]
    public void RegisterMessage_SecondTeammateReply_KeepsOriginalFirstResponse()
    {
        var convo = NewConversation();
        var teammateId = Guid.NewGuid();
        convo.RegisterMessage(NewTeammateReply(convo, teammateId), Now);

        convo.RegisterMessage(NewTeammateReply(convo, teammateId), Now.AddHours(1));

        Assert.Equal(Now, convo.FirstRespondedAt);
    }

    [Fact]
    public void RegisterMessage_Note_DoesNotSetFirstResponse_OrReopen()
    {
        var convo = NewConversation();
        convo.Close(Now);
        var note = new Message
        {
            ConversationId = convo.Id,
            Kind = MessageKind.Note,
            AuthorType = AuthorType.Teammate,
            AuthorTeammateId = Guid.NewGuid(),
            Body = "internal context",
        };

        convo.RegisterMessage(note, Now.AddMinutes(1));

        Assert.Null(convo.FirstRespondedAt);
        Assert.Equal(ConversationState.Closed, convo.State);
    }

    [Fact]
    public void RegisterMessage_ContactMessage_ReopensClosedConversation()
    {
        var convo = NewConversation();
        convo.Close(Now);
        var message = new Message
        {
            ConversationId = convo.Id,
            AuthorType = AuthorType.Contact,
            AuthorContactId = convo.ContactId,
            Body = "Actually, still broken.",
        };

        convo.RegisterMessage(message, Now.AddMinutes(10));

        Assert.Equal(ConversationState.Open, convo.State);
        Assert.Null(convo.ClosedAt);
    }

    [Fact]
    public void IsSlaBreached_NoDeadline_IsFalse()
    {
        var convo = NewConversation();

        Assert.False(convo.IsSlaBreached(Now));
    }

    [Fact]
    public void IsSlaBreached_DeadlinePassed_WithoutResponse_IsTrue()
    {
        var convo = NewConversation();
        convo.FirstResponseDueAt = Now.AddMinutes(-5);

        Assert.True(convo.IsSlaBreached(Now));
    }

    [Fact]
    public void IsSlaBreached_RespondedInTime_IsFalse_EvenAfterDeadline()
    {
        var convo = NewConversation();
        convo.FirstResponseDueAt = Now.AddMinutes(-5);
        convo.FirstRespondedAt = Now.AddMinutes(-30);

        Assert.False(convo.IsSlaBreached(Now));
    }

    [Fact]
    public void IsSlaBreached_RespondedLate_IsTrue()
    {
        var convo = NewConversation();
        convo.FirstResponseDueAt = Now.AddMinutes(-30);
        convo.FirstRespondedAt = Now.AddMinutes(-5);

        Assert.True(convo.IsSlaBreached(Now));
    }

    private static Message NewTeammateReply(Conversation convo, Guid teammateId) => new()
    {
        ConversationId = convo.Id,
        AuthorType = AuthorType.Teammate,
        AuthorTeammateId = teammateId,
        Body = "reply",
    };
}
