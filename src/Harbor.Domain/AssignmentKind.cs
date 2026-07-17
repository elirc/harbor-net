namespace Harbor.Domain;

public enum AssignmentKind
{
    /// <summary>A teammate changed the assignment through the API.</summary>
    Manual = 0,

    /// <summary>Round-robin auto-assignment picked the assignee.</summary>
    Auto = 1,
}
