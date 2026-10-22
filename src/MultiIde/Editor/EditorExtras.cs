using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Editor;

/// <summary>Colors TODO / FIXME / HACK / XXX comments, like IntelliJ's TODO highlighting.</summary>
public sealed class TodoColorizer : DocumentColorizingTransformer
{
    public static readonly Regex Pattern = new(@"\b(TODO|FIXME|HACK|XXX)\b.*", RegexOptions.Compiled);

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        var m = Pattern.Match(text);
        if (!m.Success) return;
        var brush = ThemeManager.Brush("Success");
        ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e =>
        {
            e.TextRunProperties.SetForegroundBrush(brush);
            e.TextRunProperties.SetTypeface(new Typeface(e.TextRunProperties.Typeface.FontFamily, FontStyle.Italic, FontWeight.Normal));
        });
    }
}

/// <summary>
/// Language-server inlay hints (types, parameter names) shown as small inline tags.
/// Hints are anchored to the text, so they follow edits until the next refresh.
/// </summary>
public sealed class InlayHintGenerator : VisualLineElementGenerator
{
    private readonly List<(TextAnchor Anchor, string Text, bool PadLeft, bool PadRight)> _hints = new();
    private int _consumed = -1;

    public double FontSize { get; set; } = 12;

    public void Set(TextDocument doc, IEnumerable<(int Offset, string Text, bool PadLeft, bool PadRight)> hints)
    {
        _hints.Clear();
        foreach (var (offset, text, padLeft, padRight) in hints.OrderBy(h => h.Offset))
        {
            var anchor = doc.CreateAnchor(Math.Clamp(offset, 0, doc.TextLength));
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            _hints.Add((anchor, text, padLeft, padRight));
        }
    }

    public void Clear() => _hints.Clear();

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _consumed = -1;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        foreach (var h in _hints)
        {
            if (h.Anchor.IsDeleted) continue;
            var o = h.Anchor.Offset;
            if (o >= startOffset && o <= end && o != _consumed) return o;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        _consumed = offset; // zero-length element: don't offer this offset again for this line
        var text = string.Join(" ", _hints.Where(h => !h.Anchor.IsDeleted && h.Anchor.Offset == offset).Select(h => h.Text));
        if (text.Length == 0) return null;
        var first = _hints.First(h => h.Anchor.Offset == offset);
        var tag = new Border
        {
            Background = ThemeManager.Brush("HoverSubtle"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(first.PadLeft ? 4 : 1, 1, first.PadRight ? 4 : 1, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = FontSize * 0.85, Foreground = ThemeManager.Brush("FgMuted") },
        };
        return new InlineObjectElement(0, tag);
    }
}

/// <summary>
/// The marker strip beside the scrollbar: errors, warnings and TODOs at their relative position
/// in the file, plus the caret line. Clicking a mark jumps there.
/// </summary>
public sealed class ErrorStripe : Control
{
    private readonly EditorView _view;
    public ErrorStripe(EditorView view)
    {
        _view = view;
        Width = 10;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public List<(int Line, int Severity)> Marks { get; } = new();

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var lines = Math.Max(1, _view.Doc.Text.LineCount);
        var h = Bounds.Height;
        double Y(int line) => Math.Clamp((line - 1) / (double)lines * (h - 4), 0, h - 3);
        foreach (var (line, severity) in Marks.OrderByDescending(m => m.Severity))
        {
            var brush = ThemeManager.Brush(severity switch { 1 => "Error", 2 => "Warning", 5 => "Success", _ => "Link" });
            ctx.FillRectangle(brush, new Rect(2, Y(line), Bounds.Width - 4, 3), 1);
        }
        var caret = _view.Editor.TextArea.Caret.Line;
        ctx.FillRectangle(ThemeManager.Brush("FgMuted"), new Rect(0, Y(caret), Bounds.Width, 1.5));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var lines = Math.Max(1, _view.Doc.Text.LineCount);
        var clicked = (int)Math.Round(e.GetPosition(this).Y / Math.Max(1, Bounds.Height - 4) * lines) + 1;
        // Snap to the nearest mark within a few pixels' worth of lines, else jump to the position.
        var tolerance = Math.Max(1, lines / Math.Max(1, Bounds.Height) * 4);
        var nearest = Marks.OrderBy(m => Math.Abs(m.Line - clicked)).FirstOrDefault();
        _view.GoTo(nearest.Line > 0 && Math.Abs(nearest.Line - clicked) <= tolerance ? nearest.Line : Math.Clamp(clicked, 1, lines), 1);
        e.Handled = true;
    }
}

/// <summary>Draws a predicted next word as gray text right after the caret.</summary>
public sealed class GhostTextRenderer : IBackgroundRenderer
{
    public string? Text { get; set; }
    public int Offset { get; set; } = -1;
    public KnownLayer Layer => KnownLayer.Caret;

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (string.IsNullOrEmpty(Text) || Offset < 0 || textView.Document is null || !textView.VisualLinesValid) return;
        var loc = textView.Document.GetLocation(Math.Min(Offset, textView.Document.TextLength));
        var pos = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(loc), VisualYPosition.LineTop) - textView.ScrollOffset;
        var typeface = new Typeface(textView.GetValue(TextBlock.FontFamilyProperty), FontStyle.Italic);
        var text = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            textView.GetValue(TextBlock.FontSizeProperty), new SolidColorBrush(ThemeManager.Color("FgMuted"), 0.75));
        var lineHeight = textView.DefaultLineHeight;
        ctx.DrawText(text, new Point(pos.X, pos.Y + (lineHeight - text.Height) / 2));
    }
}

