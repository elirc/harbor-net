using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Harbor.Tests.Integration;

public class ErrorHandlingTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task DomainException_BecomesProblemDetails422()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Snoozed, DateTimeOffset.UtcNow.AddDays(-1)), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.NotNull(problem);
        Assert.Equal(422, problem.Status);
        Assert.Contains("future", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationFailure_ReturnsValidationProblemDetails()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces", new CreateWorkspaceRequest(""), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json);
        Assert.NotNull(problem);
        Assert.Contains("Name", problem.Errors.Keys);
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var response = await Client.PostAsync(
            "/api/workspaces",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await Client.GetAsync("/api/definitely-not-a-thing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidGuidInRoute_Returns404()
    {
        var response = await Client.GetAsync("/api/conversations/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Conflict_ReturnsProblemDetails_WithTitle()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTeammateAsync(workspace.Id, "Ada", "conflict@acme.test");

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/teammates",
            new CreateTeammateRequest("Ada 2", "conflict@acme.test"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.NotNull(problem);
        Assert.Equal("Duplicate teammate email", problem.Title);
    }

    [Fact]
    public async Task OversizedBody_FailsValidation()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(inbox.Id, contact.Id, null, new string('x', 20_001)), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
