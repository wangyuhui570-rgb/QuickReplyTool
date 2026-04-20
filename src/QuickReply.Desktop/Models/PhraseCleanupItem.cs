using System.ComponentModel;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class PhraseCleanupItem : ObservableObject
{
    private bool _isSelected;

    public required ReplyPhrase Phrase { get; init; }

    public required string UsageSummary { get; init; }

    public required string ImageSummary { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string CategoryName => Phrase.CategoryName;

    public string Title => Phrase.HasDisplayTitle ? Phrase.DisplayTitle : "无备注标签";

    public string ContentPreview => Phrase.SingleLineContent;
}
