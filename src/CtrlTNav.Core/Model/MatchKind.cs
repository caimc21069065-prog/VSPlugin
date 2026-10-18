namespace CtrlTNav.Core.Model
{
    /// <summary>
    /// Quality of a match. Exact, Prefix, Camel and Subsequence are ordered from weakest to strongest;
    /// <see cref="FullName"/> is a separate class for qualified-name-only hits.
    /// </summary>
    public enum MatchKind
    {
        None = 0,
        Subsequence = 1,
        Camel = 2,
        Prefix = 3,
        Exact = 4,

        /// <summary>Match was found on the qualified name only (no highlight ranges available).</summary>
        FullName = 5,
    }
}
