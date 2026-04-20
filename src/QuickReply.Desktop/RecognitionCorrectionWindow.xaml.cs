using System.Windows;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop;

public partial class RecognitionCorrectionWindow : Window
{
    public RecognitionCorrectionWindow(ConversationRecognitionCorrection initialCorrection)
    {
        InitializeComponent();

        CustomerTextBox.Text = initialCorrection.CustomerText;
        ContextTextBox.Text = initialCorrection.ContextText;
        ProductIdTextBox.Text = initialCorrection.ProductId;
        ProductNameTextBox.Text = initialCorrection.ProductName;
        MessagesTextBox.Text = string.Join(
            Environment.NewLine,
            initialCorrection.Messages
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        Loaded += (_, _) =>
        {
            CustomerTextBox.Focus();
            CustomerTextBox.SelectAll();
        };
    }

    public ConversationRecognitionCorrection? Correction { get; private set; }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var customerText = NormalizeMultilineText(CustomerTextBox.Text);
        var contextText = NormalizeMultilineText(ContextTextBox.Text);
        var productId = NormalizeSingleLineText(ProductIdTextBox.Text);
        var productName = NormalizeSingleLineText(ProductNameTextBox.Text);
        var messages = ParseMessages(MessagesTextBox.Text, customerText, contextText);

        if (string.IsNullOrWhiteSpace(customerText) &&
            string.IsNullOrWhiteSpace(contextText) &&
            string.IsNullOrWhiteSpace(productId) &&
            string.IsNullOrWhiteSpace(productName) &&
            messages.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "至少保留一项修正内容。",
                "修正识别",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Correction = new ConversationRecognitionCorrection
        {
            CustomerText = customerText,
            ContextText = contextText,
            ProductId = productId,
            ProductName = productName,
            Messages = messages
        };

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static IReadOnlyList<ConversationMessageTurn> ParseMessages(
        string rawText,
        string customerText,
        string contextText)
    {
        var customerLines = SplitLines(customerText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextLines = SplitLines(contextText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return SplitLines(rawText)
            .Select((text, index) => new ConversationMessageTurn
            {
                Index = index,
                Text = text,
                Score = 0d,
                Reason = "manual_correction",
                SelectedForCustomerText = customerLines.Contains(text),
                IncludedInContext = contextLines.Contains(text)
            })
            .ToArray();
    }

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? []
            : text
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
    }

    private static string NormalizeMultilineText(string? value)
    {
        return string.Join(Environment.NewLine, SplitLines(value));
    }

    private static string NormalizeSingleLineText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
    }
}
