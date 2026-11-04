using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using QuickReply.Core.Services;

namespace QuickReply.Desktop.Services;

/// <summary>
/// Attached properties that render a TextBlock's text with the search keywords highlighted.
/// Use <c>SearchHighlight.Text</c> instead of <c>Text</c>, and bind <c>SearchHighlight.Query</c>
/// to the search box text.
/// </summary>
public static class SearchHighlight
{
    private static readonly Brush HighlightBackground = CreateFrozenBrush(Color.FromArgb(0x66, 0xFF, 0xC1, 0x07));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(SearchHighlight),
        new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query",
        typeof(string),
        typeof(SearchHighlight),
        new PropertyMetadata(string.Empty, OnChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetQuery(DependencyObject element) => (string)element.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject element, string value) => element.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock)
        {
            return;
        }

        var text = GetText(textBlock) ?? string.Empty;
        textBlock.Inlines.Clear();
        var position = 0;
        foreach (var (start, length) in SearchKeywordMatcher.FindLiteralRanges(text, GetQuery(textBlock)))
        {
            if (start > position)
            {
                textBlock.Inlines.Add(new Run(text[position..start]));
            }

            textBlock.Inlines.Add(new Run(text.Substring(start, length))
            {
                Background = HighlightBackground,
                FontWeight = FontWeights.Bold
            });
            position = start + length;
        }

        if (position < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[position..]));
        }
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
