using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Editor;

public sealed record CodeLocation(string Path, int Line, int Column, string? Preview = null);
public sealed record SymbolEntry(string Name, string Detail, int Kind, int Line, int Column, int Depth);

/// <summary>What the editor needs from the surrounding IDE.</summary>
public interface IEditorHost
{
    Settings Settings { get; }
    Task OpenFileAsync(string path, int? line = null, int? column = null);
    Task<bool> ApplyWorkspaceEditAsync(JsonNode? edit);
    Task<string?> PromptAsync(string title, string initial);
    void ShowLocations(string title, IReadOnlyList<CodeLocation> locations);
    void ShowFileStructure(EditorView view, IReadOnlyList<SymbolEntry> symbols);
    void Notify(string message, bool error = false);
    void CaretMoved(EditorView view);
    /// <summary>Formats with a plugin formatter if one applies; returns false to fall back to the language server.</summary>
    Task<bool> TryPluginFormatAsync(EditorView view);
}

/// <summary>
/// A code editor tab: AvaloniaEdit + TextMate highlighting + LSP features
/// (completion, hover, diagnostics, navigation, refactoring) + IntelliJ keymap.
/// </summary>
public sealed class EditorView : UserControl
{
    private static readonly Regex WordRegex = new(@"\b[\p{L}_][\p{L}\p{Nd}_]{2,}\b", RegexOptions.Compiled);

    private readonly IEditorHost _host;
    private readonly TextMate.Installation? _textMate;
    private readonly FoldingManager? _folding;
    private readonly DiagnosticRenderer _diagnostics = new();
    private readonly BracketHighlightRenderer _brackets = new();
    private readonly IndentGuideRenderer _indentGuides = new();
    private readonly OccurrenceRenderer _occurrences = new();
    private bool _bracketsEnabled = true;
    private readonly Popup _popup;
    private readonly DispatcherTimer _foldTimer;
    private readonly DispatcherTimer _hideTimer;
    /// <summary>Holds back new diagnostics until the user pauses typing.</summary>
    private readonly DispatcherTimer _diagnosticsTimer;
    private CompletionWindow? _completion;
    private bool _completionIncomplete;
    private CancellationTokenSource? _completionCts;
    private OverloadInsightWindow? _insight;
    private int _editVersion;

    public EditorDocument Doc { get; }
    public TextEditor Editor { get; }
    /// <summary>Shown in the preview tab (italic title), replaced by the next preview.</summary>
    public bool IsPreview { get; set; }

    public EditorView(EditorDocument doc, IEditorHost host, SyntaxRegistry syntax)
    {
        Doc = doc;
        _host = host;
        var settings = host.Settings;

        Editor = new TextEditor
        {
            Document = doc.Text,
            ShowLineNumbers = true,
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            FontSize = settings.FontSize,
            Background = ThemeManager.Brush("EditorBg"),
            Foreground = ThemeManager.Brush("EditorText"),
            LineNumbersForeground = ThemeManager.Brush("EditorLineNumber"),
            WordWrap = settings.WordWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 0, 0, 0),
        };
        var o = Editor.Options;
        o.ConvertTabsToSpaces = settings.InsertSpaces;
        o.IndentationSize = settings.TabSize;
        o.HighlightCurrentLine = true;
        o.EnableHyperlinks = false;
        o.EnableEmailHyperlinks = false;
        o.CutCopyWholeLine = true;
        o.AllowScrollBelowDocument = true;
        o.EnableRectangularSelection = true;
        o.ShowSpaces = o.ShowTabs = settings.ShowWhitespace;
        o.LineHeightFactor = 1.25;
        o.HideCursorWhileTyping = false;

        var area = Editor.TextArea;
        area.SelectionBrush = ThemeManager.Brush("EditorSelection");
        area.SelectionCornerRadius = 0;
        area.SelectionBorder = null;
        area.CaretBrush = ThemeManager.Brush("EditorCaret");
        area.TextView.CurrentLineBackground = ThemeManager.Brush("EditorCurrentLine");
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.TextView.BackgroundRenderers.Add(_indentGuides);
        area.TextView.BackgroundRenderers.Add(_occurrences);
        area.TextView.BackgroundRenderers.Add(_diagnostics);
        area.TextView.BackgroundRenderers.Add(_brackets);
        area.IndentationStrategy = new SmartIndentationStrategy { IndentUnit = o.IndentationString };
        area.RightClickMovesCaret = true;
        Editor.SearchResultsBrush = ThemeManager.Brush("EditorSearchMatch");

        if (!doc.IsHuge && syntax.ScopeFor(doc.Language, doc.FilePath) is { } scope)
        {
            try
            {
                _textMate = Editor.InstallTextMate(syntax);
                _textMate.SetGrammar(scope);
            }
            catch
            {
                // Unknown grammar: plain text is fine.
            }
        }
        var foldingMode = doc.Language?.Folding ?? "braces";
        if (!doc.IsLarge && settings.ShowFolding && foldingMode != "none")
        {
            // Our own chevron gutter instead of AvaloniaEdit's +/- boxes.
            _folding = new FoldingManager(doc.Text);
            FoldingElementGenerator.TextBrush = ThemeManager.Brush("FgMuted");
            area.TextView.ElementGenerators.Add(new FoldingElementGenerator { FoldingManager = _folding });
            area.LeftMargins.Add(new ChevronFoldingMargin(_folding));
        }

        _popup = new Popup
        {
            PlacementTarget = area.TextView,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = false,
        };
        var root = new Grid();
        root.Children.Add(Editor);
        root.Children.Add(_popup);
        Content = root;

