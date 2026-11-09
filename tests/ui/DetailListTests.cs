using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RegistryEditor.Presentation.Views;
using RegistryEditor.Presentation;
using Xunit;

namespace RegistryEditor.UI.Tests;

public sealed class DetailListTests
{
    [Fact]
    public async Task ReportAndEditors_PreserveCellsCommitCancelAndEditHexAscii()
    {
        AppBuilder.Configure<RegistryApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var list = new DetailList();
        var window = new Window { Width = 700, Height = 300, Content = list, Background = Brushes.White };
        try {
            window.Show();
            Pump(window);
            var valueName = new TextBox { Text = "Options", IsReadOnly = true };
            var cancelButton = new Button { Content = "Cancel" };
            var modal = new Window { Width = 400, Content = new StackPanel { Children = { valueName, cancelButton } } };
            try {
                modal.Show();
                Pump(modal);
                Assert.NotNull(valueName.Template);
                Assert.NotNull(cancelButton.Template);
                Assert.True(valueName.Bounds.Height >= 20, "Modal value names need a visible themed text field.");
                Assert.Contains(valueName.GetVisualDescendants().OfType<TextPresenter>(), presenter => presenter.Text == "Options");
                var textPoint = valueName.TranslatePoint(new Point(valueName.Bounds.Width / 2, valueName.Bounds.Height / 2), modal)!.Value;
                Assert.Equal("Ibeam", modal.InputHitTest(textPoint)?.Cursor?.ToString());
            }
            finally { modal.Close(); }
            list.ItemsSource = new[] {
                new ValueRow { Name = "First", Type = "REG_SZ", Data = "one" },
                new ValueRow { Name = "Second", Type = "REG_DWORD", Data = "0x00000001 (1)" }
            };
            Pump(window);
            var inner = list.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.True(inner.Bounds.Height > 0, $"window={window.Bounds}; report={list.Bounds}; list={inner.Bounds}; template={inner.Theme}");
            Assert.Null(inner.ItemTemplate!.Build(null));
            var first = inner.ItemTemplate.Build(new ValueRow { Name = "First", Type = "REG_SZ", Data = "one" });
            var second = inner.ItemTemplate.Build(new ValueRow { Name = "Second", Type = "REG_DWORD", Data = "0x00000001 (1)" });
            Assert.NotNull(first); Assert.NotNull(second);
            var text = first.GetVisualDescendants().Concat(second.GetVisualDescendants()).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("First", text);
            Assert.Contains("Second", text);
            Assert.Contains("REG_SZ", text);
            Assert.Contains("REG_DWORD", text);
            Assert.Contains("0x00000001 (1)", text);
            list.ItemsSource = Array.Empty<ValueRow>();
            Pump(window);
            Assert.Empty(inner.Items);
            list.ItemsSource = new[] { new ValueRow { Name = "Replacement", Type = "REG_QWORD", Data = "42" } };
            Pump(window);
            Assert.Single(inner.Items);
            var replacement = inner.ItemTemplate.Build(new ValueRow { Name = "Replacement", Type = "REG_QWORD", Data = "42" });
            Assert.NotNull(replacement);
            Assert.Contains("Replacement", replacement.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.DoesNotContain("First", replacement.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

            AssertHeaderResizeCursorsAndDrag(list, window);
            AssertLongCellsStayInsideColumns(list, window);

            var label = new InlineName("Before", new object());
            window.Content = label; Pump(window);
            var commit = label.Edit();
            var nameInput = label.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(label.Editing);
            Assert.Equal("Before", nameInput.Text);
            nameInput.Text = "After";
            nameInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.True(commit.IsCompletedSuccessfully);
            Assert.Equal("After", await commit);
            Assert.False(label.Editing);
            var cancel = label.Edit();
            nameInput.Text = "Cancelled";
            nameInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.True(cancel.IsCompletedSuccessfully);
            Assert.Null(await cancel);
            Assert.False(label.Editing);

            var binary = new HexEditor(new byte[] { 0, 1, 2 });
            window.Content = binary; Pump(window); binary.Focus();
            Assert.Equal("Ibeam", binary.Cursor?.ToString());
            binary.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "ab" });
            Assert.Equal(new byte[] { 0xab, 1, 2 }, binary.Document.Bytes);
            binary.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            binary.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "Z" });
            Assert.Equal(new byte[] { 0xab, (byte)'Z', 2 }, binary.Document.Bytes);
            binary.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
            binary.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
            Assert.Empty(binary.Document.Bytes);

