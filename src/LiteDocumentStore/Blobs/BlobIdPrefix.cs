using System.Text;

namespace LiteDocumentStore;

/// <summary>
/// Turns an id prefix into the exclusive upper bound of the range that holds exactly the ids
/// starting with it.
/// </summary>
/// <remarks>
/// <para>
/// Filtering a listing by prefix is a range scan, <c>id &gt;= @Prefix AND id &lt; @PrefixEnd</c>,
/// rather than <c>LIKE</c>, <c>GLOB</c> or <c>substr</c>. Measured against SQLite 3.53.3, the
/// range is the only one of the four that <em>searches</em> the primary-key index
/// (<c>SEARCH … USING COVERING INDEX (id&gt;? AND id&lt;?)</c>); the other three scan it end to
/// end. <c>LIKE</c> is also wrong here: it is case-insensitive for ASCII, so the prefix
/// <c>u/1</c> matched a stored <c>U/1UPPER</c>. And a range needs no wildcard escaping, so a
/// prefix holding <c>%</c>, <c>_</c>, <c>*</c> or <c>[</c> is matched literally with no escape
/// character to get wrong.
/// </para>
/// <para>
/// SQLite compares TEXT with <c>memcmp</c> over UTF-8, which is code-point order (measured:
/// <c>é</c> sorts after <c>z</c>), so incrementing the prefix's last code point yields the first
/// string that sorts after every string starting with it.
/// </para>
/// </remarks>
internal static class BlobIdPrefix
{
    private const int MaxCodePoint = 0x10FFFF;
    private const int FirstSurrogate = 0xD800;
    private const int AfterLastSurrogate = 0xE000;

    /// <summary>
    /// Computes the exclusive upper bound for <paramref name="prefix"/>.
    /// </summary>
    /// <returns>
    /// False when no bound exists — an empty prefix, or one made only of the maximum code point,
    /// where every id at or after the prefix is in range and the lower bound alone is exact.
    /// </returns>
    internal static bool TryGetUpperBound(string prefix, out string upperBound)
    {
        var runes = new List<Rune>();
        foreach (var rune in prefix.EnumerateRunes())
        {
            runes.Add(rune);
        }

        // Trailing maximum code points cannot be incremented, so they are dropped: every id
        // starting with "a\U0010FFFF" also starts with "a", and "b" bounds both.
        while (runes.Count > 0)
        {
            var last = runes[^1].Value;
            if (last == MaxCodePoint)
            {
                runes.RemoveAt(runes.Count - 1);
                continue;
            }

            var next = last + 1;
            if (next == FirstSurrogate)
            {
                next = AfterLastSurrogate;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < runes.Count - 1; i++)
            {
                builder.Append(runes[i]);
            }

            builder.Append(new Rune(next));
            upperBound = builder.ToString();
            return true;
        }

        upperBound = string.Empty;
        return false;
    }
}
