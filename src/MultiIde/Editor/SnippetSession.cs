using System.Text;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using MultiIde.Core;

namespace MultiIde.Editor;

/// <summary>A parsed snippet: expanded text plus placeholder positions relative to the start.</summary>
public sealed class ParsedSnippet
{
    public string Text { get; set; } = "";
    /// <summary>Placeholder occurrences: tab-stop index, offset, length, choices.</summary>
    public List<(int Index, int Offset, int Length, string[]? Choices)> Stops { get; } = new();
    /// <summary>Offset of $0, or -1.</summary>
    public int FinalOffset { get; set; } = -1;
}

/// <summary>
/// Parses LSP / TextMate snippet syntax: $1, ${1:default}, ${1|a,b|}, $0, nested placeholders
/// and variables (TM_SELECTED_TEXT, TM_FILENAME, CURRENT_YEAR, ...).
/// </summary>
public static class SnippetSyntax
{
    public sealed record Context(string Indent, string IndentUnit, string NewLine, string? Selection, string? FilePath, string? LineComment);

    public static ParsedSnippet Parse(string body, Context ctx)
    {
        // Normalize first: the end index must be the normalized length (CRLF bodies used to read past the end).
        var text = body.Replace("\r\n", "\n");
        var result = new ParsedSnippet();
        var sb = new StringBuilder();
        try
        {
            Parse(text, 0, text.Length, sb, result, ctx);
        }
        catch (Exception)
        {
            // Malformed snippet syntax: insert it as plain text rather than failing the completion.
            result = new ParsedSnippet();
            sb.Clear();
            Append(sb, text, ctx);
        }
        result.Text = sb.ToString();
        return result;
    }

    private static void Append(StringBuilder sb, string text, Context ctx)
    {
        foreach (var c in text)
        {
            if (c == '\n') sb.Append(ctx.NewLine).Append(ctx.Indent);
            else if (c == '\t') sb.Append(ctx.IndentUnit);
            else sb.Append(c);
        }
    }

    private static void Parse(string s, int from, int to, StringBuilder sb, ParsedSnippet result, Context ctx)
    {
        for (var i = from; i < to; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < to && s[i + 1] is '$' or '}' or '\\' or ',' or '|')
            {
                sb.Append(s[++i]);
                continue;
            }
            if (c != '$' || i + 1 >= to)
            {
                Append(sb, c.ToString(), ctx);
                continue;
            }
            // $1 / $0
            if (char.IsDigit(s[i + 1]))
            {
                var j = i + 1;
                while (j < to && char.IsDigit(s[j])) j++;
                Stop(int.Parse(s[(i + 1)..j]), sb.Length, 0, null);
                i = j - 1;
                continue;
            }
            // $VARIABLE
            if (char.IsLetter(s[i + 1]) || s[i + 1] == '_')
            {
                var j = i + 1;
                while (j < to && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                Append(sb, Variable(s[(i + 1)..j], null, ctx), ctx);
                i = j - 1;
                continue;
            }
            if (s[i + 1] != '{')
            {
                sb.Append(c);
                continue;
            }
            var close = FindClose(s, i + 2, to);
            if (close < 0)
            {
                // Unclosed "${": literal text.
                Append(sb, s[i..to], ctx);
                return;
            }
            var inner = s[(i + 2)..close];
            if (inner.Length > 0 && char.IsDigit(inner[0]))
            {
                var k = 0;
                while (k < inner.Length && char.IsDigit(inner[k])) k++;
                var index = int.Parse(inner[..k]);
                var start = sb.Length;
                string[]? choices = null;
                if (k < inner.Length && inner[k] == '|')
                {
                    choices = inner[(k + 1)..].TrimEnd('|').Split(',');
                    sb.Append(choices[0]);
                }
                else if (k < inner.Length && inner[k] == ':')
                {
                    Parse(s, i + 2 + k + 1, close, sb, result, ctx);
                }
                Stop(index, start, sb.Length - start, choices);
            }
            else
            {
                var colon = inner.IndexOf(':');
                var name = colon >= 0 ? inner[..colon] : inner;
                Append(sb, Variable(name, colon >= 0 ? inner[(colon + 1)..] : null, ctx), ctx);
            }
            i = close;
        }

        void Stop(int index, int offset, int length, string[]? choices)
        {
            if (index == 0) result.FinalOffset = offset;
            else result.Stops.Add((index, offset, length, choices));
        }
    }

