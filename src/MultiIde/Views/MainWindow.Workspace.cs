using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiIde.Core;
using MultiIde.Editor;
using MultiIde.Protocol;

namespace MultiIde.Views;

/// <summary>Project, documents, tabs and language-server wiring.</summary>
public partial class MainWindow : IEditorHost
{
    private string? _root;
    private FileNode? _rootNode;
    private FileSearch? _search;
    private FileSystemWatcher? _watcher;
    private readonly List<EditorView> _views = new();
    private EditorView? _active;
    private readonly List<string> _recentFiles = new();
    private readonly Dictionary<string, (string Path, List<LspDiagnostic> Items)> _diagnostics = new();
    private readonly HashSet<string> _reportedServers = new();
    private readonly ConcurrentDictionary<string, byte> _fsDirtyDirs = new();
    private readonly ConcurrentDictionary<string, byte> _fsChangedFiles = new();
    /// <summary>File events for language servers (path → 1 created, 2 changed, 3 deleted).</summary>
    private readonly ConcurrentDictionary<string, int> _fsLspEvents = new();
    private DispatcherTimer? _fsTimer;

    public override string? ProjectRoot => _root;

    // ---- Project --------------------------------------------------------------

    private void LoadProject(string path)
    {
        if (!Directory.Exists(path))
        {
            Notify($"Folder not found: {path}", true);
            Settings.RecentProjects.Remove(path);
            Settings.Save();
            return;
        }
        _root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        Settings.AddRecent(_root);
        Settings.Save();
        Lsp.Root = _root;
        _search = new FileSearch(_root);
        _terminal.WorkingDirectory = _root;

        var name = Path.GetFileName(_root);
        _rootNode = new FileNode(name, _root, true, Plugins);
        ProjectTree.ItemsSource = new[] { _rootNode };
        _rootNode.IsExpanded = true;

        Title = $"{name} – Aether";
        ProjectNameText.Text = name;
        ProjectBadgeText.Text = Initials(name);
        ProjectBadge.Background = BadgeBrush(name);

        _leftVisible = true;
        ApplyLayout();
        RefreshRunConfigs();
        StartWatcher();
        ShowEmptyEditor();
        if (Settings.RestoreSession) _ = RestoreSessionAsync();
        ProjectOptionsButton();
        _ = RefreshExternalLibrariesAsync();
        _ = RefreshGitAsync();
        UpdateStatusBar();
    }

    /// <summary>Closes this project window and returns to the Welcome window.</summary>
    public Task CloseProjectAsync()
    {
        App.ShowWelcome();
        Close();
        return Task.CompletedTask;
    }

    // ---- Session ----------------------------------------------------------------

    private void SaveSession()
    {
        if (_root is null) return;
        _projectState ??= ProjectState.Load(_root);
        _projectState.OpenFiles = _views.Where(v => !v.IsPreview || v == _active).Select(v => new ProjectState.OpenFileState
        {
            Path = Path.GetRelativePath(_root, v.Doc.FilePath),
            Line = v.Editor.TextArea.Caret.Line,
            Column = v.Editor.TextArea.Caret.Column,
        }).ToList();
        _projectState.ActiveFile = _active is null ? null : Path.GetRelativePath(_root, _active.Doc.FilePath);
        _projectState.Save(_root);
    }

    private async Task RestoreSessionAsync()
    {
        if (_root is null) return;
        var state = _projectState ??= ProjectState.Load(_root);
        foreach (var f in state.OpenFiles)
        {
            var full = Path.Combine(_root, f.Path);
            if (!File.Exists(full)) continue;
            if (await EnsureViewAsync(full) is { } view)
                view.GoTo(f.Line, f.Column);
        }
        var active = state.ActiveFile is { } a ? FindView(Path.Combine(_root, a)) : _views.LastOrDefault();
        if (active is not null) Activate(active);
        BuildTabs();
    }

    // ---- Project tree ---------------------------------------------------------

