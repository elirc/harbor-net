using System.Globalization;

namespace Harbor.Domain;

/// <summary>
/// Reduces free text to a set of comparable keywords. Used on both sides of
/// article suggestion — the conversation and the article — so matching is a
/// set intersection rather than substring search. That is what stops "cat"
/// from matching "category".
/// </summary>
public static class Keywords
{
    /// <summary>Tokens shorter than this carry no signal.</summary>
    public const int MinimumLength = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "yours", "all", "any", "can",
        "cannot", "cant", "had", "has", "have", "her", "his", "its", "our", "ours", "out", "was",
        "were", "who", "why", "how", "what", "when", "where", "with", "will", "would", "should",
        "could", "this", "that", "these", "those", "there", "then", "than", "them", "they",
        "from", "into", "onto", "over", "under", "some", "such", "only", "very", "just", "does",
        "did", "done", "doing", "get", "got", "getting", "please", "hello", "thanks", "thank",
        "hi", "hey", "regards", "help", "need", "want", "know", "like", "make", "made", "using",
        "use", "used", "about", "after", "before", "again", "still", "even", "also", "because",
        "been", "being", "here", "more", "most", "much", "many", "same", "each", "every", "one",
        "two", "now", "new", "old", "see", "say", "said", "let", "lets", "put", "way", "ways",
        "thing", "things", "something", "anything", "everything", "nothing", "someone", "anyone",
    };

    /// <summary>
    /// Splits text on anything that is not a letter or digit, lowercases, and
    /// drops stop words and very short tokens.
    /// </summary>
    public static IReadOnlySet<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>();
        }

        var tokens = new HashSet<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        return tokens;
    }

    /// <summary>Extracts the union of keywords across several texts.</summary>
    public static IReadOnlySet<string> Extract(IEnumerable<string?> texts)
    {
        var tokens = new HashSet<string>();
        foreach (var text in texts)
        {
            tokens.UnionWith(Extract(text));
        }

        return tokens;
    }

    private static void Flush(System.Text.StringBuilder current, HashSet<string> tokens)
    {
        if (current.Length >= MinimumLength)
        {
            var token = current.ToString();
            if (!StopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }

        current.Clear();
    }
}
