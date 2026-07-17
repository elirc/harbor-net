namespace Harbor.Domain.Entities;

/// <summary>An agent who answers conversations.</summary>
public class Teammate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<TeamMembership> Memberships { get; } = new List<TeamMembership>();
}
