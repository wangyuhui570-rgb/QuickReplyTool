using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class PhraseImportCategoryTests
{
    [Fact]
    public async Task Import_IgnoresUnrelatedDuplicates()
    {
        await using var scope = new MainViewModelTestScope();
        await scope.AddPhraseAsync("售后");
        await scope.AddPhraseAsync("售后");
        var path = CreateImport(scope, "物流");

        var result = await scope.Main.ImportPhrasesFromXlsAsync(path, chooseCategory: (_, _) => throw new Exception("不应询问"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("导入正文", Assert.Single(scope.Main.Categories.Single(c => c.Name == "物流").Phrases).Content);
    }

    [Fact]
    public async Task Import_UsesChosenCategoryForEveryMatchingRow()
    {
        await using var scope = new MainViewModelTestScope();
        var first = await scope.AddPhraseAsync("售后");
        var second = await scope.AddPhraseAsync("售后");
        var path = CreateImport(scope, "售后", "售后");
        var prompts = 0;

        var result = await scope.Main.ImportPhrasesFromXlsAsync(path, chooseCategory: (name, candidates) =>
        {
            prompts++;
            Assert.Equal("售后", name);
            Assert.Equal([first.CategoryId, second.CategoryId], candidates.Select(c => c.Id));
            return Task.FromResult<Guid?>(second.CategoryId);
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, prompts);
        var reloaded = await scope.Store.LoadAsync();
        Assert.Single(reloaded.Categories.Single(c => c.Id == first.CategoryId).Phrases);
        Assert.Equal(3, reloaded.Categories.Single(c => c.Id == second.CategoryId).Phrases.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_CancelOrInvalidSelectionWritesNothing(bool invalid)
    {
        await using var scope = new MainViewModelTestScope();
        await scope.AddPhraseAsync("售后");
        await scope.AddPhraseAsync("售后");
        var path = CreateImport(scope, "新分类", "售后");
        var before = await scope.Store.LoadAsync();
        var images = Directory.GetFiles(scope.Data.AppRootPath, "*.png", SearchOption.AllDirectories);

        var result = await scope.Main.ImportPhrasesFromXlsAsync(path,
            chooseCategory: (_, _) => Task.FromResult<Guid?>(invalid ? Guid.NewGuid() : null));

        Assert.False(result.Success);
        var after = await scope.Store.LoadAsync();
        Assert.Equal(before.Categories.Count, after.Categories.Count);
        Assert.Equal(before.Categories.Sum(c => c.Phrases.Count), after.Categories.Sum(c => c.Phrases.Count));
        Assert.Equal(images, Directory.GetFiles(scope.Data.AppRootPath, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Import_WithoutChooserReportsAmbiguityAndGuardResets()
    {
        await using var scope = new MainViewModelTestScope();
        await scope.AddPhraseAsync("售后");
        var second = await scope.AddPhraseAsync("售后");
        var path = CreateImport(scope, "售后");
        var result = await scope.Main.ImportPhrasesFromXlsAsync(path);
        Assert.False(result.Success);
        Assert.Contains("选择导入位置", result.Message);

        result = await scope.Main.ImportPhrasesFromXlsAsync(path,
            chooseCategory: (_, _) => Task.FromResult<Guid?>(second.CategoryId));
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task Import_ConcurrentRequestIsRejectedBeforeSecondWrite()
    {
        await using var scope = new MainViewModelTestScope();
        await scope.AddPhraseAsync("售后");
        var second = await scope.AddPhraseAsync("售后");
        var path = CreateImport(scope, "售后");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = scope.Main.ImportPhrasesFromXlsAsync(path, chooseCategory: (_, _) =>
        {
            entered.SetResult();
            return selected.Task;
        });
        await entered.Task;
        var concurrent = await scope.Main.ImportPhrasesFromXlsAsync(path);
        Assert.False(concurrent.Success);
        Assert.Contains("正在进行", concurrent.Message);
        selected.SetResult(second.CategoryId);
        Assert.True((await pending).Success);
        var after = await scope.Store.LoadAsync();
        Assert.Equal(2, after.Categories.Single(c => c.Id == second.CategoryId).Phrases.Count);
    }

    [Fact]
    public async Task Import_DatabaseFailureRollsBackRowsAndPreparedImages()
    {
        await using var scope = new MainViewModelTestScope();
        await scope.Store.LoadAsync();
        var path = CreateImport(scope, "新分类", "另一分类");
        var before = await scope.Store.LoadAsync();
        await using (var connection = new SqliteConnection($"Data Source={scope.Store.DatabasePath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_import BEFORE INSERT ON phrases
                WHEN NEW.category_id IN (SELECT id FROM categories WHERE name = '另一分类')
                BEGIN SELECT RAISE(ABORT, 'simulated import failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await scope.Main.ImportPhrasesFromXlsAsync(path);

        Assert.False(result.Success);
        var after = await scope.Store.LoadAsync();
        Assert.Equal(before.Categories.Count, after.Categories.Count);
        Assert.Equal(before.Categories.Sum(c => c.Phrases.Count), after.Categories.Sum(c => c.Phrases.Count));
        Assert.Empty(Directory.GetFiles(scope.Data.AppRootPath, "*.png", SearchOption.AllDirectories));
    }

    private static string CreateImport(MainViewModelTestScope scope, params string[] names)
    {
        var image = scope.Data.CreateFile("source.png");
        var categories = names.Select(name =>
        {
            var category = new ReplyCategory { Name = name };
            var phrase = new ReplyPhrase { Title = "导入标题", Content = "导入正文" };
            phrase.SetImageRelativePaths(["source.png"]);
            category.Phrases.Add(phrase);
            return category;
        });
        var path = Path.Combine(scope.Data.RootPath, "import.csv");
        new PhraseExcelService().Export(path, categories, _ => [image]);
        return path;
    }
}
