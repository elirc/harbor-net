namespace Harbor.Domain;

/// <summary>Where a message came from, or went out over.</summary>
public enum MessageChannel
{
    /// <summary>The in-app messenger.</summary>
    Chat = 0,

    /// <summary>Inbound or outbound email.</summary>
    Email = 1,
}
