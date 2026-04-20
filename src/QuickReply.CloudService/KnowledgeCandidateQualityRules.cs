namespace QuickReply.CloudService;

public static class KnowledgeCandidateKinds
{
    public const string Qa = "qa";
    public const string Resource = "resource";
}

public static class KnowledgeCandidateQualityRules
{
    private static readonly string[] NoiseTokens =
    [
        "企业微信",
        "拼多多工作台",
        "千牛接待台",
        "千牛工作台",
        "Chrome Legacy Window",
        "京麦工作台",
        "咚咚融合工作台",
        "工作台"
    ];

    private static readonly string[] ResourceTokens =
    [
        "教程",
        "下载",
        "下载地址",
        "软件",
        "app",
        "APP",
        "链接",
        "说明书",
        "安装",
        "测评",
        "文档"
    ];

    private static readonly string[] ResourceAddressPhrases =
    [
        "退货地址",
        "寄回地址",
        "售后地址",
        "收货地址",
        "我们的地址",
        "地址："
    ];

    private static readonly string[] LowValueResourcePhrases =
    [
        "麻烦您拍个视频",
        "拍个视频这边看一下",
        "拍个视频看一下",
        "录个视频看一下",
        "申请下退款",
        "很抱歉您的地址",
        "这边看一下",
        "稍等下亲我看下"
    ];

    private static readonly string[] QuestionIndicators =
    [
        "吗",
        "么",
        "什么",
        "怎么",
        "如何",
        "是否",
        "可以",
        "能不能",
        "能否",
        "有没有",
        "多久",
        "多少",
        "几天",
        "为什么",
        "是不是",
        "兼容",
        "区别",
        "支持",
        "怎么用",
        "怎么装",
        "如何安装"
    ];

    private static readonly string[] LowValueQuestions =
    [
        "您好",
        "您好亲",
        "稍等",
        "稍等下亲我看下",
        "麻烦您",
        "好的亲",
        "好的亲麻烦您了",
        "正常的",
        "没有这个数据的呢",
        "没有送的呢",
        "暂时没有售卖呢",
        "已经最优惠了呢",
        "已经最优惠了哈 没有其他优惠了",
        "拍不了就是没货了呢"
    ];

    private static readonly string[] LowValueAnswers =
    [
        "您好",
        "您好亲",
        "稍等",
        "稍等下亲我看下",
        "麻烦您",
        "好的亲",
        "好的亲麻烦您了",
        "没有这个数据的呢",
        "没有送的呢",
        "暂时没有售卖呢",
        "已经最优惠了呢",
        "拍不了就是没货了呢"
    ];

    public static string BuildClusterKey(string platform, string productKey, string question, string candidateKind)
    {
        return $"{NormalizePlatform(platform)}|{NormalizeQuestionText(productKey)}|{NormalizeCandidateKind(candidateKind)}|{NormalizeQuestionText(question)}";
    }

    public static string NormalizeCandidateKind(string? value)
    {
        return string.Equals(value, KnowledgeCandidateKinds.Resource, StringComparison.OrdinalIgnoreCase)
            ? KnowledgeCandidateKinds.Resource
            : KnowledgeCandidateKinds.Qa;
    }

    public static string NormalizePlatform(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();
    }

    public static string NormalizeQuestionText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Replace("\r", " ").Replace("\n", " ");
        if (text.Length < 2)
        {
            return string.Empty;
        }

