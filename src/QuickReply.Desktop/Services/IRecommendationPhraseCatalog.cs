using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IRecommendationPhraseCatalog
{
    IReadOnlyList<ReplyPhrase> GetAllPhrases();

    int GetPhraseSendCount(Guid phraseId);

    DateTimeOffset? GetPhraseLastSentAt(Guid phraseId);
}
