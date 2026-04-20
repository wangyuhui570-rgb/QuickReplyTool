using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class CategoryEditorWindow : Window
{
    public CategoryEditorWindow(string dialogHeading, string confirmButtonText, string categoryName)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        DataContext = this;
        DialogHeading = dialogHeading;
        ConfirmButtonText = confirmButtonText;
        CategoryName = categoryName;

        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string DialogHeading { get; }

    public string ConfirmButtonText { get; }

    public string CategoryName { get; set; }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = false;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
