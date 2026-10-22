using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using MultiIde.Core;

namespace MultiIde.Editor;

/// <summary>
/// IntelliJ-style folding gutter: thin chevrons instead of +/- boxes. Expanded regions
/// show only while the mouse is over the gutter; collapsed ones always show.
/// </summary>
public sealed class ChevronFoldingMargin : AbstractMargin
{
    private const double Size = 14;
    private readonly FoldingManager _manager;
    private bool _hover;
    private int _hoverLine = -1;

    public ChevronFoldingMargin(FoldingManager manager)
    {
        _manager = manager;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => new(Size, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) newTextView.VisualLinesChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private FoldingSection? FoldingStartingOn(VisualLine line)
    {
        var first = line.FirstDocumentLine;
        var f = _manager.GetNextFolding(first.Offset);
        return f is not null && f.StartOffset <= first.EndOffset ? f : null;
    }

    public override void Render(DrawingContext ctx)
    {
        // Transparent fill so the whole strip receives pointer events.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var tv = TextView;
        if (tv is null || !tv.VisualLinesValid) return;

        var normal = new Pen(ThemeManager.Brush("EditorLineNumber"), 1.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var strong = new Pen(ThemeManager.Brush("EditorLineNumberActive"), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        var visibleEnds = new HashSet<int>();
        if (_hover)
        {
            var firstLine = tv.VisualLines[0].FirstDocumentLine.LineNumber;
            var lastLine = tv.VisualLines[^1].LastDocumentLine.LineNumber;
            foreach (var f in _manager.AllFoldings)
            {
                if (f.IsFolded) continue;
                var endLine = tv.Document.GetLineByOffset(f.EndOffset).LineNumber;
                if (endLine >= firstLine && endLine <= lastLine) visibleEnds.Add(endLine);
            }
        }

        foreach (var line in tv.VisualLines)
        {
            var y = line.VisualTop - tv.VerticalOffset;
            var cy = y + line.Height / 2;
            const double cx = Size / 2;
            var lineNo = line.FirstDocumentLine.LineNumber;
            var pen = lineNo == _hoverLine ? strong : normal;

            if (FoldingStartingOn(line) is { } f && (_hover || f.IsFolded))
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    if (f.IsFolded)
                    {
                        c.BeginFigure(new Point(cx - 2, cy - 4), false);
                        c.LineTo(new Point(cx + 2, cy));
                        c.LineTo(new Point(cx - 2, cy + 4));
                    }
                    else
                    {
                        c.BeginFigure(new Point(cx - 4, cy - 2), false);
                        c.LineTo(new Point(cx, cy + 2));
                        c.LineTo(new Point(cx + 4, cy - 2));
                    }
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, f.IsFolded ? strong : pen, g);
            }
            else if (visibleEnds.Contains(line.LastDocumentLine.LineNumber))
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx - 3.5, cy + 1.5), false);
                    c.LineTo(new Point(cx, cy - 2));
                    c.LineTo(new Point(cx + 3.5, cy + 1.5));
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, normal, g);
            }
        }
    }

    private VisualLine? LineAt(PointerEventArgs e)
    {
        var tv = TextView;
        if (tv is null || !tv.VisualLinesValid) return null;
        return tv.GetVisualLineFromVisualTop(e.GetPosition(this).Y + tv.VerticalOffset);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (LineAt(e) is not { } line || FoldingStartingOn(line) is not { } f) return;
        f.IsFolded = !f.IsFolded;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var n = LineAt(e)?.FirstDocumentLine.LineNumber ?? -1;
        if (n != _hoverLine) { _hoverLine = n; InvalidateVisual(); }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        _hoverLine = -1;
        InvalidateVisual();
    }
}
