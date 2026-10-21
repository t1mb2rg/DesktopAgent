using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DesktopAgent.WinUI;

/// <summary>CommonMark 到原生文本控件；HTML 与图片保持文本，不运行脚本或加载远程资源。</summary>
internal static class ChatMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    internal static FrameworkElement Render(string text)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        try { AddBlocks(panel, Markdown.Parse(text, Pipeline)); }
        catch (Exception) { return Plain(text); }
        return panel;
    }

    private static TextBlock Plain(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15,
    };

    private static void AddBlocks(StackPanel panel, ContainerBlock blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Table table:
                    var grid = new Grid { RowSpacing = 1, ColumnSpacing = 1 };
                    var rowIndex = 0;
                    foreach (var row in table.OfType<TableRow>())
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var columnIndex = 0;
                        foreach (var cell in row.OfType<TableCell>())
                        {
                            if (grid.ColumnDefinitions.Count <= columnIndex)
                                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            var cellPanel = new StackPanel { Spacing = 5 };
                            AddBlocks(cellPanel, cell);
                            var border = new Border { Child = cellPanel, Padding = new Thickness(8),
                                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(row.IsHeader ? (byte)35 : (byte)15, 128, 128, 128)) };
                            Grid.SetRow(border, rowIndex); Grid.SetColumn(border, columnIndex++);
                            grid.Children.Add(border);
                        }
                        rowIndex++;
                    }
                    panel.Children.Add(grid);
                    break;
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var itemGrid = new Grid { ColumnSpacing = 8 };
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        itemGrid.Children.Add(Plain(list.IsOrdered ? $"{number++}." : "•"));
                        var itemPanel = new StackPanel { Spacing = 6 };
                        AddBlocks(itemPanel, item);
                        Grid.SetColumn(itemPanel, 1);
                        itemGrid.Children.Add(itemPanel);
                        panel.Children.Add(itemGrid);
                    }
                    break;
                case QuoteBlock quote:
                    var quotePanel = new StackPanel { Spacing = 8 };
                    AddBlocks(quotePanel, quote);
                    panel.Children.Add(new Border { Child = quotePanel, BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = new SolidColorBrush(Colors.Gray), Padding = new Thickness(12, 3, 0, 3) });
                    break;
                case CodeBlock code:
                    var codeText = Plain(code.Lines.ToString());
                    codeText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    codeText.FontSize = 13;
                    panel.Children.Add(new Border { Child = codeText, Padding = new Thickness(12), CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)) });
                    break;
                case ThematicBreakBlock:
                    panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.Gray), Opacity = 0.4 });
                    break;
                case LeafBlock leaf:
                    var text = new RichTextBlock { IsTextSelectionEnabled = true, FontSize = 15, TextWrapping = TextWrapping.Wrap };
                    if (leaf is HeadingBlock heading)
                    {
                        text.FontSize = heading.Level <= 2 ? 21 : 17;
                        text.FontWeight = FontWeights.SemiBold;
                    }
                    var paragraph = new Paragraph();
                    if (leaf.Inline is { } inline) AddInlines(paragraph.Inlines, inline);
                    else paragraph.Inlines.Add(new Run { Text = leaf.Lines.ToString() });
                    text.Blocks.Add(paragraph);
                    panel.Children.Add(text);
                    break;
                case ContainerBlock container:
                    AddBlocks(panel, container);
                    break;
            }
        }
    }

    private static void AddInlines(InlineCollection target, ContainerInline source)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run { Text = literal.Content.ToString() }); break;
                case CodeInline code:
                    target.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Mono, Consolas") });
                    break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis); target.Add(span); break;
                case LinkInline link:
                    if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        AddInlines(hyperlink.Inlines, link); target.Add(hyperlink);
                    }
                    else AddInlines(target, link);
                    break;
                case AutolinkInline auto:
                    if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = url };
                        hyperlink.Inlines.Add(new Run { Text = auto.Url }); target.Add(hyperlink);
                    }
                    else target.Add(new Run { Text = auto.Url });
                    break;
                case HtmlInline html: target.Add(new Run { Text = html.Tag }); break;
                case ContainerInline container: AddInlines(target, container); break;
            }
        }
    }
}
