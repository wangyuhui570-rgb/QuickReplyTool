using System.Collections.ObjectModel;
using System.ComponentModel;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Models;

public sealed class PhraseCleanupGroup : ObservableObject
{
    private int _selectedCount;

    public required string Header { get; init; }

    public required string Description { get; init; }

    public ObservableCollection<PhraseCleanupItem> Items { get; } = [];

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value))
            {
                OnPropertyChanged(nameof(SelectedSummary));
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public int TotalCount => Items.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectedSummary => SelectedCount <= 0
        ? "暂未选择删除项"
        : $"已选择删除 {SelectedCount} 条";

    public void AttachSelectionTracking()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged += ItemOnPropertyChanged;
        }

        RefreshSelectionCount();
    }

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhraseCleanupItem.IsSelected))
        {
            RefreshSelectionCount();
        }
    }

    public void RefreshSelectionCount()
    {
        SelectedCount = Items.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(TotalCount));
    }
}