        _foldTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => UpdateFoldings());
        _diagnosticsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(1, settings.DiagnosticsDelayMs)), DispatcherPriority.Background, (_, _) =>
        {
            _diagnosticsTimer!.Stop();
            _diagnostics.HiddenLine = -1;
            RefreshDiagnostics();
        });
        _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            if (_popup.Child?.IsPointerOver == true) return;
            HidePopup();
        });

        area.TextEntering += OnTextEntering;
        area.TextEntered += OnTextEntered;
        area.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        area.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        area.Caret.PositionChanged += (_, _) =>
        {
            if (_bracketsEnabled) _brackets.Update(Doc.Text, area.Caret.Offset);
            _occurrences.Update(Doc.Text, area.Caret.Offset);
            area.TextView.InvalidateLayer(_brackets.Layer);
            _host.CaretMoved(this);
        };
        area.TextView.ScrollOffsetChanged += (_, _) => HidePopup();
        Editor.PointerHover += OnPointerHover;
        Editor.PointerHoverStopped += (_, _) => _hideTimer.Start();
        doc.Text.Changed += (_, _) =>
        {
            _editVersion++;
            var delay = _host.Settings.DiagnosticsDelayMs;
            if (delay > 0)
            {
                // Typing: hide stale marks on this line and wait for a pause before showing new ones.
                _diagnostics.HiddenLine = Editor.TextArea.Caret.Line;
                Editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
                _diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(delay);
                _diagnosticsTimer.Stop();
                _diagnosticsTimer.Start();
            }
            _foldTimer.Stop();
            _foldTimer.Start();
        };
        doc.DiagnosticsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            // While the typing-pause timer runs, its tick applies the latest diagnostics.
            if (!_diagnosticsTimer.IsEnabled) RefreshDiagnostics();
        });
        ThemeManager.Changed += OnThemeChanged;
        ApplySettings(settings);
        RefreshDiagnostics();
        UpdateFoldings();
    }

    public void ApplySettings(Settings s)
    {
        var lang = Doc.Language;
        Editor.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.EditorFont) ? "Consolas" : s.EditorFont + ", Cascadia Mono, Consolas, monospace");
        Editor.FontSize = s.FontSize;
        Editor.WordWrap = s.WordWrap;
        Editor.ShowLineNumbers = s.ShowLineNumbers;
        var o = Editor.Options;
        o.IndentationSize = lang?.IndentSize ?? s.TabSize;
        o.ConvertTabsToSpaces = lang?.UseTabs is { } tabs ? !tabs : s.InsertSpaces;
        o.ShowSpaces = o.ShowTabs = s.ShowWhitespace;
        o.HighlightCurrentLine = s.HighlightCurrentLine;
        o.AllowScrollBelowDocument = s.ScrollPastEnd;
        o.LineHeightFactor = Math.Clamp(s.LineSpacing, 1, 2.5);
        _indentGuides.Enabled = s.ShowIndentGuides;
        _indentGuides.TabSize = o.IndentationSize;
        _occurrences.Enabled = s.HighlightOccurrences;
        _bracketsEnabled = s.BracketHighlight;
        if (Editor.TextArea.IndentationStrategy is SmartIndentationStrategy smart)
            smart.IndentUnit = o.IndentationString;
        Editor.TextArea.TextView.Redraw();
    }

    private void OnThemeChanged()
    {
        if (_textMate is not null && ThemeManager.TokenTheme is { } theme) _textMate.SetTheme(theme);
        Editor.TextArea.TextView.Redraw();
    }

    public void Dispose()
    {
        ThemeManager.Changed -= OnThemeChanged;
        _foldTimer.Stop();
        _hideTimer.Stop();
        _completion?.Close();
        _insight?.Close();
        _textMate?.Dispose();
        _folding?.Clear();
    }

    public void FocusEditor() => Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Input);

    public void GoTo(int line, int column)
    {
        line = Math.Clamp(line, 1, Math.Max(1, Doc.Text.LineCount));
        var docLine = Doc.Text.GetLineByNumber(line);
        Editor.CaretOffset = docLine.Offset + Math.Clamp(column - 1, 0, docLine.Length);
        Editor.TextArea.Caret.BringCaretToView();
        Dispatcher.UIThread.Post(() => Editor.ScrollTo(line, column), DispatcherPriority.Background);
        Editor.TextArea.TextView.HighlightedLine = line;
        FocusEditor();
    }

    // ---- Folding and diagnostics --------------------------------------------

    private void UpdateFoldings()
    {
        _foldTimer.Stop();
        if (_folding is null) return;
        var version = _editVersion;
        var snapshot = Doc.Text.CreateSnapshot();
        var indentMode = Doc.Language?.Folding == "indent";
        Task.Run(() => indentMode ? BraceFolding.ComputeIndent(snapshot) : BraceFolding.Compute(snapshot)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && version == _editVersion) _folding.UpdateFoldings(t.Result, -1);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshDiagnostics()
    {
        _diagnostics.Marks.Clear();
        foreach (var d in Doc.Diagnostics)
        {
            var start = Doc.OffsetOf(d.StartLine, d.StartCol);
            var end = Doc.OffsetOf(d.EndLine, d.EndCol);
            if (end <= start)
            {
                end = start;
                while (end < Doc.Text.TextLength && char.IsLetterOrDigit(Doc.Text.GetCharAt(end))) end++;
                if (end == start) end = Math.Min(start + 1, Doc.Text.TextLength);
            }
            var unnecessary = (d.Raw["tags"] as JsonArray)?.Any(t => t?.GetValue<int>() == 1) == true;
            _diagnostics.Marks.Add((new TextSegment { StartOffset = start, EndOffset = end }, d.Severity, unnecessary && d.Severity >= 3));
        }
        Editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
    }

    private IEnumerable<LspDiagnostic> DiagnosticsAt(int offset)
    {
        var line = Doc.Text.GetLineByOffset(offset).LineNumber - 1;
        foreach (var d in Doc.Diagnostics)
        {
            if (line < d.StartLine || line > d.EndLine) continue;
            var s = Doc.OffsetOf(d.StartLine, d.StartCol);
            var e = Math.Max(Doc.OffsetOf(d.EndLine, d.EndCol), s + 1);
            if (offset >= s && offset <= e) yield return d;
        }
    }

    // ---- Popups (hover, quick doc) -------------------------------------------

    private Rect CaretRect()
    {
        var r = Editor.TextArea.Caret.CalculateCaretRectangle();
        var off = Editor.TextArea.TextView.ScrollOffset;
        return new Rect(r.X - off.X, r.Y - off.Y, Math.Max(1, r.Width), r.Height);
    }

    private void ShowPopup(Control content, Rect anchor)
    {
        _hideTimer.Stop();
        _popup.IsOpen = false;
        _popup.PlacementRect = anchor;
        _popup.Child = new Border
        {
            Background = (IBrush)Application.Current!.FindResource("PopupBg")!,
            BorderBrush = (IBrush)Application.Current!.FindResource("BorderStrong")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #60000000"),
            Child = content,
        };
        _popup.IsOpen = true;
    }

    private void HidePopup()
    {
        _hideTimer.Stop();
        _popup.IsOpen = false;
    }

    private async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(Editor.TextArea.TextView);
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is null) return;
        var offset = Doc.Text.GetOffset(pos.Value.Location);
        var anchor = new Rect(point.X, point.Y - 2, 1, Editor.TextArea.TextView.DefaultLineHeight);
        await ShowInfoAsync(offset, anchor, fromMouse: true);
    }

    private async Task ShowInfoAsync(int offset, Rect anchor, bool fromMouse = false)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var d in DiagnosticsAt(offset).Take(4))
        {
            var color = d.Severity switch { 1 => "Error", 2 => "Warning", _ => "Link" };
            panel.Children.Add(new SelectableTextBlock
            {
                Text = d.Source is null ? d.Message : $"{d.Message}  ({d.Source})",
                Foreground = ThemeManager.Brush(color),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
            });
        }

        if (Doc.Lsp is { } lsp && lsp.Has("hoverProvider") && (_host.Settings.HoverDocs || !fromMouse))
        {
            var version = _editVersion;
            try
            {
                var result = await lsp.RequestAsync("textDocument/hover", Doc.PositionParams(offset));
                if (version != _editVersion) return;
                var md = LspUtil.MarkupToMarkdown(result?["contents"]);
                if (md.Trim().Length > 0) panel.Children.Add(Markdown.Render(md));
            }
            catch
            {
                // Hover is best effort.
            }
        }
        if (panel.Children.Count > 0) ShowPopup(panel, anchor);
    }

    // ---- Typing: completion, auto-close pairs, signature help ----------------

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int WordStart(int offset)
    {
        var start = offset;
        while (start > 0 && IsIdent(Doc.Text.GetCharAt(start - 1))) start--;
        return start;
    }

    private char CharAt(int offset) => offset >= 0 && offset < Doc.Text.TextLength ? Doc.Text.GetCharAt(offset) : '\0';

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;
        var c = e.Text[0];
        var caret = Editor.CaretOffset;
        // Type over an auto-inserted closing character.
        if (c is ')' or ']' or '}' or '"' or '\'' && CharAt(caret) == c && Editor.SelectionLength == 0)
        {
            Editor.CaretOffset = caret + 1;
            e.Handled = true;
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;
        var c = e.Text[0];
        var caret = Editor.CaretOffset;
        AutoClose(c, caret);

        var lsp = Doc.Lsp;
        var completionTriggers = lsp?.TriggerCharacters("completionProvider") ?? [];
        var signatureTriggers = lsp?.TriggerCharacters("signatureHelpProvider") ?? [];

        var settings = _host.Settings;
        if (IsIdent(c))
        {
            if (_completion is null) { if (settings.AutoPopupCompletion) _ = TriggerCompletionAsync(explicitInvoke: false, null); }
            else if (_completionIncomplete) _ = TriggerCompletionAsync(explicitInvoke: false, null, refresh: true);
        }
        else
        {
            _completion?.Close();
            if (!settings.AutoPopupCompletion) { }
            else if (completionTriggers.Contains(e.Text)) _ = TriggerCompletionAsync(false, e.Text);
            else if (lsp is null && c == '.') _ = TriggerCompletionAsync(false, ".");
        }

        if (settings.ParameterHints && signatureTriggers.Contains(e.Text)) _ = SignatureHelpAsync(e.Text);
        else if (c == ')') _insight?.Close();
    }

    private void AutoClose(char c, int caret)
    {
        var next = CharAt(caret);
        var nextIsFree = next == '\0' || char.IsWhiteSpace(next) || next is ')' or ']' or '}' or ';' or ',';
        var s = _host.Settings;
        string? close = c switch
        {
            '(' when s.AutoCloseBrackets => ")",
            '[' when s.AutoCloseBrackets => "]",
            '{' when s.AutoCloseBrackets => "}",
            '"' when s.AutoCloseQuotes && !IsIdent(CharAt(caret - 2)) => "\"",
            // Single quotes are lifetimes in Rust, so only pair them elsewhere.
            '\'' when s.AutoCloseQuotes && Doc.LanguageId != "rust" && !IsIdent(CharAt(caret - 2)) => "'",
            _ => null,
        };
        if (close is null || !nextIsFree) return;
        Doc.Text.Insert(caret, close);
        Editor.CaretOffset = caret;
    }

    public async Task TriggerCompletionAsync(bool explicitInvoke, string? triggerChar, bool refresh = false)
    {
        var caret = Editor.CaretOffset;
        var start = WordStart(caret);
        var prefix = Doc.Text.GetText(start, caret - start);
        var settings = _host.Settings;
        if (!explicitInvoke && triggerChar is null && prefix.Length < Math.Max(1, settings.CompletionMinChars)) return;
        if (!explicitInvoke && triggerChar is null && !refresh && char.IsDigit(prefix[0])) return;

        _completionCts?.Cancel();
        var cts = _completionCts = new CancellationTokenSource();
        if (!explicitInvoke && !refresh && settings.CompletionDelayMs > 0)
        {
            try { await Task.Delay(settings.CompletionDelayMs, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
        var items = new List<ICompletionData>();
        var incomplete = false;
        var lsp = Doc.Lsp;

        if (lsp is not null && lsp.Has("completionProvider"))
        {
            var p = Doc.PositionParams(caret);
            p["context"] = triggerChar is null
                ? new JsonObject { ["triggerKind"] = refresh ? 3 : 1 }
                : new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = triggerChar };
            try
            {
                var result = await lsp.RequestAsync("textDocument/completion", p, cts.Token);
                var list = result as JsonArray ?? result?["items"] as JsonArray ?? new JsonArray();
                incomplete = result?["isIncomplete"]?.GetValue<bool>() == true;
                // Keep the server's ranking: earlier sortText wins ties in the popup's filter.
                var ordered = list.OfType<JsonObject>()
                    .OrderBy(i => LspUtil.Str(i["sortText"] ?? i["label"]), StringComparer.Ordinal)
                    .ToList();
                for (var i = 0; i < ordered.Count; i++)
                    items.Add(new CompletionItemData(ordered[i], lsp, Doc, caret, -i));
            }
            catch
            {
                if (cts.IsCancellationRequested) return;
            }
        }

        if (cts.IsCancellationRequested || Editor.CaretOffset < start) return;

        var snippetPrefix = Doc.Text.GetText(start, Editor.CaretOffset - start);
        if (settings.ShowSnippetsInCompletion) items.AddRange(SnippetsFor(snippetPrefix));

        if (settings.WordCompletion && lsp is null && triggerChar is null && items.Count < 200)
            items.AddRange(WordCompletions(snippetPrefix));

        if (items.Count == 0)
        {
            _completion?.Close();
            return;
        }
        _completionIncomplete = incomplete;
        ShowCompletion(items, start, explicitInvoke);
    }

    /// <summary>Set by the workspace: plugin and user snippets for this document's language.</summary>
    public Func<IEnumerable<SnippetContribution>>? SnippetSource { get; set; }

    // ---- Live templates -------------------------------------------------------------

    private SnippetSession? _snippet;

    /// <summary>The editor view hosting a text area (completion items only know the text area).</summary>
    public static EditorView? Of(AvaloniaEdit.Editing.TextArea area)
    {
        for (Visual? v = area; v is not null; v = v.GetVisualParent())
            if (v is EditorView view) return view;
        return null;
    }

    /// <summary>Replaces [start, end) with a snippet and starts an interactive template session.</summary>
    public void InsertSnippet(string body, int start, int end, bool applyCodeStyle = true, string? selection = null)
    {
        _snippet?.End();
        if (applyCodeStyle) body = CodeStyles.ApplyBraceStyle(body, CodeStyles.BracesOnNewLine(_host.Settings, Doc.LanguageId));
        var line = Doc.Text.GetLineByOffset(start);
        var lineText = Doc.Text.GetText(line);
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        var ctx = new SnippetSyntax.Context(indent, Editor.Options.IndentationString, Doc.LineEnding == "CRLF" ? "\r\n" : "\n",
            selection, Doc.FilePath, Doc.Language?.LineComment);
        var parsed = SnippetSyntax.Parse(body, ctx);
        _snippet = SnippetSession.Start(Editor.TextArea, parsed, start, end);
        if (_snippet is null) return;
        _snippet.Ended += () => _snippet = null;
        _snippet.ShowChoices = (offset, choices) => Dispatcher.UIThread.Post(() =>
        {
            _completion?.Close();
            var items = choices.Select((c, i) => (ICompletionData)new SimpleCompletionData(c, "", 12, priority: -i)).ToList();
            var fieldEnd = Editor.CaretOffset;
            ShowCompletion(items, offset, explicitInvoke: true);
            if (_completion is not null) _completion.EndOffset = fieldEnd;
        });
    }

    /// <summary>Tab after a template prefix expands it (live templates).</summary>
    private bool TryExpandTemplate()
    {
        if (Editor.SelectionLength > 0 || SnippetSource is null) return false;
        var caret = Editor.CaretOffset;
        var start = WordStart(caret);
        if (start == caret) return false;
        var prefix = Doc.Text.GetText(start, caret - start);
        var template = SnippetSource().FirstOrDefault(s => s.Prefix == prefix);
        if (template is null) return false;
        InsertSnippet(template.BodyText, start, caret);
        return true;
    }

    /// <summary>Surround With… (Ctrl+Alt+T): wraps the selection with a template that uses the selection.</summary>
    public void SurroundWith()
    {
        if (SnippetSource is null) return;
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        if (length == 0)
        {
            var line = Doc.Text.GetLineByOffset(Editor.CaretOffset);
            (start, length) = (line.Offset, line.Length);
        }
        var selection = Doc.Text.GetText(start, length).Trim();
        var templates = SnippetSource().Where(s => s.BodyText.Contains("TM_SELECTED_TEXT") || s.BodyText.Contains("$SELECTION")).ToList();
        if (templates.Count == 0) { _host.Notify("No surround templates for this language. Add one with ${TM_SELECTED_TEXT} in Settings → Live Templates."); return; }
        var menu = new ContextMenu { PlacementTarget = Editor.TextArea.TextView, Placement = PlacementMode.Bottom, PlacementRect = CaretRect() };
        foreach (var t in templates)
        {
            var item = new MenuItem { Header = $"{t.Prefix}   —   {t.Description}" };
            var tpl = t;
            item.Click += (_, _) => InsertSnippet(tpl.BodyText.Replace("$SELECTION$", "${TM_SELECTED_TEXT}"), start, start + length, selection: selection);
            menu.Items.Add(item);
        }
        menu.Open(Editor.TextArea.TextView);
    }

    private IEnumerable<ICompletionData> SnippetsFor(string prefix)
    {
        if (SnippetSource is null) yield break;
        foreach (var s in SnippetSource())
            if (prefix.Length == 0 || s.Prefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new SimpleCompletionData(s.Prefix, s.Description, 15,
                    CodeStyles.ApplyBraceStyle(s.BodyText, CodeStyles.BracesOnNewLine(_host.Settings, Doc.LanguageId)), priority: -10_000);
    }

    private IEnumerable<ICompletionData> WordCompletions(string prefix)
    {
        if (Doc.Text.TextLength > 1_000_000) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal) { prefix };
        foreach (Match m in WordRegex.Matches(Doc.Text.Text))
        {
            if (seen.Count > 400) yield break;
            if (seen.Add(m.Value) && (prefix.Length == 0 || m.Value.StartsWith(prefix[0].ToString(), StringComparison.OrdinalIgnoreCase)))
                yield return new SimpleCompletionData(m.Value, "", 1, priority: -20_000);
        }
    }

    private void ShowCompletion(List<ICompletionData> items, int start, bool explicitInvoke)
    {
        var prefix = Doc.Text.GetText(start, Editor.CaretOffset - start);
        if (_completion is null)
        {
            _completion = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = start,
                CloseWhenCaretAtBeginning = !explicitInvoke,
                CloseAutomatically = true,
                MinWidth = 420,
                MaxHeight = 360,
            };
            _completion.CompletionList.CompletionData.Clear();
            foreach (var i in items) _completion.CompletionList.CompletionData.Add(i);
            _completion.Closed += (_, _) => _completion = null;
            _completion.Show();
        }
        else
        {
            var data = _completion.CompletionList.CompletionData;
            data.Clear();
            foreach (var i in items) data.Add(i);
        }
        if (prefix.Length > 0) _completion.CompletionList.SelectItem(prefix);
        else _completion.CompletionList.SelectedItem = items[0];
    }

    private async Task SignatureHelpAsync(string? trigger)
    {
        if (Doc.Lsp is not { } lsp || !lsp.Has("signatureHelpProvider")) return;
        var p = Doc.PositionParams(Editor.CaretOffset);
        p["context"] = new JsonObject
        {
            ["triggerKind"] = trigger is null ? 1 : 2,
            ["triggerCharacter"] = trigger,
            ["isRetrigger"] = _insight is not null,
        };
        JsonNode? result;
        try { result = await lsp.RequestAsync("textDocument/signatureHelp", p); }
        catch { return; }
        var signatures = result?["signatures"] as JsonArray;
        if (signatures is null || signatures.Count == 0)
        {
            _insight?.Close();
            return;
        }
        var provider = new SignatureProvider(signatures, result?["activeSignature"]?.GetValue<int>() ?? 0, result?["activeParameter"]?.GetValue<int>());
        if (_insight is null)
        {
            _insight = new OverloadInsightWindow(Editor.TextArea) { Provider = provider };
            _insight.Closed += (_, _) => _insight = null;
            _insight.Show();
        }
        else
        {
            _insight.Provider = provider;
        }
    }

    // ---- Keymap (IntelliJ defaults) -------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (_popup.IsOpen && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)) HidePopup();

        // An active live template owns Tab / Shift+Tab / Enter / Esc (unless the completion popup is open).
        if (_snippet is { IsActive: true } session && _completion is null && !ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.Tab: session.Next(shift ? -1 : 1); e.Handled = true; return;
                case Key.Enter or Key.Escape: session.Finish(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.Tab && !ctrl && !alt && !shift && _completion is null && TryExpandTemplate())
        {
            e.Handled = true;
            return;
        }

        bool handled = true;
        switch (e.Key)
        {
            case Key.T when ctrl && alt: SurroundWith(); break;
            case Key.Space when ctrl && !shift: _ = TriggerCompletionAsync(true, null); break;
            case Key.B when ctrl && !alt: _ = GoToDefinitionAsync(Editor.CaretOffset); break;
            case Key.F12 when !ctrl && !shift: _ = GoToDefinitionAsync(Editor.CaretOffset); break;
            case Key.F7 when alt: _ = FindUsagesAsync(); break;
            case Key.F12 when ctrl: _ = FileStructureAsync(); break;
            case Key.F6 when shift: _ = RenameAsync(); break;
            case Key.L when ctrl && alt: _ = FormatAsync(); break;
            case Key.Enter when alt: _ = CodeActionsAsync(); break;
            case Key.Q when ctrl: _ = ShowInfoAsync(Editor.CaretOffset, CaretRect()); break;
            case Key.P when ctrl && !shift: _ = SignatureHelpAsync(null); break;
            case Key.D when ctrl && !shift: DuplicateLine(); break;
            case Key.Y when ctrl && !shift: DeleteLine(); break;
            case Key.Z when ctrl && shift: Editor.Redo(); break;
            case Key.OemQuestion when ctrl && !shift: ToggleLineComment(); break;
            case Key.Divide when ctrl: ToggleLineComment(); break;
            case Key.OemQuestion when ctrl && shift: ToggleBlockComment(); break;
            case Key.W when ctrl && !shift: ExtendSelection(); break;
            case Key.Up when alt && shift: MoveLines(-1); break;
            case Key.Down when alt && shift: MoveLines(1); break;
            case Key.Up when ctrl && shift: MoveLines(-1); break;
            case Key.Down when ctrl && shift: MoveLines(1); break;
            case Key.R when ctrl && !shift: Editor.SearchPanel.Open(); Editor.SearchPanel.IsReplaceMode = true; break;
            case Key.Enter when !ctrl && !alt && !shift && _completion is null && BetweenBraces(): SplitBraces(); break;
            case Key.Back when !ctrl && Editor.SelectionLength == 0 && DeletePair(): break;
            case Key.Escape when _insight is not null: _insight.Close(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    private bool BetweenBraces()
    {
        var caret = Editor.CaretOffset;
        return (CharAt(caret - 1), CharAt(caret)) is ('{', '}') or ('(', ')') or ('[', ']');
    }

    private void SplitBraces()
    {
        var caret = Editor.CaretOffset;
        var line = Doc.Text.GetLineByOffset(caret);
        var text = Doc.Text.GetText(line);
        var indent = text[..(text.Length - text.TrimStart().Length)];
        var unit = Editor.Options.IndentationString;
        var nl = Doc.LineEnding == "CRLF" ? "\r\n" : "\n";
        Doc.Text.Insert(caret, nl + indent + unit + nl + indent);
        Editor.CaretOffset = caret + nl.Length + indent.Length + unit.Length;
    }

    private bool DeletePair()
    {
        var caret = Editor.CaretOffset;
        if ((CharAt(caret - 1), CharAt(caret)) is not (('(', ')') or ('[', ']') or ('{', '}') or ('"', '"') or ('\'', '\''))) return false;
        Doc.Text.Remove(caret - 1, 2);
        return true;
    }

    private (int StartLine, int EndLine) SelectedLines()
    {
        var sel = Editor.TextArea.Selection;
        if (sel.IsEmpty) return (Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Line);
        var start = Doc.Text.GetLineByOffset(Editor.SelectionStart).LineNumber;
        var endOffset = Editor.SelectionStart + Editor.SelectionLength;
        var endLine = Doc.Text.GetLineByOffset(endOffset);
        var end = endLine.Offset == endOffset && endLine.LineNumber > start ? endLine.LineNumber - 1 : endLine.LineNumber;
        return (start, end);
    }

    private void DuplicateLine()
    {
        if (Editor.SelectionLength > 0)
        {
            var text = Editor.SelectedText;
            var end = Editor.SelectionStart + Editor.SelectionLength;
            Doc.Text.Insert(end, text);
            Editor.Select(end, text.Length);
            return;
        }
        var line = Doc.Text.GetLineByOffset(Editor.CaretOffset);
        var col = Editor.CaretOffset - line.Offset;
        var nl = Doc.LineEnding == "CRLF" ? "\r\n" : "\n";
        Doc.Text.Insert(line.EndOffset, nl + Doc.Text.GetText(line));
        var next = Doc.Text.GetLineByNumber(line.LineNumber + 1);
        Editor.CaretOffset = next.Offset + Math.Min(col, next.Length);
    }

    private void DeleteLine()
    {
        var (s, e) = SelectedLines();
        var first = Doc.Text.GetLineByNumber(s);
        var last = Doc.Text.GetLineByNumber(e);
        var start = first.Offset;
        var end = last.Offset + last.TotalLength;
        if (last.DelimiterLength == 0 && first.PreviousLine is { } prev) start = prev.EndOffset;
        Doc.Text.Remove(start, end - start);
    }

    private void MoveLines(int direction)
    {
        var (s, e) = SelectedLines();
        if ((direction < 0 && s == 1) || (direction > 0 && e == Doc.Text.LineCount)) return;
        var caretLine = Editor.TextArea.Caret.Line;
        var caretCol = Editor.TextArea.Caret.Column;
        var nl = Doc.LineEnding == "CRLF" ? "\r\n" : "\n";
        var block = Enumerable.Range(s, e - s + 1).Select(n => Doc.Text.GetText(Doc.Text.GetLineByNumber(n))).ToList();
        var other = direction < 0 ? s - 1 : e + 1;
        var otherText = Doc.Text.GetText(Doc.Text.GetLineByNumber(other));
        var from = Doc.Text.GetLineByNumber(Math.Min(s, other));
        var to = Doc.Text.GetLineByNumber(Math.Max(e, other));
        var replacement = direction < 0
            ? string.Join(nl, block.Append(otherText))
            : string.Join(nl, block.Prepend(otherText));
        using (Doc.Text.RunUpdate())
            Doc.Text.Replace(from.Offset, to.EndOffset - from.Offset, replacement);
        var newLine = Doc.Text.GetLineByNumber(caretLine + direction);
        Editor.CaretOffset = newLine.Offset + Math.Min(caretCol - 1, newLine.Length);
        if (s != e)
        {
            var a = Doc.Text.GetLineByNumber(s + direction);
            var b = Doc.Text.GetLineByNumber(e + direction);
            Editor.Select(a.Offset, b.EndOffset - a.Offset);
        }
    }

    private void ToggleLineComment()
    {
        var prefix = Doc.Language?.LineComment;
        if (string.IsNullOrEmpty(prefix)) { ToggleBlockComment(); return; }
        var (s, e) = SelectedLines();
        var lines = Enumerable.Range(s, e - s + 1).Select(Doc.Text.GetLineByNumber).ToList();
        var nonEmpty = lines.Where(l => Doc.Text.GetText(l).Trim().Length > 0).ToList();
        if (nonEmpty.Count == 0) return;
        var allCommented = nonEmpty.All(l => Doc.Text.GetText(l).TrimStart().StartsWith(prefix));
        var minIndent = nonEmpty.Min(l => { var t = Doc.Text.GetText(l); return t.Length - t.TrimStart().Length; });
        using (Doc.Text.RunUpdate())
        {
            foreach (var l in Enumerable.Reverse(nonEmpty))
            {
                var t = Doc.Text.GetText(l);
                if (allCommented)
                {
                    var i = t.IndexOf(prefix, StringComparison.Ordinal);
                    var len = prefix.Length + (i + prefix.Length < t.Length && t[i + prefix.Length] == ' ' ? 1 : 0);
                    Doc.Text.Remove(l.Offset + i, len);
                }
                else
                {
                    Doc.Text.Insert(l.Offset + minIndent, prefix + " ");
                }
            }
        }
        if (s == e && Editor.SelectionLength == 0 && lines[0].NextLine is { } next) Editor.CaretOffset = next.Offset + Math.Min(Editor.TextArea.Caret.Column - 1, next.Length);
    }

    private void ToggleBlockComment()
    {
        if (Doc.Language?.BlockComment is not [var open, var close]) return;
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        if (length == 0) return;
        var text = Editor.SelectedText;
        using (Doc.Text.RunUpdate())
        {
            if (text.StartsWith(open) && text.EndsWith(close))
                Doc.Text.Replace(start, length, text[open.Length..^close.Length]);
            else
                Doc.Text.Replace(start, length, open + text + close);
        }
    }

    private void ExtendSelection()
    {
        var caret = Editor.CaretOffset;
        if (Editor.SelectionLength == 0)
        {
            var s = WordStart(caret);
            var e = caret;
            while (e < Doc.Text.TextLength && IsIdent(Doc.Text.GetCharAt(e))) e++;
            if (e > s) { Editor.Select(s, e - s); return; }
        }
        var line = Doc.Text.GetLineByOffset(Editor.SelectionStart);
        var lineText = Doc.Text.GetText(line);
        var indent = lineText.Length - lineText.TrimStart().Length;
        var lineStart = line.Offset + indent;
        if (Editor.SelectionStart > lineStart || Editor.SelectionLength < line.EndOffset - lineStart)
            Editor.Select(lineStart, line.EndOffset - lineStart);
        else
            Editor.SelectAll();
    }

    // ---- Language server actions --------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        HidePopup();
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is null) return;
        e.Handled = true;
        var offset = Doc.Text.GetOffset(pos.Value.Location);
        Editor.CaretOffset = offset;
        _ = GoToDefinitionAsync(offset);
    }

    private static List<CodeLocation> ParseLocations(JsonNode? result)
    {
        var list = new List<CodeLocation>();
        IEnumerable<JsonNode?> items = result switch
        {
            JsonArray a => a,
            JsonObject o => new[] { o },
            _ => [],
        };
        foreach (var item in items)
        {
            var uri = LspUtil.Str(item?["uri"] ?? item?["targetUri"]);
            var range = item?["range"] ?? item?["targetSelectionRange"] ?? item?["targetRange"];
            if (uri.Length == 0) continue;
            var (line, col) = LspUtil.ReadPos(range?["start"]);
            list.Add(new CodeLocation(LspUtil.ToPath(uri), line + 1, col + 1));
        }
        return list;
    }

    private bool Require(string capability, string feature)
    {
        if (Doc.Lsp is { } lsp && lsp.Has(capability)) return true;
        _host.Notify(Doc.Lsp is null
            ? $"{feature} needs a language server for {Doc.LanguageName}. Check Settings → Plugins."
            : $"{feature} is not supported by {Doc.Lsp.Config.Name}.");
        return false;
    }

    public async Task GoToDefinitionAsync(int offset)
    {
        if (!Require("definitionProvider", "Go to declaration")) return;
        try
        {
            var locations = ParseLocations(await Doc.Lsp!.RequestAsync("textDocument/definition", Doc.PositionParams(offset)));
            if (locations.Count == 0) _host.Notify("Cannot find declaration to go to");
            else if (locations.Count == 1) await _host.OpenFileAsync(locations[0].Path, locations[0].Line, locations[0].Column);
            else _host.ShowLocations("Declarations", locations);
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }

    public async Task FindUsagesAsync()
    {
        if (!Require("referencesProvider", "Find usages")) return;
        var p = Doc.PositionParams(Editor.CaretOffset);
        p["context"] = new JsonObject { ["includeDeclaration"] = true };
        try
        {
            var locations = ParseLocations(await Doc.Lsp!.RequestAsync("textDocument/references", p));
            var start = WordStart(Editor.CaretOffset);
            var end = Editor.CaretOffset;
            while (end < Doc.Text.TextLength && IsIdent(Doc.Text.GetCharAt(end))) end++;
            _host.ShowLocations($"Usages of '{Doc.Text.GetText(start, end - start)}'", locations);
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }

    public async Task RenameAsync()
    {
        if (!Require("renameProvider", "Rename")) return;
        var caret = Editor.CaretOffset;
        var start = WordStart(caret);
        var end = caret;
        while (end < Doc.Text.TextLength && IsIdent(Doc.Text.GetCharAt(end))) end++;
        var name = await _host.PromptAsync("Rename", Doc.Text.GetText(start, end - start));
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = Doc.PositionParams(caret);
        p["newName"] = name;
        try { await _host.ApplyWorkspaceEditAsync(await Doc.Lsp!.RequestAsync("textDocument/rename", p)); }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }

    public async Task FormatAsync()
    {
        if (await _host.TryPluginFormatAsync(this)) return;
        if (!Require("documentFormattingProvider", "Reformat code")) return;
        try
        {
            var edits = await Doc.Lsp!.RequestAsync("textDocument/formatting", new JsonObject
            {
                ["textDocument"] = Doc.TextDocumentId,
                ["options"] = new JsonObject { ["tabSize"] = Editor.Options.IndentationSize, ["insertSpaces"] = Editor.Options.ConvertTabsToSpaces },
            });
            Doc.ApplyEdits(edits as JsonArray);
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }

    public async Task FileStructureAsync()
    {
        if (!Require("documentSymbolProvider", "File structure")) return;
        try
        {
            var result = await Doc.Lsp!.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = Doc.TextDocumentId }) as JsonArray;
            var symbols = new List<SymbolEntry>();
            void Walk(JsonArray? items, int depth)
            {
                foreach (var s in items ?? [])
                {
                    var range = s?["selectionRange"] ?? s?["location"]?["range"] ?? s?["range"];
                    var (line, col) = LspUtil.ReadPos(range?["start"]);
                    symbols.Add(new SymbolEntry(LspUtil.Str(s?["name"]), LspUtil.Str(s?["detail"]), s?["kind"]?.GetValue<int>() ?? 0, line + 1, col + 1, depth));
                    Walk(s?["children"] as JsonArray, depth + 1);
                }
            }
            Walk(result, 0);
            _host.ShowFileStructure(this, symbols);
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }

    public async Task CodeActionsAsync()
    {
        if (!Require("codeActionProvider", "Show context actions")) return;
        var lsp = Doc.Lsp!;
        var startLoc = Doc.Text.GetLocation(Editor.SelectionStart);
        var endLoc = Doc.Text.GetLocation(Editor.SelectionStart + Editor.SelectionLength);
        var diagnostics = new JsonArray();
        foreach (var d in DiagnosticsAt(Editor.CaretOffset)) diagnostics.Add(d.Raw.DeepClone());
        JsonArray? actions;
        try
        {
            actions = await lsp.RequestAsync("textDocument/codeAction", new JsonObject
            {
                ["textDocument"] = Doc.TextDocumentId,
                ["range"] = new JsonObject
                {
                    ["start"] = LspUtil.Pos(startLoc.Line - 1, startLoc.Column - 1),
                    ["end"] = LspUtil.Pos(endLoc.Line - 1, endLoc.Column - 1),
                },
                ["context"] = new JsonObject { ["diagnostics"] = diagnostics },
            }) as JsonArray;
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); return; }

        if (actions is null || actions.Count == 0)
        {
            _host.Notify("No context actions available here");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = Editor.TextArea.TextView,
            Placement = PlacementMode.Bottom,
            PlacementRect = CaretRect(),
        };
        foreach (var action in actions.OfType<JsonObject>())
        {
            var isFix = LspUtil.Str(action["kind"]).StartsWith("quickfix");
            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = isFix ? "" : "", Classes = { "icon" }, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse(isFix ? "#E0BB65" : "#548AF7")) },
                        new TextBlock { Text = LspUtil.Str(action["title"]) },
                    },
                },
            };
            var captured = action;
            item.Click += async (_, _) => await RunCodeActionAsync(lsp, captured);
            menu.Items.Add(item);
        }
        menu.Open(Editor.TextArea.TextView);
    }

    private async Task RunCodeActionAsync(LspClient lsp, JsonObject action)
    {
        try
        {
            // A bare Command has a string "command" field.
            if (action["command"] is JsonValue)
            {
                await lsp.RequestAsync("workspace/executeCommand", new JsonObject { ["command"] = action["command"]!.DeepClone(), ["arguments"] = action["arguments"]?.DeepClone() });
                return;
            }
            if (action["edit"] is null && action["command"] is null &&
                lsp.Capabilities["codeActionProvider"]?["resolveProvider"]?.GetValue<bool>() == true)
                action = await lsp.RequestAsync("codeAction/resolve", action.DeepClone()) as JsonObject ?? action;
            if (action["edit"] is { } edit) await _host.ApplyWorkspaceEditAsync(edit);
            if (action["command"] is JsonObject cmd)
                await lsp.RequestAsync("workspace/executeCommand", new JsonObject { ["command"] = cmd["command"]?.DeepClone(), ["arguments"] = cmd["arguments"]?.DeepClone() });
        }
        catch (Exception ex) { _host.Notify(ex.Message, true); }
    }
}