    private static string Variable(string name, string? fallback, Context ctx)
    {
        var now = DateTime.Now;
        var value = name switch
        {
            "TM_SELECTED_TEXT" or "SELECTION" => ctx.Selection,
            "TM_FILENAME" => ctx.FilePath is null ? null : Path.GetFileName(ctx.FilePath),
            "TM_FILENAME_BASE" => ctx.FilePath is null ? null : Path.GetFileNameWithoutExtension(ctx.FilePath),
            "TM_DIRECTORY" => ctx.FilePath is null ? null : Path.GetDirectoryName(ctx.FilePath),
            "TM_FILEPATH" => ctx.FilePath,
            "CURRENT_YEAR" => now.Year.ToString(),
            "CURRENT_MONTH" => now.Month.ToString("00"),
            "CURRENT_DATE" => now.Day.ToString("00"),
            "CURRENT_HOUR" => now.Hour.ToString("00"),
            "CURRENT_MINUTE" => now.Minute.ToString("00"),
            "UUID" => Guid.NewGuid().ToString(),
            "LINE_COMMENT" => ctx.LineComment,
            "USER" => Environment.UserName,
            _ => null,
        };
        return value ?? fallback ?? "";
    }

    private static int FindClose(string s, int from, int to)
    {
        var depth = 0;
        for (var k = from; k < to; k++)
        {
            if (s[k] == '\\') { k++; continue; }
            if (s[k] == '{') depth++;
            else if (s[k] == '}')
            {
                if (depth == 0) return k;
                depth--;
            }
        }
        return -1; // unclosed
    }
}

/// <summary>
/// A live template being filled in: tracks placeholders as the document changes,
/// mirrors linked placeholders, and draws them (active one outlined in the accent color).
/// </summary>
public sealed class SnippetSession : IBackgroundRenderer
{
    private sealed class Field
    {
        public int Index;
        public int Start;
        public int End;
        public string[]? Choices;
    }

    private readonly TextArea _area;
    private readonly TextDocument _doc;
    private readonly List<Field> _fields = new();
    private readonly List<int> _order;
    private int _final;
    private int _current = -1;
    private bool _mirroring;

    public event Action? Ended;
    public bool IsActive { get; private set; } = true;
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Raised when a choice placeholder becomes active (the editor shows a pick list).</summary>
    public Action<int, string[]>? ShowChoices { get; set; }

