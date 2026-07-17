namespace Harbor.Domain;

/// <summary>
/// Carries an optimistic-concurrency token. The value is opaque and is
/// replaced on every update; a writer holding a stale one loses.
/// </summary>
public interface IHasVersion
{
    Guid Version { get; set; }
}
