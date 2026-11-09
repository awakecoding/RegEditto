using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class RegFileTests
{
    [Fact]
    public void Parse_RecognizesQuotedEscapesNumbersHexContinuationsAndDeletions()
    {
        const string text = """
            Windows Registry Editor Version 5.00

            ; comment
            [HKCU\Software\Example]
            @="default"
            "a\\b\"c"="x\\y\"z"
            "n"=dword:89abcdef
            "bin"=hex:00,ff,\
              01,80
            "expand"=hex(2):25,00,00,00
            "custom"=hex(7b):01,02
            "remove"=-
            [-HKCU\Software\Example\Child]
            """;
        var changes = RegFile.Parse(text);
        Assert.Equal(9, changes.Count);
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Example", Assert.IsType<CreateKeyChange>(changes[0]).Path);
        var defaultValue = Assert.IsType<SetValueChange>(changes[1]).Value;
        Assert.Equal("", defaultValue.Name);
        Assert.Equal(Encoding.Unicode.GetBytes("default\0"), defaultValue.Bytes);
        var quoted = Assert.IsType<SetValueChange>(changes[2]).Value;
        Assert.Equal("a\\b\"c", quoted.Name);
        Assert.Equal(Encoding.Unicode.GetBytes("x\\y\"z\0"), quoted.Bytes);
        var number = Assert.IsType<SetValueChange>(changes[3]).Value;
        Assert.Equal(4u, number.Kind);
        Assert.Equal(new byte[] { 0xef, 0xcd, 0xab, 0x89 }, number.Bytes);
        var binary = Assert.IsType<SetValueChange>(changes[4]).Value;
        Assert.Equal(3u, binary.Kind);
        Assert.Equal(new byte[] { 0, 255, 1, 128 }, binary.Bytes);
        var expand = Assert.IsType<SetValueChange>(changes[5]).Value;
        Assert.Equal(2u, expand.Kind);
        Assert.Equal(new byte[] { 0x25, 0, 0, 0 }, expand.Bytes);
        Assert.Equal(123u, Assert.IsType<SetValueChange>(changes[6]).Value.Kind);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<SetValueChange>(changes[6]).Value.Bytes);
        Assert.Equal("remove", Assert.IsType<DeleteValueChange>(changes[7]).Name);
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Example\Child", Assert.IsType<DeleteKeyChange>(changes[8]).Path);
        Assert.Single(RegFile.Parse("REGEDIT4\n[HKCU\\Software\\Example]\n"));
    }

    [Fact]
    public void Parse_RejectsMalformedSyntaxBeforeAnyChangesAreApplied()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, new RawValue("keep", 3, new byte[] { 42 }));
        var header = "Windows Registry Editor Version 5.00\n[" + scope.Path + "]\n\"keep\"=hex:00\n";
        var badLines = new[] {
            "\"x\"=\"unterminated",
            "\"x\"=\"bad\\q\"",
            "\"x\"=\"ok\"trailing",
            "\"x\" dword:00000000",
            "\"x\"=dword:0000000",
            "\"x\"=dword:0000000g",
            "\"x\"=hex:0",
            "\"x\"=hex:00,",
            "\"x\"=hex:gg",
            "\"x\"=hex(z):00",
            "\"x\"=hex(100000000):00",
            "\"x\"=hex:01,\\",
            "\"x\"=unknown:00",
            "[-HKEY_CURRENT_USER]",
            "[\\\\remote\\HKCU\\Software]",
            "[HKXX\\Bad]",
        };
        foreach (var bad in badLines) {
            Assert.Throws<FormatException>(() => RegFile.Apply(RegFile.Parse(header + bad)));
            var value = Assert.Single(RegistryBackend.Values(scope.Path), v => v.Name == "keep");
            Assert.Equal(new byte[] { 42 }, value.Bytes);
        }
        Assert.Throws<FormatException>(() => RegFile.Parse("wrong header\n"));
        Assert.Throws<FormatException>(() => RegFile.Parse("REGEDIT4\n\"x\"=hex:00"));
    }

    [Fact]
    public void Apply_PerformsValueAndKeyDeletionWithinTheTestSubtree()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "Child");
        RegistryBackend.Set(scope.Path, new RawValue("remove", 3, new byte[] { 1 }));
        var document = "REGEDIT4\n[" + scope.Path + "]\n" +
            "\"remove\"=-\n\"missing\"=-\n\"new\"=dword:ffffffff\n" +
            "[-" + scope.Path + "\\Child]\n";
        var changes = RegFile.Parse(document);
        Assert.Equal(5, RegFile.Apply(changes));
        Assert.Empty(RegistryBackend.Children(scope.Path));
        var value = Assert.Single(RegistryBackend.Values(scope.Path));
        Assert.Equal("new", value.Name);
        Assert.Equal(4u, value.Kind);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, value.Bytes);
    }

    [Fact]
    public void ExportImport_PreservesRawMalformedValuesAndNestedKeys()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "Source");
        RegistryBackend.CreateKey(scope.Path, "Target");
        var source = scope.Path + "\\Source";
        var target = scope.Path + "\\Target";
        RegistryBackend.CreateKey(source, "Nested");
        var expected = new[] {
            new RawValue("", 1, new byte[] { 65 }),
            new RawValue("bad-string", 1, new byte[] { 65, 0, 66 }),
            new RawValue("bad-dword", 4, new byte[] { 1, 2, 3 }),
            new RawValue("bad-qword", 11, new byte[] { 4, 5 }),
            new RawValue("unknown\\\"name", 123, new byte[] { 0, 255, 3 }),
            new RawValue("long", 3, Enumerable.Range(0, 30).Select(i => (byte)i).ToArray()),
        };
        foreach (var value in expected) RegistryBackend.Set(source, value);
        RegistryBackend.Set(source + "\\Nested", new RawValue("nested", 7, new byte[] { 1, 0, 2 }));
        var exported = RegFile.Export(source);
        Assert.Contains("hex(7b):00,ff,03", exported);
        Assert.Contains("\\\r\n  ", exported);
        var changes = RegFile.Parse(exported.Replace(source, target, StringComparison.Ordinal));
        Assert.Equal(changes.Count, RegFile.Apply(changes));
        var actual = RegistryBackend.Values(target);
        Assert.Equal(expected.Length, actual.Count);
        foreach (var value in expected) {
            var imported = Assert.Single(actual, v => v.Name == value.Name);
            Assert.Equal(value.Kind, imported.Kind);
            Assert.Equal(value.Bytes, imported.Bytes);
        }
        var nested = Assert.Single(RegistryBackend.Values(target + "\\Nested"));
        Assert.Equal(7u, nested.Kind);
        Assert.Equal(new byte[] { 1, 0, 2 }, nested.Bytes);
    }

    [Fact]
    public void Export_MultipleExplicitPathsUseCanonicalSectionsAndCanonicalStringAndDwordSyntax()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "First");
        RegistryBackend.CreateKey(scope.Path, "Second");
        RegistryBackend.CreateKey(scope.Path, "Unselected");
        var first = scope.Path + "\\First";
        var second = scope.Path + "\\Second";
        var unselected = scope.Path + "\\Unselected";

        RegistryBackend.Set(first, RegistryBackend.ParseValue("", 1, "default", hex: false));
        RegistryBackend.Set(first, RegistryBackend.ParseValue("quoted", 1, "A \"quote\" and C:\\path", hex: false));
        RegistryBackend.Set(first, new RawValue("number", 4, new byte[] { 0x78, 0x56, 0x34, 0x12 }));
        RegistryBackend.Set(second, RegistryBackend.ParseValue("second", 1, "included", hex: false));
        RegistryBackend.Set(unselected, RegistryBackend.ParseValue("excluded", 1, "not exported", hex: false));

        // Alias spelling is normalized for .reg sections; both inputs remain local fixture paths.
        var aliasFirst = first.Replace("HKEY_CURRENT_USER", "HKCU", StringComparison.Ordinal);
        var export = RegFile.Export(new[] { aliasFirst, second }, TestContext.Current.CancellationToken);

        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n", export);
        Assert.Contains("[HKEY_CURRENT_USER\\" + scope.Path[(scope.Path.IndexOf('\\') + 1)..] + "\\First]\r\n", export);
        Assert.Contains("[HKEY_CURRENT_USER\\" + scope.Path[(scope.Path.IndexOf('\\') + 1)..] + "\\Second]\r\n", export);
        Assert.True(export.IndexOf("\\First]", StringComparison.Ordinal) < export.IndexOf("\\Second]", StringComparison.Ordinal));
        Assert.Contains("@=\"default\"\r\n", export);
        Assert.Contains("\"quoted\"=\"A \\\"quote\\\" and C:\\\\path\"\r\n", export);
        Assert.Contains("\"number\"=dword:12345678\r\n", export);
        Assert.Contains("\"second\"=\"included\"\r\n", export);
        Assert.DoesNotContain("excluded", export);
        Assert.DoesNotContain("\\Unselected]", export);
    }

    [Fact]
    public void Export_ParseRoundTripPreservesMalformedAndArbitraryRawBytes()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "Raw");
        var path = scope.Path + "\\Raw";
        var expected = new[] {
            new RawValue("bad-string", 1, new byte[] { 0x41, 0, 0x42 }),
            new RawValue("bad-dword", 4, new byte[] { 1, 2, 3 }),
            new RawValue("custom", 123, new byte[] { 0, 0xff, 0x80 }),
            new RawValue("empty", 3, Array.Empty<byte>()),
            new RawValue("wrapped", 3, Enumerable.Range(0, 25).Select(value => (byte)value).ToArray())
        };
        foreach (var value in expected) RegistryBackend.Set(path, value);

        var export = RegFile.Export(new[] { path }, TestContext.Current.CancellationToken);
        Assert.Contains("\"bad-string\"=hex(1):41,00,42\r\n", export);
        Assert.Contains("\"bad-dword\"=hex(4):01,02,03\r\n", export);
        Assert.Contains("\"custom\"=hex(7b):00,ff,80\r\n", export);
        Assert.Contains("\"empty\"=hex:\r\n", export);
        Assert.Contains("\"wrapped\"=hex:00,01", export);
        Assert.Contains("\\\r\n  18\r\n", export);

        var parsed = RegFile.Parse(export).OfType<SetValueChange>().Select(change => change.Value).ToArray();
        Assert.Equal(expected.Length, parsed.Length);
        foreach (var raw in expected) {
            var actual = Assert.Single(parsed, value => value.Name == raw.Name);
            Assert.Equal(raw.Kind, actual.Kind);
            Assert.Equal(raw.Bytes, actual.Bytes);
        }
    }

    [Fact]
    public void Export_StopsAtAnUnrepresentableFixtureKeyOrValueName()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "bad]section");
        var unsafeKey = scope.Path + "\\bad]section";
        Assert.Throws<InvalidOperationException>(() => RegFile.Export(new[] { unsafeKey }, TestContext.Current.CancellationToken));

        RegistryBackend.Set(scope.Path, new RawValue("bad\nname", 3, new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => RegFile.Export(new[] { scope.Path }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Export_ObservesCancellationBeforeOpeningAnExplicitFixturePath()
    {
        using var scope = new RegistryScope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            RegFile.Export(new[] { scope.Path }, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void ExportToFile_LegacyUsesSystemAcpHeaderAndReadsBackRepresentableText()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("text", 1, "café", hex: false));
        var file = Path.Combine(Path.GetTempPath(), "RegistryEditor.Tests." + Guid.NewGuid().ToString("N") + ".reg");
        try {
            RegFile.ExportToFile(scope.Path, file, legacy: true);

            var bytes = File.ReadAllBytes(file);
            Assert.Equal("REGEDIT4", Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.False(bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe);
            Assert.Equal(AcpEncoding().GetBytes(RegFile.Export(scope.Path).Replace(
                "Windows Registry Editor Version 5.00", "REGEDIT4", StringComparison.Ordinal)), bytes);

            var parsed = RegFile.Parse(RegFile.Read(file));
            var value = Assert.Single(parsed.OfType<SetValueChange>()).Value;
            Assert.Equal("text", value.Name);
            Assert.Equal(Encoding.Unicode.GetBytes("café\0"), value.Bytes);
        }
        finally {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExportToFile_LegacyRejectsTextUnrepresentableInSystemAcp()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, new RawValue("bad\ud800", 3, new byte[] { 1 }));
        var file = Path.Combine(Path.GetTempPath(), "RegistryEditor.Tests." + Guid.NewGuid().ToString("N") + ".reg");
        try {
            Assert.Throws<EncoderFallbackException>(() => RegFile.ExportToFile(scope.Path, file, legacy: true));
            Assert.False(File.Exists(file));
        }
        finally {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void ExportToFile_UsesUtf16AndReadRejectsMalformedEncodings()
    {
        using var scope = new RegistryScope();
        RegistryBackend.Set(scope.Path, new RawValue("value", 3, new byte[] { 1, 2 }));
        var file = Path.Combine(Path.GetTempPath(), "RegistryEditor.Tests." + Guid.NewGuid().ToString("N") + ".reg");
        try {
            RegFile.ExportToFile(scope.Path, file);
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(0xff, bytes[0]);
            Assert.Equal(0xfe, bytes[1]);
            var changes = RegFile.Parse(RegFile.Read(file));
            var value = Assert.IsType<SetValueChange>(changes[1]).Value;
            Assert.Equal("value", value.Name);
            Assert.Equal(new byte[] { 1, 2 }, value.Bytes);
            File.WriteAllBytes(file, new byte[] { 0xff, 0xfe, 0x41 });
            Assert.Throws<FormatException>(() => RegFile.Read(file));
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes("Windows Registry Editor Version 5.00\r\n")
                .Concat(new byte[] { 0xc3, 0x28 }).ToArray());
            Assert.Throws<DecoderFallbackException>(() => RegFile.Read(file));
        }
        finally {
            File.Delete(file);
        }
    }

    [Fact]
    public void Parse_ContinuesOnlyHexValuesAndRejectsDanglingBackslash()
    {
        const string prefix = "REGEDIT4\n[HKCU\\Software\\Example]\n";
        foreach (var invalid in new[] {
            "\\",
            "\\\n",
            "\"x\"=\"hello\\\nworld\"",
            "\"x\"=dword:0000\\\n0001",
            "[HKCU\\Software\\Ex\\\nample]",
            "\"x\"=hex:01,\\",
            "\"x\"=hex:01,\\\n",
        })
            Assert.Throws<FormatException>(() => RegFile.Parse(prefix + invalid));
        var changes = RegFile.Parse(prefix + "\"a\\\\b\"=hex(7):01,\\\n  02,\\\n  03");
        var value = Assert.IsType<SetValueChange>(changes[1]).Value;
        Assert.Equal("a\\b", value.Name);
        Assert.Equal(7u, value.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, value.Bytes);
    }

    private static Encoding AcpEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(checked((int)GetACP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
