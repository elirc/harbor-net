using Harbor.Domain;
using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Infrastructure;

/// <summary>
/// Records SLA breach events. Detection is idempotent: a conversation can be
/// re-checked as often as we like without duplicating its history, which is
/// what lets the same code run from the live reply/close paths and from the
/// periodic sweep over conversations that are simply sitting overdue.
/// </summary>
public static class SlaBreaches
{
    /// <summary>
    /// Adds breach events for every target the given conversations have missed
    /// and that is not already on record. Callers still SaveChanges.
    /// </summary>
    public static async Task<List<SlaBreachEvent>> DetectAsync(
        HarborDbContext db, IReadOnlyCollection<Conversation> conversations, DateTimeOffset now)
    {
        if (conversations.Count == 0)
        {
            return [];
        }

        var ids = conversations.Select(c => c.Id).ToList();
        var onRecord = await db.SlaBreachEvents
            .Where(e => ids.Contains(e.ConversationId))
            .Select(e => new { e.ConversationId, e.Kind })
            .ToListAsync();

        var seen = onRecord.Select(e => (e.ConversationId, e.Kind)).ToHashSet();
        var recorded = new List<SlaBreachEvent>();

        foreach (var conversation in conversations)
        {
            // BreachedAt is the late reply/close when we have one; otherwise the
            // target is still unmet and now is the moment we noticed.
            if (conversation.IsFirstResponseBreached(now)
                && seen.Add((conversation.Id, SlaBreachKind.FirstResponse)))
            {
                recorded.Add(Event(
                    conversation, SlaBreachKind.FirstResponse,
                    conversation.FirstResponseDueAt!.Value,
                    conversation.FirstRespondedAt ?? now, now));
            }

            if (conversation.IsResolutionBreached(now)
                && seen.Add((conversation.Id, SlaBreachKind.Resolution)))
            {
                recorded.Add(Event(
                    conversation, SlaBreachKind.Resolution,
                    conversation.ResolutionDueAt!.Value,
                    conversation.FirstResolvedAt ?? now, now));
            }
        }

        db.SlaBreachEvents.AddRange(recorded);
        return recorded;
    }

    /// <summary>Detects breaches for a single conversation.</summary>
    public static Task<List<SlaBreachEvent>> DetectAsync(
        HarborDbContext db, Conversation conversation, DateTimeOffset now) =>
        DetectAsync(db, [conversation], now);

    private static SlaBreachEvent Event(
        Conversation conversation, SlaBreachKind kind,
        DateTimeOffset dueAt, DateTimeOffset breachedAt, DateTimeOffset now) =>
        new()
        {
            ConversationId = conversation.Id,
            Kind = kind,
            DueAt = dueAt,
            BreachedAt = breachedAt,
            SlaPolicyId = conversation.SlaPolicyId,
            CreatedAt = now,
        };
}
