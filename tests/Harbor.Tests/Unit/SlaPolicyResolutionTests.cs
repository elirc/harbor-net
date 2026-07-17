using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

/// <summary>Which SLA policy governs a conversation, and the targets it stamps.</summary>
public class SlaPolicyResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Support = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();

    private static SlaPolicy Policy(
        string name, Guid? inboxId, ConversationPriority? priority, int firstResponseMinutes) =>
        new()
        {
            WorkspaceId = Guid.NewGuid(),
            Name = name,
            InboxId = inboxId,
            Priority = priority,
            FirstResponseMinutes = firstResponseMinutes,
            CreatedAt = Now,
        };

    [Fact]
    public void Specificity_RanksInboxAbovePriority()
    {
        Assert.Equal(0, Policy("default", null, null, 60).Specificity);
        Assert.Equal(1, Policy("priority", null, ConversationPriority.Urgent, 60).Specificity);
        Assert.Equal(2, Policy("inbox", Support, null, 60).Specificity);
        Assert.Equal(3, Policy("both", Support, ConversationPriority.Urgent, 60).Specificity);
    }

    [Fact]
    public void Resolve_PrefersMostSpecificMatch()
    {
        var policies = new[]
        {
            Policy("default", null, null, 240),
            Policy("urgent anywhere", null, ConversationPriority.Urgent, 30),
            Policy("support", Support, null, 60),
            Policy("support urgent", Support, ConversationPriority.Urgent, 15),
        };

        var resolved = SlaPolicies.Resolve(policies, Support, ConversationPriority.Urgent);

        Assert.Equal("support urgent", resolved!.Name);
    }

    [Fact]
    public void Resolve_InboxBeatsPriority_WhenBothMatchPartially()
    {
        var policies = new[]
        {
            Policy("urgent anywhere", null, ConversationPriority.Urgent, 30),
            Policy("support", Support, null, 60),
        };

        var resolved = SlaPolicies.Resolve(policies, Support, ConversationPriority.Urgent);

        Assert.Equal("support", resolved!.Name);
    }

    [Fact]
    public void Resolve_FallsBackToWorkspaceDefault_ForUnscopedInbox()
    {
        var policies = new[]
        {
            Policy("default", null, null, 240),
            Policy("support", Support, null, 60),
        };

        var resolved = SlaPolicies.Resolve(policies, Sales, ConversationPriority.Normal);

        Assert.Equal("default", resolved!.Name);
    }

    [Fact]
    public void Resolve_IgnoresPoliciesScopedToOtherInboxOrPriority()
    {
        var policies = new[]
        {
            Policy("support urgent", Support, ConversationPriority.Urgent, 15),
            Policy("sales", Sales, null, 480),
        };

        Assert.Null(SlaPolicies.Resolve(policies, Support, ConversationPriority.Normal));
    }

    [Fact]
    public void Resolve_BreaksSpecificityTies_ByCreationOrder()
    {
        var older = Policy("older", Support, null, 60);
        var newer = Policy("newer", Support, null, 30);
        newer.CreatedAt = Now.AddHours(1);

        var resolved = SlaPolicies.Resolve([newer, older], Support, ConversationPriority.Normal);

        Assert.Equal("older", resolved!.Name);
    }

    [Fact]
    public void ApplySlaTargets_MeasuresFromCreatedAt_NotNow()
    {
        var convo = new Conversation
        {
            WorkspaceId = Guid.NewGuid(),
            InboxId = Support,
            ContactId = Guid.NewGuid(),
            CreatedAt = Now.AddHours(-5),
        };
        var policyId = Guid.NewGuid();

        convo.ApplySlaTargets(60, 1_440, policyId);

        Assert.Equal(Now.AddHours(-4), convo.FirstResponseDueAt);
        Assert.Equal(Now.AddHours(19), convo.ResolutionDueAt);
        Assert.Equal(policyId, convo.SlaPolicyId);
    }

    [Fact]
    public void ApplySlaTargets_NullMinutes_ClearTargets()
    {
        var convo = new Conversation
        {
            WorkspaceId = Guid.NewGuid(),
            InboxId = Support,
            ContactId = Guid.NewGuid(),
            CreatedAt = Now,
        };
        convo.ApplySlaTargets(60, 1_440, Guid.NewGuid());

        convo.ApplySlaTargets(null, null, null);

        Assert.Null(convo.FirstResponseDueAt);
        Assert.Null(convo.ResolutionDueAt);
        Assert.Null(convo.SlaPolicyId);
    }

    [Fact]
    public void IsResolutionBreached_TrueWhenOverdueAndUnresolved()
    {
        var convo = new Conversation
        {
            WorkspaceId = Guid.NewGuid(),
            InboxId = Support,
            ContactId = Guid.NewGuid(),
            CreatedAt = Now.AddHours(-5),
            ResolutionDueAt = Now.AddHours(-1),
        };

        Assert.True(convo.IsResolutionBreached(Now));
        Assert.True(convo.IsSlaBreached(Now));
    }

    [Fact]
    public void IsResolutionBreached_JudgesFirstClose_NotReopens()
    {
        var convo = new Conversation
        {
            WorkspaceId = Guid.NewGuid(),
            InboxId = Support,
            ContactId = Guid.NewGuid(),
            CreatedAt = Now.AddHours(-5),
            ResolutionDueAt = Now.AddHours(-1),
        };

        convo.Close(Now.AddHours(-2));
        // Reopening clears ClosedAt but the SLA was already met on first close.
        convo.Open(Now);

        Assert.Null(convo.ClosedAt);
        Assert.Equal(Now.AddHours(-2), convo.FirstResolvedAt);
        Assert.False(convo.IsResolutionBreached(Now));
    }

    [Fact]
    public void Close_StampsFirstResolvedAt_Once()
    {
        var convo = new Conversation
        {
            WorkspaceId = Guid.NewGuid(),
            InboxId = Support,
            ContactId = Guid.NewGuid(),
            CreatedAt = Now.AddHours(-5),
        };

        convo.Close(Now.AddHours(-3));
        convo.Open(Now.AddHours(-2));
        convo.Close(Now);

        Assert.Equal(Now.AddHours(-3), convo.FirstResolvedAt);
        Assert.Equal(Now, convo.ClosedAt);
    }
}
