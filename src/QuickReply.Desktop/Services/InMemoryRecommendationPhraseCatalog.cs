using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class InMemoryRecommendationPhraseCatalog : IRecommendationPhraseCatalog
{
    private readonly IReadOnlyList<ReplyPhrase> _phrases;
    private readonly IReadOnlyDictionary<Guid, PhraseUsageStat> _usageStats;

    public InMemoryRecommendationPhraseCatalog(
        IReadOnlyList<ReplyPhrase> phrases,
        IReadOnlyDictionary<Guid, PhraseUsageStat> usageStats)
    {
        _phrases = phrases;
        _usageStats = usageStats;
    }

    public static InMemoryRecommendationPhraseCatalog FromAppData(
        AppData appData,
        IReadOnlyDictionary<Guid, PhraseUsageStat> usageStats,
        DateTimeOffset? createdBeforeOrAt = null)
    {
        var phrases = appData.Categories
            .SelectMany(category => category.Phrases)
            .Where(phrase => createdBeforeOrAt is null || phrase.CreatedAt <= createdBeforeOrAt.Value)
            .GroupBy(phrase => phrase.Id)
            .Select(group => group.First())
            .ToList();

        return new InMemoryRecommendationPhraseCatalog(phrases, usageStats);
    }

    public IReadOnlyList<ReplyPhrase> GetAllPhrases()
    {
        return _phrases;
    }

    public int GetPhraseSendCount(Guid phraseId)
    {
        return _usageStats.TryGetValue(phraseId, out var stat) ? stat.SendCount : 0;
    }

    public DateTimeOffset? GetPhraseLastSentAt(Guid phraseId)
    {
        return _usageStats.TryGetValue(phraseId, out var stat) ? stat.LastSentAt : null;
    }
}
