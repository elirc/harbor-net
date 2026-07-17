namespace Harbor.Domain.Entities;

public class TeamMembership
{
    public Guid TeamId { get; set; }
    public Guid TeammateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Team? Team { get; set; }
    public Teammate? Teammate { get; set; }
}
