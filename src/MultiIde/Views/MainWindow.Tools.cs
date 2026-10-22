using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MultiIde.Core;
using MultiIde.Editor;
using MultiIde.Protocol;

namespace MultiIde.Views;

public sealed record ResultItem(string Path, string Relative, int Line, int Column, string Preview, int Severity = 0);

/// <summary>Bottom tool windows (Run, Terminal, Problems, Find, Log) and the MCP tool window.</summary>
public partial class MainWindow
{
    private ConsoleView _terminal = null!;
    private ConsoleView _log = null!;
    private ListBox _problemsList = null!;
    private ListBox _todoList = null!;
    private ListBox _findList = null!;
    private TextBox _findBox = null!;
    private CheckBox _findCase = null!;
    private TextBlock _findTitle = null!;
    private DispatcherTimer? _problemsTimer;
    private CancellationTokenSource? _findCts;
    private readonly Dictionary<string, (string Title, Control Content, Control[] Toolbar)> _bottomTools = new();

    private void BuildToolWindows()
    {
        _terminal = new ConsoleView(interactive: true) { OpenFile = (p, l, c) => OpenFileAsync(p, l, c) };
        _log = new ConsoleView(interactive: false);

        BuildRunToolWindow();
        _bottomTools["terminal"] = ("Terminal", _terminal, new Control[] { ToolButton("", "Clear", () => _terminal.Clear()) });
        _bottomTools["log"] = ("Log", _log, new Control[] { ToolButton("", "Clear", () => _log.Clear()) });

        _todoList = ResultList();
        _bottomTools["todo"] = ("TODO", _todoList, new Control[] { ToolButton("", "Refresh", () => _ = RefreshTodoAsync()) });
        _problemsList = ResultList();
        _bottomTools["problems"] = ("Problems", _problemsList, Array.Empty<Control>());

        _findBox = new TextBox { Classes = { "mono" }, Watermark = "Text to find (Enter)", Margin = new Thickness(8, 4) };
        _findCase = new CheckBox { Content = "Match case", Margin = new Thickness(8, 0) };
        _findTitle = new TextBlock { Classes = { "muted" }, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        _findBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = RunFindInFilesAsync(); e.Handled = true; } };
        _findList = ResultList();
        var findPanel = new DockPanel();
        var findBar = new DockPanel { Children = { _findCase, _findTitle, _findBox } };
        DockPanel.SetDock(_findCase, Dock.Right);
        DockPanel.SetDock(_findTitle, Dock.Right);
        DockPanel.SetDock(findBar, Dock.Top);
        findPanel.Children.Add(findBar);
        findPanel.Children.Add(_findList);
        _bottomTools["find"] = ("Find", findPanel, Array.Empty<Control>());
    }

    private static Button ToolButton(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "flat" }, Padding = new Thickness(5, 3), Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12 } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private ListBox ResultList()
    {
        var list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<ResultItem>((item, _) =>
            {
                if (item is null) return new TextBlock();
                var color = item.Severity switch { 1 => "#F75464", 2 => "#E0BB65", 3 or 4 => "#548AF7", _ => "" };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                if (color.Length > 0)
                    grid.Children.Add(new TextBlock { Classes = { "icon" }, Text = item.Severity == 1 ? "" : item.Severity == 2 ? "" : "", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse(color)), Margin = new Thickness(0, 0, 8, 0) });
                grid.Children.Add(new TextBlock
                {
                    Text = item.Preview,
                    FontFamily = color.Length > 0 ? FontFamily.Default : (FontFamily)Application.Current!.FindResource("MonoFont")!,
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                });
                grid.Children.Add(new TextBlock { Text = $"{item.Relative}:{item.Line}", Classes = { "muted" }, FontSize = 12, Margin = new Thickness(16, 0, 4, 0), [Grid.ColumnProperty] = 2 });
                return grid;
            }),
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is ResultItem r) _ = OpenFileAsync(r.Path, r.Line, r.Column); };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is ResultItem r) { _ = OpenFileAsync(r.Path, r.Line, r.Column); e.Handled = true; } };
        return list;
    }

    public void ShowBottom(string? tool)
    {
        RememberSizes();
        _bottomTool = tool;
        if (tool is not null && _bottomTools.TryGetValue(tool, out var t))
        {
            BottomTitle.Text = t.Title;
            BottomToolbar.Children.Clear();
            foreach (var c in t.Toolbar) BottomToolbar.Children.Add(c);
            BottomContent.Content = t.Content;
            if (tool == "terminal")
            {
                _terminal.WorkingDirectory = _root ?? _terminal.WorkingDirectory;
                _terminal.StartShell();
                Dispatcher.UIThread.Post(_terminal.FocusInput, DispatcherPriority.Input);
            }
            if (tool == "problems") RefreshProblems();
            if (tool == "todo") _ = RefreshTodoAsync();
        }
        ApplyLayout();
    }

    public void AppendLog(string line) => _log?.WriteLine($"{DateTime.Now:HH:mm:ss}  {line}");

    // ---- TODO -----------------------------------------------------------------

    /// <summary>Lists TODO / FIXME / HACK / XXX comments across the project.</summary>
    private async Task RefreshTodoAsync()
    {
        if (_search is null) return;
        var hits = new List<TextHit>();
        foreach (var word in new[] { "TODO", "FIXME", "HACK", "XXX" })
            hits.AddRange(await _search.FindInFilesAsync(word, matchCase: true, limit: 2000, CancellationToken.None));
        var items = hits.Where(h => TodoColorizer.Pattern.IsMatch(h.Text))
            .DistinctBy(h => (h.Path, h.Line))
            .OrderBy(h => h.Relative).ThenBy(h => h.Line)
            .Select(h => new ResultItem(h.Path, h.Relative, h.Line, h.Column, TodoColorizer.Pattern.Match(h.Text).Value))
            .ToList();
        _todoList.ItemsSource = items;
        _bottomTools["todo"] = ($"TODO  {items.Count}", _todoList, _bottomTools["todo"].Toolbar);
        if (_bottomTool == "todo") BottomTitle.Text = _bottomTools["todo"].Title;
    }

    // ---- Problems -------------------------------------------------------------

    private void ScheduleProblemsRefresh()
    {
        _problemsTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _problemsTimer!.Stop(); RefreshProblems(); });
        _problemsTimer.Stop();
        _problemsTimer.Start();
    }

    private void RefreshProblems()
    {
        if (_problemsList is null) return;
        var items = _diagnostics.Values
            .SelectMany(d => d.Items.Select(i => new ResultItem(d.Path, Rel(d.Path), i.StartLine + 1, i.StartCol + 1,
                i.Source is null ? i.Message : $"{i.Message}  [{i.Source}]", i.Severity)))
            .Where(i => i.Severity <= 2)
            .OrderBy(i => i.Severity).ThenBy(i => i.Relative).ThenBy(i => i.Line)
            .Take(5000)
            .ToList();
        _problemsList.ItemsSource = items;
        RefreshToolbarState();
        var errors = items.Count(i => i.Severity == 1);
        _bottomTools["problems"] = ($"Problems  {errors} errors, {items.Count - errors} warnings", _problemsList, Array.Empty<Control>());
        if (_bottomTool == "problems") BottomTitle.Text = _bottomTools["problems"].Title;
        BuildTabs();
    }

    private string Rel(string path) => _root is null ? path : Path.GetRelativePath(_root, path).Replace('\\', '/');

    // ---- Find in files / usages ----------------------------------------------

    private void ShowFindInFiles()
    {
        if (_root is null) { Notify("Open a project first"); return; }
        if (_active?.Editor.SelectedText is { Length: > 0 and < 200 } sel && !sel.Contains('\n')) _findBox.Text = sel;
        ShowBottom("find");
        Dispatcher.UIThread.Post(() => { _findBox.Focus(); _findBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private async Task RunFindInFilesAsync()
    {
        if (_search is null || string.IsNullOrEmpty(_findBox.Text)) return;
        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        _findTitle.Text = "Searching…";
        try
        {
            var hits = await _search.FindInFilesAsync(_findBox.Text, _findCase.IsChecked == true, 5000, cts.Token);
            _findList.ItemsSource = hits.Select(h => new ResultItem(h.Path, h.Relative, h.Line, h.Column, h.Text)).ToList();
            _findTitle.Text = hits.Count >= 5000 ? "5000+ matches" : $"{hits.Count} matches";
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async void ShowLocations(string title, IReadOnlyList<CodeLocation> locations)
    {
        ShowBottom("find");
        _findTitle.Text = $"{title}: {locations.Count}";
        var items = await Task.Run(() => locations.GroupBy(l => l.Path).SelectMany(g =>
        {
            string[] lines;
            try { lines = File.ReadAllLines(g.Key); } catch { lines = []; }
            return g.Select(l => new ResultItem(l.Path, Rel(l.Path), l.Line, l.Column,
                l.Line - 1 < lines.Length ? lines[l.Line - 1].Trim() : ""));
        }).ToList());
        _findList.ItemsSource = items;
    }


}
