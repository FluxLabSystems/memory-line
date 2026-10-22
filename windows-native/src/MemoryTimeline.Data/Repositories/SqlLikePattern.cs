namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Builds LIKE patterns from user-entered text. SQLite's LIKE reads
/// <c>%</c> and <c>_</c> as wildcards, so a search for "100%" or "a_b"
/// interpolated straight into a pattern matches far more than the user
/// typed. Every pattern built here is paired with
/// <c>EF.Functions.Like(column, pattern, SqlLikePattern.Escape)</c>, which
/// emits <c>LIKE @pattern ESCAPE '\'</c>: an escaped wildcard is read
/// literally, and the escape character is escaped too so a typed backslash
/// cannot swallow the character after it.
/// </summary>
internal static class SqlLikePattern
{
    /// <summary>The escape character every pattern from this class is used with.</summary>
    public const string Escape = "\\";

    /// <summary>A pattern matching values that contain <paramref name="term"/> literally.</summary>
    public static string Contains(string term) => "%" + EscapeWildcards(term) + "%";

    /// <summary>
    /// <paramref name="term"/> with every LIKE metacharacter escaped: the
    /// escape character first, so the escapes added for % and _ are not
    /// themselves escaped.
    /// </summary>
    public static string EscapeWildcards(string term) => term
        .Replace(Escape, Escape + Escape)
        .Replace("%", Escape + "%")
        .Replace("_", Escape + "_");
}
