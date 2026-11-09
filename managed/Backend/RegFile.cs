using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RegistryEditor.Presentation.Backend;

public abstract record RegistryChange(string Path);
public sealed record CreateKeyChange(string Key) : RegistryChange(Key);
public sealed record DeleteKeyChange(string Key) : RegistryChange(Key);
public sealed record SetValueChange(string Key, RawValue Value) : RegistryChange(Key);
public sealed record DeleteValueChange(string Key, string Name) : RegistryChange(Key);

public static class RegFile
{
    public static string Read(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) {
            if ((bytes.Length & 1) != 0) throw new FormatException("Odd UTF-16 registry file length.");
            return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "REGEDIT4")
            return AnsiEncoding().GetString(bytes);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }
    private static Encoding AnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(checked((int)GetACP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string Export(string path)
        => Export(ExportPaths(path));

    private static IEnumerable<string> ExportPaths(string path)
    {
        if (path == "Computer") return RegistryBackend.Hives;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path[2..].Contains('\\'))
            return new[] { path + "\\HKEY_LOCAL_MACHINE", path + "\\HKEY_USERS" };
        return new[] { path };
    }

    public static string Export(IEnumerable<string> paths, System.Threading.CancellationToken cancellation = default)
    {
        var output = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        var stack = new Stack<string>(paths.Select(path => RegistryBackend.ParsePath(path).ToString()).Reverse());
        while (stack.TryPop(out var key)) {
            cancellation.ThrowIfCancellationRequested();
            if (key.Contains(']') || key.Contains('\n') || key.Contains('\r')) throw new InvalidOperationException("This key name cannot be represented safely in a .reg section.");
            var parsed = RegistryBackend.ParsePath(key);
            // .reg sections describe local hives; a remote snapshot imports onto the target machine.
            var section = parsed.HiveName + (parsed.Subkey.Length == 0 ? "" : "\\" + parsed.Subkey);
            output.Append('[').Append(section).Append("]\r\n");
            foreach (var value in RegistryBackend.Values(key)) {
                cancellation.ThrowIfCancellationRequested();
                if (value.Name.Contains('\n') || value.Name.Contains('\r')) throw new InvalidOperationException("This value name cannot be represented safely in a .reg file.");
                output.Append(value.Name.Length == 0 ? "@" : Quote(value.Name)).Append('=');
                if (value.Kind == 1 && !value.Edit.Contains('\0') && !value.Edit.Contains('\r') && !value.Edit.Contains('\n') &&
                    Encoding.Unicode.GetBytes(value.Edit + '\0').SequenceEqual(value.Bytes)) {
                    output.Append(Quote(value.Edit)).Append("\r\n");
                    continue;
                }
                if (value.Kind == 4 && value.Bytes.Length == 4) {
                    output.Append("dword:").Append(BinaryPrimitives.ReadUInt32LittleEndian(value.Bytes).ToString("x8", CultureInfo.InvariantCulture)).Append("\r\n");
                    continue;
                }
                output.Append(value.Kind == 3 ? "hex:" : $"hex({value.Kind:x}):");
                for (var index = 0; index < value.Bytes.Length; index++) {
                    if (index > 0) {
                        output.Append(',');
                        if (index % 24 == 0) output.Append("\\\r\n  ");
                    }
                    output.Append(value.Bytes[index].ToString("x2", CultureInfo.InvariantCulture));
                }
                output.Append("\r\n");
            }
            output.Append("\r\n");
            foreach (var child in RegistryBackend.Children(key).Reverse()) stack.Push(key + "\\" + child);
        }
        return output.ToString();
    }

    public static void ExportToFile(string path, string file, bool legacy = false)
        => ExportToFile(ExportPaths(path), file, legacy);

    public static void ExportToFile(IEnumerable<string> paths, string file, bool legacy = false)
    {
        var contents = Export(paths);
        if (legacy) contents = contents.Replace("Windows Registry Editor Version 5.00", "REGEDIT4", StringComparison.Ordinal);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, contents, legacy ? AnsiEncoding() : new UnicodeEncoding(false, true, true));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<RegistryChange> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var header = lines[0].Trim().TrimStart('\uFEFF');
        if (header != "Windows Registry Editor Version 5.00" && header != "REGEDIT4")
            throw new FormatException("Expected a Windows Registry Editor Version 5.00 or REGEDIT4 header.");
        var changes = new List<RegistryChange>();
        string? current = null;
        var pending = new StringBuilder();
        var continuing = false;
        for (var index = 1; index < lines.Length; index++) {
            var line = lines[index].Trim();
            try {
                if (!continuing && (line.Length == 0 || line.StartsWith(';'))) continue;
                if (continuing && line.Length == 0) throw new FormatException("Unterminated hexadecimal continuation.");
                if (line.EndsWith('\\')) {
                    if (!continuing) {
                        if (current is null) throw new FormatException("A hexadecimal continuation must be inside a key section.");
                        var continuationRest = line.StartsWith('@') ? line[1..] : Unquote(line).Remaining;
                        continuationRest = continuationRest.TrimStart();
                        if (!continuationRest.StartsWith('=')) throw new FormatException("Expected '=' after value name.");
                        var continuationData = continuationRest[1..].TrimStart();
                        if (!continuationData.StartsWith("hex:", StringComparison.Ordinal) && !continuationData.StartsWith("hex(", StringComparison.Ordinal))
                            throw new FormatException("Only hexadecimal values may be continued.");
                    }
                    pending.Append(line.AsSpan(0, line.Length - 1));
                    continuing = true;
                    continue;
                }
                pending.Append(line);
                line = pending.ToString(); pending.Clear();
                continuing = false;
                if (line.StartsWith('[') && line.EndsWith(']')) {
                    var section = line[1..^1]; var delete = section.StartsWith('-');
                    var path = RegistryBackend.ParsePath(delete ? section[1..] : section);
                    if (path.Computer is not null) throw new FormatException("Remote key paths are not valid .reg sections.");
                    if (delete && path.Subkey.Length == 0) throw new FormatException("Cannot delete a registry hive.");
                    current = delete ? null : path.ToString();
                    changes.Add(delete ? new DeleteKeyChange(path.ToString()) : new CreateKeyChange(path.ToString()));
                    continue;
                }
                if (current is null) throw new FormatException("A registry value must be inside a key section.");
                var name = ""; var rest = line;
                if (line.StartsWith('@')) rest = line[1..]; else (name, rest) = Unquote(line);
                if (name.Contains('\0')) throw new FormatException("Value names cannot contain NUL.");
                rest = rest.TrimStart();
                if (!rest.StartsWith('=')) throw new FormatException("Expected '=' after value name.");
                var data = rest[1..].Trim();
                if (data == "-") { changes.Add(new DeleteValueChange(current, name)); continue; }
                RawValue value;
                if (data.StartsWith('"')) {
                    var (content, tail) = Unquote(data);
                    if (tail.Trim().Length != 0) throw new FormatException("Unexpected trailing string data.");
                    value = RegistryBackend.ParseValue(name, 1, content, false);
                } else if (data.StartsWith("dword:", StringComparison.Ordinal)) {
                    var number = data[6..];
                    if (number.Length != 8) throw new FormatException("DWORD requires eight hexadecimal digits.");
                    value = RegistryBackend.ParseValue(name, 4, number, true);
                } else {
                    uint kind; string bytes;
                    if (data.StartsWith("hex:", StringComparison.Ordinal)) { kind = 3; bytes = data[4..]; }
                    else if (data.StartsWith("hex(", StringComparison.Ordinal)) {
                        var end = data.IndexOf("):", StringComparison.Ordinal);
                        if (end < 5) throw new FormatException("Invalid hexadecimal value type.");
                        kind = uint.Parse(data[4..end], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); bytes = data[(end + 2)..];
                    } else throw new FormatException("Unsupported value syntax.");
                    var raw = bytes.Trim().Length == 0 ? Array.Empty<byte>() : bytes.Split(',').Select(part => {
                        part = part.Trim();
                        if (part.Length != 2) throw new FormatException("Each hex byte requires two digits.");
                        return byte.Parse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    }).ToArray();
                    value = new RawValue(name, kind, raw);
                }
                changes.Add(new SetValueChange(current, value));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) {
                throw new FormatException($"Line {index + 1}: {e.Message}", e);
            }
        }
        if (continuing) throw new FormatException("Unterminated hexadecimal continuation.");
        return changes;
    }

    private static (string Value, string Remaining) Unquote(string text)
    {
        if (!text.StartsWith('"')) throw new FormatException("Expected a quoted string.");
        var output = new StringBuilder(); var escaped = false;
        for (var index = 1; index < text.Length; index++) {
            var ch = text[index];
            if (escaped) {
                if (ch is not ('\\' or '"')) throw new FormatException("Invalid string escape.");
                output.Append(ch); escaped = false;
            } else if (ch == '\\') escaped = true;
            else if (ch == '"') return (output.ToString(), text[(index + 1)..]);
            else output.Append(ch);
        }
        throw new FormatException("Unterminated quoted string.");
    }

    public static int Apply(IReadOnlyList<RegistryChange> changes)
    {
        for (var index = 0; index < changes.Count; index++) {
            try {
                switch (changes[index]) {
                    case CreateKeyChange create: RegistryBackend.EnsureKey(create.Path); break;
                    case DeleteKeyChange delete: RegistryBackend.DeleteKeyIfExists(delete.Path); break;
                    case SetValueChange set: RegistryBackend.Set(set.Path, set.Value); break;
                    case DeleteValueChange delete:
                        using (var key = RegistryBackend.Open(delete.Path, 2)) key.DeleteValue(delete.Name, false);
                        break;
                    default: throw new InvalidOperationException("Unsupported registry change.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) {
                throw new IOException($"Import stopped after {index} operations; earlier changes remain applied: {e.Message}", e);
            }
        }
        return changes.Count;
    }
}
