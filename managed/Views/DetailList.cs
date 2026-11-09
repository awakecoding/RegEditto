using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RegistryEditor.Presentation.Views;

// A report-mode list using the producer's virtualized ListBox, with resizeable column headers.
public sealed class DetailList : Grid
{
    private readonly ListBox _list = new() {
        Background = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        SelectionMode = SelectionMode.Multiple,
        AutoScrollToSelectedItem = true
    };
    private readonly Grid _header = new() { Height = 22.4, Background = Brushes.White };
    private readonly List<Grid> _visibleRows = new();
    private IEnumerable<ValueRow> _items = Array.Empty<ValueRow>();
    private bool _descending;
    private int _sortColumn = -1;
    public int SortColumn => _sortColumn;
    public bool SortDescending => _descending;
    public double NameWidth { get; private set; } = 125;
    public double TypeWidth { get; private set; } = 125;
    public IEnumerable ItemsSource
    {
        get => _items;
        set {
            _items = value.Cast<ValueRow>().ToArray();
            _list.ItemsSource = Sorted();
        }
    }
    public object? SelectedItem { get => _list.SelectedItem; set => _list.SelectedItem = value; }
    public IList SelectedItems => _list.SelectedItems!;
    public bool Focus() => _list.Focus();

    public DetailList()
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        RowDefinitions = new RowDefinitions("Auto,*");
        var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _list };
        SetRow(border, 1); Children.Add(border);
        _header.ColumnDefinitions = Columns();
        for (var index = 0; index < 3; index++) {
            var column = index;
            var header = new Button {
                Content = new TextBlock { Text = new[] { "Name", "Type", "Data" }[index] },
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#e0e0e0")), BorderThickness = new Thickness(0, 0, 1, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 1), MinHeight = 0
            };
            header.Click += (_, _) => {
                _descending = _sortColumn == column && !_descending; _sortColumn = column;
                _list.ItemsSource = Sorted();
            };
            SetColumn(header, index); _header.Children.Add(header);
            if (index < 2) {
                var thumb = new Thumb {
                    Width = 4, HorizontalAlignment = HorizontalAlignment.Right,
                    Cursor = new Cursor(StandardCursorType.SizeWestEast),
                    Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brushes.Transparent })
                };
                thumb.DragDelta += (_, e) => SetWidths(column == 0 ? NameWidth + e.Vector.X : NameWidth, column == 1 ? TypeWidth + e.Vector.X : TypeWidth);
                SetColumn(thumb, index); _header.Children.Add(thumb);
            }
        }
        Children.Add(_header);
        _list.ItemTemplate = new FuncDataTemplate<ValueRow>((row, _) => {
            if (row is null) return null;
            var grid = new Grid { ColumnDefinitions = Columns(), Height = 17.6, ClipToBounds = true };
            var name = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), ClipToBounds = true };
            name.Children.Add(new TextBlock { Text = row.Kind is 1 or 2 or 7 ? "ab" : "01", Foreground = row.Kind is 1 or 2 or 7 ? Brushes.DarkRed : Brushes.DarkBlue, FontSize = 10, Width = 16 });
            var editor = new InlineName(row.DisplayName, row) { ClipToBounds = true };
            SetColumn(editor, 1); name.Children.Add(editor);
            grid.Children.Add(name);
            var type = Cell(row.Type); SetColumn(type, 1); grid.Children.Add(type);
            var data = Cell(row.Data); SetColumn(data, 2); grid.Children.Add(data);
            grid.AttachedToVisualTree += (_, _) => { _visibleRows.Add(grid); Resize(grid); };
            grid.DetachedFromVisualTree += (_, _) => _visibleRows.Remove(grid);
            return grid;
        });
    }
    public InlineName? NameEditor(ValueRow row) => _visibleRows
        .SelectMany(grid => grid.Children.OfType<Grid>())
        .SelectMany(panel => panel.Children.OfType<InlineName>()).FirstOrDefault(editor => ReferenceEquals(editor.Tag, row));
    private static TextBlock Cell(string text) => new() { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) };
    private ColumnDefinitions Columns() => new() { new ColumnDefinition(NameWidth, GridUnitType.Pixel), new ColumnDefinition(TypeWidth, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) };
    private IEnumerable<ValueRow> Sorted()
    {
        if (_sortColumn < 0) return _items;
        Func<ValueRow, string> key = _sortColumn switch { 0 => r => r.Name, 1 => r => r.Type, _ => r => r.Data };
        return _descending ? _items.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : _items.OrderBy(key, StringComparer.OrdinalIgnoreCase);
    }
    public void SetSort(int column, bool descending)
    {
        if (column is < -1 or > 2) throw new ArgumentOutOfRangeException(nameof(column));
        _sortColumn = column; _descending = descending;
        _list.ItemsSource = Sorted();
    }
    private void Resize(Grid grid)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(NameWidth);
        grid.ColumnDefinitions[1].Width = new GridLength(TypeWidth);
    }
    public void SetWidths(double name, double type)
    {
        NameWidth = Math.Clamp(name, 40, 1000); TypeWidth = Math.Clamp(type, 40, 1000);
        Resize(_header);
        foreach (var grid in _visibleRows) Resize(grid);
    }
}
