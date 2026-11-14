using System.Windows;
using System.Windows.Controls;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop;

public partial class ImportCategorySelectionWindow : Window
{
    public ImportCategorySelectionWindow(
        string name,
        IReadOnlyList<ReplyCategory> candidates,
        IReadOnlyList<ReplyCategory> allCategories)
    {
        InitializeComponent();
        Heading.Text = $"存在多个“{name}”分类，请选择本次话术的导入位置。";
        var orderedCategories = allCategories.ToList();
        Choices.ItemsSource = candidates.Select(category =>
        {
            var position = orderedCategories.FindIndex(item => item.Id == category.Id) + 1;
            var example = category.Phrases.FirstOrDefault();
            var description = $"第 {position} 个分类 · {category.Phrases.Count} 条话术";
            if (example is not null)
            {
                var text = string.IsNullOrWhiteSpace(example.Title) ? example.Content : example.Title;
                text = text.ReplaceLineEndings(" ");
                description += $" · 示例 {text[..Math.Min(text.Length, 24)]}";
            }
            return new CategoryChoice(category.Id, description);
        }).ToArray();
    }

    public Guid? SelectedCategoryId => (Choices.SelectedItem as CategoryChoice)?.Id;

    private void Choices_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = SelectedCategoryId is not null;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCategoryId is not null)
        {
            DialogResult = true;
        }
    }

    private sealed record CategoryChoice(Guid Id, string Description);
}
