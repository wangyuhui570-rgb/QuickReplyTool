using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IPublishedKnowledgeCardCatalog
{
    IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards();
}
