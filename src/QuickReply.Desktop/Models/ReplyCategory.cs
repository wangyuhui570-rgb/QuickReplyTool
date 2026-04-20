using System.Collections.ObjectModel;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class ReplyCategory : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _name = string.Empty;
    private int _sortOrder;
    private DateTimeOffset _createdAt = DateTimeOffset.Now;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    private bool _isInlineEditing;
    private string _inlineNameDraft = string.Empty;
    private InlineCategoryEditCaretMode _inlineEditCaretMode = InlineCategoryEditCaretMode.End;
    private double _inlineEditPointerRatio = 1d;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public int SortOrder
    {
        get => _sortOrder;
        set => SetProperty(ref _sortOrder, value);
    }

    public DateTimeOffset CreatedAt
    {
        get => _createdAt;
        set => SetProperty(ref _createdAt, value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    public bool IsInlineEditing
    {
        get => _isInlineEditing;
        set => SetProperty(ref _isInlineEditing, value);
    }

    public string InlineNameDraft
    {
        get => _inlineNameDraft;
        set => SetProperty(ref _inlineNameDraft, value);
    }

    public InlineCategoryEditCaretMode InlineEditCaretMode
    {
        get => _inlineEditCaretMode;
        set => SetProperty(ref _inlineEditCaretMode, value);
    }

    public double InlineEditPointerRatio
    {
        get => _inlineEditPointerRatio;
        set => SetProperty(ref _inlineEditPointerRatio, Math.Clamp(value, 0d, 1d));
    }

    public ObservableCollection<ReplyPhrase> Phrases { get; set; } = [];
}
