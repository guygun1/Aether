using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Rendering;
using MultiIde.Core;

namespace MultiIde.Editor;

/// <summary>Draws wavy underlines for language-server diagnostics.</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    private static readonly IPen ErrorPen = new Pen(ThemeManager.Brush("Error"), 1.2);
    private static readonly IPen WarningPen = new Pen(ThemeManager.Brush("Warning"), 1.2);
    private static readonly IPen InfoPen = new Pen(ThemeManager.Brush("Link"), 1.0);
    private static IBrush UnusedBrush => new SolidColorBrush(ThemeManager.Color("EditorBg"), 0.35);

    public List<(TextSegment Segment, int Severity, bool Unnecessary)> Marks { get; } = new();
    /// <summary>Line (1-based) whose marks are hidden while the user is typing on it; -1 for none.</summary>
    public int HiddenLine { get; set; } = -1;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (Marks.Count == 0 || !textView.VisualLinesValid) return;
        var visible = textView.VisualLines;
        if (visible.Count == 0) return;
        var viewStart = visible[0].FirstDocumentLine.Offset;
        var viewEnd = visible[^1].LastDocumentLine.EndOffset;

        foreach (var (segment, severity, unnecessary) in Marks)
        {
            if (HiddenLine > 0 && textView.Document is { } doc && segment.StartOffset <= doc.TextLength &&
                doc.GetLineByOffset(Math.Min(segment.StartOffset, doc.TextLength)).LineNumber == HiddenLine) continue;
            if (segment.EndOffset < viewStart || segment.StartOffset > viewEnd) continue;
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, false))
            {
                if (unnecessary)
                {
                    // Unused code is faded, like IntelliJ.
                    ctx.FillRectangle(UnusedBrush, r);
                    continue;
                }
                var pen = severity switch { 1 => ErrorPen, 2 => WarningPen, _ => InfoPen };
                if (severity == 4) continue; // hints are only shown on hover
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var y = r.Bottom - 1.5;
                    var x = r.Left;
                    g.BeginFigure(new Point(x, y), false);
                    var up = true;
                    while (x < r.Right)
                    {
                        x += 2;
                        g.LineTo(new Point(Math.Min(x, r.Right), up ? y - 2 : y));
                        up = !up;
                    }
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geometry);
            }
        }
    }
}

/// <summary>Thin vertical lines at each indentation level.</summary>
public sealed class IndentGuideRenderer : IBackgroundRenderer
{
    public bool Enabled { get; set; } = true;
    public int TabSize { get; set; } = 4;
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (!Enabled || !textView.VisualLinesValid || textView.Document is null) return;
        var pen = new Pen(ThemeManager.Brush("EditorIndentGuide"), 1);
        var charWidth = textView.WideSpaceWidth;
        foreach (var line in textView.VisualLines)
        {
            var docLine = line.FirstDocumentLine;
            var text = textView.Document.GetText(docLine);
            // Blank lines inherit the indentation of the next non-blank line, so guides stay continuous.
            var probe = docLine;
            while (text.Trim().Length == 0 && probe.NextLine is { } next && next.LineNumber - docLine.LineNumber < 50)
            {
                probe = next;
                text = textView.Document.GetText(probe);
            }
            var columns = 0;
            foreach (var c in text)
            {
                if (c == ' ') columns++;
                else if (c == '\t') columns += TabSize - columns % TabSize;
                else break;
            }
            var top = line.VisualTop - textView.VerticalOffset;
            for (var col = TabSize; col < columns; col += TabSize)
            {
                var x = Math.Round(col * charWidth - textView.HorizontalOffset) + 0.5;
                if (x < 0) continue;
                ctx.DrawLine(pen, new Point(x, top), new Point(x, top + line.Height));
            }
        }
    }
}

/// <summary>Highlights other occurrences of the identifier under the caret (visible lines only).</summary>
public sealed class OccurrenceRenderer : IBackgroundRenderer
{
    private string? _word;

    public bool Enabled { get; set; } = true;
    public KnownLayer Layer => KnownLayer.Selection;

    public void Update(TextDocument doc, int caret)
    {
        _word = null;
        if (!Enabled || doc.TextLength == 0) return;
        static bool Ident(char c) => char.IsLetterOrDigit(c) || c == '_';
        var s = caret;
        while (s > 0 && Ident(doc.GetCharAt(s - 1))) s--;
        var e = caret;
        while (e < doc.TextLength && Ident(doc.GetCharAt(e))) e++;
        if (e - s < 2 || char.IsDigit(doc.GetCharAt(s))) return;
        _word = doc.GetText(s, e - s);
    }

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (_word is null || !textView.VisualLinesValid || textView.Document is not { } doc) return;
        var brush = ThemeManager.Brush("EditorOccurrence");
        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var text = doc.GetText(start, end - start);
        var hits = new List<int>();
        for (var i = text.IndexOf(_word, StringComparison.Ordinal); i >= 0 && hits.Count < 500; i = text.IndexOf(_word, i + _word.Length, StringComparison.Ordinal))
        {
            var before = i > 0 ? text[i - 1] : ' ';
            var after = i + _word.Length < text.Length ? text[i + _word.Length] : ' ';
            if (char.IsLetterOrDigit(before) || before == '_' || char.IsLetterOrDigit(after) || after == '_') continue;
            hits.Add(start + i);
        }
        // A word that appears once is just the caret's own word: no highlight noise.
        if (hits.Count < 2) return;
        foreach (var offset in hits)
        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = offset, Length = _word.Length }, false))
            ctx.FillRectangle(brush, r, 2);
    }
}

