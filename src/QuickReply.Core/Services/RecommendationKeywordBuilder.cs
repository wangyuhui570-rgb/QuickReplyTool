namespace QuickReply.Desktop.Services;

public static class RecommendationKeywordBuilder
{
    private const int MaxKeywordCount = 12;
    private const int PreferredCustomerKeywordCount = 6;
    private const int PreferredContextKeywordCount = 4;
    private const int PreferredProductIdKeywordCount = 1;
    private const int PreferredProductNameKeywordCount = 2;

    public static IReadOnlyList<string> Build(
        string recognizedCustomerText,
        string recognizedContextText = "",
        string productId = "",
        string productName = "")
    {
        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeywordsFromText(recognizedCustomerText, seen, keywords, PreferredCustomerKeywordCount);
        AddKeywordsFromText(recognizedContextText, seen, keywords, PreferredContextKeywordCount);
        AddKeywordsFromText(productId, seen, keywords, PreferredProductIdKeywordCount);
        AddKeywordsFromText(productName, seen, keywords, PreferredProductNameKeywordCount);

        if (keywords.Count < MaxKeywordCount)
        {
            AddKeywordsFromText(recognizedCustomerText, seen, keywords, MaxKeywordCount);
            AddKeywordsFromText(recognizedContextText, seen, keywords, MaxKeywordCount);
            AddKeywordsFromText(productId, seen, keywords, MaxKeywordCount);
            AddKeywordsFromText(productName, seen, keywords, MaxKeywordCount);
        }

        return keywords.Take(MaxKeywordCount).ToArray();
    }

    private static void AddKeywordsFromText(string value, HashSet<string> seen, List<string> keywords, int maxAdditionalKeywords)
    {
        var added = 0;
        foreach (var keyword in ExtractRecommendationKeywords(value))
        {
            if (AddKeyword(keyword, seen, keywords))
            {
                added++;
                if (added >= maxAdditionalKeywords)
                {
                    return;
                }
            }

            if (keywords.Count >= MaxKeywordCount)
            {
                return;
            }
        }
    }

    private static IReadOnlyList<string> ExtractRecommendationKeywords(string recognizedCustomerText)
    {
        if (string.IsNullOrWhiteSpace(recognizedCustomerText))
        {
            return [];
        }

        var normalized = System.Text.RegularExpressions.Regex.Replace(recognizedCustomerText, @"\s+", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(normalized, @"[A-Za-z0-9_-]{2,32}"))
        {
            AddKeyword(match.Value, seen, keywords);
        }

        var chineseOnly = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^\u4e00-\u9fff]", string.Empty);
        for (var length = 4; length >= 2; length--)
        {
            for (var index = 0; index <= chineseOnly.Length - length; index++)
            {
                AddKeyword(chineseOnly.Substring(index, length), seen, keywords);
                if (keywords.Count >= MaxKeywordCount)
                {
                    return keywords;
                }
            }
        }

        return keywords;
    }

    private static bool AddKeyword(string keyword, HashSet<string> seen, List<string> keywords)
    {
        var normalized = keyword.Trim();
        if (normalized.Length < 2 ||
            normalized.Length > 32 ||
            !seen.Add(normalized) ||
            IsStopword(normalized))
        {
            return false;
        }

        keywords.Add(normalized);
        return true;
    }

    private static bool IsStopword(string keyword)
    {
        return keyword is "你好" or "您好" or "请问" or "这个" or "那个" or "一下" or "可以" or "怎么" or "为什么" or "什么" or "麻烦" or "谢谢";
    }
}
