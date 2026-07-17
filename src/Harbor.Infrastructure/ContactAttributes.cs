using System.Text.Json;
using Harbor.Domain.Entities;

namespace Harbor.Infrastructure;

/// <summary>
/// Reads and writes <see cref="Contact.AttributesJson"/> as a flat string map.
///
/// Values are kept as strings deliberately. Segment rules over attributes are
/// evaluated with json_extract, which hands back whatever type the JSON held;
/// pinning attributes to strings means a rule's comparison behaves the same
/// whether the value arrived as "5" or 5, instead of silently matching nothing.
/// </summary>
public static class ContactAttributes
{
    public const string Empty = "{}";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, string?> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options)
                ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            // Unreadable attributes must not take a contact's whole record down.
            return new Dictionary<string, string?>();
        }
    }

    /// <summary>Serializes attributes, dropping null values rather than storing JSON nulls.</summary>
    public static string Write(IReadOnlyDictionary<string, string?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return Empty;
        }

        var kept = attributes
            .Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);

        return kept.Count == 0 ? Empty : JsonSerializer.Serialize(kept, Options);
    }
}
