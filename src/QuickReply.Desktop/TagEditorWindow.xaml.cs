using System.Windows;
using System.Windows.Input;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop;

public partial class TagEditorWindow : Window
{
    public TagEditorWindow(string tagsText)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        DataContext = this;
        TagsText = tagsText;

        Loaded += (_, _) => TagsTextBox.Focus();
    }

    public string TagsText { get; set; }

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
