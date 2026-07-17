using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// Dynamic segments over contact attributes. These run against real SQLite,
/// so every passing assertion is also proof the rules compiled to SQL the
/// database accepted — including the json_extract path for custom attributes.
/// </summary>
public class SegmentTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static SegmentRuleSet All(params SegmentCondition[] conditions) =>
        new(SegmentMatch.All, conditions);

    private async Task<SegmentResponse> CreateSegmentAsync(
        Guid workspaceId, string name, SegmentRuleSet rules)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/segments",
            new CreateSegmentRequest(name, rules), Json);
        return await ReadAsync<SegmentResponse>(response);
    }

    private async Task<ContactResponse> CreateContactWithAttributesAsync(
        Guid workspaceId, string name, string? email,
        Dictionary<string, string?>? attributes = null, string? externalId = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/contacts",
            new CreateContactRequest(name, email, externalId, attributes), Json);
        return await ReadAsync<ContactResponse>(response);
    }

    private async Task<List<ContactResponse>> MembersAsync(Guid segmentId) =>
        await ReadAsync<List<ContactResponse>>(
            await Client.GetAsync($"/api/segments/{segmentId}/contacts"));

    [Fact]
    public async Task Attributes_RoundTripOnAContact()
    {
        var workspace = await CreateWorkspaceAsync();

        var contact = await CreateContactWithAttributesAsync(
            workspace.Id, "Jane", "jane@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise", ["seats"] = "250" });

        Assert.Equal("enterprise", contact.Attributes["plan"]);
        Assert.Equal("250", contact.Attributes["seats"]);
    }

    [Fact]
    public async Task Attributes_NullValuesAreDropped()
    {
        var workspace = await CreateWorkspaceAsync();

        var contact = await CreateContactWithAttributesAsync(
            workspace.Id, "Jane", "jane@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free", ["churned"] = null });

        Assert.True(contact.Attributes.ContainsKey("plan"));
        Assert.False(contact.Attributes.ContainsKey("churned"));
    }

    [Fact]
    public async Task Segment_OverACustomAttribute_IsEvaluatedByTheDatabase()
    {
        var workspace = await CreateWorkspaceAsync();
        var enterprise = await CreateContactWithAttributesAsync(
            workspace.Id, "Enterprise Emma", "emma@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        await CreateContactWithAttributesAsync(
            workspace.Id, "Free Fred", "fred@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });
        await CreateContactWithAttributesAsync(workspace.Id, "No Attributes Nora", "nora@acme.test");

        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        var members = await MembersAsync(segment.Id);
        Assert.Equal(enterprise.Id, Assert.Single(members).Id);
    }

    [Fact]
    public async Task Segment_MembershipIsDynamic()
    {
        var workspace = await CreateWorkspaceAsync();
        var contact = await CreateContactWithAttributesAsync(
            workspace.Id, "Fred", "fred@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });
        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        Assert.Empty(await MembersAsync(segment.Id));

        // Upgrading the contact is all it takes to join the segment.
        await Client.PutAsJsonAsync($"/api/contacts/{contact.Id}",
            new UpdateContactRequest("Fred", "fred@acme.test", null,
                new Dictionary<string, string?> { ["plan"] = "enterprise" }),
            Json);

        Assert.Equal(contact.Id, Assert.Single(await MembersAsync(segment.Id)).Id);
    }

    [Fact]
    public async Task Segment_MatchAll_RequiresEveryCondition()
    {
        var workspace = await CreateWorkspaceAsync();
        var both = await CreateContactWithAttributesAsync(
            workspace.Id, "Both", "both@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise", ["country"] = "US" });
        await CreateContactWithAttributesAsync(
            workspace.Id, "One", "one@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise", ["country"] = "JP" });

        var segment = await CreateSegmentAsync(workspace.Id, "US enterprise", All(
            new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise"),
            new SegmentCondition("attributes.country", SegmentOperator.Equals, "US")));

        Assert.Equal(both.Id, Assert.Single(await MembersAsync(segment.Id)).Id);
    }

    [Fact]
    public async Task Segment_MatchAny_NeedsOnlyOneCondition()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Enterprise", "e@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        await CreateContactWithAttributesAsync(
            workspace.Id, "Japanese", "j@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free", ["country"] = "JP" });
        await CreateContactWithAttributesAsync(
            workspace.Id, "Neither", "n@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free", ["country"] = "US" });

        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise or Japan",
            new SegmentRuleSet(SegmentMatch.Any, [
                new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise"),
                new SegmentCondition("attributes.country", SegmentOperator.Equals, "JP"),
            ]));

        Assert.Equal(2, (await MembersAsync(segment.Id)).Count);
    }

    [Fact]
    public async Task Segment_OverBuiltInTextFields()
    {
        var workspace = await CreateWorkspaceAsync();
        var acme = await CreateContactWithAttributesAsync(workspace.Id, "Acme Anna", "anna@acme.test");
        await CreateContactWithAttributesAsync(workspace.Id, "Other Otto", "otto@other.test");

        var segment = await CreateSegmentAsync(workspace.Id, "Acme staff",
            All(new SegmentCondition("email", SegmentOperator.EndsWith, "@acme.test")));

        Assert.Equal(acme.Id, Assert.Single(await MembersAsync(segment.Id)).Id);
    }

    [Fact]
    public async Task Segment_ExistsAndNotExists()
    {
        var workspace = await CreateWorkspaceAsync();
        var identified = await CreateContactWithAttributesAsync(
            workspace.Id, "Identified", "id@acme.test", externalId: "cust-1");
        var anonymous = await CreateContactWithAttributesAsync(workspace.Id, "Anonymous", "anon@acme.test");

        var withId = await CreateSegmentAsync(workspace.Id, "Identified",
            All(new SegmentCondition("externalId", SegmentOperator.Exists)));
        var withoutId = await CreateSegmentAsync(workspace.Id, "Unidentified",
            All(new SegmentCondition("externalId", SegmentOperator.NotExists)));

        Assert.Equal(identified.Id, Assert.Single(await MembersAsync(withId.Id)).Id);
        Assert.Equal(anonymous.Id, Assert.Single(await MembersAsync(withoutId.Id)).Id);
    }

    [Fact]
    public async Task Segment_NegativeOperators_IncludeContactsMissingTheAttribute()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Enterprise", "e@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        var free = await CreateContactWithAttributesAsync(
            workspace.Id, "Free", "f@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });
        var unknown = await CreateContactWithAttributesAsync(workspace.Id, "Unknown", "u@acme.test");

        var segment = await CreateSegmentAsync(workspace.Id, "Not enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.NotEquals, "enterprise")));

        // A contact with no plan at all is genuinely not on the enterprise plan.
        var members = await MembersAsync(segment.Id);
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.Id == free.Id);
        Assert.Contains(members, m => m.Id == unknown.Id);
    }

    [Fact]
    public async Task Segment_OverDateFields()
    {
        var workspace = await CreateWorkspaceAsync();
        var contact = await CreateContactWithAttributesAsync(workspace.Id, "Recent", "r@acme.test");

        var recent = await CreateSegmentAsync(workspace.Id, "Created recently",
            All(new SegmentCondition("createdAt", SegmentOperator.After, "2020-01-01T00:00:00Z")));
        var ancient = await CreateSegmentAsync(workspace.Id, "Created long ago",
            All(new SegmentCondition("createdAt", SegmentOperator.Before, "2020-01-01T00:00:00Z")));

        Assert.Equal(contact.Id, Assert.Single(await MembersAsync(recent.Id)).Id);
        Assert.Empty(await MembersAsync(ancient.Id));
    }

    [Fact]
    public async Task Segment_LastSeenAtNull_IsNeitherBeforeNorAfter()
    {
        var workspace = await CreateWorkspaceAsync();
        // A contact created through the API has never been seen.
        await CreateContactWithAttributesAsync(workspace.Id, "Never seen", "never@acme.test");

        var before = await CreateSegmentAsync(workspace.Id, "Seen before",
            All(new SegmentCondition("lastSeenAt", SegmentOperator.Before, "2030-01-01T00:00:00Z")));
        var after = await CreateSegmentAsync(workspace.Id, "Seen after",
            All(new SegmentCondition("lastSeenAt", SegmentOperator.After, "2000-01-01T00:00:00Z")));

        Assert.Empty(await MembersAsync(before.Id));
        Assert.Empty(await MembersAsync(after.Id));
    }

    [Fact]
    public async Task Segment_IsScopedToItsWorkspace()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateContactWithAttributesAsync(
            other.Id, "Foreign Enterprise", "foreign@other.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });

        var workspace = await CreateWorkspaceAsync();
        var mine = await CreateContactWithAttributesAsync(
            workspace.Id, "Mine", "mine@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        Assert.Equal(mine.Id, Assert.Single(await MembersAsync(segment.Id)).Id);
    }

    [Fact]
    public async Task Segment_FiltersConversations()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var enterprise = await CreateContactWithAttributesAsync(
            workspace.Id, "Enterprise", "e@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        var free = await CreateContactWithAttributesAsync(
            workspace.Id, "Free", "f@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });
        var wanted = await StartConversationAsync(workspace.Id, inbox.Id, enterprise.Id, "big customer");
        await StartConversationAsync(workspace.Id, inbox.Id, free.Id, "small customer");
        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync(
                $"/api/workspaces/{workspace.Id}/conversations?segmentId={segment.Id}"));

        Assert.Equal(wanted.Id, Assert.Single(conversations).Id);
    }

    [Fact]
    public async Task Segment_ConversationFilter_ComposesWithOtherFilters()
    {
        var workspace = await CreateWorkspaceAsync();
        var support = await CreateInboxAsync(workspace.Id, "Support");
        var sales = await CreateInboxAsync(workspace.Id, "Sales");
        var enterprise = await CreateContactWithAttributesAsync(
            workspace.Id, "Enterprise", "e@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        var wanted = await StartConversationAsync(workspace.Id, support.Id, enterprise.Id, "in support");
        await StartConversationAsync(workspace.Id, sales.Id, enterprise.Id, "in sales");
        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations"
                + $"?segmentId={segment.Id}&inboxId={support.Id}"));

        Assert.Equal(wanted.Id, Assert.Single(conversations).Id);
    }

    [Fact]
    public async Task Conversations_ForAnUnknownSegment_Return422()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/conversations?segmentId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Segment_RulesRoundTripThroughStorage()
    {
        var workspace = await CreateWorkspaceAsync();
        var rules = new SegmentRuleSet(SegmentMatch.Any, [
            new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise"),
            new SegmentCondition("email", SegmentOperator.Contains, "acme"),
        ]);

        var created = await CreateSegmentAsync(workspace.Id, "Mixed", rules);
        var fetched = await ReadAsync<SegmentResponse>(
            await Client.GetAsync($"/api/segments/{created.Id}"));

        Assert.Equal(SegmentMatch.Any, fetched.Rules.Match);
        Assert.Equal(2, fetched.Rules.Conditions.Count);
        Assert.Equal("attributes.plan", fetched.Rules.Conditions[0].Field);
        Assert.Equal(SegmentOperator.Equals, fetched.Rules.Conditions[0].Operator);
        Assert.Equal("enterprise", fetched.Rules.Conditions[0].Value);
    }

    [Fact]
    public async Task Segment_UpdateChangesMembership()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Free", "f@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });
        var segment = await CreateSegmentAsync(workspace.Id, "Plan",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));
        Assert.Empty(await MembersAsync(segment.Id));

        await Client.PutAsJsonAsync($"/api/segments/{segment.Id}",
            new UpdateSegmentRequest("Plan",
                All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "free"))),
            Json);

        Assert.Single(await MembersAsync(segment.Id));
    }

    [Fact]
    public async Task Segment_WithInvalidRules_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();

        var unknownField = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/segments",
            new CreateSegmentRequest("Bad",
                All(new SegmentCondition("nonsense", SegmentOperator.Equals, "x"))), Json);
        var noConditions = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/segments",
            new CreateSegmentRequest("Empty", new SegmentRuleSet(SegmentMatch.All, [])), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownField.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConditions.StatusCode);
    }

    [Fact]
    public async Task Segment_WithADuplicateName_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        var rules = All(new SegmentCondition("email", SegmentOperator.Exists));
        await CreateSegmentAsync(workspace.Id, "Everyone", rules);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/segments",
            new CreateSegmentRequest("Everyone", rules), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Segment_Delete()
    {
        var workspace = await CreateWorkspaceAsync();
        var segment = await CreateSegmentAsync(workspace.Id, "Temp",
            All(new SegmentCondition("email", SegmentOperator.Exists)));

        var deleted = await Client.DeleteAsync($"/api/segments/{segment.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"/api/segments/{segment.Id}")).StatusCode);
    }

    [Fact]
    public async Task Segments_WritingIsAdminOnly_ReadingIsNot()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var write = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/segments",
            new CreateSegmentRequest("Nope",
                All(new SegmentCondition("email", SegmentOperator.Exists))), Json);
        var read = await Client.GetAsync($"/api/workspaces/{workspace.Id}/segments");

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task ForeignSegment_Returns404()
    {
        var other = await CreateWorkspaceAsync("Other");
        var segment = await CreateSegmentAsync(other.Id, "Theirs",
            All(new SegmentCondition("email", SegmentOperator.Exists)));
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/segments/{segment.Id}/contacts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
