namespace Harbor.Domain;

public enum ArticleStatus
{
    /// <summary>Visible only to teammates.</summary>
    Draft = 0,

    /// <summary>Readable by anyone through the public endpoints.</summary>
    Published = 1,
}
