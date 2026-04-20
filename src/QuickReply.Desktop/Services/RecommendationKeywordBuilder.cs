namespace QuickReply.Desktop.Services;

public static class RecommendationKeywordBuilder
{
    public static IReadOnlyList<string> Build(
        string recognizedCustomerText,
        string recognizedContextText = "",
        string productId = "",
        string productName = "")
    {
        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeywordsFromText(recognizedCustomerText, seen, keywords);
        if (keywords.Count < 12)
        {
            AddKeywordsFromText(productId, seen, keywords);
        }

        if (keywords.Count < 12)
        {
            AddKeywordsFromText(productName, seen, keywords);
        }

        if (keywords.Count < 12)
        {
            AddKeywordsFromText(recognizedContextText, seen, keywords);
        }

        return keywords.Take(12).ToArray();
    }

    private static void AddKeywordsFromText(string value, HashSet<string> seen, List<string> keywords)
    {
        foreach (var keyword in ExtractRecommendationKeywords(value))
        {
            AddKeyword(keyword, seen, keywords);
            if (keywords.Count >= 12)
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
                if (keywords.Count >= 12)
                {
                    return keywords;
                }
            }
        }

        return keywords;
    }

    private static void AddKeyword(string keyword, HashSet<string> seen, List<string> keywords)
    {
        var normalized = keyword.Trim();
        if (normalized.Length < 2 ||
            normalized.Length > 32 ||
            !seen.Add(normalized) ||
            IsStopword(normalized))
        {
            return;
        }

        keywords.Add(normalized);
    }

    private static bool IsStopword(string keyword)
    {
        return keyword is "你好" or "您好" or "请问" or "这个" or "那个" or "一下" or "可以" or "怎么" or "为什么" or "什么" or "麻烦" or "谢谢";
    }
}