        if (NoiseTokens.Any(noise => text.Contains(noise, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        if (text.Contains(":\\", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (text.All(ch => char.IsDigit(ch) || ch == '-' || ch == '_' || ch == ' ' || ch == '.'))
        {
            return string.Empty;
        }

        return text;
    }

    public static string NormalizeAnswerText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Replace("\r", " ").Replace("\n", " ");
        if (text.Length < 2)
        {
            return string.Empty;
        }

        if (text.Contains(":\\", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return text;
    }

    public static bool IsLowValueQuestion(string text)
    {
        var normalized = NormalizeQuestionText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (LowValueQuestions.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!ContainsCjk(normalized) && normalized.Length < 6)
        {
            return true;
        }

        if (IsAlphaNumericDominant(normalized))
        {
            return true;
        }

        return false;
    }

    public static bool IsLowValueAnswer(string text)
    {
        var normalized = NormalizeAnswerText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (LowValueAnswers.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalized.Length < 4 && !ContainsUrl(normalized))
        {
            return true;
        }

        return false;
    }

    public static bool CanUseSearchKeywordFallback(string text)
    {
        var normalized = NormalizeQuestionText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (IsLowValueQuestion(normalized))
        {
            return false;
        }

        if (!ContainsCjk(normalized))
        {
            return false;
        }

        if (normalized.Length < 4 && !HasStrongResourceSignal(normalized, normalized))
        {
            return false;
        }

        return true;
    }

    public static bool LooksLikeCompleteQaQuestion(string text)
    {
        var normalized = NormalizeQuestionText(text);
        if (string.IsNullOrWhiteSpace(normalized) || IsLowValueQuestion(normalized))
        {
            return false;
        }

        if (normalized.Contains('？') || normalized.Contains('?'))
        {
            return true;
        }

        if (QuestionIndicators.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ContainsCjk(normalized) && normalized.Length >= 6;
    }

    public static bool IsResourceCandidate(string question, string answer)
    {
        return HasStrongResourceSignal(question, answer);
    }

    public static string ClassifyCandidateKind(string question, string answer)
    {
        return IsResourceCandidate(question, answer)
            ? KnowledgeCandidateKinds.Resource
            : KnowledgeCandidateKinds.Qa;
    }

    public static bool ShouldKeepImportedCandidate(
        string candidateKind,
        string question,
        string answer,
        int sourceCount,
        double confidence,
        bool fromKeywordFallback)
    {
        var normalizedKind = NormalizeCandidateKind(candidateKind);
        if (IsLowValueAnswer(answer))
        {
            return false;
        }

        if (normalizedKind == KnowledgeCandidateKinds.Resource)
        {
            if (IsLowValueQuestion(question))
            {
                return false;
            }

             if (IsLowValueResourceCandidate(question, answer))
            {
                return false;
            }

            if (fromKeywordFallback && sourceCount < 2 && !HasStrongResourceSignal(question, answer))
            {
                return false;
            }

            if (!ContainsUrl(answer) && sourceCount == 1 && !LooksLikeStandaloneResourceTopic(question, answer))
            {
                return false;
            }

            return true;
        }

        if (!LooksLikeCompleteQaQuestion(question))
        {
            return false;
        }

        if (fromKeywordFallback && sourceCount < 2)
        {
            return false;
        }

        if (!fromKeywordFallback && sourceCount == 1 && confidence >= 0.55d && NormalizeAnswerText(answer).Length >= 8)
        {
            return true;
        }

        if (confidence < 0.2d && sourceCount < 2)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldUseAiSynthesis(KnowledgeCandidateRecord candidate, IReadOnlyList<KnowledgeCandidateSourceRecord> sources)
    {
        if (!string.Equals(candidate.CandidateKind, KnowledgeCandidateKinds.Qa, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.SourceCount < 2 || sources.Count < 2)
        {
            return false;
        }

        return LooksLikeCompleteQaQuestion(candidate.QuestionNormalized);
    }

    public static List<string> BuildTags(string candidateKind, string platform, string question, string answer)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePlatform(platform)
        };

        if (string.Equals(candidateKind, KnowledgeCandidateKinds.Resource, StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("资料卡");
        }

        var combined = $"{question} {answer}";
        if (combined.Contains("发货", StringComparison.OrdinalIgnoreCase) || combined.Contains("时效", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("发货");
        }

        if (combined.Contains("开票", StringComparison.OrdinalIgnoreCase) || combined.Contains("发票", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("发票");
        }

        if (combined.Contains("退款", StringComparison.OrdinalIgnoreCase) || combined.Contains("赔付", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("退款");
        }

        if (IsResourceCandidate(question, answer))
        {
            tags.Add("资料");
        }

        return tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
    }

    public static string InferRiskLevel(string question, string answer)
    {
        var combined = $"{question} {answer}";
        if (combined.Contains("退款", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("赔付", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("补偿", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        if (combined.Contains("发票", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("开票", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("时效", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return "low";
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
    }

    private static bool ContainsUrl(string text)
    {
        return text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStrongResourceSignal(string question, string answer)
    {
        var combined = $"{question} {answer}";
        return ResourceTokens.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               HasResourceVideoSignal(question, answer) ||
               HasResourceAddressSignal(question, answer) ||
               ContainsUrl(answer);
    }

    private static bool HasResourceVideoSignal(string question, string answer)
    {
        var combined = $"{question} {answer}";
        if (!combined.Contains("视频", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LowValueResourcePhrases.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return ContainsUrl(answer) ||
               combined.Contains("安装", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("教程", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("说明", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasResourceAddressSignal(string question, string answer)
    {
        var combined = $"{question} {answer}";
        return ResourceAddressPhrases.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLowValueResourceCandidate(string question, string answer)
    {
        var normalizedQuestion = NormalizeQuestionText(question);
        var normalizedAnswer = NormalizeAnswerText(answer);
        var combined = $"{normalizedQuestion} {normalizedAnswer}";

        if (LowValueResourcePhrases.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!ContainsUrl(normalizedAnswer) &&
            string.Equals(normalizedQuestion, normalizedAnswer, StringComparison.OrdinalIgnoreCase) &&
            normalizedQuestion.Length >= 12)
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeStandaloneResourceTopic(string question, string answer)
    {
        var normalizedQuestion = NormalizeQuestionText(question);
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return false;
        }

        if (normalizedQuestion.Length <= 12 && !normalizedQuestion.Contains('，') && !normalizedQuestion.Contains('。'))
        {
            return true;
        }

        return HasResourceAddressSignal(question, answer);
    }

    private static bool IsAlphaNumericDominant(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var alphaNumericCount = text.Count(ch => char.IsLetterOrDigit(ch));
        var cjkCount = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
        return alphaNumericCount >= Math.Max(3, cjkCount * 2) && text.Length <= 10;
    }
}
