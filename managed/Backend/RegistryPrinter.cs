using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RegistryEditor.Presentation.Backend;

public static class RegistryPrinter
{
    public static Task<bool> PrintAsync(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Printer drivers run their own modal loop; do not block Avalonia's dispatcher or UIA callbacks.
        var thread = new Thread(() => {
            var initialized = false;
            try {
                Marshal.ThrowExceptionForHR(Native.CoInitializeEx(IntPtr.Zero, 2));
                initialized = true;
                // A HWND owned by Avalonia's dispatcher can deadlock cross-thread modal activation.
                completion.SetResult(Print(IntPtr.Zero, lines));
            }
            catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or COMException) { completion.SetException(error); }
            finally { if (initialized) Native.CoUninitialize(); }
        }) { IsBackground = true, Name = "RegEditto printing" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    public static void ExportText(string path, string file)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllLines(temporary, Lines(path), new UnicodeEncoding(false, true, true));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static IEnumerable<string> Lines(string path)
    {
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.TryPop(out var key)) {
            yield return "Key Name: " + key;
            foreach (var value in RegistryBackend.Values(key)) {
                yield return "  Value Name: " + (value.Name.Length == 0 ? "(Default)" : value.Name);
                yield return "  Type: " + value.TypeName;
                yield return "  Data: " + value.DisplayData;
                yield return "";
            }
            yield return "";
            var children = RegistryBackend.Children(key);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(key + "\\" + children[i]);
        }
    }

    public static bool Print(IntPtr owner, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var dialog = new PrintDialog {
            Size = (uint)Marshal.SizeOf<PrintDialog>(), Owner = owner,
            Flags = 0x100 | 0x40000 | 4 | 8 | 0x100000,
            MinimumPage = 1, MaximumPage = ushort.MaxValue, Copies = 1
        };
        var printing = false;
        IntPtr font = IntPtr.Zero, previousFont = IntPtr.Zero;
        try {
            if (!Native.PrintDlg(ref dialog)) {
                var error = Native.CommDlgExtendedError();
                if (error != 0) throw new IOException($"The Windows print dialog failed (0x{error:x8}).");
                return false;
            }
            if (dialog.DeviceContext == IntPtr.Zero) throw new InvalidOperationException("The selected printer did not supply a device context.");
            var dc = dialog.DeviceContext;
            var dpiX = Native.GetDeviceCaps(dc, 88);
            var dpiY = Native.GetDeviceCaps(dc, 90);
            var pageWidth = Native.GetDeviceCaps(dc, 8);
            var pageHeight = Native.GetDeviceCaps(dc, 10);
            if (dpiX <= 0 || dpiY <= 0 || pageWidth <= dpiX / 2 || pageHeight <= dpiY / 2) throw new InvalidOperationException("Invalid printer page metrics.");
            font = Native.CreateFont(-Math.Max(1, dpiY * 9 / 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Consolas");
            if (font == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            previousFont = Native.SelectObject(dc, font);
            if (previousFont == IntPtr.Zero || previousFont == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Check(Native.GetTextExtentPoint32(dc, "M", 1, out var character));
            var marginX = Math.Max(1, dpiX / 4);
            var marginY = Math.Max(1, dpiY / 4);
            var rowHeight = Math.Max(1, character.Height * 6 / 5);
            var characters = Math.Max(1, (pageWidth - marginX * 2) / Math.Max(1, character.Width));
            var document = new DocumentInfo { Size = Marshal.SizeOf<DocumentInfo>(), Name = "RegEditto" };
            Check(Native.StartDoc(dc, ref document) > 0);
            printing = true;
            Check(Native.StartPage(dc) > 0);
            var y = marginY;
            foreach (var line in lines) {
                var start = 0;
                do {
                    if (y + rowHeight > pageHeight - marginY) {
                        Check(Native.EndPage(dc) > 0); Check(Native.StartPage(dc) > 0); y = marginY;
                    }
                    var count = Math.Min(characters, line.Length - start);
                    if (count > 0 && start + count < line.Length && char.IsHighSurrogate(line[start + count - 1])) count--;
                    if (count == 0 && start < line.Length) count = Math.Min(2, line.Length - start);
                    var segment = line.Substring(start, count);
                    Check(Native.TextOut(dc, marginX, y, segment, segment.Length));
                    y += rowHeight; start += count;
                } while (start < line.Length);
            }
            Check(Native.EndPage(dc) > 0); Check(Native.EndDoc(dc) > 0);
            printing = false;
            return true;
        }
        finally {
            if (printing) Native.AbortDoc(dialog.DeviceContext);
            if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) Native.SelectObject(dialog.DeviceContext, previousFont);
            if (font != IntPtr.Zero) Native.DeleteObject(font);
            if (dialog.DeviceContext != IntPtr.Zero) Native.DeleteDC(dialog.DeviceContext);
            if (dialog.DeviceMode != IntPtr.Zero) Native.GlobalFree(dialog.DeviceMode);
            if (dialog.DeviceNames != IntPtr.Zero) Native.GlobalFree(dialog.DeviceNames);
        }
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrintDialog
    {
        public uint Size;
        public IntPtr Owner, DeviceMode, DeviceNames, DeviceContext;
        public uint Flags;
        public ushort FromPage, ToPage, MinimumPage, MaximumPage, Copies;
        public IntPtr Instance, CustomData, PrintHook, SetupHook, PrintTemplate, SetupTemplate, PrintTemplateHandle, SetupTemplateHandle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocumentInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public IntPtr Output, DataType;
        public uint Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextSize { public int Width, Height; }
    private static class Native
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("comdlg32.dll", EntryPoint = "PrintDlgW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintDlg(ref PrintDialog dialog);
        [DllImport("comdlg32.dll", ExactSpelling = true)]
        internal static extern uint CommDlgExtendedError();
        [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", SetLastError = true)]
        internal static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out TextSize size);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartDocW", SetLastError = true)]
        internal static extern int StartDoc(IntPtr dc, ref DocumentInfo document);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern int StartPage(IntPtr dc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndPage(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndDoc(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern int AbortDoc(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
    }
}
