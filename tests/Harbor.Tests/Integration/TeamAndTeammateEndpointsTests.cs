using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;

namespace Harbor.Tests.Integration;

public class TeamAndTeammateEndpointsTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task CreateTeammate_NormalizesEmail_AndRoundTrips()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/teammates",
            new CreateTeammateRequest("Ada", "ADA@Acme.Test"), Json);
        var created = await ReadAsync<TeammateResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("ada@acme.test", created.Email);
    }

    [Fact]
    public async Task CreateTeammate_DuplicateEmail_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTeammateAsync(workspace.Id, "Ada", "dup@acme.test");

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/teammates",
            new CreateTeammateRequest("Ada Again", "dup@acme.test"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTeam_DuplicateName_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTeamAsync(workspace.Id, "Frontline");

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/teams", new CreateTeamRequest("Frontline"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddMember_ThenRemove_UpdatesRoster()
    {
        var workspace = await CreateWorkspaceAsync();
        var team = await CreateTeamAsync(workspace.Id, "Roster");
        var teammate = await CreateTeammateAsync(workspace.Id);

        var added = await ReadAsync<TeamResponse>(await Client.PostAsJsonAsync(
            $"/api/teams/{team.Id}/members", new AddTeamMemberRequest(teammate.Id), Json));
        Assert.Contains(teammate.Id, added.MemberIds);

        var remove = await Client.DeleteAsync($"/api/teams/{team.Id}/members/{teammate.Id}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var after = await ReadAsync<TeamResponse>(await Client.GetAsync($"/api/teams/{team.Id}"));
        Assert.DoesNotContain(teammate.Id, after.MemberIds);
    }

    [Fact]
    public async Task AddMember_IsIdempotent()
    {
        var workspace = await CreateWorkspaceAsync();
        var team = await CreateTeamAsync(workspace.Id, "Idempotent");
        var teammate = await CreateTeammateAsync(workspace.Id);

        await Client.PostAsJsonAsync(
            $"/api/teams/{team.Id}/members", new AddTeamMemberRequest(teammate.Id), Json);
        var second = await ReadAsync<TeamResponse>(await Client.PostAsJsonAsync(
            $"/api/teams/{team.Id}/members", new AddTeamMemberRequest(teammate.Id), Json));

        Assert.Single(second.MemberIds);
    }

    [Fact]
    public async Task AddMember_FromOtherWorkspace_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var otherWorkspace = await CreateWorkspaceAsync("Other");
        var team = await CreateTeamAsync(workspace.Id, "Strict");
        var outsider = await CreateTeammateAsync(otherWorkspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/teams/{team.Id}/members", new AddTeamMemberRequest(outsider.Id), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
