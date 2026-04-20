using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class QuickTag : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private Guid _phraseId;
    private string _phraseTitle = string.Empty;
    private string _phraseContent = string.Empty;
    private string _phraseCategoryName = string.Empty;
    private int _sortOrder;
    private DateTimeOffset _createdAt = DateTimeOffset.Now;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public Guid PhraseId
    {
        get => _phraseId;
        set => SetProperty(ref _phraseId, value);
    }

    public string PhraseTitle
    {
        get => _phraseTitle;
        set => SetProperty(ref _phraseTitle, value);
    }

    public string PhraseContent
    {
        get => _phraseContent;
        set => SetProperty(ref _phraseContent, value);
    }

    public string PhraseCategoryName
    {
        get => _phraseCategoryName;
        set => SetProperty(ref _phraseCategoryName, value);
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
}