    private void WireTree()
    {
        // One click is enough: folders toggle, files open in the preview tab.
        ProjectTree.Tapped += (_, e) =>
        {
            if (e.Source is not Control source || source.DataContext is not FileNode n) return;
            for (Avalonia.Visual? v = source; v is not null && v != ProjectTree; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (v is Avalonia.Controls.Primitives.ToggleButton) return; // the expander arrow already toggled
            if (n.IsDirectory) n.IsExpanded = !n.IsExpanded;
            else if (Settings.PreviewTab) _ = OpenPreviewAsync(n.FullPath);
        };
        ProjectTree.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is FileNode { IsDirectory: false } n) { _ = OpenFileAsync(n.FullPath); e.Handled = true; }
        };
        ProjectTree.KeyDown += async (_, e) =>
        {
            if (ProjectTree.SelectedItem is not FileNode n) return;
            switch (e.Key)
            {
                case Key.Enter when !n.IsDirectory: await OpenFileAsync(n.FullPath); e.Handled = true; break;
                case Key.Enter: n.IsExpanded = !n.IsExpanded; e.Handled = true; break;
                case Key.Delete: await DeleteNodeAsync(n); e.Handled = true; break;
                case Key.F6 when e.KeyModifiers.HasFlag(KeyModifiers.Shift): await RenameNodeAsync(n); e.Handled = true; break;
                case Key.F2: await RenameNodeAsync(n); e.Handled = true; break;
            }
        };
        ProjectTree.ContextRequested += (_, e) =>
        {
            var node = (e.Source as Control)?.DataContext as FileNode ?? ProjectTree.SelectedItem as FileNode;
            if (node is null) return;
            ProjectTree.SelectedItem = node;
            BuildTreeMenu(node).Open(ProjectTree);
            e.Handled = true;
        };
    }

    private ContextMenu BuildTreeMenu(FileNode node)
    {
        var menu = new ContextMenu();
        void Add(string header, string glyph, Action run)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 13 },
            };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        var dir = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath)!;
        var newMenu = new MenuItem { Header = "New", Icon = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13 } };
        void AddNew(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            newMenu.Items.Add(item);
        }
        AddNew("File…", () => _ = NewFileAsync(dir));
        AddNew("Directory…", () => _ = NewDirectoryAsync(dir));
        if (Plugins.FileTemplates().Any()) newMenu.Items.Add(new Separator());
        AddFileTemplateItems(newMenu, dir);
        menu.Items.Add(newMenu);
        menu.Items.Add(new Separator());
        Add("Rename…", "", () => _ = RenameNodeAsync(node));
        Add("Delete", "", () => _ = DeleteNodeAsync(node));
        menu.Items.Add(new Separator());
        Add("Copy Path", "", () => Clipboard?.SetTextAsync(node.FullPath));
        Add(IdePaths.IsWindows ? "Open in Explorer" : "Reveal in File Manager", "", () => ProcessUtil.OpenInFileManager(node.FullPath));
        Add("Open in Terminal", "", () =>
        {
            ShowBottom("terminal");
            _terminal.Send(IdePaths.IsWindows ? $"cd /d \"{dir}\"" : $"cd \"{dir}\"");
        });
        return menu;
    }

    private async Task NewFileAsync(string dir)
    {
        var name = await PromptAsync("New File", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(dir, name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) { Notify($"{name} already exists", true); return; }
            await File.WriteAllTextAsync(path, "");
            await RefreshTreeDirAsync(Path.GetDirectoryName(path)!);
            await OpenFileAsync(path);
        }
        catch (Exception e) { Notify(e.Message, true); }
    }

    private async Task NewDirectoryAsync(string dir)
    {
        var name = await PromptAsync("New Directory", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            await RefreshTreeDirAsync(dir);
        }
        catch (Exception e) { Notify(e.Message, true); }
    }

    private async Task RenameNodeAsync(FileNode node)
    {
        var name = await PromptAsync("Rename", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;
        var target = Path.Combine(Path.GetDirectoryName(node.FullPath)!, name);
        try
        {
            if (node.IsDirectory) Directory.Move(node.FullPath, target);
            else File.Move(node.FullPath, target);
            foreach (var v in _views.Where(v => v.Doc.FilePath.StartsWith(node.FullPath, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                v.Doc.Renamed(target + v.Doc.FilePath[node.FullPath.Length..]);
                _ = AttachLspAsync(v.Doc);
            }
            BuildTabs();
            await RefreshTreeDirAsync(Path.GetDirectoryName(node.FullPath)!);
        }
        catch (Exception e) { Notify(e.Message, true); }
    }

    private async Task DeleteNodeAsync(FileNode node)
    {
        if (!await ConfirmAsync("Delete", $"Move \"{node.Name}\" to the {(IdePaths.IsWindows ? "Recycle Bin" : "trash")}?", "Delete")) return;
        try
        {
            foreach (var v in _views.Where(v => v.Doc.FilePath.StartsWith(node.FullPath, StringComparison.OrdinalIgnoreCase)).ToList())
                CloseView(v);
            RecycleBin.Delete(node.FullPath, node.IsDirectory);
            await RefreshTreeDirAsync(Path.GetDirectoryName(node.FullPath)!);
        }
        catch (Exception e) { Notify(e.Message, true); }
    }

    private Task RefreshTreeDirAsync(string dir)
    {
        _search?.Invalidate();
        return _rootNode?.Find(dir)?.RefreshAsync() ?? Task.CompletedTask;
    }

    // ---- File watching --------------------------------------------------------

    private static bool IsNoise(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") || path.EndsWith($"{Path.DirectorySeparatorChar}.git");

    private void StartWatcher()
    {
        _watcher?.Dispose();
        if (_root is null) return;
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler onChange = (_, e) =>
            {
                _fsLspEvents[e.FullPath] = e.ChangeType switch { WatcherChangeTypes.Created => 1, WatcherChangeTypes.Deleted => 3, _ => 2 };
                OnFsEvent(e.FullPath, e.ChangeType == WatcherChangeTypes.Changed);
            };
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (_, e) =>
            {
                _fsLspEvents[e.OldFullPath] = 3;
                _fsLspEvents[e.FullPath] = 1;
                OnFsEvent(e.OldFullPath, false);
                OnFsEvent(e.FullPath, false);
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            AppendLog($"File watcher unavailable: {e.Message}");
        }
    }

    private void OnFsEvent(string path, bool contentOnly)
    {
        if (IsNoise(path)) return;
        if (contentOnly) _fsChangedFiles[path] = 0;
        else if (Path.GetDirectoryName(path) is { } dir) _fsDirtyDirs[dir] = 0;
        Dispatcher.UIThread.Post(() =>
        {
            _fsTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => FlushFsEvents());
            _fsTimer.Stop();
            _fsTimer.Start();
        });
    }

    private async void FlushFsEvents()
    {
        _fsTimer?.Stop();
        // Forward file changes to language servers that asked for them (Cargo.toml, *.csproj, ...).
        var lspEvents = _fsLspEvents.ToList();
        _fsLspEvents.Clear();
        if (lspEvents.Count > 0 && _root is not null)
        {
            var events = lspEvents.Where(e => !IsExcluded(e.Key)).Select(e => (e.Key, e.Value)).ToList();
            foreach (var client in Lsp.Clients) client.NotifyWatchedFiles(events);
            if (_rightTool == "deps" && events.Any(e => Plugins.DependencyManagers().Any(m => Glob.ToRegex(m.Manager.Manifest).IsMatch(Path.GetFileName(e.Key)))))
                _ = RefreshDependenciesAsync();
        }
        var dirs = _fsDirtyDirs.Keys.ToList();
        var files = _fsChangedFiles.Keys.ToList();
        _fsDirtyDirs.Clear();
        _fsChangedFiles.Clear();

        if (dirs.Count > 0)
        {
            _search?.Invalidate();
            if (_rootNode is not null)
            {
                var loaded = _rootNode.LoadedDirectories().ToList();
                foreach (var node in loaded.Where(n => dirs.Any(d => string.Equals(d, n.FullPath, StringComparison.OrdinalIgnoreCase))))
                    await node.RefreshAsync();
            }
            if (_root is not null && dirs.Any(d => string.Equals(d, _root, StringComparison.OrdinalIgnoreCase) || d.Contains(".aether", StringComparison.OrdinalIgnoreCase))) RefreshRunConfigs();
        }
        foreach (var f in files)
        {
            var view = FindView(f);
            if (view is not null && await view.Doc.ReloadIfCleanAsync()) AppendLog($"Reloaded {view.Doc.Name} from disk");
        }
    }

    // ---- Documents & tabs -----------------------------------------------------

    private EditorView? FindView(string path)
    {
        var key = IdePaths.Key(path);
        return _views.FirstOrDefault(v => IdePaths.Key(v.Doc.FilePath) == key);
    }

    public async Task OpenFileAsync(string path, int? line = null, int? column = null)
    {
        var view = await EnsureViewAsync(path);
        if (view is null) return;
        if (!_suppressHistory && (view != _active || line is not null)) RecordLocation();
        view.IsPreview = false;
        Activate(view);
        if (line is not null) view.GoTo(line.Value, column ?? 1);
        else view.FocusEditor();
    }

    /// <summary>
    /// Opens a file in the single preview tab (IntelliJ "Enable preview tab"): the next
    /// preview replaces it, and editing or double-clicking keeps it open for good.
    /// </summary>
    public async Task OpenPreviewAsync(string path)
    {
        if (FindView(path) is { } existing)
        {
            Activate(existing);
            return;
        }
        var oldPreview = _views.FirstOrDefault(v => v.IsPreview && !v.Doc.IsDirty);
        var view = await EnsureViewAsync(path);
        if (view is null) return;
        view.IsPreview = true;
        if (oldPreview is not null && oldPreview != view)
        {
            _views.Remove(view);
            _views.Insert(_views.IndexOf(oldPreview), view);
            Activate(view);
            CloseView(oldPreview);
        }
        else
        {
            Activate(view);
        }
    }

    private async Task<EditorView?> EnsureViewAsync(string path)
    {
        if (FindView(path) is { } existing) return existing;
        if (!File.Exists(path))
        {
            Notify($"File not found: {path}", true);
            return null;
        }
        EditorDocument doc;
        try { doc = await EditorDocument.LoadAsync(path); }
        catch (Exception e)
        {
            Notify($"Cannot open {Path.GetFileName(path)}: {e.Message}", true);
            return null;
        }
        if (FindView(path) is { } raced) return raced;

        doc.Language = Plugins.LanguageForFile(path)?.Lang;
        var view = new EditorView(doc, this, Syntax) { SnippetSource = () => Plugins.SnippetsFor(doc.LanguageId).Concat(UserSnippets.For(doc.LanguageId)) };
        doc.DirtyChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (doc.IsDirty) view.IsPreview = false; // editing keeps a preview tab
            BuildTabs();
        });
        var insertAt = _active is null ? _views.Count : _views.IndexOf(_active) + 1;
        _views.Insert(insertAt, view);

        if (_diagnostics.TryGetValue(IdePaths.Key(path), out var known)) doc.SetDiagnostics(known.Items);
        if (doc.IsLarge) Notify($"{doc.Name} is large; code insight is turned off for this file to keep the editor fast.");
        if (IdePaths.IsLibraryPath(path)) view.Editor.IsReadOnly = true; // dependency / decompiled source
        if (!path.StartsWith(IdePaths.Decompiled, StringComparison.OrdinalIgnoreCase)) _ = AttachLspAsync(doc);
        return view;
    }

    private async Task AttachLspAsync(EditorDocument doc)
    {
        if (doc.IsLarge || _root is null || Plugins.LspFor(doc.LanguageId) is not { } cfg) return;
        var client = await Lsp.EnsureAsync(doc.LanguageId);
        if (!_views.Any(v => v.Doc == doc)) return;
        if (client is not null)
        {
            doc.AttachLsp(client);
            UpdateStatusBar();
            return;
        }
        ReportServerProblem(cfg.Config, Lsp.Get(cfg.Config.Id)?.Error);
    }

    private void ReportServerProblem(LspServerConfig config, string? error)
    {
        if (!_reportedServers.Add(config.Id)) return;
        var actions = new List<(string, Action)>();
        if (!string.IsNullOrEmpty(config.Install))
            actions.Add(("Install", () => InstallServer(config)));
        if (!string.IsNullOrEmpty(config.InstallUrl))
            actions.Add(("Instructions", () => OpenUrl(config.InstallUrl)));
        actions.Add(("Language Servers…", () => OpenSettings("Language Servers")));
        ShowBalloon($"{config.Name} is not available",
            $"{error ?? "The server could not be started."}\nCompletion, navigation and inspections need it." +
            (config.Install is { Length: > 0 } cmd ? $"\nInstall with: {cmd}" : ""),
            false, actions.ToArray());
    }

    public void InstallServer(LspServerConfig config)
    {
        if (string.IsNullOrEmpty(config.Install)) return;
        ShowBottom("run");
        RunTab($"Install {config.Name}", false).Run(config.Install, _root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), exited: async code =>
        {
            if (code != 0) { Notify($"Installing {config.Name} failed (exit code {code}). See the Run tool window.", true); return; }
            Notify($"{config.Name} installed. Starting it…");
            _reportedServers.Remove(config.Id);
            await Lsp.RestartAsync(config.Id);
            foreach (var v in _views.Where(v => config.Languages.Contains(v.Doc.LanguageId))) _ = AttachLspAsync(v.Doc);
        });
    }

    private void WireLspClient(LspClient client)
    {
        client.Diagnostics += (path, items) => Dispatcher.UIThread.Post(() =>
        {
            var key = IdePaths.Key(path);
            if (items.Count == 0) _diagnostics.Remove(key);
            else _diagnostics[key] = (path, items);
            FindView(path)?.Doc.SetDiagnostics(items);
            ScheduleProblemsRefresh();
            if (_active?.Doc is { } d && IdePaths.Key(d.FilePath) == key) UpdateStatusBar();
        });
        client.Log += line => AppendLog($"[{client.Config.Name}] {line}");
        client.ShowMessage += (type, message) => Dispatcher.UIThread.Post(() =>
        {
            if (type <= 2) Notify($"{client.Config.Name}: {message}", type == 1);
            else AppendLog($"[{client.Config.Name}] {message}");
        });
        client.ApplyEdit = edit => Dispatcher.UIThread.InvokeAsync(() => ApplyWorkspaceEditAsync(edit));
    }

    private async Task RestartLanguageServersAsync()
    {
        foreach (var v in _views) v.Doc.DetachLsp();
        await Lsp.StopAllAsync();
        _reportedServers.Clear();
        foreach (var v in _views) _ = AttachLspAsync(v.Doc);
        Notify("Language servers restarted");
    }

    private void OnPluginsChanged()
    {
        EnsurePluginCommandActions();
        RebuildKeymap();
        BuildToolbar();
        RefreshRunConfigs();
        _ = RestartLanguageServersAsync();
    }

    private void Activate(EditorView view)
    {
        _active = view;
        EditorHost.Content = view;
        EmptyEditorHost.Content = null;
        if (!_recentFiles.Contains(view.Doc.FilePath)) _recentFiles.Insert(0, view.Doc.FilePath);
        else { _recentFiles.Remove(view.Doc.FilePath); _recentFiles.Insert(0, view.Doc.FilePath); }
        BuildTabs();
        UpdateStatusBar();
        SelectInTree(view.Doc.FilePath);
    }

    /// <summary>IntelliJ-style options (⚙) for the Project tool window.</summary>
    private void ProjectOptionsButton()
    {
        if (HideLeftButton.Parent is not Panel header || header.Children.OfType<Button>().Any(b => b.Tag as string == "options")) return;
        var gear = new Button { Classes = { "flat" }, Tag = "options", Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 } };
        ToolTip.SetTip(gear, "Options");
        gear.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            void Toggle(string title, bool value, Action<bool> set, bool reload = false)
            {
                var item = new MenuItem { Header = (value ? "✓  " : "     ") + title };
                item.Click += async (_, _) => { set(!value); Settings.Save(); if (reload && _rootNode is not null) await _rootNode.RefreshAsync(); };
                menu.Items.Add(item);
            }
            Toggle("Always Select Opened File", Settings.AutoscrollFromSource, v => Settings.AutoscrollFromSource = v);
            Toggle("Open Files with Single Click", Settings.PreviewTab, v => Settings.PreviewTab = v);
            Toggle("Folders Always on Top", Settings.FoldersFirst, v => Settings.FoldersFirst = v, reload: true);
            Toggle("Sort by Type", Settings.SortByType, v => Settings.SortByType = v, reload: true);
            Toggle("Compact Middle Folders", Settings.CompactDirectories, v => Settings.CompactDirectories = v, reload: true);
            Toggle("File Nesting", Settings.FileNesting, v => Settings.FileNesting = v, reload: true);
            Toggle("Show Hidden Files", Settings.ShowHiddenFiles, v => Settings.ShowHiddenFiles = v, reload: true);
            Toggle("Show Excluded Folders", Settings.ShowExcludedFolders, v => Settings.ShowExcludedFolders = v, reload: true);
            Toggle("Show External Libraries", Settings.ShowExternalLibraries, v => { Settings.ShowExternalLibraries = v; _ = RefreshExternalLibrariesAsync(); });
            menu.Items.Add(new Separator());
            var select = new MenuItem { Header = "     Select Opened File" };
            select.Click += (_, _) => { if (_active is not null) SelectInTree(_active.Doc.FilePath, force: true); };
            menu.Items.Add(select);
            menu.ShowAt(gear);
        };
        header.Children.Insert(header.Children.IndexOf(HideLeftButton), gear);
    }

    private void SelectInTree(string path, bool force = false)
    {
        if (!force && !Settings.AutoscrollFromSource) return;
        if (_rootNode?.Find(path) is { } node) ProjectTree.SelectedItem = node;
    }

    private void CycleTab(int delta)
    {
        if (_views.Count == 0 || _active is null) return;
        var i = (_views.IndexOf(_active) + delta + _views.Count) % _views.Count;
        Activate(_views[i]);
        _views[i].FocusEditor();
    }

    public async Task CloseTabAsync(EditorView view)
    {
        if (view.Doc.IsDirty)
        {
            // IntelliJ saves automatically instead of asking.
            try { await view.Doc.SaveAsync(Settings.TrimTrailingWhitespace, Settings.EnsureFinalNewline); }
            catch (Exception e) { Notify($"Could not save {view.Doc.Name}: {e.Message}", true); return; }
        }
        CloseView(view);
    }

    private void CloseView(EditorView view)
    {
        var index = _views.IndexOf(view);
        if (index < 0) return;
        view.Doc.DetachLsp();
        view.Dispose();
        _views.RemoveAt(index);
        if (_active == view)
        {
            _active = null;
            EditorHost.Content = null;
            if (_views.Count > 0) Activate(_views[Math.Min(index, _views.Count - 1)]);
            else ShowEmptyEditor();
        }
        BuildTabs();
        UpdateStatusBar();
    }

    public async Task SaveAllAsync(bool quiet = false)
    {
        foreach (var v in _views.Where(v => v.Doc.IsDirty).ToList())
        {
            try
            {
                if (Settings.FormatOnSave && v.Doc.Lsp?.Has("documentFormattingProvider") == true) await v.FormatAsync();
                await v.Doc.SaveAsync(Settings.TrimTrailingWhitespace, Settings.EnsureFinalNewline);
            }
            catch (Exception e)
            {
                Notify($"Could not save {v.Doc.Name}: {e.Message}", true);
            }
        }
        if (!quiet) StatusMessage.Text = "All files saved";
    }

    private void BuildTabs()
    {
        TabStrip.Children.Clear();
        foreach (var view in _views)
        {
            var v = view;
            var active = v == _active;
            var lang = v.Doc.Language;
            var close = new Button
            {
                Classes = { "flat" },
                Padding = new Thickness(3),
                Margin = new Thickness(4, 0, 0, 0),
                Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9 },
                Opacity = active || v.Doc.IsDirty ? 1 : 0.55,
            };
            ToolTip.SetTip(close, "Close (Ctrl+F4)");
            close.Click += (_, _) => _ = CloseTabAsync(v);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (lang?.Icon is { Length: > 0 } badge)
                row.Children.Add(new Border
                {
                    Width = 16, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse(lang.IconColor ?? "#6F737A")),
                    Child = new TextBlock { Text = badge, FontSize = 7.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            var hasErrors = v.Doc.Diagnostics.Any(d => d.Severity == 1);
            row.Children.Add(new TextBlock
            {
                Text = v.Editor.IsReadOnly ? "🔒 " + v.Doc.Name : v.Doc.Name,
                FontStyle = v.IsPreview ? FontStyle.Italic : FontStyle.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = hasErrors ? (IBrush)Application.Current!.FindResource("Error")! : (IBrush)Application.Current!.FindResource(active ? "Fg" : "FgMuted")!,
                TextDecorations = hasErrors ? TextDecorations.Underline : null,
            });
            if (v.Doc.IsDirty) row.Children.Add(new TextBlock { Text = "●", FontSize = 9, Foreground = (IBrush)Application.Current!.FindResource("FgMuted")!, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(close);

            var tab = new Border
            {
                Padding = new Thickness(12, 0, 6, 0),
                Background = active ? (IBrush)Application.Current!.FindResource("EditorBg")! : Brushes.Transparent,
                BorderBrush = active ? (IBrush)Application.Current!.FindResource("Accent")! : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Child = row,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tab, v.Doc.FilePath);
            tab.PointerPressed += (_, e) =>
            {
                var props = e.GetCurrentPoint(tab).Properties;
                if (props.IsMiddleButtonPressed) _ = CloseTabAsync(v);
                else if (props.IsLeftButtonPressed) { Activate(v); v.FocusEditor(); }
            };
            tab.DoubleTapped += (_, _) => { v.IsPreview = false; BuildTabs(); };
            tab.ContextMenu = TabMenu(v);
            TabStrip.Children.Add(tab);
        }
    }

    private ContextMenu TabMenu(EditorView v)
    {
        var menu = new ContextMenu();
        void Add(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Close", () => _ = CloseTabAsync(v));
        Add("Close Other Tabs", async () => { foreach (var o in _views.Where(o => o != v).ToList()) await CloseTabAsync(o); });
        Add("Close All Tabs", async () => { foreach (var o in _views.ToList()) await CloseTabAsync(o); });
        menu.Items.Add(new Separator());
        Add("Copy Path", () => Clipboard?.SetTextAsync(v.Doc.FilePath));
        Add("Select in Project View", () => { ToggleLeft(true); SelectInTree(v.Doc.FilePath); });
        Add(IdePaths.IsWindows ? "Open in Explorer" : "Reveal in File Manager", () => ProcessUtil.OpenInFileManager(v.Doc.FilePath));
        return menu;
    }

    private void ShowEmptyEditor()
    {
        if (_views.Count > 0) return;
        var hints = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (title, keys) in new[]
                 {
                     ("Search Everywhere", "Double Shift"), ("Go to File", "Ctrl+Shift+N"), ("Recent Files", "Ctrl+E"),
                     ("Find in Files", "Ctrl+Shift+F"), ("Find Action", "Ctrl+Shift+A"), ("Run", "Shift+F10"),
                 })
            hints.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title, Classes = { "muted" }, FontSize = 14, Width = 170, TextAlignment = TextAlignment.Right },
                    new TextBlock { Text = keys, Foreground = (IBrush)Application.Current!.FindResource("Link")!, FontSize = 14, Width = 170 },
                },
            });
        EmptyEditorHost.Content = hints;
    }

    // ---- IEditorHost ----------------------------------------------------------

    public async Task<bool> ApplyWorkspaceEditAsync(JsonNode? edit)
    {
        if (edit is null) return false;
        var changes = new List<(string Path, JsonArray Edits)>();
        if (edit["documentChanges"] is JsonArray documentChanges)
        {
            foreach (var change in documentChanges)
            {
                switch (LspUtil.Str(change?["kind"]))
                {
                    case "create":
                        var created = LspUtil.ToPath(LspUtil.Str(change?["uri"]));
                        Directory.CreateDirectory(Path.GetDirectoryName(created)!);
                        if (!File.Exists(created)) await File.WriteAllTextAsync(created, "");
                        break;
                    case "rename":
                        var from = LspUtil.ToPath(LspUtil.Str(change?["oldUri"]));
                        var to = LspUtil.ToPath(LspUtil.Str(change?["newUri"]));
                        if (FindView(from) is { } renamed) renamed.Doc.Renamed(to);
                        File.Move(from, to);
                        break;
                    case "delete":
                        var deleted = LspUtil.ToPath(LspUtil.Str(change?["uri"]));
                        if (FindView(deleted) is { } gone) CloseView(gone);
                        if (File.Exists(deleted)) RecycleBin.Delete(deleted, false);
                        break;
                    default:
                        if (change?["edits"] is JsonArray edits)
                            changes.Add((LspUtil.ToPath(LspUtil.Str(change["textDocument"]?["uri"])), edits));
                        break;
                }
            }
        }
        else if (edit["changes"] is JsonObject byUri)
        {
            foreach (var (uri, edits) in byUri)
                if (edits is JsonArray a) changes.Add((LspUtil.ToPath(uri), a));
        }

        foreach (var (path, edits) in changes)
        {
            var view = await EnsureViewAsync(path);
            view?.Doc.ApplyEdits(edits);
        }
        BuildTabs();
        return true;
    }

    public void ShowFileStructure(EditorView view, IReadOnlyList<SymbolEntry> symbols) => ShowStructure(view, symbols);

    public void ZoomEditor(int delta) => ChangeFontSize(delta);

    public void Status(string message)
    {
        StatusMessage.Text = message;
        DispatcherTimer.RunOnce(() => { if (StatusMessage.Text == message) StatusMessage.Text = ""; }, TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// Resolves declarations that live in dependencies but have no file on disk. csharp-ls returns
    /// "csharp:/metadata/..." URIs; we fetch the decompiled source and cache it as a read-only file.
    /// </summary>
    public async Task<CodeLocation?> ResolveExternalAsync(LspClient lsp, CodeLocation location)
    {
        var uri = location.Uri!;
        if (!uri.StartsWith("csharp:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var result = await lsp.RequestAsync("csharp/metadata", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
            var source = LspUtil.Str(result?["source"]);
            if (source.Length == 0) return null;
            var assembly = Safe(LspUtil.Str(result?["assemblyName"]));
            var symbol = Safe(LspUtil.Str(result?["symbolName"]));
            var path = Path.Combine(IdePaths.Decompiled, assembly.Length > 0 ? assembly : "metadata", (symbol.Length > 0 ? symbol : "Symbol") + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "// Decompiled from " + (assembly.Length > 0 ? assembly : "metadata") + " (read-only)\n" + source);
            return location with { Path = path, Line = location.Line + 1, Uri = null };
        }
        catch
        {
            return null;
        }

        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    // Hooks used by the shared hub pages.
    protected override LspClient? RunningServer(string serverId) => Lsp.Get(serverId);
    protected override void InstallLanguageServer(LspServerConfig config) => InstallServer(config);
    protected override Task OpenInEditorAsync(string path) => OpenFileAsync(path);

    protected override async Task RestartServerAsync(LspServerConfig config)
    {
        await Lsp.RestartAsync(config.Id);
        _reportedServers.Remove(config.Id);
        foreach (var v in _views.Where(v => config.Languages.Contains(v.Doc.LanguageId))) _ = AttachLspAsync(v.Doc);
    }

    public void CaretMoved(EditorView view)
    {
        if (view == _active) UpdateStatusBar();
    }
}

/// <summary>Moves files to the OS recycle bin where supported.</summary>
internal static class RecycleBin
{
    public static void Delete(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (isDirectory)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }
        // Freedesktop / macOS trash folder fallback.
        var trash = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Trash", "files");
        Directory.CreateDirectory(trash);
        var dest = Path.Combine(trash, $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMddHHmmss}");
        if (isDirectory) Directory.Move(path, dest); else File.Move(path, dest);
    }
}
