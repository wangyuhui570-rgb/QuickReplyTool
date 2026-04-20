using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class PublishedKnowledgeCatalogService : IPublishedKnowledgeCardCatalog
{
    private readonly SqliteAppDataStore _dataStore;
    private IReadOnlyList<PublishedKnowledgeCard> _cards = [];

    public PublishedKnowledgeCatalogService(SqliteAppDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public IReadOnlyList<PublishedKnowledgeCard> GetAllPublishedKnowledgeCards()
    {
        return _cards;
    }

    public async Task ReloadAsync()
    {
        _cards = await _dataStore.LoadPublishedKnowledgeCardsAsync().ConfigureAwait(false);
    }
}
