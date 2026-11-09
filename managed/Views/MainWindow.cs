using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RegistryEditor.Presentation.Backend;

namespace RegistryEditor.Presentation.Views;

public sealed class KeyNode : INotifyPropertyChanged
{
    public string Name { get; }
    public string Path { get; }
    public bool IsComputer => Path == "Computer" || (Path.StartsWith(@"\\", StringComparison.Ordinal) && !Path[2..].Contains('\\'));
    public ObservableCollection<KeyNode> Children { get; } = new();
    public bool Loaded { get; set; }
    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; PropertyChanged?.Invoke(this, new(nameof(Expanded))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public KeyNode(string name, string path, bool placeholder = true)
    {
        Name = name; Path = path;
        if (placeholder) Children.Add(new KeyNode("", "", false));
    }
    public override string ToString() => Name;
}

public sealed class ValueRow
{
    public string Name { get; init; } = "";
    public string DisplayName => Name.Length == 0 ? "(Default)" : Name;
    public uint Kind { get; init; }
    public string Type { get; init; } = "";
    public string Data { get; init; } = "";
    public string Edit { get; init; } = "";
    public override string ToString() => $"{DisplayName}, {Type}, {Data}";
}

public sealed class Settings
{
    public string Path { get; set; } = "Computer";
    public double Width { get; set; } = 1136;
    public double Height { get; set; } = 626;
    public double Splitter { get; set; } = 216;
    public double NameWidth { get; set; } = 120;
    public double TypeWidth { get; set; } = 120;
    public int SortColumn { get; set; } = -1;
    public bool SortDescending { get; set; }
    public bool StatusBar { get; set; }
    public string FindText { get; set; } = "";
    public bool FindKeys { get; set; } = true;
    public bool FindValues { get; set; } = true;
    public bool FindData { get; set; } = true;
    public bool FindWhole { get; set; }
    public bool FindSubtree { get; set; }
    public Dictionary<string, string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSerializable(typeof(Settings))]
internal partial class SettingsJson : JsonSerializerContext { }

public sealed class MainWindow : Window
{
    private readonly Dictionary<KeyNode, Task> _loading = new();
    private readonly TextBox _address = new() { Text = "Computer", FontSize = 12, MinHeight = 0, Height = 20.8, Padding = new Thickness(4, 1), BorderThickness = new Thickness(0, 1, 0, 1), BorderBrush = Brushes.Gray };
    private readonly TreeView _tree = new() { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(0), AutoScrollToSelectedItem = true };
    private readonly DetailList _values = new();
    private readonly TextBlock _status = new() { Text = "Computer", Padding = new Thickness(4, 2) };
    private readonly Grid _panes = new();
    private readonly MenuItem _favorites = new() { Header = "F_avorites" };
    private readonly KeyNode _computer = new("Computer", "Computer", false) { Loaded = true, Expanded = true };
    private readonly ObservableCollection<KeyNode> _roots = new();
    private readonly Dictionary<string, KeyNode> _remotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Settings _settings;
    private string _path = "Computer";
    private int _navigation;
    private bool _treeNavigation;
    private bool _valuePane;
    private string? _searchOptions;
    private string _searchMarker = "";
    private string _searchStart = "Computer";
    private bool _searching;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task _watchTask = Task.CompletedTask;
    private bool _printing;
    private readonly string _settingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegEditto", "settings.json");

