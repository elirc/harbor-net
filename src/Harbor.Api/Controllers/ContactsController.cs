using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class ContactsController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/contacts")]
    public async Task<ActionResult<ContactResponse>> Create(Guid workspaceId, CreateContactRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var contact = new Contact
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Email = request.Email,
            ExternalId = request.ExternalId,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = contact.Id }, contact.ToResponse());
    }

    /// <summary>Lists contacts; optional case-insensitive search on name/email.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/contacts")]
    public async Task<ActionResult<List<ContactResponse>>> List(Guid workspaceId, [FromQuery] string? q)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var query = db.Contacts.Where(c => c.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(c =>
                c.Name.ToLower().Contains(needle)
                || (c.Email != null && c.Email.ToLower().Contains(needle)));
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => c.ToResponse())
            .ToListAsync();
    }

    [HttpGet("api/contacts/{id:guid}")]
    public async Task<ActionResult<ContactResponse>> GetById(Guid id)
    {
        var contact = await db.Contacts
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        return contact is null ? NotFound() : contact.ToResponse();
    }

    [HttpPut("api/contacts/{id:guid}")]
    public async Task<ActionResult<ContactResponse>> Update(Guid id, UpdateContactRequest request)
    {
        var contact = await db.Contacts
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (contact is null)
        {
            return NotFound();
        }

        contact.Name = request.Name;
        contact.Email = request.Email;
        contact.ExternalId = request.ExternalId;
        await db.SaveChangesAsync();

        return contact.ToResponse();
    }

    [HttpDelete("api/contacts/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var contact = await db.Contacts
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (contact is null)
        {
            return NotFound();
        }

        var hasConversations = await db.Conversations.AnyAsync(c => c.ContactId == id);
        if (hasConversations)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Contact has conversations",
                Detail = "A contact with existing conversations cannot be deleted.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        db.Contacts.Remove(contact);
        await db.SaveChangesAsync();

        return NoContent();
    }
}
