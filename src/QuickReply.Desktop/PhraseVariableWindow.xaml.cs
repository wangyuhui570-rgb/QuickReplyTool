using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyboardFocusChangedEventArgs = System.Windows.Input.KeyboardFocusChangedEventArgs;
using QuickReply.Core.Services;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop;

/// <summary>
/// Fill-in dialog for a phrase's {变量}: one row per variable, prefilled with its default.
/// Tab moves on, Enter sends, Esc cancels. Values are never remembered between sends, so one
/// customer's order number cannot leak into the next reply.
/// </summary>
public partial class PhraseVariableWindow : Window
{
    private readonly string _content;
    private readonly IReadOnlyList<PhraseVariable> _variables;
    private readonly List<VariableField> _fields;

    public PhraseVariableWindow(string phraseTitle, string content, IReadOnlyList<PhraseVariable> variables, string confirmText)
    {
        InitializeComponent();
        WindowTransitionService.Attach(this);
        _content = content;
        _variables = variables;
        _fields = variables.Select(variable => new VariableField(variable)).ToList();
        foreach (var field in _fields)
        {
            field.PropertyChanged += (_, _) => UpdatePreview();
        }

        FieldsItemsControl.ItemsSource = _fields;
        HeaderTextBlock.Text = string.IsNullOrWhiteSpace(phraseTitle) ? "填写变量" : $"填写变量：{phraseTitle}";
        ConfirmButton.Content = confirmText;
        UpdatePreview();

        Loaded += (_, _) =>
        {
            Activate();
            // Start on the first field without a default: that is the one that must be typed.
            var firstIndex = Math.Max(0, _fields.FindIndex(field => !field.Variable.HasDefault));
            FocusField(firstIndex);
        };
    }

    public IReadOnlyDictionary<string, string>? Values { get; private set; }

    private void FocusField(int index)
    {
        if (FieldsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter presenter)
        {
            presenter.ApplyTemplate();
            var textBox = FindTextBox(presenter);
            textBox?.Focus();
        }
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox textBox)
            {
                return textBox;
            }

            if (FindTextBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private Dictionary<string, string> CollectValues()
    {
        return _fields.ToDictionary(field => field.Variable.Name, field => field.Value);
    }

    private void UpdatePreview()
    {
        PreviewTextBlock.Text = "预览：" + PhraseTemplate.Fill(_content, CollectValues());
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        var values = CollectValues();
        var missing = PhraseTemplate.FindMissing(_variables, values);
        if (missing.Count > 0)
        {
            ErrorTextBlock.Text = $"请先填写：{string.Join("、", missing.Select(variable => variable.Name))}";
            ErrorTextBlock.Visibility = Visibility.Visible;
            FocusField(_fields.FindIndex(field => field.Variable == missing[0]));
            return;
        }

        Values = values;
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowTransitionService.SuppressNextCloseAnimation(this);
        DialogResult = false;
    }

    private void FieldTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private sealed class VariableField : ObservableObject
    {
        private string _value;

        public VariableField(PhraseVariable variable)
        {
            Variable = variable;
            _value = variable.DefaultValue;
        }

        public PhraseVariable Variable { get; }

        public string Name => Variable.Name;

        public string Label => Variable.HasDefault ? Variable.Name : Variable.Name + " *";

        public string Hint => Variable.HasDefault ? $"默认值：{Variable.DefaultValue}" : "必填";

        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
    }
}
