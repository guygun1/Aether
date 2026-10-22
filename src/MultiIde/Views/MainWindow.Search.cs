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

public enum SearchMode { All, Files, Symbols, Actions, Recent, Structure }

public sealed record SearchItem(string Kind, string Title, string Subtitle, Func<Task> Open, int Indent = 0);

/// <summary>"Search Everywhere" popup (Double Shift) and its relatives.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _searchCts;

    public void ShowSearch(SearchMode mode) => ShowSearchPopup(mode, null);

    private void ShowStructure(EditorView view, IReadOnlyList<SymbolEntry> symbols)
    {
        var items = symbols.Select(s => new SearchItem(SymbolKindLetter(s.Kind), s.Name, s.Detail,
            () => { view.GoTo(s.Line, s.Column); return Task.CompletedTask; }, s.Depth)).ToList();
        ShowSearchPopup(SearchMode.Structure, items);
    }

    private static string SymbolKindLetter(int kind) => kind switch
    {
        5 => "C", 6 or 9 => "m", 12 => "f", 8 => "f", 7 => "p", 10 => "E", 11 => "I", 13 => "v", 14 => "K", 23 => "S", 2 or 3 => "M", 22 => "e", 26 => "T",
        _ => "·",
    };

    private void ShowSearchPopup(SearchMode initial, List<SearchItem>? fixedItems, string title = "File Structure")
    {
        var mode = initial;
        var box = new TextBox { Watermark = "Type to search…", FontSize = 14, MinHeight = 34 };
        var list = new ListBox { MaxHeight = 440, MinHeight = 60 };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        list.ItemTemplate = new FuncDataTemplate<SearchItem>((item, _) =>
        {
            if (item is null) return new TextBlock();
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                Margin = new Thickness(item.Indent * 14, 0, 0, 0),
                Children =
                {
                    new Border
                    {
                        Width = 22, Margin = new Thickness(0, 0, 8, 0),
                        Child = new TextBlock { Text = item.Kind, Classes = { "muted" }, FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                    new TextBlock { Text = item.Title, [Grid.ColumnProperty] = 1, FontSize = 13 },
                    new TextBlock { Text = item.Subtitle, [Grid.ColumnProperty] = 2, Classes = { "muted" }, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
        });

        async Task OpenSelected()
        {
            if (list.SelectedItem is not SearchItem item) return;
            _overlayCancel = null;
            CloseOverlay();
            await item.Open();
        }

        async Task Refresh()
        {
            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();
            var query = box.Text ?? "";
            try
            {
                await Task.Delay(60, cts.Token); // debounce keystrokes
                var items = await QueryAsync(mode, query, fixedItems, cts.Token);
                if (cts.IsCancellationRequested) return;
                list.ItemsSource = items;
                if (items.Count > 0) list.SelectedIndex = 0;
            }
            catch (OperationCanceledException)
            {
            }
        }

        void BuildTabs()
        {
            tabs.Children.Clear();
            if (mode == SearchMode.Structure)
            {
                tabs.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0) });
                return;
            }
            foreach (var (m, label) in new[] { (SearchMode.All, "All"), (SearchMode.Files, "Files"), (SearchMode.Symbols, "Symbols"), (SearchMode.Actions, "Actions"), (SearchMode.Recent, "Recent") })
            {
                var b = new Button { Classes = { "flat" }, Content = label, Padding = new Thickness(10, 3) };
                b.Classes.Set("active", m == mode);
                b.Click += (_, _) => { mode = m; BuildTabs(); _ = Refresh(); box.Focus(); };
                tabs.Children.Add(b);
            }
        }

        box.TextChanged += (_, _) => _ = Refresh();
        box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; break;
                case Key.Enter: _ = OpenSelected(); e.Handled = true; break;
                case Key.Tab when mode != SearchMode.Structure:
                    mode = (SearchMode)(((int)mode + 1) % 5);
                    BuildTabs();
                    _ = Refresh();
                    e.Handled = true;
                    break;
            }
        };
        list.DoubleTapped += (_, _) => _ = OpenSelected();

        BuildTabs();
        var card = Card(new StackPanel { Spacing = 8, Children = { tabs, box, list } }, 680);
        card.Padding = new Thickness(10);
        ShowOverlay(card, top: true);
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
        _ = Refresh();
    }

    private async Task<List<SearchItem>> QueryAsync(SearchMode mode, string query, List<SearchItem>? fixedItems, CancellationToken ct)
    {
        var result = new List<SearchItem>();
        if (mode == SearchMode.Structure && fixedItems is not null)
            return query.Length == 0 ? fixedItems : fixedItems.Where(i => FileSearch.Fuzzy(query, i.Title) is not null).ToList();

        if (mode == SearchMode.Recent || (mode == SearchMode.All && query.Length == 0))
        {
            result.AddRange(_recentFiles
                .Where(f => query.Length == 0 || FileSearch.Fuzzy(query, Path.GetFileName(f)) is not null)
                .Take(30)
                .Select(f => new SearchItem("↺", Path.GetFileName(f), Rel(f), () => OpenFileAsync(f))));
            if (mode == SearchMode.Recent) return result;
        }

        if ((mode is SearchMode.All or SearchMode.Files) && _search is not null && query.Length > 0)
        {
            var files = await _search.FindFilesAsync(query, mode == SearchMode.Files ? 60 : 15, ct);
            result.AddRange(files.Select(f => new SearchItem("F", f.Name, f.Relative, () => OpenFileAsync(f.Path))));
        }

        if (mode is SearchMode.All or SearchMode.Actions)
        {
            var actions = _actions
                .Select(a => (Action: a, Score: FileSearch.Fuzzy(query, a.Title)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .Take(mode == SearchMode.Actions ? 60 : (query.Length == 0 ? 0 : 6));
            result.AddRange(actions.Select(x => new SearchItem("⚡", x.Action.Title, $"{x.Action.Group}   {x.Action.Keys}",
                () => { x.Action.Run(); return Task.CompletedTask; })));
        }

        if ((mode is SearchMode.All or SearchMode.Symbols) && query.Length > 1)
        {
            foreach (var client in Lsp.Clients.Where(c => c.State == ServerState.Running && c.Has("workspaceSymbolProvider")).ToList())
            {
                try
                {
                    var symbols = await client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, ct) as JsonArray;
                    foreach (var s in (symbols ?? new JsonArray()).Take(mode == SearchMode.Symbols ? 80 : 15))
                    {
                        var uri = LspUtil.Str(s?["location"]?["uri"]);
                        var (line, col) = LspUtil.ReadPos(s?["location"]?["range"]?["start"]);
                        if (uri.Length == 0) continue;
                        var path = LspUtil.ToPath(uri);
                        var container = LspUtil.Str(s?["containerName"]);
                        result.Add(new SearchItem(SymbolKindLetter(s?["kind"]?.GetValue<int>() ?? 0), LspUtil.Str(s?["name"]),
                            $"{container}  {Rel(path)}:{line + 1}", () => OpenFileAsync(path, line + 1, col + 1)));
                    }
                }
                catch when (!ct.IsCancellationRequested)
                {
                    // One slow or failing server should not break the popup.
                }
            }
        }
        return result;
    }
}
