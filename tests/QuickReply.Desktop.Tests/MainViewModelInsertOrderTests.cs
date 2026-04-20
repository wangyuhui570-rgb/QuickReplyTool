using QuickReply.Desktop.Models;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests;

public sealed class MainViewModelInsertOrderTests
{
    [Fact]
    public void ResolvePhraseInsertIndex_ReturnsEndWhenAnchorIsMissing()
    {
        var category = CreateCategoryWithPhrases(3);

        var insertIndex = MainViewModel.ResolvePhraseInsertIndex(category, anchorPhrase: null);

        Assert.Equal(3, insertIndex);
    }

    [Fact]
    public void ApplyInsertedPhraseOrderChanges_AppendingAtEndDoesNotRequirePersistence()
    {
        var category = CreateCategoryWithPhrases(3);
        var insertedPhrase = CreatePhrase(category.Id, "新短语", sortOrder: 4);

        var changedPhrases = MainViewModel.ApplyInsertedPhraseOrderChanges(category, insertedPhrase, insertIndex: 3);

        Assert.Empty(changedPhrases);
        Assert.Equal([1, 2, 3, 4], category.Phrases.Select(phrase => phrase.SortOrder));
        Assert.Equal(insertedPhrase.Id, category.Phrases[3].Id);
    }

    [Fact]
    public void ApplyInsertedPhraseOrderChanges_InsertingInMiddleOnlyReturnsShiftedTail()
    {
        var category = CreateCategoryWithPhrases(4);
        var insertedPhrase = CreatePhrase(category.Id, "插入短语", sortOrder: 3);

        var changedPhrases = MainViewModel.ApplyInsertedPhraseOrderChanges(category, insertedPhrase, insertIndex: 2);

        Assert.Equal([1, 2, 3, 4, 5], category.Phrases.Select(phrase => phrase.SortOrder));
        Assert.Equal(insertedPhrase.Id, category.Phrases[2].Id);
        Assert.Equal(2, changedPhrases.Count);
        Assert.Equal(category.Phrases[3].Id, changedPhrases[0].Id);
        Assert.Equal(category.Phrases[4].Id, changedPhrases[1].Id);
    }

    private static ReplyCategory CreateCategoryWithPhrases(int phraseCount)
    {
        var category = new ReplyCategory
        {
            Id = Guid.NewGuid(),
            Name = "测试分类",
            SortOrder = 1,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

        for (var index = 0; index < phraseCount; index++)
        {
            category.Phrases.Add(CreatePhrase(category.Id, $"短语 {index + 1}", index + 1));
        }

        return category;
    }

    private static ReplyPhrase CreatePhrase(Guid categoryId, string title, int sortOrder)
    {
        return new ReplyPhrase
        {
            Id = Guid.NewGuid(),
            CategoryId = categoryId,
            CategoryName = "测试分类",
            Title = title,
            Tags = string.Empty,
            Content = title,
            SortOrder = sortOrder,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
    }
}
