using System.Text;
using System.Text.Json.Nodes;
using AvaloniaEdit.Document;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Editor;

/// <summary>An open file: its text, language, dirty state and language-server sync.</summary>
public sealed class EditorDocument
{
    /// <summary>Above this size, language servers and folding are switched off.</summary>
    public const int LargeFileChars = 2 * 1024 * 1024;
    /// <summary>Above this size, syntax highlighting is switched off too.</summary>
    public const int HugeFileChars = 8 * 1024 * 1024;

    private int _version = 1;
    private bool _hadBom;

    public string FilePath { get; private set; }
    public string Name => Path.GetFileName(FilePath);
    public TextDocument Text { get; }
    public LanguageContribution? Language { get; set; }
    public string LanguageId => Language?.Id ?? "plaintext";
    public string LanguageName => Language?.Name is { Length: > 0 } n ? n : "Plain text";
    public bool IsLarge => Text.TextLength > LargeFileChars;
    public bool IsHuge => Text.TextLength > HugeFileChars;
    public bool IsDirty => !Text.UndoStack.IsOriginalFile;
    public string LineEnding { get; private set; } = "LF";
    public LspClient? Lsp { get; private set; }
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; private set; } = Array.Empty<LspDiagnostic>();
    public string Uri => LspUtil.ToUri(FilePath);

    public event Action? DirtyChanged;
    public event Action? DiagnosticsChanged;

    private EditorDocument(string path, string text)
    {
        FilePath = path;
        Text = new TextDocument(text) { FileName = path };
        Text.UndoStack.ClearAll();
        Text.UndoStack.MarkAsOriginalFile();
        Text.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) DirtyChanged?.Invoke();
        };
        Text.Changing += OnChanging;
        Text.Changed += OnChanged;
        LineEnding = text.Contains("\r\n") ? "CRLF" : "LF";
    }

    public static async Task<EditorDocument> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return new EditorDocument(path, text) { _hadBom = hasBom };
    }

    public async Task SaveAsync(bool trimTrailingWhitespace = false, bool ensureFinalNewline = false)
    {
        if (trimTrailingWhitespace || ensureFinalNewline) Tidy(trimTrailingWhitespace, ensureFinalNewline);
        var text = Text.Text;
        await File.WriteAllTextAsync(FilePath, text, new UTF8Encoding(_hadBom));
        Text.UndoStack.MarkAsOriginalFile();
        DirtyChanged?.Invoke();
        Lsp?.Notify("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["text"] = text,
        });
    }

    /// <summary>Save-time cleanup, as one undoable edit. Skips large files.</summary>
    private void Tidy(bool trim, bool finalNewline)
    {
        if (IsLarge) return;
        Text.BeginUpdate();
        try
        {
            if (trim)
            {
                for (var n = Text.LineCount; n >= 1; n--)
                {
                    var line = Text.GetLineByNumber(n);
                    var content = Text.GetText(line);
                    var trimmed = content.TrimEnd(' ', '\t');
                    if (trimmed.Length != content.Length) Text.Remove(line.Offset + trimmed.Length, content.Length - trimmed.Length);
                }
            }
            if (finalNewline && Text.TextLength > 0 && Text.GetCharAt(Text.TextLength - 1) != '\n')
                Text.Insert(Text.TextLength, LineEnding == "CRLF" ? "\r\n" : "\n");
        }
        finally
        {
            Text.EndUpdate();
        }
    }

    /// <summary>Re-reads the file from disk when it changed outside the IDE and has no local edits.</summary>
    public async Task<bool> ReloadIfCleanAsync()
    {
        if (IsDirty || !File.Exists(FilePath)) return false;
        var fresh = await File.ReadAllTextAsync(FilePath);
        if (fresh == Text.Text) return false;
        Text.Text = fresh;
        Text.UndoStack.MarkAsOriginalFile();
        return true;
    }

    public void Renamed(string newPath)
    {
        DetachLsp();
        FilePath = newPath;
        Text.FileName = newPath;
    }

    // ---- Language server sync ------------------------------------------------

    public void AttachLsp(LspClient client)
    {
        if (Lsp == client || IsLarge) return;
        DetachLsp();
        Lsp = client;
        _version = 1;
        client.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = Uri,
                ["languageId"] = LanguageId,
                ["version"] = _version,
                ["text"] = Text.Text,
            },
        });
    }

    public void DetachLsp()
    {
        if (Lsp is null) return;
        Lsp.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } });
        Lsp = null;
        SetDiagnostics(Array.Empty<LspDiagnostic>());
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (Lsp is not { SyncKind: 2 } lsp) return;
        // Positions must be computed against the text *before* the change.
        var start = Text.GetLocation(e.Offset);
        var end = Text.GetLocation(e.Offset + e.RemovalLength);
        lsp.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = ++_version },
            ["contentChanges"] = new JsonArray(new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = LspUtil.Pos(start.Line - 1, start.Column - 1),
                    ["end"] = LspUtil.Pos(end.Line - 1, end.Column - 1),
                },
                ["text"] = e.InsertedText.Text,
            }),
        });
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (Lsp is not { SyncKind: not 2 } lsp || lsp.SyncKind == 0) return;
        lsp.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = ++_version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = Text.Text }),
        });
    }

    public void SetDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        DiagnosticsChanged?.Invoke();
    }

    // ---- Position helpers ----------------------------------------------------

    public JsonObject TextDocumentId => new() { ["uri"] = Uri };

    public JsonObject PositionParams(int offset)
    {
        var loc = Text.GetLocation(offset);
        return new JsonObject
        {
            ["textDocument"] = TextDocumentId,
            ["position"] = LspUtil.Pos(loc.Line - 1, loc.Column - 1),
        };
    }

    public int OffsetOf(int line0, int char0)
    {
        if (Text.LineCount == 0) return 0;
        var line = Math.Clamp(line0 + 1, 1, Text.LineCount);
        var docLine = Text.GetLineByNumber(line);
        return docLine.Offset + Math.Clamp(char0, 0, docLine.Length);
    }

    public int OffsetOf(JsonNode? lspPosition)
    {
        var (l, c) = LspUtil.ReadPos(lspPosition);
        return OffsetOf(l, c);
    }

    /// <summary>Applies LSP TextEdits as one undoable step.</summary>
    public void ApplyEdits(JsonArray? edits)
    {
        if (edits is null || edits.Count == 0) return;
        var resolved = edits
            .Select(e => (Start: OffsetOf(e?["range"]?["start"]), End: OffsetOf(e?["range"]?["end"]), Text: LspUtil.Str(e?["newText"])))
            .OrderByDescending(e => e.Start).ThenByDescending(e => e.End)
            .ToList();
        Text.BeginUpdate();
        try
        {
            foreach (var (start, end, text) in resolved)
                Text.Replace(start, Math.Max(0, end - start), text);
        }
        finally
        {
            Text.EndUpdate();
        }
    }
}
