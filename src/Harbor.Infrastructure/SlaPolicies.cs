using Harbor.Domain;
using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Infrastructure;

/// <summary>
/// Resolves which SLA policy governs a conversation and stamps its targets.
/// </summary>
public static class SlaPolicies
{
    /// <summary>
    /// Picks the most specific policy covering the conversation's inbox and
    /// priority. Ties on specificity are broken by creation order, so the
    /// oldest policy wins and resolution stays deterministic.
    /// </summary>
    public static SlaPolicy? Resolve(
        IEnumerable<SlaPolicy> policies, Guid inboxId, ConversationPriority priority) =>
        policies
            .Where(p => p.Matches(inboxId, priority))
            .OrderByDescending(p => p.Specificity)
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

    /// <summary>
    /// Stamps the conversation's SLA targets from the governing policy. Falls
    /// back to the inbox's first-response minutes when no policy matches, which
    /// keeps inboxes configured before SLA policies existed working unchanged.
    /// </summary>
    public static async Task ApplyAsync(HarborDbContext db, Conversation conversation, Inbox inbox)
    {
        var policies = await db.SlaPolicies
            .Where(p => p.WorkspaceId == conversation.WorkspaceId)
            .ToListAsync();

        var policy = Resolve(policies, conversation.InboxId, conversation.Priority);
        if (policy is null)
        {
            conversation.ApplySlaTargets(inbox.FirstResponseSlaMinutes, null, null);
            return;
        }

        conversation.ApplySlaTargets(policy.FirstResponseMinutes, policy.ResolutionMinutes, policy.Id);
    }
}