/// <summary>Highlights the bracket matching the one next to the caret.</summary>
public sealed class BracketHighlightRenderer : IBackgroundRenderer
{
    private static IBrush Fill => ThemeManager.Brush("EditorBracketMatch");
    private static readonly IPen Border = new Pen(ThemeManager.Brush("BorderStrong"), 1);
    private const int ScanLimit = 50_000;

    private int _a = -1, _b = -1;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Update(TextDocument doc, int caret)
    {
        _a = _b = -1;
        if (doc.TextLength == 0) return;
        foreach (var pos in new[] { caret - 1, caret })
        {
            if (pos < 0 || pos >= doc.TextLength) continue;
            var c = doc.GetCharAt(pos);
            var match = c switch
            {
                '(' or '[' or '{' => Scan(doc, pos, c, Close(c), +1),
                ')' or ']' or '}' => Scan(doc, pos, c, Open(c), -1),
                _ => -2,
            };
            if (match == -2) continue;
            if (match >= 0) (_a, _b) = (pos, match);
            return;
        }
    }

    private static char Close(char c) => c switch { '(' => ')', '[' => ']', _ => '}' };
    private static char Open(char c) => c switch { ')' => '(', ']' => '[', _ => '{' };

    private static int Scan(TextDocument doc, int from, char self, char target, int dir)
    {
        var depth = 0;
        var end = dir > 0 ? Math.Min(doc.TextLength, from + ScanLimit) : Math.Max(-1, from - ScanLimit);
        for (var i = from; i != end; i += dir)
        {
            var c = doc.GetCharAt(i);
            if (c == self) depth++;
            else if (c == target && --depth == 0) return i;
        }
        return -1;
    }

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (_a < 0) return;
        foreach (var pos in new[] { _a, _b })
        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = pos, Length = 1 }, false))
            ctx.DrawRectangle(Fill, Border, r.Deflate(new Thickness(0, 1)));
    }
}

/// <summary>Folds multi-line { } and [ ] blocks, skipping strings and comments.</summary>
public static class BraceFolding
{
    public static List<NewFolding> Compute(ITextSource text)
    {
        var result = new List<NewFolding>();
        var stack = new Stack<(int Offset, char Open)>();
        var s = text.Text;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"' or '\'':
                    i = SkipString(s, i, c);
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '/':
                    while (i < s.Length && s[i] != '\n') i++;
                    i--;
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '*':
                {
                    var start = i;
                    var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 1;
                    if (HasNewline(s, start, i)) result.Add(new NewFolding(start, Math.Min(i + 1, s.Length)) { Name = "/*...*/" });
                    break;
                }
                case '{' or '[':
                    stack.Push((i, c));
                    break;
                case '}' or ']':
                    if (stack.Count > 0)
                    {
                        var (open, ch) = stack.Pop();
                        if ((ch == '{') == (c == '}') && HasNewline(s, open, i))
                            result.Add(new NewFolding(open, i + 1) { Name = ch == '{' ? "{...}" : "[...]" });
                    }
                    break;
            }
        }
        result.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }

    /// <summary>Indentation-based folding for languages without braces (Python, YAML, CMake…).</summary>
    public static List<NewFolding> ComputeIndent(ITextSource text)
    {
        var lines = text.Text.Split('\n');
        var offsets = new int[lines.Length];
        for (int i = 0, o = 0; i < lines.Length; o += lines[i].Length + 1, i++) offsets[i] = o;
        static int Indent(string l) => l.Trim().Length == 0 ? -1 : l.Length - l.TrimStart().Length;
        var result = new List<NewFolding>();
        var stack = new Stack<(int Line, int Indent)>();
        for (var i = 0; i <= lines.Length; i++)
        {
            var indent = i < lines.Length ? Indent(lines[i]) : 0;
            if (indent < 0) continue;
            while (stack.Count > 0 && indent <= stack.Peek().Indent)
            {
                var (start, _) = stack.Pop();
                var last = i - 1;
                while (last > start && Indent(lines[last]) < 0) last--;
                if (last > start)
                    result.Add(new NewFolding(offsets[start] + lines[start].TrimEnd('\r').Length, offsets[last] + lines[last].TrimEnd('\r').Length) { Name = "..." });
            }
            if (i < lines.Length && i + 1 < lines.Length) stack.Push((i, indent));
        }
        result.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }

    private static bool HasNewline(string s, int from, int to) => s.AsSpan(from, Math.Min(to, s.Length) - from).Contains('\n');

    private static int SkipString(string s, int i, char quote)
    {
        // Treat a lone ' that is not closed on the same line as a Rust lifetime.
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == quote) return j;
            if (s[j] == '\n') return quote == '\'' ? i : j - 1;
        }
        return s.Length - 1;
    }
}

/// <summary>Keeps the previous line's indentation, adding a level after an opening brace.</summary>
public sealed class SmartIndentationStrategy : IIndentationStrategy
{
    public string IndentUnit { get; set; } = "    ";

    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var previous = line.PreviousLine;
        if (previous is null) return;
        var prevText = document.GetText(previous);
        var indent = prevText[..(prevText.Length - prevText.TrimStart().Length)];
        var trimmed = prevText.TrimEnd();
        if (trimmed.EndsWith('{') || trimmed.EndsWith('(') || trimmed.EndsWith('[') || (trimmed.EndsWith(':') && !trimmed.EndsWith("::")))
            indent += IndentUnit;

        var current = document.GetText(line);
        var currentIndentLength = current.Length - current.TrimStart().Length;
        if (current.TrimStart().StartsWith('}') && indent.EndsWith(IndentUnit))
            indent = indent[..^IndentUnit.Length];
        document.Replace(line.Offset, currentIndentLength, indent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
    }
}
