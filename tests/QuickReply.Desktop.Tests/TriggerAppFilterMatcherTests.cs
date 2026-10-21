using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class TriggerAppFilterMatcherTests
{
    [Theory]
    [InlineData("WeChat.exe", "WeChat", true)]
    [InlineData("wechat", "WeChat", true)]
    [InlineData("WECHAT.EXE", "wechat", true)]
    [InlineData("WXWork.exe", "WeChat", false)]
    [InlineData(" WeChat.exe ", "WeChat.exe", true)]
    public void Matches_ProcessName_IsCaseInsensitiveAndExeOptional(string ruleProcess, string actualProcess, bool expected)
    {
        var rule = new TriggerAppRule { ProcessName = ruleProcess };

        Assert.Equal(expected, TriggerAppFilterMatcher.Matches(rule, actualProcess, "任意标题", "AnyClass"));
    }

    [Theory]
    [InlineData("拼多多", "拼多多商家工作台 - 客服", true)]
    [InlineData("客服", "Chrome - 多多客服", true)]
    [InlineData("抖店", "微信", false)]
    public void Matches_TitleKeyword_IsContainsMatch(string keyword, string title, bool expected)
    {
        var rule = new TriggerAppRule { TitleKeyword = keyword };

        Assert.Equal(expected, TriggerAppFilterMatcher.Matches(rule, "chrome", title, "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void Matches_RequiresAllNonEmptyFields()
    {
        var rule = new TriggerAppRule { ProcessName = "chrome.exe", TitleKeyword = "飞鸽" };

        Assert.True(TriggerAppFilterMatcher.Matches(rule, "chrome", "抖店飞鸽客服", string.Empty));
        Assert.False(TriggerAppFilterMatcher.Matches(rule, "chrome", "百度一下", string.Empty));
        Assert.False(TriggerAppFilterMatcher.Matches(rule, "msedge", "抖店飞鸽客服", string.Empty));
    }

    [Fact]
    public void Matches_EmptyRuleNeverMatches()
    {
        Assert.False(TriggerAppFilterMatcher.Matches(new TriggerAppRule(), "WeChat", "微信", "Class"));
    }

    [Fact]
    public void IsAllowed_DisabledFilterAllowsEverything()
    {
        Assert.True(TriggerAppFilterMatcher.IsAllowed(false, [], "notepad", "无标题", string.Empty));
    }

    [Fact]
    public void IsAllowed_EnabledFilterWithNoRulesBlocks()
    {
        Assert.False(TriggerAppFilterMatcher.IsAllowed(true, [], "WeChat", "微信", string.Empty));
    }

    [Fact]
    public void IsAllowed_EnabledFilterMatchesAnyRule()
    {
        var rules = new[]
        {
            new TriggerAppRule { ProcessName = "WXWork.exe" },
            new TriggerAppRule { ProcessName = "WeChat.exe" }
        };

        Assert.True(TriggerAppFilterMatcher.IsAllowed(true, rules, "WeChat", "微信", string.Empty));
        Assert.False(TriggerAppFilterMatcher.IsAllowed(true, rules, "notepad", "记事本", string.Empty));
    }

    [Fact]
    public void Settings_DefaultFilterIsOff_AndCloneCopiesRules()
    {
        var settings = new AppSettings();
        Assert.False(settings.TriggerAppFilterEnabled);
        Assert.Empty(settings.TriggerAppRules);

        settings.TriggerAppFilterEnabled = true;
        settings.TriggerAppRules = [new TriggerAppRule { ProcessName = "WeChat.exe", TitleKeyword = "微信" }];
        var clone = AppSettingsSnapshotService.Clone(settings);

        Assert.True(clone.TriggerAppFilterEnabled);
        var rule = Assert.Single(clone.TriggerAppRules);
        Assert.Equal("WeChat.exe", rule.ProcessName);
        Assert.NotSame(settings.TriggerAppRules[0], rule);
    }
}
