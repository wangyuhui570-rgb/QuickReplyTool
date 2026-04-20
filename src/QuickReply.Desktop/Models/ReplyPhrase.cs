using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class ReplyPhrase : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private Guid _categoryId;
    private string _categoryName = string.Empty;
    private string _title = string.Empty;
    private string _tags = string.Empty;
    private string _content = string.Empty;
    private string _imageRelativePath = string.Empty;
    private string _inlineTagDraft = string.Empty;
    private string _inlineInsertDraft = string.Empty;
    private string _inlineContentDraft = string.Empty;
    private bool _isInlineTagEditing;
    private bool _isInlineInsertEditing;
    private bool _isInlineContentEditing;
    private bool _isMultiSelected;
    private int _sortOrder;
    private DateTimeOffset _createdAt = DateTimeOffset.Now;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public Guid CategoryId
    {
        get => _categoryId;
        set => SetProperty(ref _categoryId, value);
    }

    public string CategoryName
    {
        get => _categoryName;
        set => SetProperty(ref _categoryName, value);
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                Touch();
            }
        }
    }

    public string Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value))
            {
                if (!_isInlineTagEditing)
                {
                    _inlineTagDraft = _tags;
                    OnPropertyChanged(nameof(InlineTagDraft));
                }

                Touch();
            }
        }
    }

    public string InlineTagDraft
    {
        get => _inlineTagDraft;
        set => SetProperty(ref _inlineTagDraft, value);
    }

    public bool IsInlineTagEditing
    {
        get => _isInlineTagEditing;
        set => SetProperty(ref _isInlineTagEditing, value);
    }

    public string InlineInsertDraft
    {
        get => _inlineInsertDraft;
        set => SetProperty(ref _inlineInsertDraft, value);
    }

    public bool IsInlineInsertEditing
    {
        get => _isInlineInsertEditing;
        set => SetProperty(ref _isInlineInsertEditing, value);
    }

    public string InlineContentDraft
    {
        get => _inlineContentDraft;
        set => SetProperty(ref _inlineContentDraft, value);
    }

    public bool IsInlineContentEditing
    {
        get => _isInlineContentEditing;
        set => SetProperty(ref _isInlineContentEditing, value);
    }

    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                if (!_isInlineContentEditing)
                {
                    _inlineContentDraft = _content;
                    OnPropertyChanged(nameof(InlineContentDraft));
                }

                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SingleLineContent));
                Touch();
            }
        }
    }

    // Stored in DB as a single text column, serialized by '|'.
    public string ImageRelativePath
    {
        get => _imageRelativePath;
        set
        {
            var normalized = NormalizeImageRelativePaths(value);
            if (SetProperty(ref _imageRelativePath, normalized))
            {
                OnPropertyChanged(nameof(ImageRelativePaths));
                OnPropertyChanged(nameof(PrimaryImageRelativePath));
                OnPropertyChanged(nameof(ImageCount));
                OnPropertyChanged(nameof(HasImage));
                Touch();
            }
        }
    }

    public IReadOnlyList<string> ImageRelativePaths => DeserializeImageRelativePaths(_imageRelativePath);

    public string? PrimaryImageRelativePath => ImageRelativePaths.FirstOrDefault();

    public int ImageCount => ImageRelativePaths.Count;

    public bool HasImage => ImageCount > 0;

    public string DisplayText
    {
        get
        {
            return string.IsNullOrWhiteSpace(DisplayTitle)
                ? SingleLineContent
                : $"{DisplayTitle}  {SingleLineContent}";
        }
    }

    public string SingleLineContent => NormalizeSingleLineContent(Content);

    public string DisplayTitle => IsPlaceholderTitle(Title) ? string.Empty : Title.Trim();

    public bool HasDisplayTitle => !string.IsNullOrWhiteSpace(DisplayTitle);

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
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

    public void SetImageRelativePaths(IEnumerable<string>? relativePaths)
    {
        ImageRelativePath = SerializeImageRelativePaths(relativePaths);
    }

    public static string SerializeImageRelativePaths(IEnumerable<string>? relativePaths)
    {
        if (relativePaths is null)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            relativePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3));
    }

    public static IReadOnlyList<string> DeserializeImageRelativePaths(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return [];
        }

        return serialized
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.Now;
    }

    private static bool IsPlaceholderTitle(string title)
    {
        var normalized = title.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "未命名短语", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "未命名", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSingleLineContent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeImageRelativePaths(string? serialized)
    {
        return SerializeImageRelativePaths(DeserializeImageRelativePaths(serialized));
    }
}
