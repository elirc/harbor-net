using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Api.Controllers;
using Harbor.Domain;
using Harbor.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests.Integration;

/// <summary>
/// Segment rules as SQL.
///
/// <see cref="SegmentTests"/> asserts what each rule matches. These assert
/// *where* the matching happens: the compiled predicate has to reach the
/// database as a WHERE clause, because a segment over a million contacts must
/// stay one statement rather than a million objects. The generated SQL is
/// inspected directly — a regression to in-memory filtering would still return
/// the right rows and pass every other test in the suite.
/// </summary>
public class SegmentSqlTranslationTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static SegmentRuleSet All(params SegmentCondition[] conditions) =>
        new(SegmentMatch.All, conditions);

    private async Task<SegmentResponse> CreateSegmentAsync(
        Guid workspaceId, string name, SegmentRuleSet rules) =>
        await ReadAsync<SegmentResponse>(
            await Client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/segments",
                new CreateSegmentRequest(name, rules), Json));

    private async Task<ContactResponse> CreateContactWithAttributesAsync(
        Guid workspaceId, string name, string? email = null,
        Dictionary<string, string?>? attributes = null, string? externalId = null) =>
        await ReadAsync<ContactResponse>(
            await Client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/contacts",
                new CreateContactRequest(name, email, externalId, attributes), Json));

    private async Task<List<ContactResponse>> MembersAsync(Guid segmentId) =>
        await ReadAsync<List<ContactResponse>>(
            await Client.GetAsync($"/api/segments/{segmentId}/contacts"));

    /// <summary>The SQL the compiled rules actually produce.</summary>
    private string SqlFor(Guid workspaceId, SegmentRuleSet rules)
    {
        var sql = string.Empty;
        Factory.WithDb(db =>
            sql = db.Contacts
                .Where(c => c.WorkspaceId == workspaceId)
                .Where(SegmentCompiler.Compile(rules))
                .ToQueryString());
        return sql;
    }

    // --- The rules become SQL ---------------------------------------------

    [Fact]
    public void AttributeRule_CompilesToJsonExtract_InAWhereClause()
    {
        var sql = SqlFor(
            Guid.NewGuid(),
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        // The attribute is read by the database, not by us.
        Assert.Contains("json_extract", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$.plan", sql);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuiltInFieldRule_CompilesToAColumnComparison()
    {
        var sql = SqlFor(
            Guid.NewGuid(), All(new SegmentCondition("email", SegmentOperator.Contains, "acme")));

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Email", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryOperator_IsTranslatable_AndNeverFallsBackToTheClient()
    {
        var workspaceId = Guid.NewGuid();
        var conditions = new (string Field, SegmentOperator Operator, string? Value)[]
        {
            ("attributes.plan", SegmentOperator.Equals, "enterprise"),
            ("attributes.plan", SegmentOperator.NotEquals, "enterprise"),
            ("attributes.plan", SegmentOperator.Contains, "enter"),
            ("attributes.plan", SegmentOperator.NotContains, "enter"),
            ("attributes.plan", SegmentOperator.StartsWith, "ent"),
            ("attributes.plan", SegmentOperator.EndsWith, "prise"),
            ("attributes.plan", SegmentOperator.Exists, null),
            ("attributes.plan", SegmentOperator.NotExists, null),
            ("email", SegmentOperator.Equals, "a@b.test"),
            ("name", SegmentOperator.Contains, "ada"),
            ("externalId", SegmentOperator.Exists, null),
            ("createdAt", SegmentOperator.Before, "2030-01-01T00:00:00Z"),
            ("createdAt", SegmentOperator.After, "2020-01-01T00:00:00Z"),
            ("lastSeenAt", SegmentOperator.Before, "2030-01-01T00:00:00Z"),
            ("lastSeenAt", SegmentOperator.After, "2020-01-01T00:00:00Z"),
            ("lastSeenAt", SegmentOperator.Exists, null),
            ("lastSeenAt", SegmentOperator.NotExists, null),
        };

        foreach (var (field, op, value) in conditions)
        {
            // EF throws rather than silently evaluating client-side, so getting
            // SQL out at all is the assertion.
            var sql = SqlFor(workspaceId, All(new SegmentCondition(field, op, value)));
            Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ConversationSegmentFilter_StaysASubquery_NotAMaterializedIdList()
    {
        var workspaceId = Guid.NewGuid();
        var rules = All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise"));
        var sql = string.Empty;

        Factory.WithDb(db =>
        {
            var segment = new Domain.Entities.Segment
            {
                WorkspaceId = workspaceId,
                Name = "Enterprise",
                RulesJson = System.Text.Json.JsonSerializer.Serialize(
                    rules, SegmentsController.RulesJson),
            };
            var contactIds = SegmentsController.ContactIdsQuery(db, workspaceId, segment);
            sql = db.Conversations
                .Where(c => c.WorkspaceId == workspaceId)
                .Where(c => contactIds.Contains(c.ContactId))
                .ToQueryString();
        });

        // One statement: the segment is an EXISTS/IN subquery against Contacts,
        // not a list of ids read out and pasted back in.
        Assert.Contains("json_extract", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Contacts", sql);
        Assert.Contains("Conversations", sql);
    }

    // --- Every operator, matching real rows --------------------------------

    /// <summary>
    /// A workspace holding one contact per interesting shape, so a rule's
    /// result set names exactly who it let through.
    /// </summary>
    private async Task<(Guid WorkspaceId, Dictionary<string, Guid> Contacts)> PopulateAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var contacts = new Dictionary<string, Guid>
        {
            ["enterprise"] = (await CreateContactWithAttributesAsync(
                workspace.Id, "Enterprise Emma", "emma@acme.test",
                new Dictionary<string, string?> { ["plan"] = "enterprise" })).Id,
            ["free"] = (await CreateContactWithAttributesAsync(
                workspace.Id, "Free Fred", "fred@other.test",
                new Dictionary<string, string?> { ["plan"] = "free" })).Id,
            ["none"] = (await CreateContactWithAttributesAsync(workspace.Id, "Nora Noplan", "nora@acme.test")).Id,
        };

        return (workspace.Id, contacts);
    }

    public static TheoryData<SegmentOperator, string?, string[]> AttributeOperatorCases() => new()
    {
        // operator, value, the contacts it must return
        { SegmentOperator.Equals, "enterprise", ["enterprise"] },
        { SegmentOperator.NotEquals, "enterprise", ["free", "none"] },
        { SegmentOperator.Contains, "erpris", ["enterprise"] },
        { SegmentOperator.NotContains, "erpris", ["free", "none"] },
        { SegmentOperator.StartsWith, "enter", ["enterprise"] },
        { SegmentOperator.EndsWith, "prise", ["enterprise"] },
        { SegmentOperator.Exists, null, ["enterprise", "free"] },
        { SegmentOperator.NotExists, null, ["none"] },
    };

    [Theory]
    [MemberData(nameof(AttributeOperatorCases))]
    public async Task AttributeOperator_SelectsExactlyTheRightContacts(
        SegmentOperator op, string? value, string[] expected)
    {
        var (workspaceId, contacts) = await PopulateAsync();
        var segment = await CreateSegmentAsync(
            workspaceId, $"seg-{op}", All(new SegmentCondition("attributes.plan", op, value)));

        var members = await MembersAsync(segment.Id);

        var expectedIds = expected.Select(k => contacts[k]).OrderBy(id => id);
        Assert.Equal(expectedIds, members.Select(m => m.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task AttributeMatching_IsCaseInsensitive_OnBothSides()
    {
        var workspace = await CreateWorkspaceAsync();
        var shouty = await CreateContactWithAttributesAsync(
            workspace.Id, "Shouty", "s@acme.test",
            new Dictionary<string, string?> { ["plan"] = "ENTERPRISE" });

        var segment = await CreateSegmentAsync(workspace.Id, "Enterprise",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Equals, "enterprise")));

        Assert.Equal(shouty.Id, Assert.Single(await MembersAsync(segment.Id)).Id);
    }

    // --- Absent and nested attributes --------------------------------------

    [Fact]
    public async Task ARuleOverANestedPath_MatchesNothing_RatherThanFailing()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Flat", "flat@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });

        // Attributes are a flat string map, so $.plan.tier resolves to nothing.
        // json_extract returns null for a path that is not there — the segment
        // is empty, not an error.
        var segment = await CreateSegmentAsync(workspace.Id, "Nested",
            All(new SegmentCondition("attributes.plan.tier", SegmentOperator.Equals, "gold")));

        Assert.Empty(await MembersAsync(segment.Id));
    }

    [Fact]
    public async Task ANestedPath_IsAbsent_SoNotExistsMatchesEveryone()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Flat", "flat@acme.test",
            new Dictionary<string, string?> { ["plan"] = "enterprise" });
        await CreateContactWithAttributesAsync(workspace.Id, "Bare", "bare@acme.test");

        var segment = await CreateSegmentAsync(workspace.Id, "No tier",
            All(new SegmentCondition("attributes.plan.tier", SegmentOperator.NotExists)));

        Assert.Equal(2, (await MembersAsync(segment.Id)).Count);
    }

    [Fact]
    public async Task ARuleOverAnAttributeNobodyHas_IsEmpty()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateContactWithAttributesAsync(
            workspace.Id, "Someone", "s@acme.test",
            new Dictionary<string, string?> { ["plan"] = "free" });

        var segment = await CreateSegmentAsync(workspace.Id, "Unheard of",
            All(new SegmentCondition("attributes.favouriteColour", SegmentOperator.Exists)));

        Assert.Empty(await MembersAsync(segment.Id));
    }

    [Fact]
    public async Task AContactWithNoAttributesAtAll_IsHandledByJsonExtract()
    {
        var workspace = await CreateWorkspaceAsync();
        var bare = await CreateContactWithAttributesAsync(workspace.Id, "Bare", "bare@acme.test");

        // The column holds "{}", not NULL; json_extract has to cope either way.
        var exists = await CreateSegmentAsync(workspace.Id, "Has plan",
            All(new SegmentCondition("attributes.plan", SegmentOperator.Exists)));
        var missing = await CreateSegmentAsync(workspace.Id, "No plan",
            All(new SegmentCondition("attributes.plan", SegmentOperator.NotExists)));

        Assert.Empty(await MembersAsync(exists.Id));
        Assert.Equal(bare.Id, Assert.Single(await MembersAsync(missing.Id)).Id);
    }

    // --- Degenerate segments ------------------------------------------------

    [Fact]
    public async Task ASegmentMatchingEveryone_ReturnsEveryContact()
    {
        var (workspaceId, contacts) = await PopulateAsync();

        var segment = await CreateSegmentAsync(workspaceId, "Everyone",
            All(new SegmentCondition("name", SegmentOperator.Exists)));

        Assert.Equal(contacts.Count, (await MembersAsync(segment.Id)).Count);
    }

    [Fact]
    public async Task ASegmentMatchingNobody_IsAnEmptyList_NotAnError()
    {
        var (workspaceId, _) = await PopulateAsync();

        var segment = await CreateSegmentAsync(workspaceId, "Nobody",
            All(new SegmentCondition("email", SegmentOperator.Equals, "nobody@nowhere.test")));

        Assert.Empty(await MembersAsync(segment.Id));
    }

    [Fact]
    public async Task AnEmptySegment_FiltersConversationsToNothing()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactWithAttributesAsync(workspace.Id, "Someone", "s@acme.test");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var segment = await CreateSegmentAsync(workspace.Id, "Nobody",
            All(new SegmentCondition("email", SegmentOperator.Equals, "nobody@nowhere.test")));

        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync(
                $"/api/workspaces/{workspace.Id}/conversations?segmentId={segment.Id}"));

        Assert.Empty(conversations);
    }

    [Fact]
    public async Task ASegmentMatchingEveryone_LeavesTheConversationListUntouched()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactWithAttributesAsync(workspace.Id, "Someone", "s@acme.test");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        var segment = await CreateSegmentAsync(workspace.Id, "Everyone",
            All(new SegmentCondition("name", SegmentOperator.Exists)));

        var filtered = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync(
                $"/api/workspaces/{workspace.Id}/conversations?segmentId={segment.Id}"));

        Assert.Equal(2, filtered.Count);
    }
}
