using System.Globalization;
using System.Text;

namespace Harbor.Infrastructure;

/// <summary>URL-safe identifiers derived from human titles.</summary>
public static class Slugs
{
    /// <summary>
    /// Lowercases, replaces any run of non-alphanumeric characters with a
    /// single hyphen, and trims hyphens from the ends. Returns null when the
    /// text has nothing sluggable in it, so callers can fall back rather than
    /// mint an empty identifier.
    /// </summary>
    public static string? From(string? text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder();
        var pendingHyphen = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));

                if (builder.Length >= maxLength)
                {
                    break;
                }

                continue;
            }

            pendingHyphen = true;
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? null : slug;
    }
}
