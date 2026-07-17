using System.Net;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>Volume, response-time, breakdown, and tag-distribution reports.</summary>
public class ReportingTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static readonly DateTimeOffset Base = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private Task<VolumeReportResponse> VolumeAsync(Guid workspaceId, string query = "") =>
        GetAsync<VolumeReportResponse>($"/api/workspaces/{workspaceId}/reports/volume{query}");

    private Task<ResponseTimeReportResponse> ResponseTimesAsync(Guid workspaceId, string query = "") =>
        GetAsync<ResponseTimeReportResponse>(
            $"/api/workspaces/{workspaceId}/reports/response-times{query}");

    private async Task<T> GetAsync<T>(string url) => await ReadAsync<T>(await Client.GetAsync(url));

    /// <summary>
    /// Three conversations created on consecutive days, with first responses of
    /// 10, 20 and 60 minutes; the first two are resolved after 2h and 4h.
    /// </summary>
    private async Task<(WorkspaceResponse Workspace, InboxResponse Inbox, List<Guid> Ids)>
        SeedTimedCohortAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        var third = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "three");

        SetConversationTimings(first.Id, Base, Base.AddMinutes(10), Base.AddHours(2));
        SetConversationTimings(second.Id, Base.AddDays(1), Base.AddDays(1).AddMinutes(20),
            Base.AddDays(1).AddHours(4));
        SetConversationTimings(third.Id, Base.AddDays(2), Base.AddDays(2).AddMinutes(60));

        return (workspace, inbox, [first.Id, second.Id, third.Id]);
    }

    [Fact]
    public async Task Volume_BucketsByDay_AndPointsSumToTotals()
    {
        var (workspace, _, _) = await SeedTimedCohortAsync();

        var report = await VolumeAsync(workspace.Id);

        Assert.Equal(ReportInterval.Day, report.Interval);
        Assert.Equal(3, report.TotalStarted);
        Assert.Equal(2, report.TotalClosed);
        Assert.Equal(3, report.Points.Sum(p => p.Started));
        Assert.Equal(2, report.Points.Sum(p => p.Closed));
        Assert.All(report.Points, p => Assert.Equal(p.BucketStart.AddDays(1), p.BucketEnd));
    }

    [Fact]
    public async Task Volume_EmitsContiguousBuckets_IncludingEmptyOnes()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        SetConversationTimings(first.Id, Base);
        SetConversationTimings(second.Id, Base.AddDays(3));

        var report = await VolumeAsync(workspace.Id);

        Assert.Equal(4, report.Points.Count);
        Assert.Equal([1, 0, 0, 1], report.Points.Select(p => p.Started));
    }

    [Fact]
    public async Task Volume_ByHour_UsesHourBuckets()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        SetConversationTimings(first.Id, Base);
        SetConversationTimings(second.Id, Base.AddMinutes(90));

        var report = await VolumeAsync(workspace.Id, "?interval=Hour");

        Assert.Equal(ReportInterval.Hour, report.Interval);
        Assert.Equal(2, report.Points.Count);
        Assert.Equal(Base, report.Points[0].BucketStart);
        Assert.Equal(Base.AddHours(1), report.Points[0].BucketEnd);
    }

    [Fact]
    public async Task Volume_ByWeek_StartsBucketsOnMonday()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        // 2026-06-04 is a Thursday; its week starts Monday 2026-06-01.
        SetConversationTimings(convo.Id, new DateTimeOffset(2026, 6, 4, 15, 0, 0, TimeSpan.Zero));

        var report = await VolumeAsync(workspace.Id, "?interval=Week");

        var point = Assert.Single(report.Points);
        Assert.Equal(DayOfWeek.Monday, point.BucketStart.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), point.BucketStart);
    }

    [Fact]
    public async Task Volume_TooManyBuckets_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var old = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "old");
        var recent = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "recent");
        SetConversationTimings(old.Id, Base.AddYears(-1));
        SetConversationTimings(recent.Id, Base);

        var response = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/reports/volume?interval=Hour");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Volume_WithNoConversations_IsEmpty()
    {
        var workspace = await CreateWorkspaceAsync();

        var report = await VolumeAsync(workspace.Id);

        Assert.Equal(0, report.TotalStarted);
        Assert.Empty(report.Points);
    }

    [Fact]
    public async Task ResponseTimes_ReportMedianAndPercentiles()
    {
        var (workspace, _, _) = await SeedTimedCohortAsync();

        var report = await ResponseTimesAsync(workspace.Id);

        Assert.Equal(3, report.Conversations);
        Assert.Equal(0, report.Awaiting);
        Assert.Equal(2, report.Resolved);
        // First responses of 10, 20, 60 minutes.
        Assert.Equal(3, report.FirstResponse.Count);
        Assert.Equal(20, report.FirstResponse.P50Minutes);
        Assert.Equal(30, report.FirstResponse.AverageMinutes);
        // Resolutions of 120 and 240 minutes.
        Assert.Equal(2, report.Resolution.Count);
        Assert.Equal(180, report.Resolution.P50Minutes);
    }

    [Fact]
    public async Task ResponseTimes_WithNoData_ReportsNullPercentiles()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var report = await ResponseTimesAsync(workspace.Id);

        Assert.Equal(1, report.Conversations);
        Assert.Equal(1, report.Awaiting);
        Assert.Equal(0, report.FirstResponse.Count);
        Assert.Null(report.FirstResponse.P50Minutes);
        Assert.Null(report.Resolution.P95Minutes);
    }

    [Fact]
    public async Task ResponseTimes_CountBreaches_FromTheSlaEngine()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(5));

        var report = await ResponseTimesAsync(workspace.Id);

        Assert.Equal(1, report.FirstResponseBreaches);
        Assert.Equal(1, report.ResolutionBreaches);
    }

    [Fact]
    public async Task Teammates_BreakDownByAssignee_WithUnassignedRow()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var agent = await CreateTeammateAsync(workspace.Id, "Grace");
        var assigned = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "assigned");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "unassigned");
        await AssignAsync(assigned.Id, agent.Id);

        var rows = await GetAsync<List<BreakdownRowResponse>>(
            $"/api/workspaces/{workspace.Id}/reports/teammates");

        Assert.Equal(2, rows.Count);
        var graceRow = Assert.Single(rows, r => r.Id == agent.Id);
        Assert.Equal("Grace", graceRow.Name);
        Assert.Equal(1, graceRow.Conversations);
        var unassigned = Assert.Single(rows, r => r.Id is null);
        Assert.Equal("(unassigned)", unassigned.Name);
        Assert.Equal(1, unassigned.Conversations);
    }

    [Fact]
    public async Task Inboxes_BreakDownByInbox_WithStateCounts()
    {
        var workspace = await CreateWorkspaceAsync();
        var support = await CreateInboxAsync(workspace.Id, "Support");
        var sales = await CreateInboxAsync(workspace.Id, "Sales");
        var contact = await CreateContactAsync(workspace.Id);
        var open = await StartConversationAsync(workspace.Id, support.Id, contact.Id, "open");
        var closed = await StartConversationAsync(workspace.Id, support.Id, contact.Id, "closed");
        await StartConversationAsync(workspace.Id, sales.Id, contact.Id, "sales");
        SetConversationTimings(closed.Id, Base, Base.AddMinutes(5), Base.AddMinutes(30));

        var rows = await GetAsync<List<BreakdownRowResponse>>(
            $"/api/workspaces/{workspace.Id}/reports/inboxes");

        var supportRow = Assert.Single(rows, r => r.Id == support.Id);
        Assert.Equal("Support", supportRow.Name);
        Assert.Equal(2, supportRow.Conversations);
        Assert.Equal(1, supportRow.Open);
        Assert.Equal(1, supportRow.Closed);
        Assert.Equal(30, supportRow.Resolution.P50Minutes);
        Assert.NotEqual(open.Id, closed.Id);
        Assert.Equal(1, Assert.Single(rows, r => r.Id == sales.Id).Conversations);
    }

    [Fact]
    public async Task Tags_ReportDistributionAndUntaggedCount()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var billing = await CreateTagAsync(workspace.Id, "billing");
        var bug = await CreateTagAsync(workspace.Id, "bug");
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "three");
        await TagConversationAsync(first.Id, billing.Id);
        await TagConversationAsync(first.Id, bug.Id);
        await TagConversationAsync(second.Id, billing.Id);

        var report = await GetAsync<TagDistributionResponse>(
            $"/api/workspaces/{workspace.Id}/reports/tags");

        Assert.Equal(3, report.Conversations);
        Assert.Equal(1, report.Untagged);
        var billingRow = Assert.Single(report.Tags, t => t.Name == "billing");
        Assert.Equal(2, billingRow.Conversations);
        Assert.Equal(0.6667, billingRow.Share);
        Assert.Equal(1, Assert.Single(report.Tags, t => t.Name == "bug").Conversations);
    }

    [Fact]
    public async Task Reports_ShareFilters_AcrossEndpoints()
    {
        var workspace = await CreateWorkspaceAsync();
        var support = await CreateInboxAsync(workspace.Id, "Support");
        var sales = await CreateInboxAsync(workspace.Id, "Sales");
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, support.Id, contact.Id, "support");
        await StartConversationAsync(workspace.Id, sales.Id, contact.Id, "sales one");
        await StartConversationAsync(workspace.Id, sales.Id, contact.Id, "sales two");

        var volume = await VolumeAsync(workspace.Id, $"?inboxId={sales.Id}");
        var times = await ResponseTimesAsync(workspace.Id, $"?inboxId={sales.Id}");
        var rows = await GetAsync<List<BreakdownRowResponse>>(
            $"/api/workspaces/{workspace.Id}/reports/inboxes?inboxId={sales.Id}");

        Assert.Equal(2, volume.TotalStarted);
        Assert.Equal(2, times.Conversations);
        Assert.Equal(2, Assert.Single(rows).Conversations);
    }

    [Fact]
    public async Task Reports_FilterByCreationWindow()
    {
        var (workspace, _, ids) = await SeedTimedCohortAsync();

        // From is inclusive, To is exclusive: this window holds only day 2.
        var from = Uri.EscapeDataString(Base.AddDays(1).ToString("o"));
        var to = Uri.EscapeDataString(Base.AddDays(2).ToString("o"));
        var report = await ResponseTimesAsync(workspace.Id, $"?from={from}&to={to}");

        Assert.Equal(1, report.Conversations);
        Assert.Equal(20, report.FirstResponse.P50Minutes);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task Reports_FilterByPriorityAndTag()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var vip = await CreateTagAsync(workspace.Id, "vip");
        var urgent = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "urgent");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "normal");
        await TagConversationAsync(urgent.Id, vip.Id);
        await SetPriorityAsync(urgent.Id, ConversationPriority.Urgent);

        var byPriority = await ResponseTimesAsync(workspace.Id, "?priority=Urgent");
        var byTag = await ResponseTimesAsync(workspace.Id, "?tag=VIP");

        Assert.Equal(1, byPriority.Conversations);
        Assert.Equal(1, byTag.Conversations);
    }

    [Fact]
    public async Task Reports_ForForeignWorkspace_Return403()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{other.Id}/reports/volume");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
