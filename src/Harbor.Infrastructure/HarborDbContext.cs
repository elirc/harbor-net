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
            e.HasIndex(t => new { t.WorkspaceId, t.Email }).IsUnique();
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
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });
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
