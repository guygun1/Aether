using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using MultiIde.Core;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Snippets;
using MultiIde.Protocol;

namespace MultiIde.Editor;

/// <summary>Kind badges in the style of IntelliJ's completion popup.</summary>
public static class KindBadge
{
    private static readonly Dictionary<int, (string Letter, string Color)> Kinds = new()
    {
        [1] = ("t", "#868A91"), [2] = ("m", "#E06C75"), [3] = ("f", "#E06C75"), [4] = ("m", "#E06C75"),
        [5] = ("f", "#D8A657"), [6] = ("v", "#7EA7D8"), [7] = ("C", "#4C9AE6"), [8] = ("I", "#5FB865"),
        [9] = ("M", "#9876AA"), [10] = ("p", "#D8A657"), [11] = ("u", "#868A91"), [12] = ("v", "#7EA7D8"),
        [13] = ("E", "#C77DBB"), [14] = ("k", "#CF8E6D"), [15] = ("T", "#2AACB8"), [16] = ("c", "#868A91"),
        [17] = ("F", "#868A91"), [18] = ("r", "#868A91"), [19] = ("D", "#868A91"), [20] = ("e", "#C77DBB"),
        [21] = ("K", "#C77DBB"), [22] = ("S", "#4C9AE6"), [23] = ("e", "#D8A657"), [24] = ("o", "#868A91"),
        [25] = ("T", "#5FB865"),
    };

    public static Control Create(int kind)
    {
        var (letter, color) = Kinds.GetValueOrDefault(kind, ("·", "#868A91"));
        var brush = new SolidColorBrush(Color.Parse(color));
        return new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(brush.Color, 0.22),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}

/// <summary>One completion entry. Its visuals and documentation are built lazily,
/// because servers can return thousands of items and only a handful are ever shown.</summary>
public sealed class CompletionItemData : ICompletionData
{
    private readonly JsonObject _item;
    private readonly LspClient? _client;
    private readonly EditorDocument _doc;
    private readonly int _requestCaret;
    private Control? _content;
    private TextBlock? _description;
    private Task<JsonObject>? _resolving;

    public CompletionItemData(JsonObject item, LspClient? client, EditorDocument doc, int requestCaret, double priority)
    {
        _item = item;
        _client = client;
        _doc = doc;
        _requestCaret = requestCaret;
        Priority = priority;
        Label = LspUtil.Str(item["label"]);
        Text = item["filterText"] is { } f ? LspUtil.Str(f) : Label;
        SortText = item["sortText"] is { } s ? LspUtil.Str(s) : Label;
        Kind = item["kind"]?.GetValue<int>() ?? 1;
    }

    public string Label { get; }
    /// <summary>IntelliCode-style: one of your most-used picks, shown with a star at the top.</summary>
    public bool Starred { get; set; }
    public string SortText { get; }
    public int Kind { get; }
    public string Text { get; }
    public double Priority { get; }
    public IImage? Image => null;

    public object Content => _content ??= BuildContent();

    private Control BuildContent()
    {
        var detail = LspUtil.Str(_item["labelDetails"]?["description"]);
        if (detail.Length == 0) detail = LspUtil.Str(_item["detail"]);
        var labelSuffix = LspUtil.Str(_item["labelDetails"]?["detail"]);
        var deprecated = _item["deprecated"]?.GetValue<bool>() == true ||
                         (_item["tags"] as JsonArray)?.Any(t => t?.GetValue<int>() == 1) == true;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), MinWidth = 380 };
        var badge = KindBadge.Create(Kind);
        badge.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(badge);

        var label = new TextBlock
        {
            Text = Starred ? "★ " + Label : Label,
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = deprecated ? TextDecorations.Strikethrough : null,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (labelSuffix.Length > 0)
        {
            var suffix = new TextBlock { Text = labelSuffix, Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(suffix, 2);
            grid.Children.Add(suffix);
        }

        var right = new TextBlock
        {
            Text = detail.Length > 60 ? detail[..57] + "…" : detail,
            Foreground = ThemeManager.Brush("FgMuted"),
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    public object Description
    {
        get
        {
            if (_description is not null) return _description;
            _description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, FontSize = 12 };
            SetDescription(_item);
            if (CanResolve && _item["documentation"] is null)
                _ = ResolveAsync().ContinueWith(t => { if (t.IsCompletedSuccessfully) Avalonia.Threading.Dispatcher.UIThread.Post(() => SetDescription(t.Result)); });
            return _description;
        }
    }

    private void SetDescription(JsonObject item)
    {
        if (_description is null) return;
        var detail = LspUtil.Str(item["detail"]);
        var doc = Markdown.ToPlain(LspUtil.MarkupToMarkdown(item["documentation"]));
        _description.Text = string.Join("\n\n", new[] { detail, doc }.Where(s => s.Length > 0));
    }

    private bool CanResolve => _client?.Capabilities["completionProvider"]?["resolveProvider"]?.GetValue<bool>() == true;

    private Task<JsonObject> ResolveAsync() => _resolving ??= Resolve();

    private async Task<JsonObject> Resolve()
    {
        try
        {
            if (await _client!.RequestAsync("completionItem/resolve", _item.DeepClone()) is JsonObject resolved) return resolved;
        }
        catch
        {
            // Fall back to the unresolved item.
        }
        return _item;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        CompletionUsage.Record(_doc.LanguageId, Label);
        var doc = textArea.Document;
        var caret = completionSegment.EndOffset;
        var typedSince = caret - _requestCaret;
        string newText;
        int start, end;

        var edit = _item["textEdit"];
        if (edit is JsonObject e)
        {
            var range = e["range"] ?? e["replace"] ?? e["insert"];
            start = _doc.OffsetOf(range?["start"]);
            var rangeEnd = _doc.OffsetOf(range?["end"]);
            // The user may have kept typing after the request was sent.
            end = rangeEnd <= _requestCaret ? caret : rangeEnd + Math.Max(0, typedSince);
            newText = LspUtil.Str(e["newText"]);
        }
        else
        {
            start = completionSegment.Offset;
            end = caret;
            newText = _item["insertText"] is { } it ? LspUtil.Str(it) : Label;
        }
        start = Math.Clamp(start, 0, doc.TextLength);
        end = Math.Clamp(end, start, doc.TextLength);

        var isSnippet = _item["insertTextFormat"]?.GetValue<int>() == 2;
        if (isSnippet && EditorView.Of(textArea) is { } view) view.InsertSnippet(newText, start, end, applyCodeStyle: false);
        else
        {
            doc.Replace(start, end - start, newText);
            textArea.Caret.Offset = start + newText.Length;
        }

        if (_item["additionalTextEdits"] is JsonArray extra) ApplyAdditional(extra, start);
        else if (CanResolve)
            _ = ResolveAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result["additionalTextEdits"] is JsonArray more)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyAdditional(more, start));
            });
    }

    /// <summary>Applies edits such as auto-imports. They normally sit before the completion point.</summary>
    private void ApplyAdditional(JsonArray edits, int insertionOffset)
    {
        var before = new JsonArray();
        foreach (var e in edits)
            if (_doc.OffsetOf(e?["range"]?["start"]) <= insertionOffset)
                before.Add(e!.DeepClone());
        _doc.ApplyEdits(before);
    }
}

