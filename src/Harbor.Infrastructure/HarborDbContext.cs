using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Infrastructure;

public class HarborDbContext(DbContextOptions<HarborDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Inbox> Inboxes => Set<Inbox>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Teammate> Teammates => Set<Teammate>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ConversationTag> ConversationTags => Set<ConversationTag>();
    public DbSet<CannedReply> CannedReplies => Set<CannedReply>();
    public DbSet<AssignmentEvent> AssignmentEvents => Set<AssignmentEvent>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<SlaBreachEvent> SlaBreachEvents => Set<SlaBreachEvent>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native DateTimeOffset ordering/comparison; store UTC ticks.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Inbox>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(200);
            e.Property(i => i.EmailAddress).HasMaxLength(320);
            // Inbound mail is routed by this address, so it must identify one
            // inbox. SQLite treats NULLs as distinct, which is what we want:
            // any number of inboxes can be chat-only.
            e.HasIndex(i => new { i.WorkspaceId, i.EmailAddress }).IsUnique();
            e.HasOne(i => i.Workspace)
                .WithMany(w => w.Inboxes)
                .HasForeignKey(i => i.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Email).HasMaxLength(320);
            e.Property(c => c.ExternalId).HasMaxLength(200);
            e.HasIndex(c => new { c.WorkspaceId, c.Email });
            e.HasOne(c => c.Workspace)
                .WithMany()
                .HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Teammate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Email).HasMaxLength(320);
            e.Property(t => t.ApiKeyHash).HasMaxLength(64);
            e.HasIndex(t => new { t.WorkspaceId, t.Email }).IsUnique();
            e.HasIndex(t => t.ApiKeyHash).IsUnique();
            e.HasOne(t => t.Workspace)
                .WithMany()
                .HasForeignKey(t => t.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique();
            e.HasOne(t => t.Workspace)
                .WithMany()
                .HasForeignKey(t => t.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMembership>(e =>
        {
            e.HasKey(m => new { m.TeamId, m.TeammateId });
            e.HasOne(m => m.Team)
                .WithMany(t => t.Members)
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Teammate)
                .WithMany(t => t.Memberships)
                .HasForeignKey(m => m.TeammateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.Property(c => c.Subject).HasMaxLength(500);
            e.HasIndex(c => new { c.WorkspaceId, c.State });
            e.HasIndex(c => c.LastMessageAt);
            e.HasOne(c => c.Workspace)
                .WithMany()
                .HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Inbox)
                .WithMany()
                .HasForeignKey(c => c.InboxId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.Contact)
                .WithMany()
                .HasForeignKey(c => c.ContactId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.AssignedTeammate)
                .WithMany()
                .HasForeignKey(c => c.AssignedTeammateId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.AssignedTeam)
                .WithMany()
                .HasForeignKey(c => c.AssignedTeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.Property(m => m.Body).HasMaxLength(20_000);
            e.Property(m => m.EmailMessageId).HasMaxLength(500);
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });
            // Inbound threading looks messages up by their Message-ID.
            e.HasIndex(m => m.EmailMessageId);
            e.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.AuthorContact)
                .WithMany()
                .HasForeignKey(m => m.AuthorContactId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.AuthorTeammate)
                .WithMany()
                .HasForeignKey(m => m.AuthorTeammateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(100);
            e.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique();
            e.HasOne(t => t.Workspace)
                .WithMany()
                .HasForeignKey(t => t.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationTag>(e =>
        {
            e.HasKey(ct => new { ct.ConversationId, ct.TagId });
            e.HasOne(ct => ct.Conversation)
                .WithMany(c => c.Tags)
                .HasForeignKey(ct => ct.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ct => ct.Tag)
                .WithMany()
                .HasForeignKey(ct => ct.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssignmentEvent>(e =>
        {
            // Actor/from/to ids are intentionally unconstrained: the audit
            // trail must survive directory changes.
            e.HasIndex(a => new { a.ConversationId, a.CreatedAt });
            e.HasOne(a => a.Conversation)
                .WithMany()
                .HasForeignKey(a => a.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SlaPolicy>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.HasIndex(p => new { p.WorkspaceId, p.InboxId, p.Priority });
            e.HasOne(p => p.Workspace)
                .WithMany()
                .HasForeignKey(p => p.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting an inbox drops the policies written for it; workspace-wide
            // policies (null InboxId) are unaffected.
            e.HasOne(p => p.Inbox)
                .WithMany()
                .HasForeignKey(p => p.InboxId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SlaBreachEvent>(e =>
        {
            // One event per conversation per kind makes detection idempotent.
            e.HasIndex(b => new { b.ConversationId, b.Kind }).IsUnique();
            e.HasOne(b => b.Conversation)
                .WithMany()
                .HasForeignKey(b => b.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookSubscription>(e =>
        {
            e.Property(s => s.Url).HasMaxLength(2_000);
            e.Property(s => s.Secret).HasMaxLength(100);
            e.HasOne(s => s.Workspace)
                .WithMany()
                .HasForeignKey(s => s.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookSubscriptionEvent>(e =>
        {
            e.HasKey(x => new { x.SubscriptionId, x.EventType });
            e.HasOne(x => x.Subscription)
                .WithMany(s => s.Events)
                .HasForeignKey(x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.Property(d => d.Payload).HasMaxLength(100_000);
            e.Property(d => d.Error).HasMaxLength(2_000);
            // The dispatcher's hot query: pending deliveries that are due.
            e.HasIndex(d => new { d.WorkspaceId, d.Status, d.NextAttemptAt });
            e.HasOne(d => d.Subscription)
                .WithMany()
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CannedReply>(e =>
        {
            e.Property(r => r.Shortcut).HasMaxLength(100);
            e.Property(r => r.Title).HasMaxLength(200);
            e.Property(r => r.Body).HasMaxLength(20_000);
            e.HasIndex(r => new { r.WorkspaceId, r.Shortcut }).IsUnique();
            e.HasOne(r => r.Workspace)
                .WithMany()
                .HasForeignKey(r => r.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
