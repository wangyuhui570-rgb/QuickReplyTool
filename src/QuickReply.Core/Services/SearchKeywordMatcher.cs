using System.Runtime.CompilerServices;
using System.Text;

namespace QuickReply.Core.Services;

/// <summary>
/// Keyword matching for phrase and file search: space-separated keywords must all match, and a
/// keyword of plain letters also matches the pinyin initials of Chinese text ("th" finds "退货").
/// </summary>
public static class SearchKeywordMatcher
{
    private const int LiteralTitleWeight = 4;
    private const int LiteralTagWeight = 3;
    private const int PinyinWeight = 2;
    private const int LiteralBodyWeight = 1;

    // First GB2312 code of each initial. GB2312 level 1 (3755 common characters) is sorted by
    // pinyin, so a character's initial is the last boundary at or below its code.
    private static readonly (int Code, char Initial)[] InitialBoundaries =
    [
        (0xB0A1, 'a'), (0xB0C5, 'b'), (0xB2C1, 'c'), (0xB4EE, 'd'), (0xB6EA, 'e'), (0xB7A2, 'f'),
        (0xB8C1, 'g'), (0xB9FE, 'h'), (0xBBF7, 'j'), (0xBFA6, 'k'), (0xC0AC, 'l'), (0xC2E8, 'm'),
        (0xC4C3, 'n'), (0xC5B6, 'o'), (0xC5BE, 'p'), (0xC6DA, 'q'), (0xC8BB, 'r'), (0xC8F6, 's'),
        (0xCBFA, 't'), (0xCDDA, 'w'), (0xCEF4, 'x'), (0xD1B9, 'y'), (0xD4D1, 'z')
    ];

    private const int LevelOneEnd = 0xD7F9;

    // Placeholder for characters without a known initial, so "退?货" never reads as "th".
    private const char UnknownInitial = '_';

    private static readonly ConditionalWeakTable<string, string> InitialsCache = new();
    private static readonly Lazy<Encoding?> Gb2312 = new(CreateGb2312);

    public static IReadOnlyList<string> Tokenize(string? query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static bool Matches(string? query, string? title, string? tags, string? body)
    {
        return Score(query, title, tags, body) > 0;
    }

    /// <summary>
    /// 0 when any keyword matches nowhere. Otherwise higher means a better match: literal title hits
    /// rank above tags, pinyin initials and body text. Pinyin is checked on title and tags only;
    /// two letters match too many places in a long reply.
    /// </summary>
    public static int Score(string? query, string? title, string? tags, string? body)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var token in tokens)
        {
            var best = 0;
            if (ContainsLiteral(title, token))
            {
                best = LiteralTitleWeight;
            }
            else if (ContainsLiteral(tags, token))
            {
                best = LiteralTagWeight;
            }
            else if (IsPinyinQuery(token) &&
                     (ContainsInitials(title, token) || ContainsInitials(tags, token)))
            {
                best = PinyinWeight;
            }
            else if (ContainsLiteral(body, token))
            {
                best = LiteralBodyWeight;
            }

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        return total;
    }

    /// <summary>Literal (non-pinyin) keyword ranges inside <paramref name="text"/>, for highlighting.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindLiteralRanges(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var marked = new bool[text.Length];
        foreach (var token in Tokenize(query))
        {
            var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                Array.Fill(marked, true, index, token.Length);
                index = text.IndexOf(token, index + token.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        var ranges = new List<(int Start, int Length)>();
        for (var i = 0; i < marked.Length; i++)
        {
            if (!marked[i])
            {
                continue;
            }

            var start = i;
            while (i < marked.Length && marked[i])
            {
                i++;
            }

            ranges.Add((start, i - start));
        }

        return ranges;
    }

    /// <summary>
    /// One lowercase letter per character: pinyin initial for Chinese, the letter or digit itself
    /// otherwise, '_' for anything else, so positions line up with the source text.
    /// </summary>
    public static string GetPinyinInitials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return InitialsCache.GetValue(text, BuildInitials);
    }

    private static string BuildInitials(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(GetInitial(character));
        }

        return builder.ToString();
    }

    private static char GetInitial(char character)
    {
        if (character < 0x80)
        {
            return char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : UnknownInitial;
        }

        if (character is < '一' or > '鿿')
        {
            return UnknownInitial;
        }

        var encoding = Gb2312.Value;
        if (encoding is null)
        {
            return UnknownInitial;
        }

        Span<char> source = [character];
        Span<byte> bytes = stackalloc byte[4];
        if (encoding.GetBytes(source, bytes) != 2)
        {
            return UnknownInitial;
        }

        var code = (bytes[0] << 8) | bytes[1];
        if (code < InitialBoundaries[0].Code || code > LevelOneEnd)
        {
            return UnknownInitial;
        }

        var initial = UnknownInitial;
        foreach (var (boundary, letter) in InitialBoundaries)
        {
            if (code < boundary)
            {
                break;
            }

            initial = letter;
        }

        return initial;
    }

    private static bool IsPinyinQuery(string token)
    {
        return token.Length >= 2 && token.All(char.IsAsciiLetter);
    }

    private static bool ContainsInitials(string? text, string token)
    {
        return !string.IsNullOrEmpty(text) &&
               GetPinyinInitials(text).Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsLiteral(string? text, string token)
    {
        return !string.IsNullOrEmpty(text) && text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding? CreateGb2312()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return null;
        }
    }
}