    private SnippetSession(TextArea area, int start, ParsedSnippet snippet)
    {
        _area = area;
        _doc = area.Document;
        foreach (var (index, offset, length, choices) in snippet.Stops)
            _fields.Add(new Field { Index = index, Start = start + offset, End = start + offset + length, Choices = choices });
        _final = snippet.FinalOffset >= 0 ? start + snippet.FinalOffset : start + snippet.Text.Length;
        _order = _fields.Select(f => f.Index).Distinct().Order().ToList();
        _doc.Changing += OnChanging;
        _doc.Changed += OnChanged;
        _area.Caret.PositionChanged += OnCaretMoved;
        _area.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>Replaces [start, end) with the snippet and starts editing its first placeholder.</summary>
    public static SnippetSession? Start(TextArea area, ParsedSnippet snippet, int start, int end)
    {
        var doc = area.Document;
        doc.BeginUpdate();
        try { doc.Replace(start, end - start, snippet.Text); }
        finally { doc.EndUpdate(); }

        if (snippet.Stops.Count == 0)
        {
            area.Caret.Offset = snippet.FinalOffset >= 0 ? start + snippet.FinalOffset : start + snippet.Text.Length;
            return null;
        }
        var session = new SnippetSession(area, start, snippet);
        session.Select(0);
        return session;
    }

    private IEnumerable<Field> Group(int index) => _fields.Where(f => f.Index == index);
    private Field? Primary(int index) => _fields.FirstOrDefault(f => f.Index == index);

    private void Select(int orderPosition)
    {
        _current = orderPosition;
        if (Primary(_order[orderPosition]) is not { } f) return;
        _area.Selection = Selection.Create(_area, f.Start, f.End);
        _area.Caret.Offset = f.End;
        _area.TextView.InvalidateLayer(Layer);
        if (f.Choices is { Length: > 1 } choices) ShowChoices?.Invoke(f.Start, choices);
    }

    /// <summary>Tab / Shift+Tab. Moving past the last placeholder finishes at $0.</summary>
    public void Next(int direction)
    {
        var next = _current + direction;
        if (next < 0) return;
        if (next >= _order.Count) { Finish(); return; }
        Select(next);
    }

    /// <summary>Ends the session and puts the caret at $0.</summary>
    public void Finish()
    {
        if (!IsActive) return;
        var final = Math.Clamp(_final, 0, _doc.TextLength);
        End();
        _area.ClearSelection();
        _area.Caret.Offset = final;
    }

    public void End()
    {
        if (!IsActive) return;
        IsActive = false;
        _doc.Changing -= OnChanging;
        _doc.Changed -= OnChanged;
        _area.Caret.PositionChanged -= OnCaretMoved;
        _area.TextView.BackgroundRenderers.Remove(this);
        _area.TextView.InvalidateLayer(Layer);
        Ended?.Invoke();
    }

    private Field? _editing;

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        var start = e.Offset;
        var removedEnd = e.Offset + e.RemovalLength;
        var delta = e.InsertionLength - e.RemovalLength;
        // The edited field: the one containing the change (edges included), active field first.
        _editing = _mirroring ? null :
            (_current >= 0 ? Group(_order[_current]).FirstOrDefault(f => start >= f.Start && removedEnd <= f.End) : null)
            ?? _fields.FirstOrDefault(f => start >= f.Start && removedEnd <= f.End);

        foreach (var f in _fields)
        {
            if (f == _editing) f.End += delta;                              // typing inside this field
            else if (removedEnd <= f.Start) { f.Start += delta; f.End += delta; } // change before the field
            else if (start >= f.End) { }                                       // change after the field
            else { f.Start = Math.Min(f.Start, start); f.End = Math.Max(f.Start, f.End + delta); } // overlapping change
        }
        if (removedEnd <= _final) _final += delta;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_mirroring || _editing is not { } edited) return;
        var text = _doc.GetText(edited.Start, edited.End - edited.Start);
        var others = _fields.Where(f => f.Index == edited.Index && f != edited).OrderByDescending(f => f.Start).ToList();
        if (others.Count == 0) return;
        // Mirror linked placeholders after the current edit completes.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive) return;
            _mirroring = true;
            try
            {
                foreach (var f in others)
                {
                    var len = f.End - f.Start;
                    if (_doc.GetText(f.Start, len) == text) continue;
                    var before = f.Start;
                    _doc.Replace(f.Start, len, text);
                    f.Start = before;
                    f.End = before + text.Length;
                }
            }
            finally
            {
                _mirroring = false;
            }
        });
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_mirroring) return;
        var caret = _area.Caret.Offset;
        if (_fields.Any(f => caret >= f.Start && caret <= f.End)) return;
        if (caret == _final) return;
        End(); // the caret left the template
    }

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (!IsActive || !textView.VisualLinesValid) return;
        var accent = ThemeManager.Color("Accent");
        var activePen = new Pen(new SolidColorBrush(accent), 1.5);
        var activeFill = new SolidColorBrush(accent, 0.16);
        var otherPen = new Pen(new SolidColorBrush(ThemeManager.Color("FgMuted"), 0.7), 1, new DashStyle(new double[] { 2, 2 }, 0));
        var activeIndex = _current >= 0 ? _order[_current] : -1;
        foreach (var f in _fields)
        {
            var segment = new TextSegment { StartOffset = Math.Min(f.Start, _doc.TextLength), EndOffset = Math.Min(Math.Max(f.End, f.Start), _doc.TextLength) };
            var rects = segment.Length == 0
                ? BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = segment.StartOffset, Length = 0 }, true)
                : BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, false);
            foreach (var r in rects)
            {
                var box = new Rect(r.X - 1, r.Y + 1, Math.Max(r.Width + 2, 4), r.Height - 2);
                if (f.Index == activeIndex) ctx.DrawRectangle(activeFill, activePen, box, 3, 3);
                else ctx.DrawRectangle(null, otherPen, box, 3, 3);
            }
        }
    }
}
