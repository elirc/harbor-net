using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Tests.Unit;

/// <summary>
/// The exact instant a target is missed.
///
/// Breach checks compare strictly (now &gt; due), so the target instant itself
/// is still on time. Driving this through the API cannot pin the boundary —
/// the wall clock never lands exactly on a target — so it is pinned here, one
/// tick either side. An off-by-one in this comparison either forgives a real
/// breach or invents one that never happened, and both are invisible until a
/// customer argues about a report.
/// </summary>
public class SlaBoundaryTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = Created.AddMinutes(60);

    private static Conversation Convo() => new()
    {
        WorkspaceId = Guid.NewGuid(),
        InboxId = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        CreatedAt = Created,
        UpdatedAt = Created,
    };

    private static Conversation WithFirstResponseTarget()
    {
        var convo = Convo();
        convo.ApplySlaTargets(60, null, Guid.NewGuid());
        return convo;
    }

    private static Conversation WithResolutionTarget()
    {
        var convo = Convo();
        convo.ApplySlaTargets(null, 60, Guid.NewGuid());
        return convo;
    }

    // --- First response --------------------------------------------------

    [Fact]
    public void FirstResponse_ExactlyAtTarget_IsNotYetBreached()
    {
        var convo = WithFirstResponseTarget();

        // now == due: the target has been reached, not missed.
        Assert.False(convo.IsFirstResponseBreached(Due));
    }

    [Fact]
    public void FirstResponse_OneTickPastTarget_IsBreached()
    {
        var convo = WithFirstResponseTarget();

        Assert.True(convo.IsFirstResponseBreached(Due.AddTicks(1)));
    }

    [Fact]
    public void FirstResponse_ReplyLandingExactlyOnTarget_IsOnTime()
    {
        var convo = WithFirstResponseTarget();
        convo.FirstRespondedAt = Due;

        // Judged on the reply, not on how long ago the target passed.
        Assert.False(convo.IsFirstResponseBreached(Due.AddDays(7)));
    }

    [Fact]
    public void FirstResponse_ReplyOneTickLate_IsBreached()
    {
        var convo = WithFirstResponseTarget();
        convo.FirstRespondedAt = Due.AddTicks(1);

        Assert.True(convo.IsFirstResponseBreached(Due.AddTicks(1)));
    }

    // --- Resolution ------------------------------------------------------

    [Fact]
    public void Resolution_ExactlyAtTarget_IsNotYetBreached()
    {
        var convo = WithResolutionTarget();

        Assert.False(convo.IsResolutionBreached(Due));
    }

    [Fact]
    public void Resolution_OneTickPastTarget_IsBreached()
    {
        var convo = WithResolutionTarget();

        Assert.True(convo.IsResolutionBreached(Due.AddTicks(1)));
    }

    [Fact]
    public void Resolution_CloseLandingExactlyOnTarget_IsOnTime()
    {
        var convo = WithResolutionTarget();

        convo.Close(Due);

        Assert.False(convo.IsResolutionBreached(Due.AddDays(7)));
    }

    [Fact]
    public void Resolution_CloseOneTickLate_IsBreached()
    {
        var convo = WithResolutionTarget();

        convo.Close(Due.AddTicks(1));

        Assert.True(convo.IsResolutionBreached(Due.AddTicks(1)));
    }

    // --- The two targets are judged independently ------------------------

    [Fact]
    public void LateReply_ButCloseInTime_BreachesOnlyFirstResponse()
    {
        var convo = Convo();
        convo.ApplySlaTargets(60, 240, Guid.NewGuid());
        convo.FirstRespondedAt = Created.AddMinutes(90);

        convo.Close(Created.AddMinutes(200));

        Assert.True(convo.IsFirstResponseBreached(Created.AddMinutes(200)));
        Assert.False(convo.IsResolutionBreached(Created.AddMinutes(200)));
        Assert.True(convo.IsSlaBreached(Created.AddMinutes(200)));
    }

    [Fact]
    public void PromptReply_ButLateClose_BreachesOnlyResolution()
    {
        var convo = Convo();
        convo.ApplySlaTargets(60, 240, Guid.NewGuid());
        convo.FirstRespondedAt = Created.AddMinutes(5);

        convo.Close(Created.AddMinutes(300));

        Assert.False(convo.IsFirstResponseBreached(Created.AddMinutes(300)));
        Assert.True(convo.IsResolutionBreached(Created.AddMinutes(300)));
    }

    [Fact]
    public void NoTargets_AreNeverBreached_HoweverLongItSits()
    {
        var convo = Convo();
        convo.ApplySlaTargets(null, null, null);

        Assert.False(convo.IsFirstResponseBreached(Created.AddYears(1)));
        Assert.False(convo.IsResolutionBreached(Created.AddYears(1)));
        Assert.False(convo.IsSlaBreached(Created.AddYears(1)));
    }

    // --- State changes do not move the clock -----------------------------

    [Fact]
    public void Snoozing_DoesNotPauseTheSlaClock()
    {
        var convo = WithFirstResponseTarget();

        convo.Snooze(Created.AddMinutes(30), Created.AddMinutes(1));

        // Snooze is a inbox-management convenience, not an SLA hold: the
        // customer is still waiting, so the target is unmoved and still missed.
        Assert.Equal(Due, convo.FirstResponseDueAt);
        Assert.True(convo.IsFirstResponseBreached(Due.AddMinutes(1)));
    }

    [Fact]
    public void Reopening_DoesNotMoveTargets_NorRestartTheClock()
    {
        var convo = WithFirstResponseTarget();
        convo.Close(Created.AddMinutes(10));

        convo.Open(Created.AddMinutes(20));

        Assert.Equal(Due, convo.FirstResponseDueAt);
        Assert.Equal(Created, convo.CreatedAt);
    }

    [Fact]
    public void ReopeningAfterAnOnTimeClose_DoesNotRetroactivelyBreachResolution()
    {
        var convo = WithResolutionTarget();
        convo.Close(Created.AddMinutes(30));

        // Reopened and left sitting far past the original target.
        convo.Open(Created.AddMinutes(40));

        // The resolution SLA was met at the first close and stays met.
        Assert.Equal(Created.AddMinutes(30), convo.FirstResolvedAt);
        Assert.False(convo.IsResolutionBreached(Created.AddDays(7)));
    }
}
