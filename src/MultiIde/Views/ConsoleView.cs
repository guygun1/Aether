using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>
/// Process output console used by the Run and Terminal tool windows.
/// Output is buffered and flushed to the UI at most every 50 ms, and the
/// buffer is capped, so chatty builds never stall the editor.
/// </summary>
public sealed class ConsoleView : UserControl
{
    private const int MaxChars = 2_000_000;

    // rustc "--> src/main.rs:3:5", gcc/clang "a.cpp:12:5", MSVC/dotnet "a.cpp(12,5)" or "a.cs(12,5)".
    private static readonly Regex FileRef = new(
        @"(?<path>(?:[A-Za-z]:)?[^\s:()""'<>|*?]+\.[A-Za-z0-9]+)(?:[:(](?<line>\d+)(?:[:,](?<col>\d+))?)",
        RegexOptions.Compiled);
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07", RegexOptions.Compiled);

    private readonly TextEditor _output;
    private readonly TextBox? _input;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;
    private readonly bool _interactive;
    private Process? _process;

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
    public bool IsRunning => _process is { HasExited: false };
    public Func<string, int, int, Task>? OpenFile { get; set; }

    public bool WordWrap
    {
        get => _output.WordWrap;
        set => _output.WordWrap = value;
    }

    public event Action? RunningChanged;

    public ConsoleView(bool interactive)
    {
        _interactive = interactive;
        _output = new TextEditor
        {
            IsReadOnly = true,
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            FontSize = 13,
            Background = ThemeManager.Brush("EditorBg"),
            Foreground = ThemeManager.Brush("EditorText"),
            ShowLineNumbers = false,
            WordWrap = IdeServices.Settings.ConsoleWordWrap,
            Padding = new Thickness(10, 6),
        };
        _output.Options.EnableHyperlinks = false;
        _output.Options.AllowScrollBelowDocument = false;
        _output.TextArea.SelectionBrush = ThemeManager.Brush("EditorSelection");
        _output.TextArea.Caret.CaretBrush = Brushes.Transparent;
        _output.DoubleTapped += OnDoubleTapped;
        ToolTip.SetTip(_output, "Double-click a file:line reference to open it");

        var dock = new DockPanel();
        if (interactive)
        {
            _input = new TextBox
            {
                Classes = { "mono" },
                Watermark = "Type a command and press Enter (runs in " + (IdePaths.IsWindows ? "cmd" : "sh") + ")",
                Margin = new Thickness(6, 4, 6, 6),
                CornerRadius = new CornerRadius(4),
            };
            _input.KeyDown += OnInputKey;
            DockPanel.SetDock(_input, Dock.Bottom);
            dock.Children.Add(_input);
        }
        dock.Children.Add(_output);
        Content = dock;

        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush());
    }

    public void FocusInput() => (_input as Control ?? _output).Focus();

    public void Clear()
    {
        lock (_pending) _pending.Clear();
        _output.Document.Text = "";
    }

    public void Write(string text)
    {
        lock (_pending) _pending.Append(Ansi.Replace(text, ""));
        Dispatcher.UIThread.Post(() => { if (!_flush.IsEnabled) _flush.Start(); });
    }

    public void WriteLine(string text) => Write(text + "\n");

    private void Flush()
    {
        string chunk;
        lock (_pending)
        {
            if (_pending.Length == 0) { _flush.Stop(); return; }
            chunk = _pending.ToString();
            _pending.Clear();
        }
        var doc = _output.Document;
        var atEnd = _output.VerticalOffset + _output.ViewportHeight >= _output.ExtentHeight - 4;
        doc.Insert(doc.TextLength, chunk.Replace("\r\n", "\n"));
        if (doc.TextLength > MaxChars) doc.Remove(0, doc.TextLength - MaxChars);
        if (atEnd) _output.ScrollToEnd();
    }

    /// <summary>Runs a command line through the shell, streaming its output.</summary>
    public void Run(string commandLine, string? cwd = null, bool clear = true, Action<int>? exited = null, IDictionary<string, string>? env = null)
    {
        Stop();
        if (clear) Clear();
        var dir = cwd ?? WorkingDirectory;
        WriteLine($"> {commandLine}");
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = ProcessUtil.ShellStartInfo(commandLine, dir);
            if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
            p.Exited += (_, _) =>
            {
                p.WaitForExit();
                var code = p.ExitCode;
                WriteLine($"\nProcess finished with exit code {code} ({sw.Elapsed.TotalSeconds:0.0}s)");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_process == p) _process = null;
                    RunningChanged?.Invoke();
                    exited?.Invoke(code);
                });
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!_interactive) p.StandardInput.Close();
            _process = p;
        }
        catch (Exception e)
        {
            WriteLine($"Failed to start: {e.Message}");
            exited?.Invoke(-1);
        }
        RunningChanged?.Invoke();
    }

    /// <summary>Starts a persistent shell for the Terminal tool window.</summary>
    public void StartShell()
    {
        if (IsRunning) return;
        Clear();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = IdePaths.IsWindows ? "cmd.exe" : "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Environment.CurrentDirectory,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (IdePaths.IsWindows) psi.ArgumentList.Add("/Q");
            psi.Environment["NO_COLOR"] = "1";
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
            p.Exited += (_, _) => Dispatcher.UIThread.Post(() => { _process = null; WriteLine("[shell exited — press Enter to restart]"); });
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (IdePaths.IsWindows) p.StandardInput.WriteLine("chcp 65001 >NUL");
            _process = p;
            WriteLine($"{(IdePaths.IsWindows ? "cmd" : "sh")} — {psi.WorkingDirectory}");
        }
        catch (Exception e)
        {
            WriteLine($"Failed to start shell: {e.Message}");
        }
    }

    public void Send(string line)
    {
        if (!IsRunning) StartShell();
        WriteLine((IdePaths.IsWindows ? "> " : "$ ") + line);
        try { _process?.StandardInput.WriteLine(line); }
        catch { /* shell died */ }
    }

    public void Stop()
    {
        if (_process is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exiting */ }
        }
        _process = null;
        RunningChanged?.Invoke();
    }

    private readonly List<string> _history = new();
    private int _historyIndex;

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (_input is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                var line = _input.Text ?? "";
                _input.Text = "";
                if (line.Length > 0) _history.Add(line);
                _historyIndex = _history.Count;
                if (line.Trim() is "cls" or "clear") Clear();
                else Send(line);
                e.Handled = true;
                break;
            case Key.Up when _history.Count > 0:
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                e.Handled = true;
                break;
            case Key.Down when _history.Count > 0:
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && string.IsNullOrEmpty(_input.SelectedText):
                // Ctrl+C with nothing selected interrupts, like a real terminal.
                if (_process is { HasExited: false }) { Stop(); WriteLine("^C"); }
                e.Handled = true;
                break;
        }
    }

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OpenFile is null) return;
        var line = _output.Document.GetLineByOffset(_output.CaretOffset);
        var text = _output.Document.GetText(line);
        foreach (Match m in FileRef.Matches(text))
        {
            var path = m.Groups["path"].Value;
            var full = Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path);
            if (!File.Exists(full)) continue;
            var ln = int.TryParse(m.Groups["line"].Value, out var l) ? l : 1;
            var col = int.TryParse(m.Groups["col"].Value, out var c) ? c : 1;
            await OpenFile(full, ln, col);
            e.Handled = true;
            return;
        }
    }
}