public sealed partial class EditorView
{
    private ErrorStripe? _stripe;
    private readonly GhostTextRenderer _ghost = new();
    private static readonly System.Text.RegularExpressions.Regex TrailingWords =
        new(@"(?:([A-Za-z_#][A-Za-z0-9_]*|<|::|->|=>)[ \t]+)?([A-Za-z_#][A-Za-z0-9_]*|<|::|->|=>)[ \t]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void InstallGhost()
    {
        Editor.TextArea.TextView.BackgroundRenderers.Add(_ghost);
        Editor.TextArea.Caret.PositionChanged += (_, _) => { if (Editor.CaretOffset != _ghost.Offset) ClearGhost(); };
    }

    /// <summary>
    /// IntelliCode-style: right after "word " (e.g. "private "), suggests the clearly predictable
    /// next word (e.g. "static") as gray text, from what this project's code usually does.
    /// </summary>
    private void ScheduleGhost()
    {
        ClearGhost();
        if (!_host.Settings.SmartSuggestions || Doc.IsLarge || _completion is not null || _snippet is { IsActive: true }) return;
        var caret = Editor.CaretOffset;
        var line = Doc.Text.GetLineByOffset(caret);
        var rest = Doc.Text.GetText(caret, line.EndOffset - caret);
        if (rest.Trim().Trim(')', ']', '}', ';').Length > 0) return; // only at the end of the line
        var before = Doc.Text.GetText(line.Offset, caret - line.Offset);
        var m = TrailingWords.Match(before);
        if (!m.Success) return;
        var prev2 = m.Groups[1].Success ? m.Groups[1].Value : null;
        var suggestion = TokenPredictor.Predict(Doc.LanguageId, prev2, m.Groups[2].Value);
        if (suggestion is null) return;
        _ghost.Text = suggestion;
        _ghost.Offset = caret;
        Editor.TextArea.TextView.InvalidateLayer(_ghost.Layer);
    }

    private bool HasGhost => !string.IsNullOrEmpty(_ghost.Text) && _ghost.Offset == Editor.CaretOffset;

    private void AcceptGhost()
    {
        var text = _ghost.Text!;
        var at = _ghost.Offset;
        ClearGhost();
        Doc.Text.Insert(at, text);
        Editor.CaretOffset = at + text.Length;
    }

    private void ClearGhost()
    {
        if (_ghost.Text is null) return;
        _ghost.Text = null;
        _ghost.Offset = -1;
        Editor.TextArea.TextView.InvalidateLayer(_ghost.Layer);
    }
    private InlayHintGenerator? _inlays;
    private Avalonia.Threading.DispatcherTimer? _inlayTimer;

    /// <summary>Called from the constructor: stripe, TODO colors, inlay hints, Ctrl+wheel zoom.</summary>
    private void InstallExtras(Grid root)
    {
        root.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _stripe = new ErrorStripe(this);
        Grid.SetColumn(_stripe, 1);
        root.Children.Add(_stripe);
        Editor.TextArea.TextView.LineTransformers.Add(new TodoColorizer());

        _inlays = new InlayHintGenerator { FontSize = _host.Settings.FontSize };
        Editor.TextArea.TextView.ElementGenerators.Add(_inlays);
        _inlayTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, async (_, _) =>
        {
            _inlayTimer!.Stop();
            await RefreshInlayHintsAsync();
        });
        Doc.Text.Changed += (_, _) => { _inlayTimer.Stop(); _inlayTimer.Start(); RefreshStripe(); };
        Editor.TextArea.Caret.PositionChanged += (_, _) => _stripe.InvalidateVisual();

        Editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            _host.ZoomEditor(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RefreshStripe();
        _inlayTimer.Start();
        InstallGhost();
    }

    /// <summary>Rebuilds the stripe marks from diagnostics and TODO comments.</summary>
    private void RefreshStripe()
    {
        if (_stripe is null) return;
        _stripe.Marks.Clear();
        foreach (var d in Doc.Diagnostics) if (d.Severity <= 2) _stripe.Marks.Add((d.StartLine + 1, d.Severity));
        if (Doc.Text.TextLength < 2_000_000)
        {
            var n = 0;
            foreach (var line in Doc.Text.Lines)
            {
                n++;
                if (line.Length > 3 && TodoColorizer.Pattern.IsMatch(Doc.Text.GetText(line))) _stripe.Marks.Add((n, 5));
            }
        }
        _stripe.InvalidateVisual();
    }

    private async Task RefreshInlayHintsAsync()
    {
        if (_inlays is null) return;
        if (!_host.Settings.InlayHints || Doc.Lsp is not { } lsp || !lsp.Has("inlayHintProvider") || Doc.IsLarge)
        {
            if (_inlays is not null) { _inlays.Clear(); Editor.TextArea.TextView.Redraw(); }
            return;
        }
        var version = _editVersion;
        try
        {
            var end = Doc.Text.GetLocation(Doc.Text.TextLength);
            var result = await lsp.RequestAsync("textDocument/inlayHint", new JsonObject
            {
                ["textDocument"] = Doc.TextDocumentId,
                ["range"] = new JsonObject { ["start"] = LspUtil.Pos(0, 0), ["end"] = LspUtil.Pos(end.Line - 1, end.Column - 1) },
            }) as JsonArray;
            if (version != _editVersion || result is null) return;
            var hints = result.OfType<JsonObject>().Select(h =>
            {
                var label = h["label"] is JsonArray parts
                    ? string.Concat(parts.Select(p => LspUtil.Str(p?["value"])))
                    : LspUtil.Str(h["label"]);
                return (Doc.OffsetOf(h["position"]), label.Trim(), h["paddingLeft"]?.GetValue<bool>() == true, h["paddingRight"]?.GetValue<bool>() == true);
            }).Where(h => h.Item2.Length > 0).ToList();
            _inlays.FontSize = Editor.FontSize;
            _inlays.Set(Doc.Text, hints);
            Editor.TextArea.TextView.Redraw();
        }
        catch
        {
            // Hints are optional.
        }
    }

    /// <summary>Jumps to the next (1) or previous (-1) error or warning, wrapping around.</summary>
    public void NextProblem(int direction)
    {
        var problems = Doc.Diagnostics.Where(d => d.Severity <= 2)
            .Select(d => Doc.OffsetOf(d.StartLine, d.StartCol)).Distinct().Order().ToList();
        if (problems.Count == 0) { _host.Status("No problems in this file"); return; }
        var caret = Editor.CaretOffset;
        var target = direction > 0
            ? problems.FirstOrDefault(o => o > caret, problems[0])
            : problems.LastOrDefault(o => o < caret, problems[^1]);
        var loc = Doc.Text.GetLocation(target);
        GoTo(loc.Line, loc.Column);
        _ = ShowInfoAsync(target, CaretRect());
    }
}
