using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class SearchOverlayPanelStatusBuilder
{
    public static string BuildResultsSectionTitle(SearchOverlayMode mode)
    {
        return mode switch
        {
            SearchOverlayMode.File => "文件结果",
            SearchOverlayMode.Mixed => "搜索结果",
            _ => "短语结果"
        };
    }

    public static string BuildResultsSectionSummary(
        SearchOverlayMode mode,
        string searchText,
        int resultCount)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "输入文件名后可直接发送文件。",
                SearchOverlayMode.Mixed => "可同时搜索短语和文件。",
                _ => "输入关键词后可发送短语。"
            };
        }

        if (resultCount > 0)
        {
            return mode switch
            {
                SearchOverlayMode.File => $"已找到 {resultCount} 条文件结果。",
                SearchOverlayMode.Mixed => $"已找到 {resultCount} 条短语或文件结果。",
                _ => $"已找到 {resultCount} 条短语结果。"
            };
        }

        return mode switch
        {
            SearchOverlayMode.File => "当前没有匹配的文件结果。",
            SearchOverlayMode.Mixed => "当前没有匹配的短语或文件结果。",
            _ => "当前没有匹配的短语结果。"
        };
    }

    public static string BuildEmptyStateTitle(SearchOverlayMode mode, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "输入文件名开始搜索",
                SearchOverlayMode.Mixed => "输入关键词开始混合搜索",
                _ => "输入关键词开始搜索短语"
            };
        }

        return mode switch
        {
            SearchOverlayMode.File => "没有匹配的文件结果",
            SearchOverlayMode.Mixed => "没有匹配的短语或文件结果",
            _ => "没有匹配的短语结果"
        };
    }

    public static string BuildEmptyStateHintText(
        SearchOverlayMode mode,
        string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return mode switch
            {
                SearchOverlayMode.File => "你可以继续输入文件名，也可以切回短语或混合搜索。",
                SearchOverlayMode.Mixed => "你可以输入问题关键词，或切到纯短语/文件模式。",
                _ => "你可以输入问题关键词，或切到文件/混合搜索。"
            };
        }

        return mode switch
        {
            SearchOverlayMode.File => "试试换一个文件名关键词，或切到短语/混合搜索继续处理当前聊天。",
            SearchOverlayMode.Mixed => "试试换一个更短或更明确的关键词，或者切到纯短语/文件模式继续搜索。",
            _ => "试试换一个更短或更明确的关键词，或切到混合/文件搜索继续查找。"
        };
    }
}
