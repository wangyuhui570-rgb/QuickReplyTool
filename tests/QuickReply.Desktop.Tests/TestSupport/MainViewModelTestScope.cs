using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests.TestSupport;

internal sealed class MainViewModelTestScope : IAsyncDisposable
{
    public TestAppDataScope Data { get; } = new();
    public SqliteAppDataStore Store { get; }
    public StubMessageComposeService Compose { get; } = new();
    public MainViewModel Main { get; }
    private readonly ConversationLoggingService _logs;

    public MainViewModelTestScope()
    {
        Store = Data.CreateDataStore();
        _logs = Data.CreateConversationLoggingService();
        Main = new MainViewModel(Store, Compose, new PhraseExcelService(), new UsageReportExcelService(),
            new PersonalConfigService(), new FakeExternalWorkbenchService(), new WindowDockService(),
            () => _logs, new ExceptionLogService());
    }

    public async Task<ReplyPhrase> AddPhraseAsync(string name = "测试", string content = "正文")
    {
        await Store.LoadAsync();
        var category = new ReplyCategory { Name = name };
        var phrase = new ReplyPhrase { Title = "操作说明", Content = content, CategoryId = category.Id };
        category.Phrases.Add(phrase);
        await Store.InsertCategoryAsync(category);
        await Store.InsertPhraseAsync(phrase);
        Main.Categories.Add(category);
        Main.SelectedCategory = category;
        Main.SelectedPhrase = phrase;
        return phrase;
    }

    public async ValueTask DisposeAsync()
    {
        await _logs.DisposeAsync();
        Data.Dispose();
    }
}

internal sealed class StubMessageComposeService : IMessageComposeService
{
    public ReplyActionResult Result { get; set; } = new(true, "ok");
    public int CallCount { get; private set; }

    private Task<ReplyActionResult> Complete()
    {
        CallCount++;
        return Task.FromResult(Result);
    }

    public Task<ReplyActionResult> CopyToClipboardAsync(string text, IReadOnlyCollection<string>? imagePaths = null) => Complete();
    public Task<ReplyActionResult> ExecuteAsync(string text, AppSettings settings, ComposeActionMode mode, IReadOnlyCollection<string>? imagePaths = null) => Complete();
    public Task<ReplyActionResult> CopyFilesToClipboardAsync(IReadOnlyCollection<string> filePaths) => Complete();
    public Task<ReplyActionResult> ExecuteFilesAsync(IReadOnlyCollection<string> filePaths, AppSettings settings, ComposeActionMode mode) => Complete();
}
