namespace Harbor.Domain;

public enum MessageKind
{
    /// <summary>A reply visible to the contact.</summary>
    Reply = 0,

    /// <summary>An internal note visible only to teammates.</summary>
    Note = 1,
}
