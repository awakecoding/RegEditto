using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class RegistryBackendTests
{
    [Fact]
    public void Open_ReadsPredefinedHivesAndMachineValuesWithoutRequiringWritableRootHandles()
    {
        foreach (var hive in RegistryBackend.Hives)
            Assert.NotEmpty(RegistryBackend.Children(hive));

        Assert.NotEmpty(RegistryBackend.Children(@"HKEY_LOCAL_MACHINE\SYSTEM"));
        var values = RegistryBackend.Values("HKEY_LOCAL_MACHINE");
        Assert.All(values, value => Assert.NotNull(value.Bytes));
    }

    [Fact]
    public void RemainingSearchRoots_NestedFixtureStartsSelectedThenLaterSiblingsAtEachAncestor()
    {
        using var scope = new RegistryScope();
        foreach (var child in new[] { "A-earlier", "M-parent", "N-later", "z-last" })
            RegistryBackend.CreateKey(scope.Path, child);
        var parent = scope.Path + "\\M-parent";
        foreach (var child in new[] { "A-earlier", "M-selected", "N-later", "z-last" })
            RegistryBackend.CreateKey(parent, child);
        var selected = parent + "\\M-selected";
        RegistryBackend.CreateKey(selected, "Descendant");

        var roots = RegistryBackend.RemainingSearchRoots(
            selected.Replace("HKEY_CURRENT_USER", "hkcu", StringComparison.Ordinal));

        Assert.Equal(new[] {
            selected, parent + "\\N-later", parent + "\\z-last",
            scope.Path + "\\N-later", scope.Path + "\\z-last"
        }, roots.Where(path => path.StartsWith(scope.Path + "\\", StringComparison.Ordinal)));
        Assert.Equal(selected, roots[0]);
        Assert.DoesNotContain(parent, roots);
        Assert.DoesNotContain(scope.Path, roots);
        Assert.DoesNotContain(selected + "\\Descendant", roots);
        Assert.DoesNotContain(parent + "\\A-earlier", roots);
        Assert.DoesNotContain(scope.Path + "\\A-earlier", roots);
        Assert.Equal(new[] { "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG" }, roots.TakeLast(3));

        // Only compare returned external names: never open or search those trees.
        var selectedParts = RegistryBackend.ParsePath(selected).Subkey.Split('\\');
        foreach (var external in roots.Where(path => path.StartsWith("HKEY_CURRENT_USER\\", StringComparison.Ordinal)
            && !path.StartsWith(scope.Path + "\\", StringComparison.Ordinal))) {
            var parts = RegistryBackend.ParsePath(external).Subkey.Split('\\');
            Assert.InRange(parts.Length, 1, selectedParts.Length);
            Assert.Equal(selectedParts.Take(parts.Length - 1), parts.Take(parts.Length - 1));
            Assert.True(StringComparer.OrdinalIgnoreCase.Compare(parts[^1], selectedParts[parts.Length - 1]) > 0);
        }
    }

    [Fact]
    public void RemainingSearchRoots_HiveBoundariesReturnOnlySelectedAndLaterHives()
    {
        // Hive-only inputs do not enumerate registry data or open a key.
        var hives = new[] {
            "HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE",
            "HKEY_USERS", "HKEY_CURRENT_CONFIG"
        };
        for (var index = 0; index < hives.Length; index++)
            Assert.Equal(hives.Skip(index), RegistryBackend.RemainingSearchRoots(hives[index]));
    }

    [Fact]
    public void Find_ContinuationStopsAtTheNextFixtureSiblingAndSubtreeModeStaysBounded()
    {
        using var scope = new RegistryScope();
        var query = "needle-" + Guid.NewGuid().ToString("N");
        RegistryBackend.CreateKey(scope.Path, "A-start");
        RegistryBackend.CreateKey(scope.Path, "B-match");
        var start = scope.Path + "\\A-start";
        var match = scope.Path + "\\B-match";
        RegistryBackend.Set(match, new RawValue(query, 3, new byte[] { 0x31 }));
        var options = new RegistryBackend.FindOptions(query, false, true, false, true, "");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));

        Assert.Null(RegistryBackend.Find(start, options, cancellation.Token));
        Assert.Null(RegistryBackend.Find(start, options, cancellation.Token, continueOutsideSubtree: false));
        var found = RegistryBackend.Find(start, options, cancellation.Token, continueOutsideSubtree: true);

        Assert.NotNull(found);
        Assert.Equal(match, found.Path);
        Assert.Equal(query, found.Name);
        Assert.Equal(match + "\0V" + query, found.Marker);
    }

    [Fact]
    public void Execute_FindOptionalContinueMatchesDirectFindBehavior()
    {
        using var scope = new RegistryScope();
        var query = "needle-" + Guid.NewGuid().ToString("N");
        RegistryBackend.CreateKey(scope.Path, "A-start");
        RegistryBackend.CreateKey(scope.Path, "B-match");
        var start = scope.Path + "\\A-start";
        var match = scope.Path + "\\B-match";
        RegistryBackend.Set(match, new RawValue(query, 3, new byte[] { 0x32 }));
        var json = JsonSerializer.Serialize(new { query, keys = false, names = true, data = false, whole = true, after = "" });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));

        foreach (var suffix in new[] { "", ",\"continue\":false" }) {
            var result = RegistryBackend.Execute("find", start, "", json[..^1] + suffix + "}", 0, false, cancellation.Token);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("found").ValueKind);
        }
        var continued = RegistryBackend.Execute("find", start, "", json[..^1] + ",\"continue\":true}", 0, false, cancellation.Token);
        Assert.Equal(match, continued.GetProperty("found").GetProperty("path").GetString());
        Assert.Equal(query, continued.GetProperty("found").GetProperty("name").GetString());
        Assert.Equal(match + "\0V" + query, continued.GetProperty("found").GetProperty("marker").GetString());
    }

    [Fact]
    public void ParsePath_NormalizesAliasesAndRemotePaths()
    {
        var cases = new[] {
            ("HKCR", RegistryHive.ClassesRoot, "HKEY_CLASSES_ROOT"),
            ("hkcu", RegistryHive.CurrentUser, "HKEY_CURRENT_USER"),
            ("HKLM", RegistryHive.LocalMachine, "HKEY_LOCAL_MACHINE"),
            ("HKU", RegistryHive.Users, "HKEY_USERS"),
            ("HKCC", RegistryHive.CurrentConfig, "HKEY_CURRENT_CONFIG"),
        };
        foreach (var (alias, hive, canonical) in cases) {
            var path = RegistryBackend.ParsePath(" Computer\\" + alias + "\\Software\\Example\\ ");
            Assert.Equal(hive, path.Hive);
            Assert.Null(path.Computer);
            Assert.Equal("Software\\Example", path.Subkey);
            Assert.Equal(canonical + "\\Software\\Example", path.ToString());
            Assert.Equal(hive, RegistryBackend.ParsePath(canonical).Hive);
        }
        var remote = RegistryBackend.ParsePath(@"\\host-1\hkcu\Software\Example");
        Assert.Equal("host-1", remote.Computer);
        Assert.Equal(RegistryHive.CurrentUser, remote.Hive);
        Assert.Equal(@"\\host-1\HKEY_CURRENT_USER\Software\Example", remote.ToString());
    }

    [Fact]
    public void ParsePath_RejectsInvalidPaths()
    {
        foreach (var path in new[] { "", "Computer", "HKZZ\\X", @"\\\HKCU", @"\\host", @"HKCU\A\\B", "HKCU\\A\0B" })
            Assert.Throws<ArgumentException>(() => RegistryBackend.ParsePath(path));
    }

    [Fact]
    public void ParseValue_EncodesStringAndMultiStringExactly()
    {
        foreach (var kind in new uint[] { 1, 2 }) {
            var value = RegistryBackend.ParseValue("text", kind, "A\u00e9", false);
            Assert.Equal(kind, value.Kind);
            Assert.Equal(new byte[] { 0x41, 0, 0xe9, 0, 0, 0 }, value.Bytes);
            Assert.Equal("A\u00e9", value.Edit);
        }
        var multi = RegistryBackend.ParseValue("multi", 7, "alpha\r\nbeta", false);
        Assert.Equal(Encoding.Unicode.GetBytes("alpha\0beta\0\0"), multi.Bytes);
        Assert.Equal("alpha\nbeta", multi.Edit);
        Assert.Equal("alpha beta", multi.DisplayData);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, RegistryBackend.ParseValue("", 7, "", false).Bytes);
        Assert.Throws<ArgumentException>(() => RegistryBackend.ParseValue("", 7, "a\n\nb", false));
        Assert.Throws<ArgumentException>(() => RegistryBackend.ParseValue("", 7, "a\n", false));
        Assert.Throws<ArgumentException>(() => RegistryBackend.ParseValue("", 1, "a\0b", false));
        Assert.Throws<ArgumentException>(() => RegistryBackend.ParseValue("a\0b", 3, "", false));
    }

    [Fact]
    public void ParseValue_EncodesNumericBoundariesAndRejectsOverflow()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, RegistryBackend.ParseValue("", 4, "0", false).Bytes);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, RegistryBackend.ParseValue("", 4, "0x12345678", true).Bytes);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, RegistryBackend.ParseValue("", 4, "4294967295", false).Bytes);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, RegistryBackend.ParseValue("", 4, "FFFFFFFF", true).Bytes);
        Assert.Equal(new byte[8], RegistryBackend.ParseValue("", 11, "0", false).Bytes);
        Assert.Equal(new byte[] { 0xef, 0xcd, 0xab, 0x89, 0x67, 0x45, 0x23, 0x01 }, RegistryBackend.ParseValue("", 11, "0x0123456789abcdef", true).Bytes);
        Assert.Equal(Enumerable.Repeat((byte)255, 8).ToArray(), RegistryBackend.ParseValue("", 11, "18446744073709551615", false).Bytes);
        Assert.Equal(Enumerable.Repeat((byte)255, 8).ToArray(), RegistryBackend.ParseValue("", 11, "FFFFFFFFFFFFFFFF", true).Bytes);
        Assert.Throws<OverflowException>(() => RegistryBackend.ParseValue("", 4, "4294967296", false));
        Assert.Throws<OverflowException>(() => RegistryBackend.ParseValue("", 4, "100000000", true));
        Assert.Throws<OverflowException>(() => RegistryBackend.ParseValue("", 11, "18446744073709551616", false));
        Assert.Throws<OverflowException>(() => RegistryBackend.ParseValue("", 11, "10000000000000000", true));
        Assert.Throws<FormatException>(() => RegistryBackend.ParseValue("", 4, "-1", false));
        Assert.Throws<FormatException>(() => RegistryBackend.ParseValue("", 11, "0xG0", true));
        var dword = RegistryBackend.ParseValue("", 4, "42", false);
        Assert.Equal("2a", dword.Edit);
        Assert.Equal("0x0000002a (42)", dword.DisplayData);
        Assert.Equal("0x000000000000002a (42)", RegistryBackend.ParseValue("", 11, "42", false).DisplayData);
    }

    [Fact]
    public void ParseBytes_ValidatesPairsAndPreservesUnknownKinds()
    {
        Assert.Equal(new byte[] { 0, 0xaf, 255 }, RegistryBackend.ParseBytes("00\tAf\nFF"));
        Assert.Empty(RegistryBackend.ParseBytes("  "));
        foreach (var invalid in new[] { "0", "000", "gg", "00,ff", "0x10" })
            Assert.Throws<FormatException>(() => RegistryBackend.ParseBytes(invalid));
        var binary = RegistryBackend.ParseValue("b", 3, "00 ff 10", false);
        Assert.Equal(new byte[] { 0, 255, 16 }, binary.Bytes);
        var unknown = RegistryBackend.ParseValue("u", 123, "01 80 ff", false);
        Assert.Equal(123u, unknown.Kind);
        Assert.Equal("REG_TYPE_123", unknown.TypeName);
        Assert.Equal("01 80 ff", unknown.Edit);
        Assert.Equal(new byte[] { 1, 128, 255 }, unknown.Bytes);
        Assert.Equal("01 02", new RawValue("malformed", 4, new byte[] { 1, 2 }).DisplayData);
    }

    [Fact]
    public void KeyAndValueCrud_EnforcesCollisionsAndPreservesRawBytes()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "Original");
        Assert.Throws<InvalidOperationException>(() => RegistryBackend.CreateKey(scope.Path, "original"));
        Assert.Throws<ArgumentException>(() => RegistryBackend.CreateKey(scope.Path, "bad\\name"));
        Assert.Equal(new[] { "Original" }, RegistryBackend.Children(scope.Path));
        var key = scope.Path + "\\Original";
        RegistryBackend.Set(key, new RawValue("old", 123, new byte[] { 0, 0xff, 3 }));
        RegistryBackend.Set(key, new RawValue("occupied", 3, new byte[] { 9 }));
        Assert.Throws<InvalidOperationException>(() => RegistryBackend.RenameValue(key, "old", "OCCUPIED"));
        Assert.Throws<InvalidOperationException>(() => RegistryBackend.RenameValue(key, "", "other"));
        Assert.Throws<InvalidOperationException>(() => RegistryBackend.Execute("create-value", key, "OLD", "", 3, false, TestContext.Current.CancellationToken));
        RegistryBackend.RenameValue(key, "old", "renamed");
        var values = RegistryBackend.Values(key);
        Assert.Equal(new[] { "occupied", "renamed" }, values.Select(v => v.Name).ToArray());
        var renamed = Assert.Single(values, v => v.Name == "renamed");
        Assert.Equal(123u, renamed.Kind);
        Assert.Equal(new byte[] { 0, 0xff, 3 }, renamed.Bytes);
        RegistryBackend.RenameKey(key, "Moved");
        key = scope.Path + "\\Moved";
        Assert.Equal(new[] { "Moved" }, RegistryBackend.Children(scope.Path));
        Assert.Equal(new byte[] { 0, 0xff, 3 }, Assert.Single(RegistryBackend.Values(key), v => v.Name == "renamed").Bytes);
        RegistryBackend.DeleteValue(key, "renamed");
        Assert.Equal(new[] { "occupied" }, RegistryBackend.Values(key).Select(v => v.Name).ToArray());
        RegistryBackend.DeleteKey(key);
        Assert.Empty(RegistryBackend.Children(scope.Path));
        RegistryBackend.DeleteKeyIfExists(key);
    }

    [Fact]
    public void Find_ContinuesAfterDefaultValueAndDoesNotRepeatMatches()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("", 1, "needle default", false));
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("a", 1, "needle first", false));
        RegistryBackend.CreateKey(scope.Path, "needle-child");
        var child = scope.Path + "\\needle-child";
        RegistryBackend.Set(child, RegistryBackend.ParseValue("needle-name", 1, "other", false));
        var options = new RegistryBackend.FindOptions("needle", true, true, true, false, "");
        var first = RegistryBackend.Find(scope.Path, options, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal("", first.Name);
        Assert.Equal(scope.Path + "\0V", first.Marker);
        var second = RegistryBackend.Find(scope.Path, options with { After = first.Marker }, TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal("a", second.Name);
        var third = RegistryBackend.Find(scope.Path, options with { After = second.Marker }, TestContext.Current.CancellationToken);
        Assert.NotNull(third);
        Assert.Equal(child, third.Path);
        Assert.Null(third.Name);
        var fourth = RegistryBackend.Find(scope.Path, options with { After = third.Marker }, TestContext.Current.CancellationToken);
        Assert.NotNull(fourth);
        Assert.Equal("needle-name", fourth.Name);
        Assert.Null(RegistryBackend.Find(scope.Path, options with { After = fourth.Marker }, TestContext.Current.CancellationToken));
        Assert.Null(RegistryBackend.Find(scope.Path, options with { Query = "needle", Whole = true }, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => RegistryBackend.Find(scope.Path, options with { Query = "" }, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => RegistryBackend.Find(scope.Path, options with { Keys = false, Names = false, Data = false }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Execute_ListsUnsetDefaultAndCreatesValueWithoutOverwriting()
    {
        using var scope = new RegistryScope();
        var empty = RegistryBackend.Execute("list", scope.Path, "", "", 0, false, TestContext.Current.CancellationToken);
        var placeholder = Assert.Single(empty.GetProperty("values").EnumerateArray());
        Assert.Equal("", placeholder.GetProperty("name").GetString());
        Assert.Equal("(value not set)", placeholder.GetProperty("data").GetString());
        RegistryBackend.Execute("create-value", scope.Path, "setting", "42", 4, false, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => RegistryBackend.Execute("create-value", scope.Path, "SETTING", "99", 4, false, TestContext.Current.CancellationToken));
        var actual = Assert.Single(RegistryBackend.Values(scope.Path));
        Assert.Equal("setting", actual.Name);
        Assert.Equal(4u, actual.Kind);
        Assert.Equal(new byte[] { 42, 0, 0, 0 }, actual.Bytes);
        RegistryBackend.Set(scope.Path, new RawValue("", 3, new byte[] { 9 }));
        var list = RegistryBackend.Execute("list", scope.Path, "", "", 0, false, TestContext.Current.CancellationToken);
        var values = list.GetProperty("values").EnumerateArray().ToArray();
        Assert.Equal(2, values.Length);
        Assert.Equal("", values[0].GetProperty("name").GetString());
        Assert.Equal("09", values[0].GetProperty("data").GetString());
    }

    [Fact]
    public void Find_WholeMatchAndFieldSelectionsFilterResults()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("Needle", 1, "prefix needle suffix", false));
        RegistryBackend.CreateKey(scope.Path, "Needle");
        var namesOnly = new RegistryBackend.FindOptions("needle", false, true, false, true, "");
        var name = RegistryBackend.Find(scope.Path, namesOnly, TestContext.Current.CancellationToken);
        Assert.NotNull(name);
        Assert.Equal(scope.Path, name.Path);
        Assert.Equal("Needle", name.Name);
        Assert.Null(RegistryBackend.Find(scope.Path, namesOnly with { After = name.Marker }, TestContext.Current.CancellationToken));
        Assert.Null(RegistryBackend.Find(scope.Path, namesOnly with { Names = false, Data = true }, TestContext.Current.CancellationToken));
        var key = RegistryBackend.Find(scope.Path, namesOnly with { Names = false, Keys = true }, TestContext.Current.CancellationToken);
        Assert.NotNull(key);
        Assert.Equal(scope.Path + "\\Needle", key.Path);
        Assert.Equal(key.Path + "\0K", key.Marker);
    }

    [Fact]
    public void Execute_FindForwardsCancellationAndStillFindsWithoutCancellation()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("needle", 1, "value", false));
        const string options = """{"query":"needle","keys":false,"names":true,"data":false,"whole":true,"after":""}""";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            RegistryBackend.Execute("find", scope.Path, "", options, 0, false, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var result = RegistryBackend.Execute("find", scope.Path, "", options, 0, false, TestContext.Current.CancellationToken);
        Assert.Equal("needle", result.GetProperty("found").GetProperty("name").GetString());
    }
}

internal sealed class RegistryScope : IDisposable
{
    private readonly string _subkey = @"Software\RegEditto.Tests." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
    public string Path => @"HKEY_CURRENT_USER\" + _subkey;

    public RegistryScope()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var created = root.CreateSubKey(_subkey, true) ?? throw new InvalidOperationException("Failed to create test key.");
    }

    public void Dispose()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        root.DeleteSubKeyTree(_subkey, false);
    }
}
