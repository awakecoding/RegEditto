using System;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class HexDocumentTests
{
    [Fact]
    public void Move_ClampsCaretAndExtendsSelectionWithoutMovingAnchor()
    {
        var document = new HexDocument(new byte[] { 0x10, 0x20, 0x30, 0x40 });

        document.Move(2);
        document.Move(int.MaxValue, extend: true);

        Assert.Equal(4, document.Caret);
        Assert.Equal(2, document.SelectionStart);
        Assert.Equal(2, document.SelectionLength);
        Assert.Equal(new byte[] { 0x30, 0x40 }, document.SelectedBytes);
        Assert.False(document.LowNibble);

        document.Move(int.MinValue);
        Assert.Equal(0, document.Caret);
        Assert.Equal(0, document.SelectionStart);
        Assert.Equal(0, document.SelectionLength);

        document.Move(3);
        document.Move(1, extend: true);
        Assert.Equal(1, document.SelectionStart);
        Assert.Equal(2, document.SelectionLength);
        Assert.Equal(new byte[] { 0x20, 0x30 }, document.SelectedBytes);
    }

    [Fact]
    public void SelectAll_SelectsEveryByteAndEmptyDocumentRemainsEmpty()
    {
        var document = new HexDocument(new byte[] { 1, 2, 3 });
        document.SelectAll();

        Assert.Equal(0, document.SelectionStart);
        Assert.Equal(3, document.SelectionLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, document.SelectedBytes);

        var empty = new HexDocument(Array.Empty<byte>());
        empty.SelectAll();
        Assert.Equal(0, empty.Caret);
        Assert.Equal(0, empty.SelectionLength);
        Assert.Empty(empty.SelectedBytes);
    }

    [Fact]
    public void WriteHex_OverwritesHighThenLowNibbleAndAdvancesAfterLowNibble()
    {
        var document = new HexDocument(new byte[] { 0xab, 0xcd });
        document.Move(0);

        document.WriteHex('1');
        Assert.Equal(new byte[] { 0x1b, 0xcd }, document.Bytes);
        Assert.Equal(0, document.Caret);
        Assert.True(document.LowNibble);

        document.WriteHex('2');
        Assert.Equal(new byte[] { 0x12, 0xcd }, document.Bytes);
        Assert.Equal(1, document.Caret);
        Assert.False(document.LowNibble);

        document.WriteHex('f');
        document.WriteHex('F');
        Assert.Equal(new byte[] { 0x12, 0xff }, document.Bytes);
        Assert.Equal(2, document.Caret);
        Assert.False(document.LowNibble);
    }

    [Fact]
    public void WriteHex_AtEndAppendsOneByteInReplaceMode()
    {
        var document = new HexDocument(new byte[] { 0x12 });
        document.Move(document.Length);

        document.WriteHex('a');
        Assert.Equal(new byte[] { 0x12, 0xa0 }, document.Bytes);
        Assert.True(document.LowNibble);
        document.WriteHex('B');

        Assert.Equal(new byte[] { 0x12, 0xab }, document.Bytes);
        Assert.Equal(2, document.Caret);
        Assert.False(document.LowNibble);
    }

    [Fact]
    public void WriteHex_InsertModeInsertsRatherThanOverwriting()
    {
        var document = new HexDocument(new byte[] { 0x10, 0x20 });
        document.InsertMode = true;
        document.Move(1);

        document.WriteHex('f');
        document.WriteHex('e');

        Assert.Equal(new byte[] { 0x10, 0xfe, 0x20 }, document.Bytes);
        Assert.Equal(2, document.Caret);
        Assert.False(document.LowNibble);
    }

    [Fact]
    public void WriteHex_ReplacesSelectionBeforeWritingTheNewByte()
    {
        var document = new HexDocument(new byte[] { 0x10, 0x20, 0x30, 0x40 });
        document.Move(1);
        document.Move(3, extend: true);

        document.WriteHex('a');
        document.WriteHex('b');

        Assert.Equal(new byte[] { 0x10, 0xab, 0x40 }, document.Bytes);
        Assert.Equal(2, document.Caret);
        Assert.Equal(0, document.SelectionLength);
    }

    [Fact]
    public void Paste_UsesInsertOverwriteAndSelectionReplacementSemantics()
    {
        var insert = new HexDocument(new byte[] { 0x10, 0x20 });
        insert.InsertMode = true;
        insert.Move(1);
        insert.Paste(new byte[] { 0xaa, 0xbb });
        Assert.Equal(new byte[] { 0x10, 0xaa, 0xbb, 0x20 }, insert.Bytes);
        Assert.Equal(3, insert.Caret);

        var overwrite = new HexDocument(new byte[] { 0x10, 0x20, 0x30 });
        overwrite.Move(1);
        overwrite.Paste(new byte[] { 0xaa, 0xbb, 0xcc, 0xdd });
        Assert.Equal(new byte[] { 0x10, 0xaa, 0xbb, 0xcc, 0xdd }, overwrite.Bytes);
        Assert.Equal(5, overwrite.Caret);

        var selected = new HexDocument(new byte[] { 0x10, 0x20, 0x30, 0x40 });
        selected.InsertMode = true;
        selected.Move(1);
        selected.Move(3, extend: true);
        selected.Paste(new byte[] { 0xaa });
        Assert.Equal(new byte[] { 0x10, 0xaa, 0x40 }, selected.Bytes);
        Assert.Equal(2, selected.Caret);

        var emptyPaste = new HexDocument(new byte[] { 1, 2 });
        emptyPaste.Move(1);
        emptyPaste.Paste(Array.Empty<byte>());
        Assert.Equal(new byte[] { 1, 2 }, emptyPaste.Bytes);
        Assert.Equal(1, emptyPaste.Caret);
    }

    [Fact]
    public void Delete_RemovesForwardBackwardAndSelectedBytesButNotPastBoundaries()
    {
        var document = new HexDocument(new byte[] { 0x10, 0x20, 0x30 });
        document.Move(1);
        document.Delete(backwards: false);
        Assert.Equal(new byte[] { 0x10, 0x30 }, document.Bytes);
        Assert.Equal(1, document.Caret);

        document.Delete(backwards: true);
        Assert.Equal(new byte[] { 0x30 }, document.Bytes);
        Assert.Equal(0, document.Caret);

        document.Delete(backwards: true);
        Assert.Equal(new byte[] { 0x30 }, document.Bytes);
        document.Move(document.Length);
        document.Delete(backwards: false);
        Assert.Equal(new byte[] { 0x30 }, document.Bytes);
        Assert.Equal(1, document.Caret);

        document.Move(0);
        document.Move(1, extend: true);
        document.Delete(backwards: true);
        Assert.Empty(document.Bytes);
        Assert.Equal(0, document.Caret);
        Assert.Equal(0, document.SelectionLength);
    }

    [Fact]
    public void WriteAscii_InsertsAndOverwritesPrintableAsciiIncludingBoundaries()
    {
        var document = new HexDocument(new byte[] { 0x41, 0x42 });
        document.Move(1);
        document.WriteAscii('~');
        Assert.Equal(new byte[] { 0x41, 0x7e }, document.Bytes);
        Assert.Equal(2, document.Caret);

        document.InsertMode = true;
        document.Move(0);
        document.WriteAscii(' ');
        Assert.Equal(new byte[] { 0x20, 0x41, 0x7e }, document.Bytes);
        Assert.Equal(1, document.Caret);
    }

    [Fact]
    public void WriteAscii_ReplacesSelectionAndRejectsControlsAndNonAsciiWithoutMutation()
    {
        var document = new HexDocument(new byte[] { 0x41, 0x42, 0x43 });
        document.Move(0);
        document.Move(2, extend: true);
        document.WriteAscii('Z');
        Assert.Equal(new byte[] { 0x5a, 0x43 }, document.Bytes);
        document.Move(0);
        document.Move(1, extend: true);

        foreach (var invalid in new[] { '\0', '\n', '\x7f', '\u0080', '\ud800' }) {
            var before = document.Bytes;
            var caret = document.Caret;
            var selectionStart = document.SelectionStart;
            var selectionLength = document.SelectionLength;
            Assert.Throws<FormatException>(() => document.WriteAscii(invalid));
            Assert.Equal(before, document.Bytes);
            Assert.Equal(caret, document.Caret);
            Assert.Equal(selectionStart, document.SelectionStart);
            Assert.Equal(selectionLength, document.SelectionLength);
        }
    }

    [Fact]
    public void WriteHex_RejectsInvalidDigitWithoutChangingBytesOrPendingNibble()
    {
        var document = new HexDocument(new byte[] { 0xab });
        document.Move(0);
        document.WriteHex('a');
        var before = document.Bytes;

        Assert.Throws<FormatException>(() => document.WriteHex('g'));

        Assert.Equal(before, document.Bytes);
        Assert.Equal(0, document.Caret);
        Assert.True(document.LowNibble);
    }

    [Fact]
    public void EmptyAndInvalidInputsRespectDocumentBoundaries()
    {
        var empty = new HexDocument(Array.Empty<byte>());
        empty.Move(-1);
        empty.Delete(backwards: true);
        empty.Delete(backwards: false);
        empty.Paste(Array.Empty<byte>());
        Assert.Empty(empty.Bytes);
        Assert.Equal(0, empty.Caret);

        empty.WriteHex('0');
        empty.WriteHex('f');
        Assert.Equal(new byte[] { 0x0f }, empty.Bytes);
        Assert.Throws<ArgumentNullException>(() => new HexDocument(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = empty[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = empty[1]);
    }

    [Fact]
    public void ConstructorAndByteSnapshotsNeverExposeTheMutableBackingBuffer()
    {
        var original = new byte[] { 0x11, 0x22 };
        var document = new HexDocument(original);
        original[0] = 0xff;
        var bytes = document.Bytes;
        bytes[1] = 0xee;

        Assert.Equal(new byte[] { 0x11, 0x22 }, document.Bytes);
        document.SelectAll();
        var selected = document.SelectedBytes;
        selected[0] = 0xdd;
        Assert.Equal(new byte[] { 0x11, 0x22 }, document.Bytes);
    }
}