/// <summary>Parameter info popup content (Ctrl+P).</summary>
internal sealed class SignatureProvider : IOverloadProvider
{
    private readonly JsonArray _signatures;
    private readonly int? _activeParameter;
    private int _selected;

    public SignatureProvider(JsonArray signatures, int active, int? activeParameter)
    {
        _signatures = signatures;
        _selected = Math.Clamp(active, 0, signatures.Count - 1);
        _activeParameter = activeParameter;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, 0, Count - 1);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentIndexText)));
        }
    }

    public int Count => _signatures.Count;
    public string? CurrentIndexText => Count > 1 ? $"{_selected + 1} of {Count}" : null;

    public object CurrentHeader
    {
        get
        {
            var sig = _signatures[_selected];
            var label = LspUtil.Str(sig?["label"]);
            var active = sig?["activeParameter"]?.GetValue<int>() ?? _activeParameter ?? -1;
            var block = new TextBlock { FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, FontSize = 13 };
            var (start, end) = (-1, -1);
            if (sig?["parameters"] is JsonArray ps && active >= 0 && active < ps.Count)
            {
                var pl = ps[active]?["label"];
                if (pl is JsonArray offsets) (start, end) = (offsets[0]!.GetValue<int>(), offsets[1]!.GetValue<int>());
                else if (LspUtil.Str(pl) is { Length: > 0 } text && label.IndexOf(text, StringComparison.Ordinal) is var i and >= 0) (start, end) = (i, i + text.Length);
            }
            if (start < 0)
            {
                block.Text = label;
            }
            else
            {
                block.Inlines = new InlineCollection
                {
                    new Run(label[..start]),
                    new Run(label[start..end]) { FontWeight = FontWeight.Bold, Foreground = ThemeManager.Brush("Link") },
                    new Run(label[end..]),
                };
            }
            return block;
        }
    }

    public object CurrentContent
    {
        get
        {
            var doc = Markdown.ToPlain(LspUtil.MarkupToMarkdown(_signatures[_selected]?["documentation"]));
            return new TextBlock { Text = doc, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontSize = 12, Foreground = Brushes.Gray };
        }
    }
}
