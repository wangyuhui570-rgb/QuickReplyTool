using QuickReply.Core.Services;

namespace QuickReply.Desktop.Tests;

public sealed class PhraseTemplateTests
{
    [Fact]
    public void Parse_ReadsNamesAndDefaultsOncePerName()
    {
        var variables = PhraseTemplate.Parse("您的单号是 {单号}，预计 {天数=3} 天到货。单号 {单号} 请保存。");

        Assert.Equal(
            [new PhraseVariable("单号", "", false), new PhraseVariable("天数", "3", true)],
            variables);
    }

    [Theory]
    [InlineData("""{"code":1}""")]
    [InlineData("没有变量的短语")]
    [InlineData("{}")]
    public void Parse_IgnoresTextThatIsNotAVariable(string content)
    {
        Assert.Empty(PhraseTemplate.Parse(content));
    }

    [Fact]
    public void Fill_UsesEnteredValuesAndFallsBackToDefaults()
    {
        var content = "单号 {单号}，{天数=3} 天，再说一次 {单号}";

        var filled = PhraseTemplate.Fill(content, new Dictionary<string, string> { ["单号"] = "SF123", ["天数"] = " " });

        Assert.Equal("单号 SF123，3 天，再说一次 SF123", filled);
    }

    [Fact]
    public void FindMissing_ReportsOnlyVariablesWithoutDefault()
    {
        var variables = PhraseTemplate.Parse("{单号} {天数=3}");

        var missing = PhraseTemplate.FindMissing(variables, new Dictionary<string, string> { ["天数"] = "" });

        Assert.Equal(["单号"], missing.Select(variable => variable.Name));
    }
}
