using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class PhraseExcelServiceTests
{
    [Theory]
    [InlineData("xls")]
    [InlineData("csv")]
    public void Export_PreservesExistingFilesAndPreviousExportImages(string extension)
    {
        using var scope = new TestAppDataScope();
        var service = new PhraseExcelService();
        var image = scope.CreateFile("source.png", "original image");
        var unrelated = scope.CreateFile("export_assets/phrase-0001-1.png", "user file");
        var path = Path.Combine(scope.RootPath, $"export.{extension}");
        var category = CreateCategory("hello", imageCount: 1);

        service.Export(path, [category], _ => [image]);
        var firstImage = Assert.Single(Assert.Single(service.Import(path)).ImagePaths);
        File.WriteAllText(image, "second image");
        service.Export(path, [category], _ => [image]);
        var secondImage = Assert.Single(Assert.Single(service.Import(path)).ImagePaths);

        Assert.NotEqual(firstImage, secondImage);
        Assert.Equal("original image", File.ReadAllText(firstImage));
        Assert.Equal("second image", File.ReadAllText(secondImage));
        Assert.Equal("user file", File.ReadAllText(unrelated));
        Assert.Empty(Directory.GetFiles(scope.RootPath, "*.tmp"));
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("csv")]
    public void Export_WithoutImagesDoesNotCreateOrDeleteAssetDirectories(string extension)
    {
        using var scope = new TestAppDataScope();
        var marker = scope.CreateFile("export_assets/keep.txt");
        var directories = Directory.GetDirectories(scope.RootPath).Order().ToArray();

        new PhraseExcelService().Export(Path.Combine(scope.RootPath, $"export.{extension}"), [CreateCategory("text")]);

        Assert.True(File.Exists(marker));
        Assert.Equal(directories, Directory.GetDirectories(scope.RootPath).Order().ToArray());
    }

    [Theory]
    [InlineData("xls", false)]
    [InlineData("csv", false)]
    [InlineData("xls", true)]
    [InlineData("csv", true)]
    public void Export_FailureKeepsPreviousWorkbookAndImages(string extension, bool lockDestination)
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("image.png");
        var path = Path.Combine(scope.RootPath, $"export.{extension}");
        var service = new PhraseExcelService();
        service.Export(path, [CreateCategory("before", 1)], _ => [image]);
        var nextImage = scope.CreateFile("next.png");
        var oldBytes = File.ReadAllBytes(path);
        var oldFiles = Directory.GetFiles(scope.RootPath, "*", SearchOption.AllDirectories).Order().ToArray();
        using (var lockedFile = new FileStream(
            lockDestination ? path : nextImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Action export = () => service.Export(path, [CreateCategory("after", 2)], _ => [image, nextImage]);
            if (lockDestination)
            {
                Assert.Throws<UnauthorizedAccessException>(export);
            }
            else
            {
                Assert.Throws<IOException>(export);
            }
        }

        Assert.Equal(oldBytes, File.ReadAllBytes(path));
        Assert.Equal(oldFiles, Directory.GetFiles(scope.RootPath, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Theory]
    [InlineData("xls", false)]
    [InlineData("csv", false)]
    [InlineData("xls", true)]
    [InlineData("csv", true)]
    public void Export_MissingOrFilteredImageFailsWithoutPublishing(string extension, bool filtered)
    {
        using var scope = new TestAppDataScope();
        var image = scope.CreateFile("image.png");
        var path = Path.Combine(scope.RootPath, $"export.{extension}");
        var missing = Path.Combine(scope.RootPath, "missing.png");
        var service = new PhraseExcelService();

        Action export = () => service.Export(path, [CreateCategory("图文", 2)], _ => filtered ? [image] : [image, missing]);
        if (filtered)
        {
            Assert.Throws<InvalidDataException>(export);
        }
        else
        {
            Assert.Throws<FileNotFoundException>(export);
        }

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetDirectories(scope.RootPath, "export_assets_*"));
        Assert.Empty(Directory.GetFiles(scope.RootPath, "*.tmp"));
    }

    [Theory]
    [InlineData("xls", "退换货")]
    [InlineData("csv", "退换货")]
    [InlineData("xls", "")]
    [InlineData("csv", "")]
    public async Task ExportImport_PreservesTitleTagsAndMultipleImages(string extension, string tags)
    {
        await using var scope = new MainViewModelTestScope();
        await scope.Store.LoadAsync();
        var category = CreateCategory("请查看图片", imageCount: 3);
        category.Phrases[0].Tags = tags;
        var paths = Enumerable.Range(1, 3).Select(i => scope.Data.CreateFile($"image{i}.png", $"image {i}")).ToArray();
        var path = Path.Combine(scope.Data.RootPath, $"roundtrip.{extension}");
        new PhraseExcelService().Export(path, [category], _ => paths);
        var row = Assert.Single(new PhraseExcelService().Import(path));
        Assert.Equal("操作说明", row.Remark);
        Assert.Equal(tags, row.Tags);
        Assert.Equal(3, row.ImagePaths.Count);
        Assert.Equal(["image 1", "image 2", "image 3"], row.ImagePaths.Select(File.ReadAllText));

        var result = await scope.Main.ImportPhrasesFromXlsAsync(path);
        Assert.True(result.Success, result.Message);
        var imported = Assert.Single(scope.Main.Categories.Single(c => c.Name == "售后").Phrases);
        Assert.Equal("操作说明", imported.Title);
        Assert.Equal(tags, imported.Tags);
        Assert.Equal("请查看图片", imported.Content);
        Assert.Equal(3, imported.ImageCount);
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("csv")]
    public async Task Import_LegacyFileKeepsContentAndImageColumnsAndFallsBackToRemark(string extension)
    {
        await using var scope = new MainViewModelTestScope();
        await scope.Store.LoadAsync();
        var image = scope.Data.CreateFile("old.png");
        var path = Path.Combine(scope.Data.RootPath, $"legacy.{extension}");
        if (extension == "xls")
        {
            using var workbook = new NPOI.HSSF.UserModel.HSSFWorkbook();
            var sheet = workbook.CreateSheet();
            var headers = new[] { "分类", "备注标签", "短语内容", "图片1", "图片2", "图片3" };
            var values = new[] { "旧分类", "旧标题", "旧正文", "old.png", "", "" };
            for (var i = 0; i < headers.Length; i++)
            {
                (sheet.GetRow(0) ?? sheet.CreateRow(0)).CreateCell(i).SetCellValue(headers[i]);
                (sheet.GetRow(1) ?? sheet.CreateRow(1)).CreateCell(i).SetCellValue(values[i]);
            }
            using var stream = File.Create(path);
            workbook.Write(stream);
        }
        else
        {
            File.WriteAllText(path, "快捷短语编码,快捷短语,快捷短语分类,快捷短语图片\r\n旧标题,旧正文,旧分类,old.png",
                new System.Text.UTF8Encoding(true));
        }

        var row = Assert.Single(new PhraseExcelService().Import(path));
        Assert.Null(row.Tags);
        Assert.Equal([image], row.ImagePaths);
        var result = await scope.Main.ImportPhrasesFromXlsAsync(path);
        Assert.True(result.Success, result.Message);
        var phrase = Assert.Single(scope.Main.Categories.Single(c => c.Name == "旧分类").Phrases);
        Assert.Equal("旧标题", phrase.Title);
        Assert.Equal("旧标题", phrase.Tags);
        Assert.Equal("旧正文", phrase.Content);
        Assert.Equal(1, phrase.ImageCount);
    }

    private static ReplyCategory CreateCategory(string content, int imageCount = 0)
    {
        var category = new ReplyCategory { Name = "售后" };
        var phrase = new ReplyPhrase { Title = "操作说明", Content = content };
        phrase.SetImageRelativePaths(Enumerable.Range(1, imageCount).Select(i => $"image{i}.png"));
        category.Phrases.Add(phrase);
        return category;
    }
}
