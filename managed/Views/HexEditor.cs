using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using RegistryEditor.Presentation.Backend;

namespace RegistryEditor.Presentation.Views;

public sealed class HexEditor : Control
{
    private const double CharacterWidth = 7.3, RowHeight = 18;
    private const int BytesPerRow = 8;
    private readonly Typeface _typeface = new("Consolas");
    private bool _ascii, _dragging;
    public HexDocument Document { get; }
    public event Action<string>? Error;
    public HexEditor(byte[] bytes)
    {
        Document = new HexDocument(bytes);
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        MinWidth = 410;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize) => new(410, Math.Max(150, (Document.Length / BytesPerRow + 1) * RowHeight + 8));
    private FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, 12, brush);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        for (var row = 0; row <= Document.Length / BytesPerRow; row++) {
            var offset = row * BytesPerRow;
            var y = row * RowHeight + 3;
            context.DrawText(Text(offset.ToString("x8", CultureInfo.InvariantCulture), Brushes.DimGray), new Point(3, y));
            for (var col = 0; col < BytesPerRow && offset + col < Document.Length; col++) {
                var index = offset + col;
                var selected = index >= Document.SelectionStart && index < Document.SelectionStart + Document.SelectionLength;
                var brush = selected ? Brushes.White : Brushes.Black;
                var hexX = (10 + col * 3) * CharacterWidth;
                var asciiX = (36 + col) * CharacterWidth;
                if (selected) {
                    context.FillRectangle(Brushes.DodgerBlue, new Rect(hexX, y, CharacterWidth * 2, RowHeight));
                    context.FillRectangle(Brushes.DodgerBlue, new Rect(asciiX, y, CharacterWidth, RowHeight));
                }
                context.DrawText(Text(Document[index].ToString("x2", CultureInfo.InvariantCulture), brush), new Point(hexX, y));
                var ch = Document[index] is >= 32 and <= 126 ? (char)Document[index] : '.';
                context.DrawText(Text(ch.ToString(), brush), new Point(asciiX, y));
            }
        }
        if (IsFocused) {
            var col = Document.Caret % BytesPerRow;
            var x = (_ascii ? 36 + col : 10 + col * 3 + (Document.LowNibble ? 1 : 0)) * CharacterWidth;
            var y = Document.Caret / BytesPerRow * RowHeight + 3;
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(x, y, CharacterWidth, RowHeight - 2));
        }
    }
    private void Changed()
    {
        InvalidateMeasure(); InvalidateVisual();
        this.BringIntoView(new Rect(0, Document.Caret / BytesPerRow * RowHeight, 410, RowHeight));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); _dragging = true; e.Pointer.Capture(this);
        PointToCaret(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true;
    }
    private void PointToCaret(Point point, bool extend)
    {
        _ascii = point.X >= 35 * CharacterWidth;
        var col = _ascii ? (int)(point.X / CharacterWidth) - 36 : ((int)(point.X / CharacterWidth) - 10) / 3;
        Document.Move(Math.Max(0, (int)((point.Y - 3) / RowHeight)) * BytesPerRow + Math.Clamp(col, 0, BytesPerRow - 1), extend);
        Changed();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) PointToCaret(e.GetPosition(this), true);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); _dragging = false; e.Pointer.Capture(null); }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        try {
            foreach (var ch in e.Text) { if (_ascii) Document.WriteAscii(ch); else Document.WriteHex(ch); }
            Changed();
        }
        catch (FormatException error) { Error?.Invoke(error.Message); }
        e.Handled = true;
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (control && e.Key is Key.C or Key.X or Key.V) {
            e.Handled = true;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { Error?.Invoke("Clipboard unavailable."); return; }
            try {
                if (e.Key == Key.V) {
                    var text = await clipboard.TryGetTextAsync() ?? "";
                    byte[] bytes;
                    if (_ascii) {
                        if (text.Any(ch => ch > 127)) throw new FormatException("Paste ASCII text in the ASCII pane.");
                        bytes = Encoding.ASCII.GetBytes(text);
                    } else bytes = RegistryBackend.ParseBytes(text);
                    Document.Paste(bytes);
                } else {
                    var bytes = Document.SelectedBytes;
                    await clipboard.SetTextAsync(_ascii ? Encoding.ASCII.GetString(bytes) : string.Join(" ", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))));
                    if (e.Key == Key.X && Document.SelectionLength > 0) Document.Delete(false);
                }
                Changed();
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException) { Error?.Invoke(error.Message); }
            return;
        }
        switch (e.Key) {
            case Key.Left: Document.Move(Document.Caret - 1, extend); break;
            case Key.Right: Document.Move(Document.Caret + 1, extend); break;
            case Key.Up: Document.Move(Document.Caret - BytesPerRow, extend); break;
            case Key.Down: Document.Move(Document.Caret + BytesPerRow, extend); break;
            case Key.Home: Document.Move(control ? 0 : Document.Caret / BytesPerRow * BytesPerRow, extend); break;
            case Key.End: Document.Move(control ? Document.Length : (Document.Caret / BytesPerRow + 1) * BytesPerRow, extend); break;
            case Key.PageUp: Document.Move(Document.Caret - 128, extend); break;
            case Key.PageDown: Document.Move(Document.Caret + 128, extend); break;
            case Key.Delete: Document.Delete(false); break;
            case Key.Back: Document.Delete(true); break;
            case Key.Insert: Document.InsertMode = !Document.InsertMode; break;
            case Key.A when control: Document.SelectAll(); break;
            case Key.Tab when !control: _ascii = !_ascii; break;
            default: return;
        }
        e.Handled = true; Changed();
    }
}
