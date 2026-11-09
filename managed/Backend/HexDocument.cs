using System;
using System.Collections.Generic;
using System.Linq;

namespace RegistryEditor.Presentation.Backend;

public sealed class HexDocument
{
    private readonly List<byte> _bytes;
    private int _caret, _anchor;
    private bool _lowNibble;
    public HexDocument(byte[] bytes) => _bytes = new List<byte>(bytes);
    public int Length => _bytes.Count;
    public int Caret => _caret;
    public int SelectionStart => Math.Min(_caret, _anchor);
    public int SelectionLength => Math.Abs(_caret - _anchor);
    public bool LowNibble => _lowNibble;
    public bool InsertMode { get; set; }
    public byte this[int index] => _bytes[index];
    public byte[] Bytes => _bytes.ToArray();
    public void Move(int position, bool extend = false)
    {
        _caret = Math.Clamp(position, 0, Length);
        if (!extend) _anchor = _caret;
        _lowNibble = false;
    }
    public void SelectAll() { _anchor = 0; _caret = Length; _lowNibble = false; }
    public byte[] SelectedBytes => _bytes.Skip(SelectionStart).Take(SelectionLength).ToArray();
    private void RemoveSelection()
    {
        var start = SelectionStart;
        _bytes.RemoveRange(start, SelectionLength);
        _caret = _anchor = start; _lowNibble = false;
    }
    public void Delete(bool backwards)
    {
        if (SelectionLength > 0) { RemoveSelection(); return; }
        var index = backwards ? _caret - 1 : _caret;
        if (index < 0 || index >= Length) return;
        _bytes.RemoveAt(index); Move(index);
    }
    public void Paste(byte[] bytes)
    {
        var selected = SelectionLength > 0;
        if (selected) RemoveSelection();
        if (!InsertMode && !selected) _bytes.RemoveRange(_caret, Math.Min(bytes.Length, Length - _caret));
        _bytes.InsertRange(_caret, bytes);
        Move(_caret + bytes.Length);
    }
    public void WriteHex(char ch)
    {
        var nibble = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
        if (nibble < 0) throw new FormatException("Enter a hexadecimal digit from 0 to 9 or A to F.");
        var replacingSelection = SelectionLength > 0;
        if (replacingSelection) RemoveSelection();
        if (!_lowNibble && (replacingSelection || InsertMode || _caret == Length)) _bytes.Insert(_caret, 0);
        if (_lowNibble) {
            _bytes[_caret] = (byte)((_bytes[_caret] & 0xf0) | nibble);
            Move(_caret + 1);
        } else {
            _bytes[_caret] = (byte)((_bytes[_caret] & 0x0f) | nibble << 4);
            _lowNibble = true;
        }
    }
    public void WriteAscii(char ch)
    {
        if (ch > 0x7f || char.IsControl(ch)) throw new FormatException("The ASCII pane accepts printable ASCII characters only.");
        Paste(new[] { (byte)ch });
    }
}
