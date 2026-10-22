using Avalonia.Controls;
using Avalonia.Input;
using MultiIde.Core;
using MultiIde.Editor;

namespace MultiIde.Views;

/// <summary>Runtime for plugin contributions: commands (external tools), formatters, file templates and keybindings.</summary>
public partial class MainWindow
{
    private Dictionary<string, string> _keymap = new(StringComparer.OrdinalIgnoreCase);

    // ---- Commands ---------------------------------------------------------------

    private Macros.Context MacroContextFor(Plugin? plugin) => MacroContext() with { PluginDir = plugin?.Directory };

    public async Task RunPluginCommandAsync(CommandContribution c, Plugin plugin)
    {
        if (c.Languages.Count > 0 && (_active is null || !c.Languages.Contains(_active.Doc.LanguageId)))
        {
            Notify($"'{c.Title}' works in {string.Join(", ", c.Languages)} files.");
            return;
        }
        if (c.SaveBefore) await SaveAllAsync(quiet: true);
        var ctx = MacroContextFor(plugin);
        var command = Macros.Expand(c.Command, ctx);
        var cwd = Macros.Expand(c.WorkingDirectory ?? "${workspaceFolder}", ctx);
        if (string.IsNullOrWhiteSpace(cwd)) cwd = _root ?? Environment.CurrentDirectory;

        if (c.Output == "run")
        {
            ShowBottom("run");
            RunTab(c.Title, false).Run(command, cwd);
            return;
        }

        var view = _active;
        string? stdin = c.Input switch
        {
            "selection" => view is null ? null : view.Editor.SelectionLength > 0 ? view.Editor.SelectedText : view.Doc.Text.Text,
            "document" => view?.Doc.Text.Text,
            _ => null,
        };
        StatusMessage.Text = $"{c.Title}…";
        (int Code, string Stdout, string Stderr) result;
        try { result = await ProcessUtil.RunCaptureAsync(command, cwd, stdin); }
        catch (Exception e) { Notify($"{c.Title}: {e.Message}", true); return; }
        StatusMessage.Text = "";

        if (result.Code != 0)
        {
            Notify($"{c.Title} failed (exit code {result.Code}):\n{Tail(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)}", true);
            return;
        }
        var output = result.Stdout;
        switch (c.Output)
        {
            case "notify":
                Notify(output.Trim().Length > 0 ? Tail(output) : $"{c.Title} finished.");
                break;
            case "insert" when view is not null:
                view.Doc.Text.Insert(view.Editor.CaretOffset, output.TrimEnd('\r', '\n'));
                break;
            case "replaceSelection" when view is not null:
                if (view.Editor.SelectionLength > 0) view.Doc.Text.Replace(view.Editor.SelectionStart, view.Editor.SelectionLength, output.TrimEnd('\r', '\n'));
                else view.Doc.Text.Replace(0, view.Doc.Text.TextLength, output);
                break;
            case "replaceDocument" when view is not null:
                ReplaceKeepingCaret(view, output);
                break;
        }
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length > 800 ? "…" + text[^800..] : text;
    }

    private static void ReplaceKeepingCaret(EditorView view, string text)
    {
        if (text == view.Doc.Text.Text) return;
        var caret = view.Editor.TextArea.Caret;
        var (line, col) = (caret.Line, caret.Column);
        view.Doc.Text.Replace(0, view.Doc.Text.TextLength, text);
        view.GoTo(Math.Min(line, view.Doc.Text.LineCount), col);
    }

    // ---- Formatters -------------------------------------------------------------

