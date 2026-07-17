using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

/// <summary>
/// Webhook subscriptions and their delivery log. Managing subscriptions is
/// admin-only: a subscription is an egress path for every conversation in the
/// workspace.
/// </summary>
[ApiController]
public class WebhooksController(HarborDbContext db, WebhookDispatcher dispatcher) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/webhooks")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WebhookCreatedResponse>> Create(
        Guid workspaceId, CreateWebhookRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var subscription = new WebhookSubscription
        {
            WorkspaceId = workspaceId,
            Url = request.Url,
            Secret = WebhookSigner.GenerateSecret(),
        };
        foreach (var eventType in request.Events.Distinct())
        {
            subscription.Events.Add(new WebhookSubscriptionEvent
            {
                SubscriptionId = subscription.Id,
                EventType = eventType,
            });
        }

        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();

        // The only time the secret is ever returned.
        return CreatedAtAction(
            nameof(GetById), new { id = subscription.Id }, subscription.ToCreatedResponse());
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/webhooks")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<WebhookResponse>>> List(
        Guid workspaceId, [FromQuery] PageRequest paging)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var subscriptions = await db.WebhookSubscriptions
            .Include(s => s.Events)
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderBy(s => s.CreatedAt)
            .ToPageAsync(paging, Response);

        return subscriptions.Select(s => s.ToResponse()).ToList();
    }

    [HttpGet("api/webhooks/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WebhookResponse>> GetById(Guid id)
    {
        var subscription = await FindAsync(id);
        return subscription is null ? NotFound() : subscription.ToResponse();
    }

    [HttpPut("api/webhooks/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WebhookResponse>> Update(Guid id, UpdateWebhookRequest request)
    {
        var subscription = await FindAsync(id);
        if (subscription is null)
        {
            return NotFound();
        }

        subscription.Url = request.Url;
        subscription.IsActive = request.IsActive;

        var wanted = request.Events.Distinct().ToHashSet();
        var current = subscription.Events.ToList();
        foreach (var stale in current.Where(e => !wanted.Contains(e.EventType)))
        {
            subscription.Events.Remove(stale);
        }

        foreach (var added in wanted.Where(w => current.All(e => e.EventType != w)))
        {
            subscription.Events.Add(new WebhookSubscriptionEvent
            {
                SubscriptionId = subscription.Id,
                EventType = added,
            });
        }

        await db.SaveChangesAsync();
        return subscription.ToResponse();
    }

    [HttpDelete("api/webhooks/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var subscription = await FindAsync(id);
        if (subscription is null)
        {
            return NotFound();
        }

        db.WebhookSubscriptions.Remove(subscription);
        await db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>The delivery log for one subscription, newest first.</summary>
    [HttpGet("api/webhooks/{id:guid}/deliveries")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<WebhookDeliveryResponse>>> Deliveries(
        Guid id, [FromQuery] PageRequest paging)
    {
        if (await FindAsync(id) is null)
        {
            return NotFound();
        }

        return await db.WebhookDeliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.ToResponse())
            .ToPageAsync(paging, Response);
    }

    /// <summary>
    /// Drains the workspace's delivery outbox, attempting everything that is
    /// due and retrying failures with backoff. Safe to call repeatedly; a
    /// scheduler would call this on a timer.
    /// </summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/webhooks/dispatch")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<WebhookDeliveryResponse>>> Dispatch(
        Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken))
        {
            return NotFound();
        }

        var attempted = await dispatcher.DispatchAsync(
            workspaceId, DateTimeOffset.UtcNow, cancellationToken);

        return attempted.Select(d => d.ToResponse()).ToList();
    }

    private Task<WebhookSubscription?> FindAsync(Guid id) =>
        db.WebhookSubscriptions
            .Include(s => s.Events)
            .SingleOrDefaultAsync(s => s.Id == id && s.WorkspaceId == User.GetWorkspaceId());
}