            var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("200,3,*") };
            panes.Children.Add(new Border { Background = Brushes.White });
            var splitter = new GridSplitter {
                Width = 3, Background = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns
            };
            Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
            window.Content = panes; Pump(window);
            var splitterPoint = splitter.TranslatePoint(new Point(1, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseMove(splitterPoint); Pump(window);
            Assert.Equal("SizeWestEast", window.InputHitTest(splitterPoint)?.Cursor?.ToString());
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void AssertHeaderResizeCursorsAndDrag(DetailList list, Window window)
    {
        var grips = list.GetVisualDescendants().OfType<Thumb>().ToArray();
        Assert.Equal(2, grips.Length);
        for (var index = 0; index < grips.Length; index++) {
            var grip = grips[index];
            var point = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            Pump(window);
            Assert.True(grip.IsPointerOver, "The resize grip must have a hit-testable surface.");
            Assert.Equal("SizeWestEast", window.InputHitTest(point)?.Cursor?.ToString());
            var before = index == 0 ? list.NameWidth : list.TypeWidth;
            IPointer? pointer = null;
            grip.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, handledEventsToo: true);
            window.MouseDown(point, MouseButton.Left);
            var end = point + new Vector(30, 40);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Pump(window);
            Assert.Equal(before + 30, index == 0 ? list.NameWidth : list.TypeWidth, 1);
            Assert.NotNull(pointer);
            var captured = Assert.IsAssignableFrom<Control>(pointer.Captured);
            Assert.True(captured == grip || captured.GetVisualAncestors().Contains(grip));
            Assert.Equal("SizeWestEast", captured.Cursor?.ToString());
            window.MouseUp(end, MouseButton.Left);
            Pump(window);
            Assert.Null(pointer.Captured);
            Assert.False(grip.IsPointerOver);
            Assert.NotEqual("SizeWestEast", window.InputHitTest(end)?.Cursor?.ToString());
        }
    }

    private static void AssertLongCellsStayInsideColumns(DetailList list, Window window)
    {
        var row = new ValueRow {
            Name = "A very long value name that must never overlap the Type column",
            Type = "REG_RESOURCE_REQUIREMENTS_LIST",
            Data = string.Join(" ", Enumerable.Repeat("00", 200))
        };
        list.ItemsSource = new[] { row };
        foreach (var width in new[] { 125d, 60d, 200d, 125d }) {
            list.SetWidths(width, width);
            Pump(window);
            var editor = list.NameEditor(row);
            Assert.NotNull(editor);
            var label = editor.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.InRange(editor.Bounds.Width, 1, width - 20);
            Assert.True(label.TextLayout.TextLines.Any(line => line.HasCollapsed), "Long names must display an ellipsis.");
            var cells = list.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var type = cells.Single(cell => cell.Text == row.Type);
            Assert.InRange(type.Bounds.Width, 1, width);
            Assert.True(type.TextLayout.TextLines.Any(line => line.HasCollapsed), "Long types must display an ellipsis.");
            var data = cells.Single(cell => cell.Text == row.Data);
            Assert.InRange(data.Bounds.Width, 1, window.Bounds.Width - 2 * width);
            Assert.True(data.TextLayout.TextLines.Any(line => line.HasCollapsed), "Long data must fit the remaining viewport.");
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