    public MainWindow(string? initialPath = null, string? initialImport = null, IReadOnlyList<string>? initialImports = null)
    {
        _settings = LoadSettings();
        Title = "RegEditto";
        Icon = CreateIcon();
        Width = Math.Clamp(_settings.Width, 640, 2400); Height = Math.Clamp(_settings.Height, 400, 1800);
        MinWidth = 640; MinHeight = 400;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        Background = Brushes.White;
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new Style(s => s.OfType<TreeViewItem>()) {
            Setters = {
                new Setter(MinHeightProperty, 17.6d),
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(TreeViewItem.IsExpandedProperty, new CompiledBindingExtension(
                    new CompiledBindingPathBuilder().Property(
                        new ClrPropertyInfo(nameof(KeyNode.Expanded), o => ((KeyNode)o).Expanded,
                            (o, v) => ((KeyNode)o).Expanded = (bool)v!, typeof(bool)),
                        PropertyInfoAccessorFactory.CreateInpcPropertyAccessor).Build()) { Mode = BindingMode.TwoWay })
            }
        });
        Styles.Add(new Style(s => s.OfType<MenuItem>()) { Setters = { new Setter(PaddingProperty, new Thickness(6, 2)), new Setter(MinHeightProperty, 20d) } });
        Styles.Add(new Style(s => s.OfType<TreeViewItem>().Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter")) {
            Setters = { new Setter(PaddingProperty, new Thickness(0, 1)), new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Left) }
        });
        Styles.Add(new Style(s => s.OfType<TreeViewItem>().Class(":selected").Template().OfType<Border>().Name("SelectionBorder")) {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) }
        });
        Styles.Add(new Style(s => s.OfType<TreeViewItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter")) {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.Parse("#cce8ff"))) }
        });
        Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(PaddingProperty, new Thickness(3, 0)), new Setter(MinHeightProperty, 17.6d), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _tree.ItemTemplate = new FuncTreeDataTemplate<KeyNode>((node, _) => {
            if (node is null) return new Border();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            // Original vector shapes, not Windows resources.
            var glyph = new Avalonia.Controls.Shapes.Path {
                Data = Geometry.Parse(node.IsComputer ? "M1,1 L14,1 L14,10 L1,10 Z M7,10 L7,13 M4,13 L11,13" : "M1,4 L1,2 L6,2 L8,4 L15,4 L15,13 L1,13 Z"),
                Fill = node.IsComputer ? new SolidColorBrush(Color.Parse("#86c5d5")) : new SolidColorBrush(Color.Parse("#ffce58")),
                Stroke = node.IsComputer ? Brushes.SlateGray : new SolidColorBrush(Color.Parse("#b69032")),
                StrokeThickness = 1, Width = 16, Height = 15.6
            };
            row.Children.Add(glyph);
            row.Children.Add(new InlineName(node.Name, node));
            return row;
        }, node => node is null ? Array.Empty<KeyNode>() : node.Children);
        foreach (var hive in new[] { "HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG" })
            _computer.Children.Add(new KeyNode(hive, hive));
        _roots.Add(_computer); _tree.ItemsSource = _roots;
        _tree.AddHandler(TreeViewItem.ExpandedEvent, async (_, e) => {
            if (e.Source is TreeViewItem { DataContext: KeyNode node } && !node.Loaded)
                await Guard(() => LoadChildren(node));
        });
        _tree.SelectionChanged += async (_, _) => {
            if (!_treeNavigation && _tree.SelectedItem is KeyNode node && node.Path.Length > 0)
                await Guard(() => Navigate(node.Path, false));
        };
        _values.SetWidths(Math.Clamp(_settings.NameWidth, 60, 600), Math.Clamp(_settings.TypeWidth, 60, 600));
        _values.SetSort(_settings.SortColumn, _settings.SortDescending);
        _values.DoubleTapped += async (_, _) => await Guard(Modify);
        _values.GotFocus += (_, _) => _valuePane = true;
        _tree.GotFocus += (_, _) => _valuePane = false;
        _values.ContextMenu = ValueMenu();
        _tree.ContextMenu = KeyMenu();
        _address.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Guard(() => Navigate(_address.Text ?? "")); } };
        KeyDown += OnKeyDown;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var menu = MakeMenu(); root.Children.Add(menu);
        Grid.SetRow(_address, 1); root.Children.Add(_address);
        _panes.ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(Math.Clamp(_settings.Splitter, 120, 700), GridUnitType.Pixel), new ColumnDefinition(3, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) };
        _panes.Children.Add(_tree);
        var splitter = new GridSplitter { Width = 3, Background = new SolidColorBrush(Color.Parse("#f0f0f0")), HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1); _panes.Children.Add(splitter);
        Grid.SetColumn(_values, 2); _panes.Children.Add(_values);
        Grid.SetRow(_panes, 2); root.Children.Add(_panes);
        _status.IsVisible = _settings.StatusBar; Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        Opened += async (_, _) => await Guard(async () => {
            if (initialPath is null) await RestoreLastPath(); else await Navigate(initialPath);
            if (initialImport is not null) await ImportFile(initialImport);
            if (initialImports is not null) {
                foreach (var file in initialImports) await ImportFile(file);
                Close();
            }
        });
        Closing += (_, e) => { if (_printing) e.Cancel = true; else SaveSettings(); };
        Closed += async (_, _) => { _searchCancellation?.Cancel(); _watchCancellation?.Cancel(); await _watchTask; };
        RefreshFavorites();
    }

    private Task<JsonElement> Send(string op, string? path = null, string name = "", string text = "", uint kind = 0, bool hex = true, CancellationToken cancellation = default)
    {
        var target = path ?? _path;
        return Task.Run(() => RegistryBackend.Execute(op, target, name, text, kind, hex, cancellation), cancellation);
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or Win32Exception or FormatException or OverflowException or COMException) {
            _status.Text = error.Message;
            await Message("RegEditto", error.Message);
        }
    }

    private async Task LoadChildren(KeyNode node)
    {
        if (_loading.TryGetValue(node, out var existing)) { await existing; return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading[node] = completion.Task;
        try { await LoadChildrenCore(node); completion.SetResult(); }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or InvalidOperationException) {
            completion.SetException(e);
            throw;
        }
        finally { _loading.Remove(node); }
    }

    private async Task LoadChildrenCore(KeyNode node)
    {
        var result = await Send("children", node.Path);
        node.Children.Clear();
        foreach (var child in result.GetProperty("children").EnumerateArray()) {
            var name = child.GetString()!;
            node.Children.Add(new KeyNode(name, node.Path + "\\" + name));
        }
        node.Loaded = true;
    }

    private static string Canonical(string path)
    {
        path = path.Trim().TrimEnd('\\');
        if (path.Equals("Computer", StringComparison.OrdinalIgnoreCase)) return "Computer";
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
            if (!path[2..].Contains('\\')) return path;
            return RegistryBackend.ParsePath(path).ToString();
        }
        if (path.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) path = path[9..];
        var parts = path.Split('\\', 2);
        parts[0] = parts[0].ToUpperInvariant() switch {
            "HKCU" => "HKEY_CURRENT_USER", "HKLM" => "HKEY_LOCAL_MACHINE", "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS", "HKCC" => "HKEY_CURRENT_CONFIG", var hive => hive
        };
        return string.Join("\\", parts);
    }

    private async Task Navigate(string path, bool selectTree = true)
    {
        path = Canonical(path);
        var generation = ++_navigation;
        var rows = new List<ValueRow>();
        var remoteRoot = path.StartsWith(@"\\", StringComparison.Ordinal) && !path[2..].Contains('\\');
        if (path != "Computer" && !remoteRoot) {
            var result = await Send("list", path);
            foreach (var item in result.GetProperty("values").EnumerateArray())
                rows.Add(new ValueRow {
                    Name = item.GetProperty("name").GetString()!, Kind = item.GetProperty("kind").GetUInt32(),
                    Type = item.GetProperty("type_name").GetString()!, Data = item.GetProperty("data").GetString()!, Edit = item.GetProperty("edit").GetString()!
                });
        }
        if (generation != _navigation) return;
        var pathChanged = !path.Equals(_path, StringComparison.OrdinalIgnoreCase);
        var selections = pathChanged ? Array.Empty<string>() : _values.SelectedItems.Cast<ValueRow>().Select(value => value.Name).ToArray();
        _path = path; _address.Text = path == "Computer" || path.StartsWith(@"\\", StringComparison.Ordinal) ? path : "Computer\\" + path; _status.Text = _address.Text;
        _values.ItemsSource = rows;
        foreach (var selected in rows.Where(row => selections.Contains(row.Name, StringComparer.OrdinalIgnoreCase))) _values.SelectedItems.Add(selected);
        if (pathChanged) _watchTask = StartWatching(path);
        if (selectTree) {
            var node = _computer;
            var components = path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
                var end = path.IndexOf('\\', 2);
                var machine = end < 0 ? path[2..] : path[2..end];
                node = await ConnectMachine(machine);
                components = end < 0 ? "" : path[(end + 1)..];
            }
            node.Expanded = true;
            if (path != "Computer" && !remoteRoot) {
                foreach (var part in components.Split('\\')) {
                    if (!node.Loaded) await LoadChildren(node);
                    var next = node.Children.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (next is null) throw new InvalidOperationException("Key no longer exists: " + path);
                    node.Expanded = true; node = next;
                }
            }
            _treeNavigation = true;
            try { _tree.SelectedItem = node; }
            finally { _treeNavigation = false; }
        }
    }

    private async Task StartWatching(string path)
    {
        _watchCancellation?.Cancel();
        _watchCancellation = null;
        if (path == "Computer" || path.StartsWith(@"\\", StringComparison.Ordinal)) return;
        using var cancellation = new CancellationTokenSource();
        _watchCancellation = cancellation;
        try {
            using var watcher = new RegistryWatcher(path);
            while (!cancellation.IsCancellationRequested) {
                await watcher.WaitForChangeAsync(cancellation.Token);
                // Keep a rename editor alive until its commit/cancel has finished.
                while (await Dispatcher.UIThread.InvokeAsync(() => this.GetVisualDescendants().OfType<InlineName>().Any(editor => editor.Editing)))
                    await Task.Delay(100, cancellation.Token);
                if (!cancellation.IsCancellationRequested && _path == path) await Refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException) {
            if (!cancellation.IsCancellationRequested && _path == path) {
                _status.Text = "Automatic refresh stopped: " + error.Message;
                await Message("RegEditto", _status.Text);
            }
        }
        finally { if (ReferenceEquals(_watchCancellation, cancellation)) _watchCancellation = null; }
    }

    private async Task Refresh()
    {
        if (_tree.SelectedItem is KeyNode node && !node.IsComputer) {
            node.Loaded = false; await LoadChildren(node);
        }
        await Navigate(_path);
    }
    private async Task RestoreLastPath()
    {
        var path = _settings.Path;
        while (true) {
            try {
                await Navigate(path);
                if (path != _settings.Path) _status.Text = "The last visited key no longer exists; opened " + _address.Text;
                return;
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) {
                var separator = path.LastIndexOf('\\');
                path = separator < 0 ? "Computer" : path[..separator];
            }
        }
    }

    private MenuItem Item(string header, Func<Task> action, string? gesture = null)
    {
        var item = new MenuItem { Header = header };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
        item.Click += async (_, _) => await Guard(action);
        return item;
    }

    private Menu MakeMenu()
    {
        var file = new MenuItem { Header = "_File", ItemsSource = new object[] {
            Item("_Import...", Import), Item("_Export...", Export), new Separator(),
            Item("Connect _Network Registry...", ConnectRemote),
            Item("_Disconnect Network Registry", DisconnectRemote),
            new Separator(), Item("_Load Hive...", LoadHive),
            Item("_Unload Hive...", UnloadHive), new Separator(),
            Item("_Print...", Print, "Ctrl+P"), new Separator(), Item("E_xit", () => { Close(); return Task.CompletedTask; })
        }};
        var edit = new MenuItem { Header = "_Edit", ItemsSource = new object[] {
            Item("_Modify...", Modify), Item("Modify _Binary Data...", () => Modify(true)), new Separator(), NewMenu(),
            Item("_Permissions...", Permissions),
            Item("_Delete", Delete, "Delete"), Item("_Rename", Rename, "F2"), new Separator(),
            Item("_Copy Key Name", CopyPath), new Separator(), Item("_Find...", FindDialog, "Ctrl+F"), Item("Find _Next", FindNext, "F3")
        }};
        var toggle = Item("_Status Bar", () => { _status.IsVisible = !_status.IsVisible; return Task.CompletedTask; });
        toggle.ToggleType = MenuItemToggleType.CheckBox; toggle.IsChecked = _settings.StatusBar;
        var view = new MenuItem { Header = "_View", ItemsSource = new object[] { toggle, new Separator(), Item("_Refresh", Refresh, "F5") } };
        var help = new MenuItem { Header = "_Help", ItemsSource = new object[] {
            Item("_Help Topics", () => Message("RegEditto Help", "F5 Refresh  ·  F2 Rename  ·  Delete Delete\nCtrl+F Find  ·  F3 Find Next  ·  Ctrl+L Address  ·  Ctrl+P Print\nTab switches panes; Enter or double-click modifies a value.\nF2 and New use inline name editing; Enter commits, Escape cancels.\nThe binary editor has offset, hexadecimal and ASCII panes.\n\nChanges affect the real registry immediately. Export before editing.\nPermissions uses the Windows security editor.\nThis independent application never elevates or enables privileges.")),
            new Separator(), Item("_About RegEditto", () => Message("About RegEditto", "RegEditto\nClassic Windows registry editor\nBuilt with C# and Avalonia"))
        }};
        return new Menu { Height = 19.2, MinHeight = 0, ItemsSource = new[] { file, edit, view, _favorites, help } };
    }

    private MenuItem NewMenu() => new() { Header = "_New", ItemsSource = new object[] {
        Item("_Key", NewKey), new Separator(),
        Item("_String Value", () => NewValue(1)), Item("_Binary Value", () => NewValue(3)),
        Item("_DWORD (32-bit) Value", () => NewValue(4)), Item("_QWORD (64-bit) Value", () => NewValue(11)),
        Item("_Multi-String Value", () => NewValue(7)), Item("_Expandable String Value", () => NewValue(2))
    }};
    private ContextMenu ValueMenu() => new() { ItemsSource = new object[] {
        Item("_Modify...", Modify), Item("Modify _Binary Data...", () => Modify(true)), new Separator(),
        Item("_Delete", DeleteValues), Item("_Rename", RenameValue), new Separator(), NewMenu()
    }};
    private ContextMenu KeyMenu() => new() { ItemsSource = new object[] {
        Item("_Expand", async () => { if (_tree.SelectedItem is KeyNode n) { if (!n.Loaded) await LoadChildren(n); n.Expanded = !n.Expanded; } }),
        NewMenu(), Item("_Delete", DeleteKey), Item("_Rename", RenameKey),
        Item("_Export...", Export), Item("_Permissions...", Permissions),
        new Separator(), Item("_Copy Key Name", CopyPath)
    }};
    private async Task Print()
    {
        RequireKey();
        if (_printing) throw new InvalidOperationException("A print dialog is already open.");
        var path = _path;
        _printing = true;
        try {
            var lines = await Task.Run(() => RegistryPrinter.Lines(path).ToArray());
            IsEnabled = false;
            var printed = await RegistryPrinter.PrintAsync(lines);
            _status.Text = printed ? "Sent to printer: " + path : "Printing cancelled.";
        }
        finally { IsEnabled = true; _printing = false; }
    }
    private async Task Permissions()
    {
        RequireKey();
        using var information = new RegistryPermissions(_path);
        information.Show(TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("The native window handle is unavailable."));
        await Refresh();
    }
    private void RequireKey() { if (_path == "Computer" || (_path.StartsWith(@"\\", StringComparison.Ordinal) && !_path[2..].Contains('\\'))) throw new InvalidOperationException("Select a registry key first."); }
    private ValueRow SelectedValue() => _values.SelectedItem as ValueRow ?? throw new InvalidOperationException("Select a value first.");

    private Task Modify() => Modify(false);
    private async Task Modify(bool binary)
    {
        RequireKey();
        var row = SelectedValue();
        var raw = binary || row.Kind is not (1 or 2 or 4 or 7 or 11);
        var text = row.Edit;
        if (binary) {
            var result = await Send("list");
            var entry = result.GetProperty("values").EnumerateArray().First(e => e.GetProperty("name").GetString() == row.Name);
            text = string.Join(" ", entry.GetProperty("bytes").EnumerateArray().Select(b => b.GetByte().ToString("x2")));
        }
        if (raw) {
            var hexEditor = new HexEditor(RegistryBackend.ParseBytes(text));
            var error = new TextBlock { Foreground = Brushes.DarkRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
            hexEditor.Error += message => { error.Text = message; error.IsVisible = true; };
            var binaryFields = new StackPanel { Spacing = 5, Children = {
                new TextBlock { Text = "Value name:" }, new TextBox { Text = row.DisplayName, IsReadOnly = true },
                new TextBlock { Text = "Value data:" },
                new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                    Child = new ScrollViewer { Height = 200, Content = hexEditor } },
                new TextBlock { Text = "Tab: Hex / ASCII    Insert: Insert / Overwrite    Shift+arrows: Select", FontSize = 11 }, error
            }};
            await Dialog("Edit Binary Value", binaryFields, async () => {
                await Send("set-raw", name: row.Name, text: string.Join(" ", hexEditor.Document.Bytes.Select(b => b.ToString("x2"))), kind: row.Kind);
                await Refresh();
            }, hexEditor);
            return;
        }
        var title = raw ? "Edit Binary Value" : row.Kind switch { 4 => "Edit DWORD (32-bit) Value", 11 => "Edit QWORD (64-bit) Value", 7 => "Edit Multi-String", _ => "Edit String" };
        var numeric = !raw && row.Kind is 4 or 11;
        var editor = new TextBox { Text = text, MinHeight = row.Kind == 7 || raw ? 110 : 23, AcceptsReturn = row.Kind == 7 || raw, TextWrapping = TextWrapping.Wrap, FontFamily = raw ? new FontFamily("Consolas") : FontFamily };
        var hexadecimal = new RadioButton { Content = "Hexadecimal", IsChecked = true, GroupName = "base" };
        var decimalBase = new RadioButton { Content = "Decimal", GroupName = "base" };
        var lastBase = 16;
        var switchingBase = false;
        var numericError = new TextBlock { Foreground = Brushes.DarkRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        foreach (var choice in new[] { hexadecimal, decimalBase })
            choice.IsCheckedChanged += (_, _) => {
                if (switchingBase || choice.IsChecked != true) return;
                var radix = ReferenceEquals(choice, hexadecimal) ? 16 : 10;
                if (radix == lastBase) return;
                switchingBase = true;
                try {
                    var number = Convert.ToUInt64(editor.Text, lastBase);
                    if (row.Kind == 4 && number > uint.MaxValue) throw new OverflowException("The value must fit in 32 bits.");
                    editor.Text = number.ToString(radix == 16 ? "x" : "d");
                    lastBase = radix;
                    numericError.IsVisible = false;
                }
                catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) {
                    hexadecimal.IsChecked = lastBase == 16; decimalBase.IsChecked = lastBase == 10;
                    numericError.Text = "Invalid number: " + e.Message; numericError.IsVisible = true;
                }
                finally { switchingBase = false; }
            };
        var controls = new StackPanel { Spacing = 5 };
        controls.Children.Add(new TextBlock { Text = "Value name:" });
        controls.Children.Add(new TextBox { Text = row.DisplayName, IsReadOnly = true });
        controls.Children.Add(new TextBlock { Text = raw ? "Value data (hexadecimal bytes):" : "Value data:" }); controls.Children.Add(editor);
        if (numeric) {
            controls.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { hexadecimal, decimalBase } });
            controls.Children.Add(numericError);
        }
        await Dialog(title, controls, async () => {
            if (raw) {
                // The backend accepts raw bytes for all types through a dedicated operation.
                await Send("set-raw", name: row.Name, text: editor.Text ?? "", kind: row.Kind);
            } else await Send("set", name: row.Name, text: editor.Text ?? "", kind: row.Kind, hex: hexadecimal.IsChecked == true);
            await Refresh();
        }, editor);
    }

    private async Task NewKey()
    {
        RequireKey();
        var existing = (await Send("children")).GetProperty("children").EnumerateArray().Select(child => child.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (existing.Contains("New Key #" + number)) number++;
        var name = "New Key #" + number;
        await Send("create-key", name: name); await Refresh(); await Navigate(_path + "\\" + name);
        await RenameKey();
    }
    private async Task NewValue(uint kind)
    {
        RequireKey();
        var existing = ((IEnumerable<ValueRow>)_values.ItemsSource).Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (existing.Contains("New Value #" + number)) number++;
        var name = "New Value #" + number;
        await Send("create-value", name: name, kind: kind, text: kind is 4 or 11 ? "0" : ""); await Refresh();
        _values.SelectedItem = ((IEnumerable<ValueRow>)_values.ItemsSource).FirstOrDefault(v => v.Name == name);
        _values.Focus();
        await RenameValue();
    }
    private Task Delete() => _valuePane ? DeleteValues() : DeleteKey();
    private Task Rename() => _valuePane ? RenameValue() : RenameKey();
    private async Task DeleteValues()
    {
        RequireKey();
        var rows = _values.SelectedItems.Cast<ValueRow>().ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Select a value first.");
        if (!await Confirm("Confirm Value Delete", "Deleting certain registry values could cause system instability.\nAre you sure you want to permanently delete the selected value(s)?")) return;
        foreach (var row in rows) await Send("delete-value", name: row.Name);
        await Refresh();
    }
    private async Task DeleteKey()
    {
        RequireKey();
        if (!_path.Contains('\\')) throw new InvalidOperationException("Cannot delete a registry hive.");
        if (!await Confirm("Confirm Key Delete", "Are you sure you want to permanently delete this key and all of its subkeys?\n\n" + _path)) return;
        var parent = _path[.._path.LastIndexOf('\\')];
        await Send("delete-key"); await Navigate(parent); await Refresh();
    }
    private async Task RenameValue()
    {
        var row = SelectedValue();
        var original = _path;
        if (row.Name.Length == 0) throw new InvalidOperationException("The default value cannot be renamed.");
        _values.UpdateLayout();
        var editor = _values.NameEditor(row) ?? throw new InvalidOperationException("The selected value is not visible. Scroll it into view before renaming.");
        var name = await editor.Edit();
        _values.Focus();
        if (name is null || name == row.Name) return;
        await Send("rename-value", original, name: row.Name, text: name); await Refresh();
    }
    private async Task RenameKey()
    {
        RequireKey();
        if (!_path.Contains('\\')) throw new InvalidOperationException("Cannot rename a registry hive.");
        var index = _path.LastIndexOf('\\');
        var original = _path;
        _tree.UpdateLayout();
        var node = _tree.SelectedItem;
        var editor = _tree.GetVisualDescendants().OfType<InlineName>().FirstOrDefault(label => ReferenceEquals(label.Tag, node))
            ?? throw new InvalidOperationException("The selected key is not visible. Scroll it into view before renaming.");
        var name = await editor.Edit();
        _tree.Focus();
        if (_path != original) return;
        if (name is null || name == _path[(index + 1)..]) return;
        var parent = _path[..index];
        await Send("rename-key", name: name); await Navigate(parent); await Refresh(); await Navigate(parent + "\\" + name);
    }
    private async Task CopyPath()
    {
        if (Clipboard is null) throw new InvalidOperationException("Clipboard unavailable.");
        await Clipboard.SetTextAsync(_path);
    }

    private async Task<KeyNode> ConnectMachine(string machine)
    {
        if (_remotes.TryGetValue(machine, out var existing)) return existing;
        if (machine.Length == 0 || machine.Contains('\\') || machine.Contains('\0')) throw new ArgumentException("Enter a valid computer name.");
        await Send("children", $@"\\{machine}\HKEY_LOCAL_MACHINE");
        var node = new KeyNode(machine, @"\\" + machine, false) { Loaded = true, Expanded = true };
        foreach (var hive in new[] { "HKEY_LOCAL_MACHINE", "HKEY_USERS" })
            node.Children.Add(new KeyNode(hive, node.Path + "\\" + hive));
        _remotes.Add(machine, node); _roots.Add(node);
        return node;
    }
    private async Task ConnectRemote()
    {
        var machine = await Prompt("Connect Network Registry", "Computer name:", "");
        if (machine is null) return;
        machine = machine.Trim().TrimStart('\\');
        await ConnectMachine(machine); await Navigate(@"\\" + machine);
    }
    private async Task DisconnectRemote()
    {
        if (_remotes.Count == 0) { await Message("Disconnect Network Registry", "No remote registry is connected."); return; }
        var list = new ListBox { ItemsSource = _remotes.Keys.ToArray(), SelectedIndex = 0, MinHeight = 100 };
        await Dialog("Disconnect Network Registry", list, async () => {
            if (list.SelectedItem is not string machine) throw new InvalidOperationException("Select a computer.");
            _roots.Remove(_remotes[machine]); _remotes.Remove(machine);
            if (_path.StartsWith(@"\\" + machine, StringComparison.OrdinalIgnoreCase)) await Navigate("Computer");
        });
    }
    private async Task LoadHive()
    {
        RequireKey();
        if (_path is not ("HKEY_LOCAL_MACHINE" or "HKEY_USERS")) throw new InvalidOperationException("Select HKEY_LOCAL_MACHINE or HKEY_USERS first.");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Load Hive", AllowMultiple = false });
        if (files.Count == 0) return;
        var file = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Select a local hive file.");
        var name = await Prompt("Load Hive", "Key name:", "");
        if (name is null) return;
        if (!await Confirm("Load Hive", "Load this offline hive under " + _path + "\\" + name + "?\n\nExisting process privileges will be used; this app never elevates or enables privileges.")) return;
        await Send("load-hive", name: name, text: file); await Refresh();
    }
    private async Task UnloadHive()
    {
        RequireKey();
        if (!await Confirm("Unload Hive", "Unload the selected hive?\n\n" + _path + "\n\nOnly a hive directly below HKEY_LOCAL_MACHINE or HKEY_USERS can be unloaded.")) return;
        var parent = _path[.._path.LastIndexOf('\\')];
        await Send("unload-hive"); await Navigate(parent); await Refresh();
    }

    private async Task Import()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions {
            Title = "Import Registry File", AllowMultiple = false,
            FileTypeFilter = new[] {
                new FilePickerFileType("Registration Files") { Patterns = new[] { "*.reg" } },
                new FilePickerFileType("Registry Hive Files") { Patterns = new[] { "*.hiv" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("A local registry file is required.");
        await ImportFile(path);
    }
    private async Task ImportFile(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(".hiv", StringComparison.OrdinalIgnoreCase)) {
            RequireKey();
            if (!await Confirm("Restore Registry Hive", "Replace the selected key and its contents from this hive file?\n\n" + _path + "\n\n" + path +
                "\n\nExisting process privileges only. Export before restoring; this operation cannot be undone.")) return;
            await Send("restore-hive", text: path);
            await Refresh(); return;
        }
        if (!await Confirm("RegEditto", "Adding information can unintentionally change or delete values and cause components to stop working correctly.\n\nImport " + path + "?")) return;
        var result = await Send("import", text: path);
        await Refresh(); await Message("RegEditto", result.GetProperty("count").GetInt32() + " registry operations imported successfully.");
    }
    private async Task Export()
    {
        var exportPath = _path;
        var all = new RadioButton { Content = "All", GroupName = "export", IsChecked = _path == "Computer" };
        var selected = new RadioButton { Content = "Selected branch", GroupName = "export", IsChecked = _path != "Computer", IsEnabled = _path != "Computer" };
        var branch = new TextBox { Text = _path, IsReadOnly = true };
        var formats = new ComboBox { ItemsSource = new[] { "Registration Files (*.reg)", "Win9x/NT4 Registration Files (*.reg)", "Registry Hive Files (*.hiv)", "Text Files (*.txt)" }, SelectedIndex = 0 };
        var format = 0;
        var accepted = false;
        await Dialog("Export Registry File", new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "Export range:" }, all, selected, branch,
            new TextBlock { Text = "Save as type:" }, formats } }, () => {
            exportPath = all.IsChecked == true ? "Computer" : _path;
            format = formats.SelectedIndex;
            if (format is 2 or 3 && exportPath == "Computer") throw new InvalidOperationException("Select a branch for hive or text export.");
            accepted = true; return Task.CompletedTask;
        });
        if (!accepted) return;
        if (exportPath.StartsWith(@"\\", StringComparison.Ordinal) &&
            !await Confirm("Export Registry File", "This remote snapshot will contain local hive paths. Importing it applies those paths to the computer running the import. Continue?")) return;
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions {
            Title = "Export Registry File", SuggestedFileName = format == 2 ? "registry.hiv" : format == 3 ? "registry.txt" : "registry.reg",
            DefaultExtension = format == 2 ? "hiv" : format == 3 ? "txt" : "reg",
            ShowOverwritePrompt = true, FileTypeChoices = new[] { new FilePickerFileType(format == 2 ? "Registry Hive Files" : format == 3 ? "Text Files" : "Registration Files") {
                Patterns = new[] { format == 2 ? "*.hiv" : format == 3 ? "*.txt" : "*.reg" }
            } }
        });
        if (file is null) return;
        var path = file.TryGetLocalPath() ?? throw new InvalidOperationException("A local export file is required.");
        await Send(format switch { 1 => "export-legacy", 2 => "save-hive", 3 => "export-text", _ => "export" }, exportPath, text: path);
        _status.Text = "Exported " + exportPath;
    }

    private async Task FindDialog()
    {
        var accepted = false;
        var text = new TextBox { MinWidth = 220, Text = _settings.FindText };
        var keys = new CheckBox { Content = "Keys", IsChecked = _settings.FindKeys };
        var names = new CheckBox { Content = "Values", IsChecked = _settings.FindValues };
        var data = new CheckBox { Content = "Data", IsChecked = _settings.FindData };
        var whole = new CheckBox { Content = "Match whole string only", IsChecked = _settings.FindWhole };
        var subtree = new CheckBox { Content = "Search selected branch only", IsChecked = _settings.FindSubtree };
        var panel = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Find what:" }, text, new TextBlock { Text = "Look at:" },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { keys, names, data } }, whole, subtree } };
        await Dialog("Find", panel, async () => {
            if (string.IsNullOrEmpty(text.Text)) throw new InvalidOperationException("Enter search text.");
            if (!(keys.IsChecked == true || names.IsChecked == true || data.IsChecked == true)) throw new InvalidOperationException("Select at least one search field.");
            _settings.FindText = text.Text; _settings.FindKeys = keys.IsChecked == true; _settings.FindValues = names.IsChecked == true;
            _settings.FindData = data.IsChecked == true; _settings.FindWhole = whole.IsChecked == true;
            _settings.FindSubtree = subtree.IsChecked == true;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject(); writer.WriteString("query", text.Text);
                writer.WriteBoolean("keys", keys.IsChecked == true); writer.WriteBoolean("names", names.IsChecked == true);
                writer.WriteBoolean("data", data.IsChecked == true); writer.WriteBoolean("whole", whole.IsChecked == true);
                writer.WriteBoolean("continue", subtree.IsChecked != true); writer.WriteEndObject();
            }
            _searchOptions = System.Text.Encoding.UTF8.GetString(buffer.ToArray()); _searchMarker = ""; _searchStart = _path;
            accepted = true;
            await Task.CompletedTask;
        }, text, "Find Next");
        if (accepted) await FindNext();
    }
    private async Task FindNext()
    {
        if (_searchOptions is null) { await FindDialog(); return; }
        if (_searching) throw new InvalidOperationException("A search is already running.");
        _searching = true; Cursor = new Cursor(StandardCursorType.Wait);
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _status.Text = _settings.FindSubtree ? "Searching the selected subtree... Press Escape to cancel." : "Searching the registry from the selected key... Press Escape to cancel.";
        try {
            using var document = JsonDocument.Parse(_searchOptions);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject();
                foreach (var prop in document.RootElement.EnumerateObject()) prop.WriteTo(writer);
                writer.WriteString("after", _searchMarker); writer.WriteEndObject();
            }
            var result = await Send("find", _searchStart, text: System.Text.Encoding.UTF8.GetString(buffer.ToArray()), cancellation: cancellation.Token);
            var found = result.GetProperty("found");
            if (found.ValueKind == JsonValueKind.Null) { await Message("RegEditto", _settings.FindSubtree ? "Finished searching the selected registry subtree." : "Finished searching the registry."); return; }
            _searchMarker = found.GetProperty("marker").GetString()!;
            await Navigate(found.GetProperty("path").GetString()!);
            if (found.GetProperty("name").ValueKind != JsonValueKind.Null) {
                var name = found.GetProperty("name").GetString();
                _values.SelectedItem = ((IEnumerable<ValueRow>)_values.ItemsSource).FirstOrDefault(v => v.Name == name);
                _values.Focus();
            }
        }
        catch (OperationCanceledException) { _status.Text = "Search cancelled."; }
        finally { _searching = false; _searchCancellation = null; Cursor = Cursor.Default; }
    }
    private void RefreshFavorites()
    {
        var items = new List<object> {
            Item("_Add to Favorites...", async () => {
                var name = await Prompt("Add to Favorites", "Favorite name:", _path.Split('\\').Last());
                if (name is null) return;
                if (_settings.Favorites.ContainsKey(name) && !await Confirm("Favorites", "Replace the existing favorite?")) return;
                _settings.Favorites[name] = _path; RefreshFavorites(); SaveSettings();
            }),
            Item("_Remove Favorite...", async () => {
                var names = _settings.Favorites.Keys.OrderBy(n => n).ToArray();
                if (names.Length == 0) { await Message("Favorites", "No favorites have been saved."); return; }
                var list = new ListBox { ItemsSource = names, SelectedIndex = 0, MinHeight = 120, SelectionMode = SelectionMode.Multiple };
                await Dialog("Remove Favorites", list, () => {
                    foreach (var name in list.SelectedItems!.Cast<string>().ToArray()) _settings.Favorites.Remove(name);
                    RefreshFavorites(); SaveSettings(); return Task.CompletedTask;
                });
            }), new Separator()
        };
        foreach (var favorite in _settings.Favorites.OrderBy(p => p.Key))
            items.Add(Item(favorite.Key.Replace("_", "__"), () => Navigate(favorite.Value)));
        _favorites.ItemsSource = items;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _searching) { _searchCancellation?.Cancel(); e.Handled = true; return; }
        Func<Task>? action = null;
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control) { _address.Focus(); _address.SelectAll(); e.Handled = true; return; }
        if (_address.IsKeyboardFocusWithin) return;
        if (this.GetVisualDescendants().OfType<InlineName>().Any(editor => editor.Editing)) return;
        if (e.Key == Key.F5) action = Refresh;
        else if (e.Key == Key.F2) action = Rename;
        else if (e.Key == Key.Delete) action = Delete;
        else if (e.Key == Key.F3) action = FindNext;
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) action = FindDialog;
        else if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control) action = Print;
        else if (e.Key == Key.Enter && _values.IsKeyboardFocusWithin) action = Modify;
        else if (e.Key == Key.Tab) { if (_tree.IsKeyboardFocusWithin) _values.Focus(); else _tree.Focus(); e.Handled = true; return; }
        if (action is not null) { e.Handled = true; await Guard(action); }
    }

    private Window Modal(string title, Control content)
    {
        var window = new Window { Title = title, Icon = Icon, Width = title == "Edit Binary Value" ? 480 : 430, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = FontFamily, FontSize = 12, RequestedThemeVariant = ThemeVariant.Light,
            Foreground = Brushes.Black, Background = new SolidColorBrush(Color.Parse("#f0f0f0")) };
        window.Content = content;
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(false); e.Handled = true; } };
        return window;
    }
    private async Task Dialog(string title, Control fields, Func<Task> accept, Control? focus = null, string okLabel = "OK")
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
        panel.Children.Add(fields);
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(error);
        var ok = new Button { Content = okLabel, MinWidth = 75, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { ok, cancel } });
        var dialog = Modal(title, panel);
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += async (_, _) => {
            ok.IsEnabled = false;
            try { await accept(); dialog.Close(); }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or FormatException or OverflowException) { error.Text = e.Message; error.IsVisible = true; }
            finally { ok.IsEnabled = true; }
        };
        dialog.Opened += (_, _) => { focus?.Focus(); if (focus is TextBox box) box.SelectAll(); };
        await dialog.ShowDialog(this);
    }
    private async Task<string?> Prompt(string title, string label, string initial)
    {
        string? result = null;
        var text = new TextBox { Text = initial };
        await Dialog(title, new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, text } }, () => {
            if (string.IsNullOrWhiteSpace(text.Text)) throw new InvalidOperationException("Enter a name.");
            result = text.Text; return Task.CompletedTask;
        }, text);
        return result;
    }
    private async Task<bool> Confirm(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var yes = new Button { Content = "Yes", MinWidth = 75 };
        var no = new Button { Content = "No", MinWidth = 75, IsDefault = true, IsCancel = true };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } });
        var dialog = Modal(title, panel);
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }
    private Task Message(string title, string text) => Dialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, () => Task.CompletedTask);

    private Settings LoadSettings()
    {
        if (!File.Exists(_settingsPath)) return new Settings();
        try {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(_settingsPath), SettingsJson.Default.Settings) ?? throw new JsonException("Empty settings file.");
            if (string.IsNullOrWhiteSpace(settings.Path) || settings.Favorites is null ||
                settings.FindText is null || settings.SortColumn is < -1 or > 2 ||
                settings.Favorites.Any(f => string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Value)) ||
                !double.IsFinite(settings.Width) || !double.IsFinite(settings.Height) || !double.IsFinite(settings.Splitter) ||
                !double.IsFinite(settings.NameWidth) || !double.IsFinite(settings.TypeWidth))
                throw new JsonException("Saved preferences contain invalid paths or dimensions.");
            if (settings.Favorites.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Favorites.Count)
                throw new JsonException("Saved favorites contain duplicate names.");
            settings.Favorites = new Dictionary<string, string>(settings.Favorites, StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            Dispatcher.UIThread.Post(async () => await Message("Settings", "Could not load saved preferences: " + e.Message));
            return new Settings();
        }
    }
    private void SaveSettings()
    {
        _settings.Path = _path; _settings.Width = Width; _settings.Height = Height;
        _settings.Splitter = _panes.ColumnDefinitions[0].ActualWidth;
        _settings.NameWidth = _values.NameWidth; _settings.TypeWidth = _values.TypeWidth;
        _settings.SortColumn = _values.SortColumn; _settings.SortDescending = _values.SortDescending;
        _settings.StatusBar = _status.IsVisible;
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, SettingsJson.Default.Settings));
            File.Move(temporary, _settingsPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = "Could not save preferences: " + e.Message; Console.Error.WriteLine(_status.Text); }
    }

    private static WindowIcon CreateIcon()
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://RegEditto/Assets/RegistryEditor.ico"));
        return new WindowIcon(stream);
    }
}
