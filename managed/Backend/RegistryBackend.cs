using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RegistryEditor.Presentation.Backend;

public sealed record RawValue(string Name, uint Kind, byte[] Bytes)
{
    public string TypeName => Kind switch {
        0 => "REG_NONE", 1 => "REG_SZ", 2 => "REG_EXPAND_SZ", 3 => "REG_BINARY",
        4 => "REG_DWORD", 5 => "REG_DWORD_BIG_ENDIAN", 6 => "REG_LINK", 7 => "REG_MULTI_SZ",
        8 => "REG_RESOURCE_LIST", 9 => "REG_FULL_RESOURCE_DESCRIPTOR",
        10 => "REG_RESOURCE_REQUIREMENTS_LIST", 11 => "REG_QWORD", _ => $"REG_TYPE_{Kind}"
    };
    public string Edit => Kind switch {
        1 or 2 => Encoding.Unicode.GetString(Bytes).TrimEnd('\0'),
        7 => Encoding.Unicode.GetString(Bytes).TrimEnd('\0').Replace('\0', '\n'),
        4 when Bytes.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(Bytes).ToString("x", CultureInfo.InvariantCulture),
        11 when Bytes.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(Bytes).ToString("x", CultureInfo.InvariantCulture),
        _ => string.Join(" ", Bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))
    };
    public string DisplayData => Kind switch {
        1 or 2 or 7 => Edit.Replace('\n', ' '),
        4 when Bytes.Length == 4 => $"0x{BinaryPrimitives.ReadUInt32LittleEndian(Bytes):x8} ({BinaryPrimitives.ReadUInt32LittleEndian(Bytes)})",
        11 when Bytes.Length == 8 => $"0x{BinaryPrimitives.ReadUInt64LittleEndian(Bytes):x16} ({BinaryPrimitives.ReadUInt64LittleEndian(Bytes)})",
        _ => Edit
    };
}

public sealed record KeyPath(string? Computer, RegistryHive Hive, string HiveName, string Subkey)
{
    public override string ToString() => (Computer is null ? "" : $@"\\{Computer}\") + HiveName + (Subkey.Length == 0 ? "" : "\\" + Subkey);
}

public static class RegistryBackend
{
    public static readonly string[] Hives = { "HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG" };
    private const int KeyRead = 0x20019;
    private const int KeyWrite = 0x20006;
    private const int KeyQuery = 1;
    private const int KeySet = 2;
    private const int KeyCreate = 4;
    private const int KeyWow64_64Key = 0x100;
    private const int ErrorMoreData = 234;

    public static KeyPath ParsePath(string path)
    {
        path = path.Trim().TrimEnd('\\');
        if (path.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) path = path[9..];
        string? machine = null;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
            var end = path.IndexOf('\\', 2);
            if (end < 3) throw new ArgumentException("A remote path requires a computer name and registry hive.");
            machine = path[2..end]; path = path[(end + 1)..];
        }
        if (path.Contains('\0')) throw new ArgumentException("A registry path cannot contain NUL.");
        var parts = path.Split('\\', 2);
        var hive = parts[0].ToUpperInvariant() switch {
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => throw new ArgumentException("Unknown registry hive: " + parts[0])
        };
        var name = hive switch {
            RegistryHive.ClassesRoot => Hives[0], RegistryHive.CurrentUser => Hives[1],
            RegistryHive.LocalMachine => Hives[2], RegistryHive.Users => Hives[3], _ => Hives[4]
        };
        var subkey = parts.Length == 2 ? parts[1] : "";
        if (subkey.Split('\\').Any(p => p.Length == 0) && subkey.Length != 0)
            throw new ArgumentException("A registry path cannot contain an empty component.");
        return new KeyPath(machine, hive, name, subkey);
    }