/// <summary>Word, choice and live-template completion entries (used with or without a language server).</summary>
public sealed class SimpleCompletionData(string text, string detail, int kind, string? snippet = null, double priority = 0) : ICompletionData
{
    private Control? _content;
    private Control? _description;

    public string Text => text;
    public double Priority => priority;
    public IImage? Image => null;

    /// <summary>For templates: a code preview of what will be inserted.</summary>
    public object Description => _description ??= snippet is null ? new TextBlock { Text = detail } : Preview();

    private Control Preview()
    {
        var parsed = SnippetSyntax.Parse(snippet!, new SnippetSyntax.Context("", "    ", "\n", "", null, "//"));
        return new StackPanel
        {
            Spacing = 6,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = detail.Length > 0 ? detail : "Live template", FontWeight = FontWeight.SemiBold },
                new Border
                {
                    Background = ThemeManager.Brush("EditorBg"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Child = new TextBlock
                    {
                        Text = parsed.Text,
                        FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
                        FontSize = 12.5,
                        Foreground = ThemeManager.Brush("EditorText"),
                    },
                },
                new TextBlock { Text = "Tab moves between fields · Enter finishes", Foreground = ThemeManager.Brush("FgMuted"), FontSize = 11 },
            },
        };
    }

    public object Content => _content ??= new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
        MinWidth = 380,
        Children =
        {
            new ContentControl { Content = KindBadge.Create(kind), Margin = new Thickness(0, 0, 8, 0) },
            new TextBlock { Text = text, FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
            new TextBlock { Text = detail, Foreground = ThemeManager.Brush("FgMuted"), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 2 },
            new Border
            {
                IsVisible = snippet is not null,
                [Grid.ColumnProperty] = 3,
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(6, 1),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ThemeManager.Color("Link"), 0.18),
                Child = new TextBlock { Text = "template", FontSize = 10.5, Foreground = ThemeManager.Brush("Link") },
            },
        },
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (snippet is not null && EditorView.Of(textArea) is { } view)
        {
            view.InsertSnippet(snippet, completionSegment.Offset, completionSegment.EndOffset, applyCodeStyle: false);
            return;
        }
        textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, text);
        textArea.Caret.Offset = completionSegment.Offset + text.Length;
    }
}

/// <summary>Minimal markdown → Avalonia controls, for hover and quick documentation.</summary>
public static class Markdown
{
    public static string ToPlain(string md) =>
        md.Replace("```", "").Replace("**", "").Replace("__", "").Replace("`", "").Trim();

    public static Control Render(string md)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 640 };
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var buffer = new StringBuilder();
        var inCode = false;

        void FlushText()
        {
            var t = buffer.ToString().Trim();
            buffer.Clear();
            if (t.Length == 0) return;
            if (t == "---")
            {
                panel.Children.Add(new Border { Height = 1, Background = ThemeManager.Brush("Border"), Margin = new Thickness(0, 2) });
                return;
            }
            panel.Children.Add(new SelectableTextBlock
            {
                Text = t.Replace("**", "").Replace("`", "").Replace("\\", ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = ThemeManager.Brush("Fg"),
            });
        }

        void FlushCode()
        {
            var t = buffer.ToString().TrimEnd();
            buffer.Clear();
            if (t.Length == 0) return;
            panel.Children.Add(new Border
            {
                Background = ThemeManager.Brush("EditorBg"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6),
                Child = new SelectableTextBlock
                {
                    Text = t,
                    FontFamily = mono,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeManager.Brush("EditorText"),
                },
            });
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) FlushCode(); else FlushText();
                inCode = !inCode;
                continue;
            }
            if (!inCode && line.Trim().Length == 0)
            {
                FlushText();
                continue;
            }
            buffer.AppendLine(inCode ? line : line.TrimEnd());
        }
        if (inCode) FlushCode(); else FlushText();
        return new ScrollViewer { Content = panel, MaxHeight = 420, MaxWidth = 660 };
    }
}
