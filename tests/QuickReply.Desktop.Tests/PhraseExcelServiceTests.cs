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
            Assert.ThrowsAny<IOException>(() => service.Export(path, [CreateCategory("after", 2)], _ => [image, nextImage]));
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

        Assert.ThrowsAny<IOException>(() => service.Export(path, [CreateCategory("图文", 2)], _ => filtered ? [image] : [image, missing]));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetDirectories(scope.RootPath, "export_assets_*"));
        Assert.Empty(Directory.GetFiles(scope.RootPath, "*.tmp"));
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
