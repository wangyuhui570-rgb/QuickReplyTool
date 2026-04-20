using System.Collections.ObjectModel;

namespace QuickReply.Desktop.Models;

public sealed class AppData
{
    public ObservableCollection<ReplyCategory> Categories { get; set; } = [];

    public ObservableCollection<QuickTag> QuickTags { get; set; } = [];

    public ObservableCollection<LibraryFileItem> FileItems { get; set; } = [];

    public AppSettings Settings { get; set; } = new();
}
