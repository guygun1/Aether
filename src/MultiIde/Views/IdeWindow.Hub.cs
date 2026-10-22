using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Views;

/// <summary>
/// JetBrains-style Welcome hub (Projects, New Project, Customize, Plugins,
/// Language Servers, Learn). The same pages power the Settings dialog.
/// </summary>
public abstract partial class IdeWindow
{
    protected string _hubPage = "Projects";

    protected static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

    protected Control BuildHub(bool inSettings, string page)
    {
        var content = new ContentControl();
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0) };
        var pages = inSettings
            ? new[] { "Customize", "Keymap", "Live Templates", "Plugins", "Language Servers", "New Project", "Learn" }
            : new[] { "Projects", "Setup", "Customize", "Live Templates", "Plugins", "Language Servers", "Learn" };
        var buttons = new Dictionary<string, Button>();

        void Navigate(string p)
        {
            _hubPage = p;
            foreach (var (name, b) in buttons) b.Classes.Set("active", name == p || (p == "New Project" && name == "Projects" && !inSettings));
            content.Content = p switch
            {
                "Projects" => ProjectsPage(Navigate),
                "New Project" => NewProjectPage(() => Navigate(inSettings ? "Customize" : "Projects")),
                "Customize" => CustomizePage(),
                "Keymap" => KeymapPage(),
                "Setup" => SetupPage(() => Navigate("Projects")),
                "Live Templates" => LiveTemplatesPage(),
                "Plugins" => PluginsPage(() => Navigate("Plugins")),
                "Language Servers" => ServersPage(() => Navigate("Language Servers")),
                _ => LearnPage(),
            };
        }

        foreach (var p in pages)
        {
            var b = new Button
            {
                Classes = { "flat" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 7),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Classes = { "icon" }, FontSize = 14,
                            Text = p switch { "Projects" => "", "Customize" => "", "Keymap" => "", "Setup" => "", "Live Templates" => "", "Plugins" => "", "Language Servers" => "", "New Project" => "", _ => "" },
                        },
                        new TextBlock { Text = p == "Setup" ? "Setup & Import" : p, FontSize = 13 },
                    },
                },
            };
            var name = p;
            b.Click += (_, _) => Navigate(name);
            buttons[p] = b;
            nav.Children.Add(b);
        }

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(18, 22, 18, 22),
            Children =
            {
                new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    Child = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Aether/Assets/aether.png"))) },
                },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = inSettings ? "Settings" : "Aether", FontWeight = FontWeight.SemiBold, FontSize = 14 },
                        new TextBlock { Text = "0.1.0", Classes = { "muted" }, FontSize = 11 },
                    },
                },
            },
        };

        var side = new DockPanel { Background = Res("PanelBg") };
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        if (inSettings)
        {
            var close = new Button { Content = "Close", Classes = { "secondary" }, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left };
            close.Click += (_, _) => CloseOverlay();
            DockPanel.SetDock(close, Dock.Bottom);
            side.Children.Add(close);
        }
        side.Children.Add(nav);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), Background = Res("EditorBg") };
        grid.Children.Add(new Border { Child = side, BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 1, 0) });
        // Margin on the content (not ScrollViewer.Padding) so wrapping text measures against the visible width.
        content.Margin = new Thickness(32, 24);
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            [Grid.ColumnProperty] = 1,
        };
        grid.Children.Add(scroll);
        Navigate(pages.Contains(page) ? page : pages[0]);
        return grid;
    }

    protected static TextBlock PageTitle(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 16) };

    // ---- Projects -------------------------------------------------------------

    private Control ProjectsPage(Action<string> navigate)
    {
        var search = new TextBox { Watermark = "Search projects", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var newButton = new Button { Content = "New Project", Classes = { "primary" } };
        var openButton = new Button { Content = "Open", Classes = { "secondary" } };
        newButton.Click += (_, _) => navigate("New Project");
        openButton.Click += (_, _) => _ = PickAndOpenFolderAsync();

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
        top.Children.Add(search);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1, Children = { newButton, openButton } });

        var list = new StackPanel { Spacing = 2 };
        void Fill()
        {
            list.Children.Clear();
            var query = search.Text ?? "";
            var projects = Settings.RecentProjects.Where(p => query.Length == 0 || FileSearch.Fuzzy(query, p) is not null).ToList();
            if (projects.Count == 0)
            {
                list.Children.Add(new StackPanel
                {
                    Spacing = 8,
                    Margin = new Thickness(0, 60, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Welcome to Aether", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "Create a new project to start from scratch, or open an existing folder.", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "Rust, C++ / CMake and C# are supported out of the box.", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                });
                return;
            }
            foreach (var p in projects)
            {
                var path = p;
                var name = Path.GetFileName(path);
                var exists = Directory.Exists(path);
                var remove = new Button { Classes = { "flat" }, Padding = new Thickness(6), Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10 }, Opacity = 0.6 };
                ToolTip.SetTip(remove, "Remove from recent projects");
                remove.Click += (_, e) => { Settings.RecentProjects.Remove(path); Settings.Save(); Fill(); e.Handled = true; };
                var row = new Button
                {
                    Classes = { "flat" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 8),
                    Content = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                        Children =
                        {
                            new Border
                            {
                                Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 12, 0),
                                Background = BadgeBrush(name),
                                Child = new TextBlock { Text = Initials(name), FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                            },
                            new StackPanel
                            {
                                [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = exists ? Res("Fg") : Res("FgDisabled") },
                                    new TextBlock { Text = exists ? path : $"{path} (missing)", Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                                },
                            },
                            new ContentControl { Content = remove, [Grid.ColumnProperty] = 2 },
                        },
                    },
                };
                row.Click += (_, _) => OpenProject(path);
                list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        return new StackPanel { Children = { top, list } };
    }

    // ---- New Project ----------------------------------------------------------

    private static string DefaultProjectsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { "IdeaProjects", "RiderProjects", "Projects", "source\\repos" })
        {
            var dir = Path.Combine(home, candidate);
            if (Directory.Exists(dir)) return dir;
        }
        return Path.Combine(home, "Projects");
    }

    private Control NewProjectPage(Action back)
    {
        var templates = Plugins.ProjectTemplates().ToList();
        if (templates.Count == 0) return new TextBlock { Text = "No project templates. Enable a language plugin (Rust, C++, C#) first.", Classes = { "muted" } };

        var selected = templates[0];
        var templateList = new ListBox
        {
            Width = 280,
            ItemsSource = templates,
            SelectedIndex = 0,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ProjectTemplate>((t, _) => t is null ? new TextBlock() : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 3),
                Children =
                {
                    new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
                        Background = new SolidColorBrush(Color.Parse(t.IconColor ?? "#6F737A")),
                        Child = new TextBlock
                        {
                            Text = t.Icon ?? (t.Name.Length > 0 ? t.Name[..Math.Min(2, t.Name.Length)].ToUpperInvariant() : "?"),
                            FontWeight = FontWeight.Bold, FontSize = 11, Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = t.Name.Contains(": ") ? t.Name[(t.Name.IndexOf(": ") + 2)..] : t.Name, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = t.Category ?? (t.Name.Contains(": ") ? t.Name[..t.Name.IndexOf(':')] : ""), Classes = { "muted" }, FontSize = 11 },
                        },
                    },
                },
            }),
        };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var paramsPanel = new StackPanel { Spacing = 8 };
        var paramValues = new Dictionary<string, string>();
        var paramProblems = new Dictionary<string, string>();
        var refreshers = new Dictionary<string, Action>();
        ProjectTemplate? paramsFor = null;
        var optionsPanel = new StackPanel { Spacing = 6 };
        var chosenOptions = new List<ProjectTemplateOption>();
        var name = new TextBox { Text = "untitled" };
        var location = new TextBox { Text = DefaultProjectsDir() };
        var browse = new Button { Content = "…", Classes = { "secondary" }, Padding = new Thickness(10, 4) };
        var preview = new TextBlock { Classes = { "muted" }, FontSize = 12 };
        var status = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var create = new Button { Content = "Create", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };

        // ---- Template parameters (versions, ids …) --------------------------------------

        bool DependsOn(TemplateParameter p, string id) =>
            TemplateParameters.References(p.Default).Contains(id) ||
            TemplateParameters.References(p.Source?.Url).Contains(id) ||
            TemplateParameters.References(p.Source?.Regex).Contains(id);

        void ParamChanged(string id)
        {
            foreach (var p in selected.Parameters)
                if (p.Id != id && DependsOn(p, id) && refreshers.TryGetValue(p.Id, out var refresh)) refresh();
        }

        string DefaultOf(TemplateParameter p) => Macros.Expand(TemplateParameters.Expand(
            PluginManager.Expand(p.Default ?? "", null, null, (name.Text ?? "").Trim()), paramValues, (name.Text ?? "").Trim()), new Macros.Context(null));

        Control ParamInput(TemplateParameter p)
        {
            var updating = false;
            switch (p.Type)
            {
                case "boolean":
                {
                    var box = new CheckBox { Content = p.Label };
                    refreshers[p.Id] = () => { updating = true; box.IsChecked = DefaultOf(p) == "true"; updating = false; paramValues[p.Id] = box.IsChecked == true ? "true" : "false"; };
                    box.IsCheckedChanged += (_, _) => { if (updating) return; paramValues[p.Id] = box.IsChecked == true ? "true" : "false"; ParamChanged(p.Id); };
                    return box;
                }
                case "choice":
                case "version":
                {
                    var combo = new ComboBox { MinWidth = 260, MaxDropDownHeight = 360 };
                    var state = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
                    var load = 0;
                    void SetItems(List<string> items)
                    {
                        updating = true;
                        combo.ItemsSource = items;
                        var wanted = DefaultOf(p);
                        combo.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, items.IndexOf(wanted));
                        updating = false;
                        if (combo.SelectedItem is string s) { paramValues[p.Id] = s; paramProblems.Remove(p.Id); }
                        else { paramValues.Remove(p.Id); paramProblems[p.Id] = $"{p.Label}: no versions available"; }
                        ParamChanged(p.Id);
                    }
                    refreshers[p.Id] = async () =>
                    {
                        if (p.Type == "choice" || p.Source is null) { SetItems(p.Options); return; }
                        var mine = ++load;
                        var waiting = TemplateParameters.References(p.Source.Url).Concat(TemplateParameters.References(p.Source.Regex))
                            .Where(r => r != "name" && !paramValues.ContainsKey(r)).ToList();
                        if (waiting.Count > 0)
                        {
                            // Loads once the parameter it depends on has a value.
                            state.Text = "Waiting for " + string.Join(", ", waiting.Select(w => selected.Parameters.FirstOrDefault(x => x.Id == w)?.Label ?? w));
                            paramValues.Remove(p.Id);
                            paramProblems[p.Id] = $"{p.Label} is still loading";
                            combo.ItemsSource = null;
                            return;
                        }
                        state.Text = "Loading…";
                        state.Foreground = Res("FgMuted");
                        paramValues.Remove(p.Id);
                        paramProblems[p.Id] = $"{p.Label} is still loading";
                        combo.IsEnabled = false;
                        try
                        {
                            var items = await TemplateParameters.LoadAsync(p.Source, paramValues, (name.Text ?? "").Trim());
                            if (mine != load || paramsFor != selected) return;
                            state.Text = items.Count > 0 ? $"{items.Count} available" : "none found";
                            SetItems(items);
                        }
                        catch (Exception e)
                        {
                            if (mine != load || paramsFor != selected) return;
                            state.Text = $"Could not load: {e.Message}";
                            state.Foreground = Res("Error");
                            SetItems(p.Options);
                        }
                        finally
                        {
                            if (mine == load) combo.IsEnabled = true;
                        }
                    };
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (updating || combo.SelectedItem is not string s) return;
                        paramValues[p.Id] = s;
                        paramProblems.Remove(p.Id);
                        ParamChanged(p.Id);
                    };
                    return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { combo, state } };
                }
                default:
                {
                    var box = new TextBox { MinWidth = 260, Watermark = p.Default };
                    var edited = false;
                    var problem = new TextBlock { FontSize = 11.5, Foreground = Res("Error"), VerticalAlignment = VerticalAlignment.Center };
                    void Validate()
                    {
                        var text = box.Text ?? "";
                        string? error = text.Trim().Length == 0 ? $"{p.Label} is required"
                            : p.Pattern is { } pat && !System.Text.RegularExpressions.Regex.IsMatch(text, pat) ? p.PatternMessage ?? $"{p.Label} must match {pat}" : null;
                        problem.Text = error ?? "";
                        if (error is null) paramProblems.Remove(p.Id); else paramProblems[p.Id] = error;
                    }
                    refreshers[p.Id] = () =>
                    {
                        if (edited) return;
                        updating = true;
                        box.Text = DefaultOf(p);
                        updating = false;
                        paramValues[p.Id] = box.Text ?? "";
                        Validate();
                    };
                    box.TextChanged += (_, _) =>
                    {
                        if (updating) return;
                        edited = true;
                        paramValues[p.Id] = box.Text ?? "";
                        Validate();
                        ParamChanged(p.Id);
                    };
                    return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, problem } };
                }
            }
        }

        void BuildParams()
        {
            if (paramsFor == selected) return;
            paramsFor = selected;
            paramsPanel.Children.Clear();
            paramValues.Clear();
            paramProblems.Clear();
            refreshers.Clear();
            if (selected.Parameters.Count == 0) return;
            paramsPanel.Children.Add(new TextBlock { Text = "Settings", FontWeight = FontWeight.SemiBold });
            foreach (var p in selected.Parameters)
            {
                var input = ParamInput(p);
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("170,*"),
                    Children =
                    {
                        new TextBlock { Text = p.Type == "boolean" ? "" : p.Label + ":", VerticalAlignment = VerticalAlignment.Center },
                        new ContentControl { Content = input, [Grid.ColumnProperty] = 1 },
                    },
                };
                row.IsVisible = !p.Hidden;
                paramsPanel.Children.Add(row);
                if (p.Description is { Length: > 0 } d && !p.Hidden)
                    paramsPanel.Children.Add(new TextBlock { Text = d, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(170, -4, 0, 0) });
            }
            // In declaration order, so a parameter's dependencies get their values first.
            foreach (var p in selected.Parameters) refreshers[p.Id]();
        }

        void Update()
        {
            selected = templates[Math.Max(0, templateList.SelectedIndex)];
            description.Text = selected.Description;
            BuildParams();
            // Opt-in extras other plugins offer for this template (e.g. a graphics setup).
            optionsPanel.Children.Clear();
            chosenOptions.Clear();
            foreach (var option in Plugins.TemplateOptions(selected.Id))
            {
                var o = option;
                var box = new CheckBox { Content = o.Label };
                box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) chosenOptions.Add(o); else chosenOptions.Remove(o); };
                optionsPanel.Children.Add(new StackPanel { Children = { box, new TextBlock { Text = o.Description, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0) } } });
            }
            if (optionsPanel.Children.Count > 0) optionsPanel.Children.Insert(0, new TextBlock { Text = "Options", FontWeight = FontWeight.SemiBold });
            preview.Text = $"Project will be created in: {Path.Combine(location.Text ?? "", name.Text ?? "")}";
        }
        templateList.SelectionChanged += (_, _) => Update();
        name.TextChanged += (_, _) => { Update(); ParamChanged("name"); };
        location.TextChanged += (_, _) => Update();
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Project location" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } p) location.Text = p;
        };
        cancel.Click += (_, _) => back();
        create.Click += async (_, _) =>
        {
            if (paramProblems.Count > 0)
            {
                status.Foreground = Res("Error");
                status.Text = string.Join("\n", paramProblems.Values);
                return;
            }
            create.IsEnabled = false;
            status.Foreground = Res("FgMuted");
            status.Text = "Creating…";
            var error = await CreateProjectAsync(selected, (name.Text ?? "").Trim(), (location.Text ?? "").Trim(), chosenOptions.ToList(), new Dictionary<string, string>(paramValues));
            create.IsEnabled = true;
            if (error is null) { if (OverlayOpen) { _overlayCancel = null; CloseOverlay(); } }
            else { status.Foreground = Res("Error"); status.Text = error; }
        };
        Update();

        static Control Field(string label, Control input) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, new ContentControl { Content = input, [Grid.ColumnProperty] = 1 } },
        };

        var locationRow = new DockPanel { Children = { browse, location } };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 0, 0, 0);

        var form = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24, 0, 0, 0),
            Children =
            {
                description,
                paramsPanel,
                optionsPanel,
                Field("Name:", name),
                Field("Location:", locationRow),
                preview,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { create, cancel } },
                status,
            },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(new Border { Child = templateList, BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 8, 0) });
        body.Children.Add(new ContentControl { Content = form, [Grid.ColumnProperty] = 1 });
        return new StackPanel { Children = { PageTitle("New Project"), body } };
    }

    /// <summary>Creates a project from a template; returns an error message or null on success.</summary>
    private async Task<string?> CreateProjectAsync(ProjectTemplate template, string name, string location, List<ProjectTemplateOption>? options = null, IReadOnlyDictionary<string, string>? values = null)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Enter a valid project name.";
        if (location.Length == 0) return "Choose a location.";
        var dir = Path.Combine(location, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return $"{dir} already exists and is not empty.";
        values ??= new Dictionary<string, string>();
        // Template answers (${param.mc}, ${name|id} …) first, then the usual macros.
        string X(string text) => PluginManager.Expand(TemplateParameters.Expand(text, values, name), dir, null, name);
        try
        {
            Directory.CreateDirectory(location);
            if (!string.IsNullOrWhiteSpace(template.Command))
            {
                var command = X(template.Command);
                var psi = ProcessUtil.ShellStartInfo(command, location);
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                    return $"`{command}` failed (exit code {p.ExitCode}):\n{(await stderr + await stdout).Trim()}";
            }
            Directory.CreateDirectory(dir);
            foreach (var (relative, content) in template.Files)
            {
                var text = content switch
                {
                    JsonArray lines => string.Join("\n", lines.Select(l => l?.GetValue<string>() ?? "")),
                    JsonValue v => v.GetValue<string>(),
                    _ => "",
                };
                var file = Path.Combine(dir, X(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, X(text) + "\n");
            }
            foreach (var (relative, props) in template.Properties)
                TemplateParameters.SetProperties(Path.Combine(dir, X(relative)), props.ToDictionary(kv => kv.Key, kv => X(kv.Value)));
            foreach (var relative in template.Delete)
            {
                var path = Path.Combine(dir, X(relative));
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e)
        {
            return e.Message;
        }
        // Opt-in template options (files first, then their commands, e.g. "cargo add winit").
        foreach (var option in options ?? new())
        {
            try
            {
                foreach (var (relative, content) in option.Files)
                {
                    var file = Path.Combine(dir, X(relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, X(FileTemplateContribution.Text(content)) + Environment.NewLine);
                }
                foreach (var command in option.Commands)
                {
                    var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync(X(command), dir);
                    if (code != 0) Notify($"{option.Label}: '{command}' failed:{Environment.NewLine}{(stderr + stdout).Trim()}", true);
                }
            }
            catch (Exception e)
            {
                Notify($"{option.Label}: {e.Message}", true);
            }
        }

        // Follow-up commands of the template itself (e.g. "gradle wrapper"); a failure is reported, the project stays.
        foreach (var command in template.Commands)
        {
            try
            {
                var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync(X(command), dir);
                if (code != 0) Notify($"'{X(command)}' failed:{Environment.NewLine}{(stderr + stdout).Trim()}", true);
            }
            catch (Exception e)
            {
                Notify($"'{X(command)}': {e.Message}", true);
            }
        }

        // Code style files matching the chosen preset (kept if the template brought its own).
        foreach (var (file, content) in CodeStyles.ProjectFiles(Settings))
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) try { await File.WriteAllTextAsync(path, content); } catch { /* optional */ }
        }
        var main = new[] { template.Open is null ? null : X(template.Open), "src/main.rs", "src/main.cpp", "main.cpp", "Program.cs" }
            .OfType<string>().Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        OpenProject(dir, main);
        return null;
    }

    // ---- Plugins --------------------------------------------------------------

    private Control PluginsPage(Action reload)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(PageTitle("Plugins"));

        var install = new Button { Content = "Install from Disk…", Classes = { "secondary" } };
        install.Click += async (_, _) => { await InstallPluginFromDiskAsync(); reload(); };
        var create = new Button { Content = "Create Plugin…", Classes = { "secondary" } };
        create.Click += (_, _) => _ = CreatePluginAsync();
        var folder = new Button { Content = "Open Plugins Folder", Classes = { "secondary" } };
        folder.Click += (_, _) => { Directory.CreateDirectory(IdePaths.UserPlugins); ProcessUtil.OpenInFileManager(IdePaths.UserPlugins); };
        var refresh = new Button { Content = "Reload", Classes = { "secondary" } };
        refresh.Click += (_, _) => { Plugins.Reload(); reload(); };
        var addLanguage = new Button { Content = "Add Language Support…", Classes = { "primary" } };
        addLanguage.Click += (_, _) => ShowLanguageWizard(reload);
        var convert = new Button { Content = "Convert IntelliJ Plugin…", Classes = { "secondary" } };
        convert.Click += async (_, _) => { await ConvertIntelliJPluginAsync(); reload(); };
        panel.Children.Add(new WrapPanel { Children = { addLanguage, convert, install, create, folder, refresh } });
        foreach (var b in new Control[] { addLanguage, convert, install, create, folder, refresh }) b.Margin = new Thickness(0, 0, 8, 8);
        panel.Children.Add(new TextBlock
        {
            Text = "Plugins are folders with a plugin.json. They can add languages, language servers (LSP), themes, commands, formatters, file templates, snippets, run configurations, project templates and settings.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "muted" },
            Margin = new Thickness(0, 4, 0, 8),
        });

        foreach (var plugin in Plugins.All.OrderBy(p => p.Manifest.Name))
        {
            var p = plugin;
            var c = p.Manifest.Contributes;
            var chips = new WrapPanel();
            void Chip(string text) => chips.Children.Add(new Border
            {
                Background = Res("Hover"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 6, 4),
                Child = new TextBlock { Text = text, FontSize = 11 },
            });
            foreach (var l in c.Languages) Chip(l.Name);
            foreach (var s in c.LspServers) Chip($"LSP: {s.Name}");
            if (c.Snippets.Count > 0) Chip($"{c.Snippets.Count} snippets");
            if (c.RunConfigurations.Count > 0) Chip($"{c.RunConfigurations.Count} run configs");
            if (c.ProjectTemplates.Count > 0) Chip($"{c.ProjectTemplates.Count} templates");

            var toggle = new ToggleSwitch { IsChecked = p.Enabled, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Top };
            toggle.IsCheckedChanged += (_, _) => Plugins.SetEnabled(p, toggle.IsChecked == true);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            if (!p.Bundled)
            {
                var uninstall = new Button { Content = "Uninstall", Classes = { "secondary" }, Padding = new Thickness(8, 2), FontSize = 12 };
                uninstall.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync("Uninstall plugin", $"Move {p.Manifest.Name} to the recycle bin?", "Uninstall")) return;
                    try { RecycleBin.Delete(p.Directory, true); Plugins.Reload(); reload(); }
                    catch (Exception e) { Notify(e.Message, true); }
                };
                right.Children.Add(uninstall);
            }
            right.Children.Add(toggle);

            panel.Children.Add(new Border
            {
                Background = Res("PanelBg"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = $"{p.Manifest.Name}   {p.Manifest.Version}{(p.Bundled ? "   · bundled" : "")}", FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = p.Manifest.Description, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 12 },
                                chips,
                            },
                        },
                        new ContentControl { Content = right, [Grid.ColumnProperty] = 1, Margin = new Thickness(12, 0, 0, 0) },
                    },
                },
            });
        }
        return panel;
    }

    protected async Task InstallPluginFromDiskAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a plugin folder (contains plugin.json)" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;
        try
        {
            var id = PluginManager.Install(dir);
            Plugins.Reload();
            Notify($"Plugin '{id}' installed.");
        }
        catch (Exception e)
        {
            Notify(e.Message, true);
        }
    }

    protected async Task CreatePluginAsync()
    {
        var id = await PromptAsync("New plugin id (folder name)", "my-language");
        if (string.IsNullOrWhiteSpace(id)) return;
        var manifest = PluginManager.CreateTemplate(id.Trim());
        Plugins.Reload();
        await OpenInEditorAsync(manifest);
        Notify($"Created {manifest}. Edit it, then press Reload in Plugins.");
    }

    // ---- Language servers -----------------------------------------------------

    private Control ServersPage(Action reload)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(PageTitle("Language Servers"));
        panel.Children.Add(new TextBlock
        {
            Text = "Code completion, navigation, inspections and refactorings come from language servers. Install the ones you need; they start automatically when you open a matching file.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "muted" },
        });

        foreach (var (config, plugin) in Plugins.AllLspServers())
        {
            var cfg = config;
            var command = PluginManager.Expand(cfg.Command, ProjectRoot, plugin.Directory);
            var resolved = cfg.Requires is { } req && !File.Exists(PluginManager.Expand(req, ProjectRoot, plugin.Directory)) ? null : ProcessUtil.Resolve(command);
            var client = RunningServer(cfg.Id);
            var state = client?.State switch
            {
                ServerState.Running => ("Running", "#5FB865"),
                ServerState.Starting => ("Starting", "#E0BB65"),
                ServerState.Failed => ("Failed", "#F75464"),
                _ => resolved is null ? ("Not installed", "#F75464") : ("Installed", "#868A91"),
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (!string.IsNullOrEmpty(cfg.Install) || cfg.Download?.UrlForThisOs() is not null)
            {
                var install = new Button { Content = resolved is null ? "Install" : "Reinstall", Classes = { resolved is null ? "primary" : "secondary" }, Padding = new Thickness(10, 3) };
                install.Click += (_, _) =>
                {
                    if (OverlayOpen) { _overlayCancel = null; CloseOverlay(); }
                    InstallLanguageServer(cfg);
                };
                buttons.Children.Add(install);
            }
            if (client is not null)
            {
                var restart = new Button { Content = "Restart", Classes = { "secondary" }, Padding = new Thickness(10, 3) };
                restart.Click += async (_, _) =>
                {
                    await RestartServerAsync(cfg);
                    reload();
                };
                buttons.Children.Add(restart);
            }
            if (!string.IsNullOrEmpty(cfg.InstallUrl))
            {
                var docs = new Button { Content = "Docs", Classes = { "secondary" }, Padding = new Thickness(10, 3) };
                docs.Click += (_, _) => OpenUrl(cfg.InstallUrl!);
                buttons.Children.Add(docs);
            }

            panel.Children.Add(new Border
            {
                Background = Res("PanelBg"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal, Spacing = 8,
                                    Children =
                                    {
                                        new TextBlock { Text = cfg.Name, FontWeight = FontWeight.SemiBold },
                                        new TextBlock { Text = "● " + state.Item1, Foreground = new SolidColorBrush(Color.Parse(state.Item2)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                                    },
                                },
                                new TextBlock { Text = $"{string.Join(", ", cfg.Languages)}  ·  {resolved ?? command}", Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                                new SelectableTextBlock { Text = client?.Error ?? (cfg.Install is null ? "" : $"Install: {cfg.Install}"), FontSize = 12, Foreground = Res(client?.Error is null ? "FgMuted" : "Error"), TextWrapping = TextWrapping.Wrap },
                            },
                        },
                        new ContentControl { Content = buttons, [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
        }
        return panel;
    }

    /// <summary>Installs a language server from the Welcome hub.</summary>
    protected async void RunInstallWithoutProject(LspServerConfig cfg)
    {
        await InstallServerAsync(cfg);
        RefreshHub();
    }

    /// <summary>
    /// Installs a plugin's language server: runs its install command when the tool that command
    /// needs exists (e.g. rustup), otherwise downloads it (the plugin's "download"). Afterwards it
    /// checks the server's prerequisite (e.g. cargo for rust-analyzer) and offers to install that too.
    /// </summary>
    protected async Task<bool> InstallServerAsync(LspServerConfig cfg)
    {
        var plugin = Plugins.AllLspServers().FirstOrDefault(x => x.Config == cfg).Plugin;
        var install = cfg.Install is { Length: > 0 } i ? PluginManager.Expand(i, ProjectRoot, plugin?.Directory) : null;
        var program = install?.Split(' ', 2)[0];
        var canRunCommand = install is not null && ProcessUtil.Resolve(program!) is not null;
        var ok = false;
        try
        {
            if (canRunCommand)
            {
                Notify($"Installing {cfg.Name}: {install}");
                var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync(install!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var text = (stderr + stdout).Trim();
                ok = code == 0;
                if (!ok) Notify($"Installing {cfg.Name} failed (exit code {code}):{Environment.NewLine}{(text.Length > 600 ? text[^600..] : text)}", true);
            }
            else if (cfg.Download?.UrlForThisOs() is not null)
            {
                Notify($"Downloading {cfg.Name}…");
                await ToolInstaller.InstallAsync(cfg.Id, cfg.Download, msg => Dispatcher.UIThread.Post(() => OnStatus(msg)));
                ok = true;
            }
            else if (install is not null)
            {
                Notify($"Can't install {cfg.Name}: '{program}' is not installed.{(cfg.InstallUrl is null ? "" : $" See {cfg.InstallUrl}")}", true);
            }
            else if (cfg.InstallUrl is not null)
            {
                OpenUrl(cfg.InstallUrl);
            }
        }
        catch (Exception e)
        {
            Notify($"Installing {cfg.Name} failed: {e.Message}", true);
        }
        if (ok) Notify($"{cfg.Name} installed.");

        if (cfg.Prerequisite is { } pre && ProcessUtil.Resolve(pre.Command) is null)
        {
            var actions = new List<(string, Action)>();
            if (pre.Install is { } preInstall)
                actions.Add(($"Install {pre.Name}", async () =>
                {
                    Notify($"Installing {pre.Name}… (this can take a few minutes)");
                    var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync(preInstall, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    if (code == 0) Notify($"{pre.Name} installed. Restart Aether so it picks up the new PATH.");
                    else Notify($"Installing {pre.Name} failed:{Environment.NewLine}{(stderr + stdout).Trim()}", true);
                }));
            if (pre.Url is { } url) actions.Add(("Instructions", () => OpenUrl(url)));
            ShowBalloon($"{cfg.Name} needs {pre.Name}", $"'{pre.Command}' was not found. {cfg.Name} uses it to understand your projects.", false, actions.ToArray());
        }
        return ok;
    }

    // ---- Learn ----------------------------------------------------------------

    private Control LearnPage()
    {
        Button Link(string text, Action click)
        {
            var b = new Button { Classes = { "flat" }, Content = new TextBlock { Text = text, Foreground = Res("Link") }, Padding = new Thickness(0, 4) };
            b.Click += (_, _) => click();
            return b;
        }
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                PageTitle("Learn"),
                new TextBlock { Text = "The essential shortcuts of the default keymap:", Classes = { "muted" } },
                new TextBlock { Text = "Double Shift  Search Everywhere\nCtrl+Shift+A  Find Action\nCtrl+Space  Completion   ·   Alt+Enter  Quick fixes\nCtrl+B  Go to declaration   ·   Alt+F7  Find usages\nShift+F6  Rename   ·   Ctrl+Alt+L  Reformat\nShift+F10  Run   ·   Alt+F12  Terminal", FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, LineHeight = 22 },
                Link("Full keymap reference", ShowKeymap),
                Link("Writing a plugin (plugin.json format)", () => OpenUrl(Path.Combine(AppContext.BaseDirectory, "plugins", "README.md"))),
            },
        };
    }
}
