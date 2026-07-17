namespace Harbor.Domain;

/// <summary>Thrown when a domain invariant is violated. Maps to HTTP 422/400 at the API edge.</summary>
public class DomainException(string message) : Exception(message);
