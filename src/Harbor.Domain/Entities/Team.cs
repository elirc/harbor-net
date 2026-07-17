namespace Harbor.Domain.Entities;

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<TeamMembership> Members { get; } = new List<TeamMembership>();
}
