using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.ViewModels;

public sealed class SearchOverlayResultItem
{
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    public required int Number { get; init; }

    public required SearchOverlayMode Mode { get; init; }

    public ReplyPhrase? Phrase { get; init; }

    public LibraryFileItem? FileItem { get; init; }

    public string? PreviewTitle { get; init; }

    public string? PreviewLabelText { get; init; }

    public string? PreviewContent { get; init; }

    public bool? PreviewHasLabel { get; init; }

    public bool IsRecommended { get; init; }

    public string RecommendationBadgeText { get; init; } = string.Empty;

    public string RecommendationDetailText { get; init; } = string.Empty;

    public string NumberLabel => Number.ToString(CultureInfo.InvariantCulture);

    public string Title => !string.IsNullOrWhiteSpace(PreviewTitle)
        ? PreviewTitle
        : Mode == SearchOverlayMode.File
            ? FileItem?.DisplayName ?? string.Empty
            : Phrase?.DisplayTitle ?? string.Empty;

    public bool HasLabel => PreviewHasLabel
        ?? (Mode == SearchOverlayMode.File
            ? !string.IsNullOrWhiteSpace(FileItem?.FileExtension)
            : Phrase?.HasDisplayTitle ?? false);

    public string LabelText => !string.IsNullOrWhiteSpace(PreviewLabelText)
        ? PreviewLabelText
        : Mode == SearchOverlayMode.File
            ? BuildFileLabel()
            : Phrase?.DisplayTitle ?? string.Empty;

    public string Content => !string.IsNullOrWhiteSpace(PreviewContent)
        ? PreviewContent
        : Mode == SearchOverlayMode.File
            ? BuildFileContent()
            : Phrase?.Content ?? string.Empty;

    public string SingleLineContent => BuildSingleLineContent(Content);

    private string BuildFileLabel()
    {
        if (FileItem is null)
        {
            return string.Empty;
        }

        var extension = FileItem.FileExtension?.Trim().TrimStart('.');
        return string.IsNullOrWhiteSpace(extension)
            ? "FILE"
            : extension.ToUpperInvariant();
    }

    private string BuildFileContent()
    {
        if (FileItem is null)
        {
            return string.Empty;
        }

        if (!File.Exists(FileItem.FullPath))
        {
            return "文件不存在，请重新同步文件库";
        }

        var fileInfo = new FileInfo(FileItem.FullPath);
        return $"{FileItem.FolderPath} | {FormatFileSize(fileInfo.Length)} | {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
    }

    private static string FormatFileSize(long byteCount)
    {
        const double kilobyte = 1024d;
        const double megabyte = kilobyte * 1024d;
        const double gigabyte = megabyte * 1024d;

        if (byteCount >= gigabyte)
        {
            return $"{byteCount / gigabyte:0.0}G";
        }

        if (byteCount >= megabyte)
        {
            return $"{byteCount / megabyte:0.0}M";
        }

        if (byteCount >= kilobyte)
        {
            return $"{byteCount / kilobyte:0.0}K";
        }

        return $"{byteCount}B";
    }

    private static string BuildSingleLineContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content
            .Replace("\r\n", " / ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');

        return MultiWhitespaceRegex.Replace(normalized, " ").Trim();
    }
}
