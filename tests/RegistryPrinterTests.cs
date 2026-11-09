using System.Linq;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class RegistryPrinterTests
{
    [Fact]
    public void Lines_ReportsFixtureKeysValuesTypesDataAndNestedTraversalWithoutPrinting()
    {
        using var scope = new RegistryScope();
        RegistryBackend.CreateKey(scope.Path, "Child");
        RegistryBackend.Set(scope.Path, RegistryBackend.ParseValue("", 1, "root", hex: false));
        RegistryBackend.Set(scope.Path, new RawValue("count", 4, new byte[] { 42, 0, 0, 0 }));
        RegistryBackend.Set(scope.Path, new RawValue("malformed", 4, new byte[] { 1, 2, 3 }));
        RegistryBackend.Set(scope.Path + "\\Child", new RawValue("payload", 3, new byte[] { 0, 255 }));

        var lines = RegistryPrinter.Lines(scope.Path).ToArray();

        Assert.Equal(new[] {
            "Key Name: " + scope.Path,
            "  Value Name: (Default)",
            "  Type: REG_SZ",
            "  Data: root",
            "",
            "  Value Name: count",
            "  Type: REG_DWORD",
            "  Data: 0x0000002a (42)",
            "",
            "  Value Name: malformed",
            "  Type: REG_DWORD",
            "  Data: 01 02 03",
            "",
            "",
            "Key Name: " + scope.Path + "\\Child",
            "  Value Name: payload",
            "  Type: REG_BINARY",
            "  Data: 00 ff",
            "",
            ""
        }, lines);
    }
}