    public static RegistryKey Open(string path, int access = KeyRead)
    {
        var parsed = ParsePath(path);
        var predefined = HiveHandle(parsed.Hive);
        using var remoteRoot = parsed.Computer is null ? null : ConnectRemote(parsed.Computer, predefined);
        var root = remoteRoot is null ? predefined : remoteRoot.DangerousGetHandle();
        var status = Native.RegOpenKeyEx(root, parsed.Subkey, 0, access | KeyWow64_64Key, out var handle);
        if (status == 5)
            throw new UnauthorizedAccessException($"Access to registry key '{parsed}' was denied for the current Windows account (requested access 0x{access | KeyWow64_64Key:x}). This application does not elevate.", new Win32Exception(status));
        Check(status);
        return RegistryKey.FromHandle(handle, RegistryView.Registry64);
    }

    private static IntPtr HiveHandle(RegistryHive hive) => new(unchecked((long)(hive switch {
        RegistryHive.ClassesRoot => 0x80000000u,
        RegistryHive.CurrentUser => 0x80000001u,
        RegistryHive.LocalMachine => 0x80000002u,
        RegistryHive.Users => 0x80000003u,
        RegistryHive.CurrentConfig => 0x80000005u,
        _ => throw new ArgumentOutOfRangeException(nameof(hive), "Unsupported predefined registry hive.")
    })));

    private static SafeRegistryHandle ConnectRemote(string computer, IntPtr hive)
    {
        Check(Native.RegConnectRegistry(@"\\" + computer, hive, out var root));
        return root;
    }

    public static IReadOnlyList<string> Children(string path)
    {
        using var key = Open(path);
        return key.GetSubKeyNames().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static RawValue Read(RegistryKey key, string name)
    {
        ValidateValueName(name);
        var size = 0u;
        var status = Native.RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var kind, null, ref size);
        if (status != 0 && status != ErrorMoreData) Check(status);
        for (var attempt = 0; attempt < 8; attempt++) {
            var bytes = new byte[checked((int)size)];
            status = Native.RegQueryValueEx(key.Handle, name, IntPtr.Zero, out kind, bytes, ref size);
            if (status == ErrorMoreData) continue;
            Check(status);
            Array.Resize(ref bytes, checked((int)size));
            return new RawValue(name, kind, bytes);
        }
        throw new IOException("The registry value changed repeatedly while being read.");
    }

