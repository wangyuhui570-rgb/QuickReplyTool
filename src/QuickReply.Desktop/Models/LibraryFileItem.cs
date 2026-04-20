using System.IO;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class LibraryFileItem : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _displayName = string.Empty;
    private string _fullPath = string.Empty;
    private DateTimeOffset _createdAt = DateTimeOffset.Now;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                Touch();
            }
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (SetProperty(ref _fullPath, value))
            {
                OnPropertyChanged(nameof(FolderPath));
                OnPropertyChanged(nameof(FileExtension));
                OnPropertyChanged(nameof(FileName));
                Touch();
            }
        }
    }

    public string FileName => Path.GetFileName(FullPath);

    public string FolderPath => Path.GetDirectoryName(FullPath) ?? string.Empty;

    public string FileExtension => Path.GetExtension(FullPath);

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

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.Now;
    }
}
