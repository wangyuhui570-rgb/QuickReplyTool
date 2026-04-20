using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class OllamaModelCatalogServiceTests
{
    [Fact]
    public void ParseModelList_ExtractsModelNamesFromTypicalOutput()
    {
        const string output = """
            NAME              ID              SIZE      MODIFIED
            qwen2.5:3b        1234567890ab    2.0 GB    2 hours ago
            llama3.2:latest   abcdef123456    4.1 GB    1 day ago
            """;

        var models = OllamaModelCatalogService.ParseModelList(output);

        Assert.Equal(["llama3.2:latest", "qwen2.5:3b"], models);
    }

    [Fact]
    public void ParseModelList_ReturnsEmptyList_WhenNoModelsFound()
    {
        var models = OllamaModelCatalogService.ParseModelList("NAME ID SIZE MODIFIED");

        Assert.Empty(models);
    }
}