    /// <summary>Formats with a plugin formatter when one exists (and is preferred or the server can't format).</summary>
    public async Task<bool> TryPluginFormatAsync(EditorView view)
    {
        if (Plugins.FormatterFor(view.Doc.LanguageId) is not { } f) return false;
        var lspCan = view.Doc.Lsp?.Has("documentFormattingProvider") == true;
        if (lspCan && !f.Formatter.Preferred) return false;
        var ctx = MacroContextFor(f.Plugin) with { File = view.Doc.FilePath };
        var args = string.Join(' ', f.Formatter.Args.Select(a => Macros.Expand(a, ctx)).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var command = Macros.Expand(f.Formatter.Command, ctx);
        if (ProcessUtil.Resolve(command) is null)
        {
            Notify($"Formatter '{command}' is not installed (from the {f.Plugin.Manifest.Name} plugin).", true);
            return true;
        }
        try
        {
            var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync($"{command} {args}", Path.GetDirectoryName(view.Doc.FilePath), view.Doc.Text.Text);
            if (code != 0 || stdout.Length == 0) { Notify($"{f.Formatter.Name} failed:\n{Tail(stderr)}", true); return true; }
            ReplaceKeepingCaret(view, stdout);
        }
        catch (Exception e)
        {
            Notify(e.Message, true);
        }
        return true;
    }

    // ---- File templates ---------------------------------------------------------

    private void AddFileTemplateItems(MenuItem parent, string dir)
    {
        foreach (var (template, plugin) in Plugins.FileTemplates())
        {
            var t = template;
            var item = new MenuItem { Header = t.Name };
            item.Click += async (_, _) =>
            {
                var name = await PromptAsync($"New {t.Name}", "");
                if (string.IsNullOrWhiteSpace(name)) return;
                name = name.Trim();
                if (t.Extension.Length > 0 && name.EndsWith(t.Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^t.Extension.Length];
                var ctx = MacroContextFor(plugin);
                string Fill(string text) => Macros.Expand(text.Replace("${name}", name).Replace("${NAME}", name.ToUpperInvariant()), ctx);
                var path = Path.Combine(dir, name + t.Extension);
                if (File.Exists(path)) { Notify($"{Path.GetFileName(path)} already exists", true); return; }
                try
                {
                    await File.WriteAllTextAsync(path, Fill(FileTemplateContribution.Text(t.Body)));
                    foreach (var (companion, body) in t.Companions)
                    {
                        var extra = Path.Combine(dir, Fill(companion));
                        if (!File.Exists(extra)) await File.WriteAllTextAsync(extra, Fill(FileTemplateContribution.Text(body)));
                    }
                    await RefreshTreeDirAsync(dir);
                    await OpenFileAsync(path);
                }
                catch (Exception e)
                {
                    Notify(e.Message, true);
                }
            };
            parent.Items.Add(item);
        }
    }

    // ---- Keymap -----------------------------------------------------------------

    /// <summary>Canonical form of a shortcut: modifiers in Ctrl, Alt, Shift, Meta order.</summary>
    public static string NormalizeGesture(string keys)
    {
        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (keys.EndsWith("++")) parts.Add("+");
        var mods = new List<string>();
        string key = "";
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods.Add("Ctrl"); break;
                case "alt": mods.Add("Alt"); break;
                case "shift": mods.Add("Shift"); break;
                case "meta" or "cmd" or "win": mods.Add("Meta"); break;
                default: key = p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]; break;
            }
        }
        var order = new[] { "Ctrl", "Alt", "Shift", "Meta" };
        return string.Join('+', order.Where(mods.Contains).Append(key));
    }

    private static string? GestureOf(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            >= Key.A and <= Key.Z => e.Key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(e.Key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)(e.Key - Key.NumPad0)).ToString(),
            >= Key.F1 and <= Key.F24 => e.Key.ToString(),
            Key.OemQuestion or Key.Divide => "/",
            Key.OemPlus or Key.Add => "=",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemTilde => "`",
            Key.OemPipe or Key.OemBackslash => "\\",
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Space or Key.Tab or Key.Escape or Key.Back or Key.Delete
                or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert => e.Key == Key.Back ? "Backspace" : e.Key.ToString(),
            _ => null,
        };
        if (key is null) return null;
        var m = e.KeyModifiers;
        var mods = new List<string>();
        if (m.HasFlag(KeyModifiers.Control)) mods.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) mods.Add("Alt");
        if (m.HasFlag(KeyModifiers.Shift)) mods.Add("Shift");
        if (m.HasFlag(KeyModifiers.Meta)) mods.Add("Meta");
        return string.Join('+', mods.Append(key));
    }

    /// <summary>Effective shortcut(s) of an action: user override, else plugin binding, else keymap preset, else default.</summary>
    public string? KeysFor(IdeAction a)
    {
        if (Settings.Keymap.TryGetValue(a.Id, out var user)) return user.Length == 0 ? null : user;
        if (Plugins.Keybindings().LastOrDefault(k => k.Action == a.Id)?.Keys is { } plugin) return plugin;
        if (KeymapPresets.Override(Settings.KeymapPreset, a.Id) is { } preset) return preset.Length == 0 ? null : preset;
        return a.Keys;
    }

    /// <summary>Normalizes "Ctrl+B|F12" and chords like "ctrl+k ctrl+d" into canonical alternatives.</summary>
    public static IEnumerable<string> NormalizeKeys(string keys) =>
        keys.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => !k.Contains("Double", StringComparison.OrdinalIgnoreCase))
            .Select(k => string.Join(' ', k.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NormalizeGesture)));

    private void RebuildKeymap()
    {
        _keymap = new(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _actions)
            if (KeysFor(a) is { Length: > 0 } keys)
                foreach (var k in NormalizeKeys(keys))
                    _keymap.TryAdd(k, a.Id);
    }

    /// <summary>Actions that act on the editor; they only fire when the editor has focus.</summary>
    private static readonly HashSet<string> EditorActions = new()
    {
        "undo", "redo", "find", "replace", "duplicate", "deleteLine", "complete", "contextActions", "reformat", "rename",
        "findUsages", "comment", "quickDoc", "paramInfo", "gotoDeclaration", "fileStructure", "surroundWith", "expandSnippet",
    };

    private string? _pendingChord;
    private bool _replayingKey;

    /// <summary>Dispatches a key press through the keymap (including two-stroke chords). Returns true when handled.</summary>
    private bool DispatchKeymap(KeyEventArgs e)
    {
        if (_replayingKey) return false;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return false;
        if (GestureOf(e) is not { } gesture) return false;

        if (_pendingChord is { } first)
        {
            _pendingChord = null;
            StatusMessage.Text = "";
            if (_keymap.TryGetValue($"{first} {gesture}", out var chordId)) return RunKeymapAction(chordId);
            StatusMessage.Text = $"({first}, {gesture}) is not bound to an action";
            return true;
        }
        if (_keymap.TryGetValue(gesture, out var id) && RunKeymapAction(id)) return true;
        if (_keymap.Keys.Any(k => k.StartsWith(gesture + " ", StringComparison.OrdinalIgnoreCase)))
        {
            _pendingChord = gesture;
            StatusMessage.Text = $"({gesture}) was pressed. Waiting for the second key of the chord…";
            return true;
        }
        return false;
    }

    private bool RunKeymapAction(string id)
    {
        var action = _actions.First(a => a.Id == id);
        if (EditorActions.Contains(id) && _active?.IsKeyboardFocusWithin != true) return false;
        action.Run();
        return true;
    }

    private void EnsurePluginCommandActions()
    {
        _actions.RemoveAll(a => a.Id.StartsWith("command:"));
        foreach (var (cmd, plugin) in Plugins.Commands())
        {
            var c = cmd;
            var p = plugin;
            _actions.Add(new IdeAction("command:" + c.Id, c.Title, "Tools", c.Keys, () => _ = RunPluginCommandAsync(c, p)));
        }
    }
}
