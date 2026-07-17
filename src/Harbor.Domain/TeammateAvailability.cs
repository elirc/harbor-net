namespace Harbor.Domain;

public enum TeammateAvailability
{
    /// <summary>Eligible for auto-assignment.</summary>
    Available = 0,

    /// <summary>Skipped by auto-assignment; can still be assigned manually.</summary>
    Away = 1,
}
