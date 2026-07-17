namespace Harbor.Domain;

public enum TeammateRole
{
    /// <summary>Answers conversations; cannot manage the workspace directory.</summary>
    Agent = 0,

    /// <summary>Full workspace management: inboxes, teammates, teams, tags, canned replies.</summary>
    Admin = 1,
}
