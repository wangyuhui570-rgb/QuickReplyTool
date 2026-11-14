using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests;

public sealed class UsageRecordingTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("selected")]
    [InlineData("overlay")]
    [InlineData("quick_tag")]
    public async Task PhraseSendRecordsOnlySuccessfulActions(string entry)
    {
        await using var scope = new MainViewModelTestScope();
        var phrase = await scope.AddPhraseAsync();
        var overlay = new SearchOverlayViewModel(scope.Main) { SearchText = phrase.Title };
        async Task Send()
        {
            switch (entry)
            {
                case "direct": await scope.Main.SendPhraseDirectAsync(phrase); break;
                case "selected": await scope.Main.SendSelectedPhraseAsync(); break;
                case "overlay": await overlay.SendResultAsync(1); break;
                case "quick_tag": await scope.Main.SendQuickTagCommand.ExecuteAsync(new QuickTag { PhraseId = phrase.Id }); break;
            }
        }

        await Send();
        Assert.Equal(1, scope.Main.GetPhraseSendCount(phrase.Id));
        var lastSent = scope.Main.GetPhraseLastSentAt(phrase.Id);
        Assert.NotNull(lastSent);
        scope.Compose.Result = new ReplyActionResult(false, "failed");
        await Send();
        Assert.Equal(1, scope.Main.GetPhraseSendCount(phrase.Id));
        Assert.Equal(lastSent, scope.Main.GetPhraseLastSentAt(phrase.Id));

        // Cancelling a variable prompt must stop before compose and leave existing statistics intact.
        phrase.Content = "{单号}";
        scope.Main.PhraseVariablePrompt = (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        var calls = scope.Compose.CallCount;
        await Send();
        Assert.Equal(calls, scope.Compose.CallCount);
        Assert.Equal(1, scope.Main.GetPhraseSendCount(phrase.Id));
        Assert.Equal(lastSent, scope.Main.GetPhraseLastSentAt(phrase.Id));
        var stored = await scope.Store.LoadPhraseUsageStatsAsync();
        Assert.Equal(1, stored[phrase.Id].SendCount);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("input")]
    [InlineData("copy")]
    [InlineData("overlay_send")]
    [InlineData("overlay_copy")]
    public async Task FileUseRecordsOnlySuccessfulActions(string action)
    {
        await using var scope = new MainViewModelTestScope();
        await scope.Store.LoadAsync();
        var file = new LibraryFileItem { DisplayName = "说明.txt", FullPath = scope.Data.CreateFile("说明.txt") };
        await scope.Store.UpsertFileItemAsync(file);
        scope.Main.FileItems.Add(file);
        var overlay = new SearchOverlayViewModel(scope.Main) { SearchText = "说明" };
        overlay.SetMode(SearchOverlayMode.File);
        async Task Use()
        {
            switch (action)
            {
                case "send": await scope.Main.SendFileItemDirectAsync(file); break;
                case "input": await scope.Main.InputFileItemDirectAsync(file); break;
                case "copy": await scope.Main.CopyFileItemDirectAsync(file); break;
                case "overlay_send": await overlay.SendResultAsync(1); break;
                case "overlay_copy": await overlay.CopyResultAsync(1); break;
            }
        }

        await Use();
        var lastUsed = scope.Main.GetFileLastUsedAt(file.Id);
        Assert.Equal(1, scope.Main.GetFileUseCount(file.Id));
        Assert.NotNull(lastUsed);
        scope.Compose.Result = new ReplyActionResult(false, "failed");
        await Use();
        Assert.Equal(1, scope.Main.GetFileUseCount(file.Id));
        Assert.Equal(lastUsed, scope.Main.GetFileLastUsedAt(file.Id));
        File.Delete(file.FullPath);
        var calls = scope.Compose.CallCount;
        await Use();
        Assert.Equal(calls, scope.Compose.CallCount);
        Assert.Equal(1, scope.Main.GetFileUseCount(file.Id));
        Assert.Equal(lastUsed, scope.Main.GetFileLastUsedAt(file.Id));
        var stored = await scope.Store.LoadFileUsageStatsAsync();
        Assert.Equal(1, stored[file.Id].UseCount);
    }
}
