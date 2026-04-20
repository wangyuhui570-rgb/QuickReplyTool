using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class InMemoryPublishedKnowledgeCardCatalog : IPublishedKnowledgeCardCatalog
{
    private readonly IReadOnlyList<PublishedKnowledgeCard> _cards;

    public InMemoryPublishedKnowledgeCardCatalog(IReadOnlyList<PublishedKnowledgeCard> cards)
    {
        _cards = cards;
    }

    public IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards()
    {
        return _cards;
    }
}
