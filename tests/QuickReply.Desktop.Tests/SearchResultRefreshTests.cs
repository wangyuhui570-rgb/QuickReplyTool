using System.Collections.Specialized;
using System.ComponentModel;
using QuickReply.Desktop.Tests.TestSupport;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests;

public sealed class SearchResultRefreshTests
{
    [Fact]
    public void ReplaceAllPublishesOneCompleteSnapshotAndPreservesNormalCollectionOperations()
    {
        var collection = new RangeObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, args) =>
        {
            events.Add(args.Action);
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                Assert.Equal(5000, collection.Count);
                Assert.Equal(4999, collection[^1]);
            }
        };
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) => properties.Add(args.PropertyName);

        collection.ReplaceAll(Enumerable.Range(0, 5000));

        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(["Count", "Item[]"], properties);
        collection.Add(5000);
        Assert.Equal(NotifyCollectionChangedAction.Add, events[^1]);
    }

    [Fact]
    public void ReplacingWithSameResultsOrItselfKeepsSelectionContainersUntouched()
    {
        var collection = new RangeObservableCollection<int> { 1, 2, 3 };
        var events = 0;
        collection.CollectionChanged += (_, _) => events++;

        collection.ReplaceAll([1, 2, 3]);
        collection.ReplaceAll(collection);

        Assert.Equal(0, events);
        Assert.Equal([1, 2, 3], collection);
    }

    [Fact]
    public async Task SearchUpdatesImmediatelyAndCommandsUseNewestResult()
    {
        await using var scope = new MainViewModelTestScope();
        var old = await scope.AddPhraseAsync("旧分类", "旧消息");
        old.Title = "旧目标";
        var latest = await scope.AddPhraseAsync("新分类", "新消息");
        latest.Title = "最新目标";
        scope.Main.SearchText = "旧目标";
        Assert.Same(old, scope.Main.SelectedPhrase);

        scope.Main.SearchText = "最新";
        await scope.Main.SendPhraseCommand.ExecuteAsync();

        Assert.Same(latest, scope.Main.SelectedPhrase);
        Assert.Equal("新消息", scope.Compose.LastText);
        Assert.Equal(1, scope.Main.GetPhraseSendCount(latest.Id));
        Assert.Equal(0, scope.Main.GetPhraseSendCount(old.Id));
        var overlay = new SearchOverlayViewModel(scope.Main) { SearchText = "旧目标" };
        overlay.SearchText = "最新";
        Assert.True((await overlay.SendResultAsync(1)).Success);
        Assert.Equal("新消息", scope.Compose.LastText);
        Assert.Equal(2, scope.Main.GetPhraseSendCount(latest.Id));

        scope.Main.SearchText = "目标";
        Assert.Same(latest, scope.Main.SelectedPhrase);
        var calls = scope.Compose.CallCount;
        scope.Main.SearchText = "没有任何匹配";
        Assert.Empty(scope.Main.FilteredPhrases);
        Assert.Null(scope.Main.SelectedPhrase);
        await scope.Main.SendPhraseCommand.ExecuteAsync();
        Assert.Equal(calls, scope.Compose.CallCount);
    }
}