    public static IReadOnlyList<RawValue> Values(string path)
    {
        using var key = Open(path);
        return key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => Read(key, n)).ToArray();
    }

    public static void Set(string path, RawValue value)
    {
        ValidateValueName(value.Name);
        using var key = Open(path, KeySet);
        Check(Native.RegSetValueEx(key.Handle, value.Name, 0, value.Kind, value.Bytes, checked((uint)value.Bytes.Length)));
    }

    public static RawValue ParseValue(string name, uint kind, string text, bool hex)
    {
        ValidateValueName(name);
        byte[] bytes;
        switch (kind) {
            case 1: case 2:
                if (text.Contains('\0')) throw new ArgumentException("String data cannot contain NUL.");
                bytes = Encoding.Unicode.GetBytes(text + '\0'); break;
            case 7:
                if (text.Contains('\0')) throw new ArgumentException("Multi-string data cannot contain NUL.");
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                if (text.Length > 0 && lines.Any(l => l.Length == 0))
                    throw new ArgumentException("Multi-string entries cannot be empty; remove blank lines.");
                bytes = Encoding.Unicode.GetBytes(string.Join("\0", text.Length == 0 ? Array.Empty<string>() : lines) + "\0\0"); break;
            case 4:
                bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.Parse(NumericText(text, hex), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture));
                break;
            case 11:
                bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.Parse(NumericText(text, hex), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture));
                break;
            default:
                bytes = ParseBytes(text); break;
        }
        return new RawValue(name, kind, bytes);
    }

    private static string NumericText(string text, bool hex)
    {
        text = text.Trim();
        return hex && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
    }
    public static byte[] ParseBytes(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(part => {
            if (part.Length != 2) throw new FormatException("Each hexadecimal byte must have exactly two digits.");
            return byte.Parse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }).ToArray();
    private static void ValidateValueName(string name)
    {
        if (name.Contains('\0')) throw new ArgumentException("A value name cannot contain NUL.");
    }
    private static void ValidateLeaf(string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Contains('\0')) throw new ArgumentException("A key name must be nonempty and cannot contain a backslash or NUL.");
    }

    public static void CreateKey(string parent, string name)
    {
        ValidateLeaf(name);
        using var key = Open(parent, KeyCreate);
        Check(Native.RegCreateKeyEx(key.Handle, name, 0, null, 0, KeyRead | KeyWow64_64Key, IntPtr.Zero, out var handle, out var disposition));
        handle.Dispose();
        if (disposition == 2) throw new InvalidOperationException("A key with that name already exists.");
    }

    public static void EnsureKey(string path)
    {
        var parsed = ParsePath(path);
        using var root = parsed.Computer is null ? RegistryKey.OpenBaseKey(parsed.Hive, RegistryView.Registry64)
            : RegistryKey.OpenRemoteBaseKey(parsed.Hive, parsed.Computer, RegistryView.Registry64);
        using var key = root.CreateSubKey(parsed.Subkey, true) ?? throw new IOException("Could not create the key.");
    }

    public static void DeleteKey(string path)
    {
        var parsed = ParsePath(path);
        if (parsed.Subkey.Length == 0) throw new InvalidOperationException("Cannot delete a registry hive.");
        var index = path.LastIndexOf('\\');
        using var parent = Open(path[..index], KeyWrite);
        var leaf = path[(index + 1)..];
        ValidateLeaf(leaf);
        parent.DeleteSubKeyTree(leaf, true);
    }

    public static void DeleteKeyIfExists(string path)
    {
        var parsed = ParsePath(path);
        if (parsed.Subkey.Length == 0) throw new InvalidOperationException("Cannot delete a registry hive.");
        using var root = parsed.Computer is null ? RegistryKey.OpenBaseKey(parsed.Hive, RegistryView.Registry64)
            : RegistryKey.OpenRemoteBaseKey(parsed.Hive, parsed.Computer, RegistryView.Registry64);
        root.DeleteSubKeyTree(parsed.Subkey, false);
    }

    public static void RenameKey(string path, string name)
    {
        ValidateLeaf(name);
        var parsed = ParsePath(path);
        if (parsed.Subkey.Length == 0) throw new InvalidOperationException("Cannot rename a registry hive.");
        var index = path.LastIndexOf('\\');
        using var parent = Open(path[..index], KeyWrite);
        Check(Native.RegRenameKey(parent.Handle, path[(index + 1)..], name));
    }

    public static void DeleteValue(string path, string name)
    {
        ValidateValueName(name);
        using var key = Open(path, KeySet);
        key.DeleteValue(name, true);
    }

    public static void RenameValue(string path, string name, string replacement)
    {
        ValidateValueName(name); ValidateValueName(replacement);
        if (name.Length == 0 || replacement.Length == 0) throw new InvalidOperationException("The default value cannot be renamed.");
        using var key = Open(path, KeyQuery | KeySet);
        if (key.GetValueNames().Contains(replacement, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("A value with that name already exists.");
        var value = Read(key, name);
        Check(Native.RegSetValueEx(key.Handle, replacement, 0, value.Kind, value.Bytes, checked((uint)value.Bytes.Length)));
        try { key.DeleteValue(name, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new IOException("The new value was created, but the old value could not be removed: " + e.Message, e);
        }
    }

    public sealed record FindOptions(string Query, bool Keys, bool Names, bool Data, bool Whole, string After);
    public sealed record FindMatch(string Path, string? Name, string Marker);

    public static FindMatch? Find(string start, FindOptions options, System.Threading.CancellationToken cancellation = default, bool continueOutsideSubtree = false)
    {
        if (string.IsNullOrEmpty(options.Query) || !(options.Keys || options.Names || options.Data))
            throw new ArgumentException("Enter search text and select at least one search field.");
        var roots = start == "Computer" ? Hives : start.StartsWith(@"\\", StringComparison.Ordinal) && !start[2..].Contains('\\')
            ? new[] { start + "\\HKEY_LOCAL_MACHINE", start + "\\HKEY_USERS" }
            : continueOutsideSubtree ? RemainingSearchRoots(start) : new[] { start };
        var stack = new Stack<string>(roots.Reverse());
        var past = options.After.Length == 0;
        bool Matches(string text) => options.Whole ? text.Equals(options.Query, StringComparison.OrdinalIgnoreCase) : text.Contains(options.Query, StringComparison.OrdinalIgnoreCase);
        while (stack.TryPop(out var path)) {
            cancellation.ThrowIfCancellationRequested();
            using var key = Open(path);
            var marker = path + "\0K";
            if (past && options.Keys && Matches(path.Split('\\').Last())) return new FindMatch(path, null, marker);
            if (marker == options.After) past = true;
            foreach (var value in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) {
                cancellation.ThrowIfCancellationRequested();
                marker = path + "\0V" + value;
                if (past && ((options.Names && Matches(value)) || (options.Data && Matches(Read(key, value).DisplayData))))
                    return new FindMatch(path, value, marker);
                if (marker == options.After) past = true;
            }
            foreach (var child in key.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                stack.Push(path + "\\" + child);
        }
        return null;
    }
    public static IReadOnlyList<string> RemainingSearchRoots(string start)
    {
        var parsed = ParsePath(start);
        var roots = new List<string> { parsed.ToString() };
        var current = parsed.ToString();
        var subkey = parsed.Subkey;
        while (subkey.Length > 0) {
            var split = current.LastIndexOf('\\');
            var parent = current[..split];
            var leaf = current[(split + 1)..];
            foreach (var sibling in Children(parent).Where(name => StringComparer.OrdinalIgnoreCase.Compare(name, leaf) > 0))
                roots.Add(parent + "\\" + sibling);
            current = parent;
            var last = subkey.LastIndexOf('\\');
            subkey = last < 0 ? "" : subkey[..last];
        }
        var hives = parsed.Computer is null ? Hives : new[] { "HKEY_LOCAL_MACHINE", "HKEY_USERS" };
        var index = Array.IndexOf(hives, parsed.HiveName);
        foreach (var hive in hives.Skip(index + 1)) roots.Add((parsed.Computer is null ? "" : $@"\\{parsed.Computer}\") + hive);
        return roots;
    }

    public static void LoadHive(string parent, string name, string file)
    {
        ValidateLeaf(name);
        var parsed = ParsePath(parent);
        if (parsed.Subkey.Length != 0 || parsed.Hive is not (RegistryHive.LocalMachine or RegistryHive.Users))
            throw new InvalidOperationException("Select HKEY_LOCAL_MACHINE or HKEY_USERS before loading a hive.");
        if (parsed.Computer is not null) throw new InvalidOperationException("Offline hive loading is supported on the local computer only.");
        using var root = Open(parent, KeyWrite);
        Check(Native.RegLoadKey(root.Handle, name, file));
    }
    public static void UnloadHive(string path)
    {
        var parsed = ParsePath(path);
        if (parsed.Computer is not null || parsed.Hive is not (RegistryHive.LocalMachine or RegistryHive.Users) || parsed.Subkey.Length == 0 || parsed.Subkey.Contains('\\'))
            throw new InvalidOperationException("Select a locally loaded hive directly below HKEY_LOCAL_MACHINE or HKEY_USERS.");
        using var root = Open(parsed.HiveName, KeyWrite);
        Check(Native.RegUnLoadKey(root.Handle, parsed.Subkey));
    }
    public static void SaveHive(string path, string file)
    {
        var parsed = ParsePath(path);
        if (parsed.Computer is not null) throw new InvalidOperationException("Hive file export is local only.");
        using var key = Open(path);
        file = Path.GetFullPath(file);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Check(Native.RegSaveKeyEx(key.Handle, temporary, IntPtr.Zero, 2));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void RestoreHive(string path, string file)
    {
        var parsed = ParsePath(path);
        if (parsed.Computer is not null || parsed.Subkey.Length == 0) throw new InvalidOperationException("Select a local subkey to restore a hive file.");
        using var key = Open(path, KeyWrite);
        Check(Native.RegRestoreKey(key.Handle, Path.GetFullPath(file), 0));
    }

    public static JsonElement Execute(string op, string path, string name, string text, uint kind, bool hex, System.Threading.CancellationToken cancellation = default)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            switch (op) {
                case "list": case "children":
                    writer.WriteStartArray("children");
                    foreach (var child in Children(path)) writer.WriteStringValue(child);
                    writer.WriteEndArray();
                    if (op == "list") {
                        var values = Values(path).ToList();
                        writer.WriteStartArray("values");
                        if (!values.Any(v => v.Name.Length == 0)) WriteValue(writer, new RawValue("", 1, Array.Empty<byte>()), true);
                        foreach (var value in values) WriteValue(writer, value, false);
                        writer.WriteEndArray();
                    }
                    break;
                case "set": Set(path, ParseValue(name, kind, text, hex)); break;
                case "set-raw": Set(path, new RawValue(name, kind, ParseBytes(text))); break;
                case "create-value":
                    using (var key = Open(path, KeyQuery)) {
                        if (key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("A value with that name already exists.");
                    }
                    Set(path, ParseValue(name, kind, text, hex)); break;
                case "create-key": CreateKey(path, name); break;
                case "delete-key": DeleteKey(path); break;
                case "rename-key": RenameKey(path, name); break;
                case "delete-value": DeleteValue(path, name); break;
                case "rename-value": RenameValue(path, name, text); break;
                case "export": RegFile.ExportToFile(path, text); break;
                case "export-legacy": RegFile.ExportToFile(path, text, true); break;
                case "save-hive": SaveHive(path, text); break;
                case "restore-hive": RestoreHive(path, text); break;
                case "export-text": RegistryPrinter.ExportText(path, text); break;
                case "import": writer.WriteNumber("count", RegFile.Apply(RegFile.Parse(RegFile.Read(text)))); break;
                case "find":
                    using (var options = JsonDocument.Parse(text)) {
                        var o = options.RootElement;
                        var found = Find(path, new FindOptions(o.GetProperty("query").GetString()!, o.GetProperty("keys").GetBoolean(), o.GetProperty("names").GetBoolean(),
                            o.GetProperty("data").GetBoolean(), o.GetProperty("whole").GetBoolean(), o.GetProperty("after").GetString()!), cancellation,
                            o.TryGetProperty("continue", out var outside) && outside.GetBoolean());
                        if (found is null) writer.WriteNull("found");
                        else {
                            writer.WriteStartObject("found"); writer.WriteString("path", found.Path); writer.WriteString("name", found.Name);
                            writer.WriteString("marker", found.Marker); writer.WriteEndObject();
                        }
                    }
                    break;
                case "load-hive": LoadHive(path, name, text); break;
                case "unload-hive": UnloadHive(path); break;
                default: throw new InvalidOperationException("Unknown registry operation: " + op);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, RawValue value, bool unset)
    {
        writer.WriteStartObject(); writer.WriteString("name", value.Name); writer.WriteNumber("kind", value.Kind);
        writer.WriteString("type_name", value.TypeName); writer.WriteString("data", unset ? "(value not set)" : value.DisplayData);
        writer.WriteString("edit", value.Edit); writer.WriteStartArray("bytes");
        foreach (var b in value.Bytes) writer.WriteNumberValue(b);
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegOpenKeyExW")]
        internal static extern int RegOpenKeyEx(IntPtr key, string subkey, uint options, int access, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegConnectRegistryW")]
        internal static extern int RegConnectRegistry(string computer, IntPtr key, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegCreateKeyExW")]
        internal static extern int RegCreateKeyEx(SafeRegistryHandle key, string subkey, uint reserved, string? className, uint options, int access, IntPtr security, out SafeRegistryHandle result, out uint disposition);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegQueryValueExW")]
        internal static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint kind, byte[]? data, ref uint size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegSetValueExW")]
        internal static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint kind, byte[] data, uint size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegRenameKey")]
        internal static extern int RegRenameKey(SafeRegistryHandle key, string oldName, string newName);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegLoadKeyW")]
        internal static extern int RegLoadKey(SafeRegistryHandle key, string subkey, string file);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegUnLoadKeyW")]
        internal static extern int RegUnLoadKey(SafeRegistryHandle key, string subkey);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegSaveKeyExW")]
        internal static extern int RegSaveKeyEx(SafeRegistryHandle key, string file, IntPtr security, uint flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegRestoreKeyW")]
        internal static extern int RegRestoreKey(SafeRegistryHandle key, string file, uint flags);
    }
}
