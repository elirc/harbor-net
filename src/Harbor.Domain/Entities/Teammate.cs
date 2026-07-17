namespace Harbor.Domain.Entities;

/// <summary>An agent who answers conversations.</summary>
public class Teammate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public TeammateRole Role { get; set; } = TeammateRole.Agent;
    public TeammateAvailability Availability { get; set; } = TeammateAvailability.Available;

    /// <summary>Max open conversations assignable to this teammate; null = unlimited.</summary>
    public int? CapacityLimit { get; set; }

    /// <summary>SHA-256 hex digest of the teammate's API key. The raw key is never stored.</summary>
    public required string ApiKeyHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<TeamMembership> Memberships { get; } = new List<TeamMembership>();
}
