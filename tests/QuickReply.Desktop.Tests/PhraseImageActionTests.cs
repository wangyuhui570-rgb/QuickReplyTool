using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests;

public sealed class PhraseImageActionTests
{
    [Theory]
    [InlineData("selected_send", false)]
    [InlineData("selected_send", true)]
    [InlineData("selected_input", false)]
    [InlineData("selected_input", true)]
    [InlineData("selected_copy", false)]
    [InlineData("selected_copy", true)]
    [InlineData("direct_send", false)]
    [InlineData("direct_send", true)]
    [InlineData("direct_input", false)]
    [InlineData("direct_input", true)]
    [InlineData("direct_copy", false)]
    [InlineData("direct_copy", true)]
    [InlineData("images_copy", false)]
    [InlineData("images_copy", true)]
    [InlineData("overlay_send", false)]
    [InlineData("overlay_send", true)]
    [InlineData("overlay_copy", false)]
    [InlineData("overlay_copy", true)]
    [InlineData("overlay_images", false)]
    [InlineData("overlay_images", true)]
    [InlineData("quick_tag", false)]
    [InlineData("quick_tag", true)]
    public async Task EveryPhraseEntryPointBlocksMissingImage(string entry, bool imageOnly)
    {
        using var scope = new TestAppDataScope();
        var store = scope.CreateDataStore();
        await store.LoadAsync();
        await using var logs = scope.CreateConversationLoggingService();
        var clipboard = new FakeClipboardService();
        var keyboard = new FakeKeyboardInputSimulator();
        var workbench = new FakeExternalWorkbenchService();
        var compose = new MessageComposeService(workbench, clipboard, keyboard, new FakeWindowFocusService());
        var main = new MainViewModel(store, compose, new PhraseExcelService(), new UsageReportExcelService(),
            new PersonalConfigService(), workbench, new WindowDockService(), () => logs, new ExceptionLogService());
        var imageStore = new PhraseImageStore(scope.AppRootPath);
        var paths = await imageStore.SavePhraseImagesAsync(Guid.NewGuid(),
            [scope.CreateFile("one.png"), scope.CreateFile("two.png"), scope.CreateFile("three.png")]);
        var phrase = new ReplyPhrase { Title = "缺图", Content = imageOnly ? "" : "请按下图操作" };
        phrase.SetImageRelativePaths(paths);
        var category = new ReplyCategory { Name = "售后" };
        phrase.CategoryId = category.Id;
        category.Phrases.Add(phrase);
        await store.InsertCategoryAsync(category);
        await store.InsertPhraseAsync(phrase);
        main.Categories.Add(category);
        main.SelectedCategory = category;
        main.SelectedPhrase = phrase;
        File.Delete(imageStore.ResolveAllAbsolutePaths(phrase.ImageRelativePath)[1]);
        Assert.Equal(2, main.GetPhraseImageAbsolutePaths(phrase).Count);
        var overlay = new SearchOverlayViewModel(main) { SearchText = "缺图" };
        switch (entry)
        {
            case "quick_tag":
                await main.SendQuickTagCommand.ExecuteAsync(new QuickTag { PhraseId = phrase.Id, PhraseContent = phrase.Content });
                break;
            case "selected_send": await main.SendSelectedPhraseAsync(); break;
            case "selected_input": await main.InputSelectedPhraseAsync(); break;
            case "selected_copy": await main.CopyPhraseCommand.ExecuteAsync(); break;
            case "direct_send": await main.SendPhraseDirectAsync(phrase); break;
            case "direct_input": await main.InputPhraseDirectAsync(phrase); break;
            case "direct_copy": await main.CopyPhraseDirectAsync(phrase); break;
            case "images_copy": await main.CopyPhraseImagesDirectAsync(phrase); break;
            case "overlay_send": await overlay.SendResultAsync(1); break;
            case "overlay_copy": await overlay.CopyResultAsync(1); break;
            case "overlay_images": await overlay.CopyResultImagesAsync(1); break;
        }

        Assert.Contains("未找到", main.StatusMessage);
        Assert.Equal(0, clipboard.SetTextCallCount);
        Assert.Equal(0, clipboard.SetTextAndFilesCallCount);
        Assert.Equal(0, clipboard.SetFileDropListCallCount);
        Assert.Equal(0, keyboard.PasteShortcutCount);
        Assert.Equal(0, keyboard.EnterCount);
        Assert.Equal(0, keyboard.CtrlEnterCount);
        Assert.Equal(0, keyboard.AltSCount);
    }
}
