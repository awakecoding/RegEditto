using System;
using System.IO;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.UI.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Parse_RoutesOpenPromptSilentAndUnicodeAnsiExportWithoutExecuting()
    {
        Assert.Equal(new RegistryCommand(RegistryCommandMode.Open), RegistryCommandLine.Parse(Array.Empty<string>()));
        Assert.Equal(new RegistryCommand(RegistryCommandMode.Open), RegistryCommandLine.Parse(new[] { "/m" }));
        Assert.Equal("HKEY_CURRENT_USER\\Software", RegistryCommandLine.Parse(new[] { "HKCU\\Software" }).Path);
        Assert.Equal(RegistryCommandMode.ImportPrompt, RegistryCommandLine.Parse(new[] { "input.reg" }).Mode);
        var silent = RegistryCommandLine.Parse(new[] { "/m", "/S", "input.reg" });
        Assert.Equal(RegistryCommandMode.ImportSilent, silent.Mode);
        Assert.Equal(Path.GetFullPath("input.reg"), silent.File);
        var export = RegistryCommandLine.Parse(new[] { "/e", "output.reg", "HKCU\\Software" });
        Assert.Equal(RegistryCommandMode.Export, export.Mode);
        Assert.Equal(Path.GetFullPath("output.reg"), export.File);
        Assert.Equal("HKEY_CURRENT_USER\\Software", export.Path);
        var legacy = RegistryCommandLine.Parse(new[] { "/A", "legacy.reg" });
        Assert.Equal(RegistryCommandMode.ExportLegacy, legacy.Mode);
        Assert.Equal("Computer", legacy.Path);
    }
    [Fact]
    public void Parse_SilentWithoutFilesOpensEditorLikeRegedit()
        => Assert.Equal(new RegistryCommand(RegistryCommandMode.Open), RegistryCommandLine.Parse(new[] { "/s" }));

    [Theory]
    [InlineData("/regserver")]
    [InlineData("/unregserver")]
    public void Parse_RejectsUnsupportedRegistration(string argument)
        => Assert.Throws<ArgumentException>(() => RegistryCommandLine.Parse(new[] { argument }));

    [Fact]
    public void Parse_IgnoresOverspecifiedExportsAndExecuteRejectsInteractiveCommands()
    {
        Assert.Equal(RegistryCommandMode.NoOp, RegistryCommandLine.Parse(new[] { "/e", "input.reg", "HKCU", "extra" }).Mode);
        Assert.Throws<InvalidOperationException>(() => RegistryCommandLine.Execute(new RegistryCommand(RegistryCommandMode.Open)));
        Assert.Throws<InvalidOperationException>(() => RegistryCommandLine.Execute(new RegistryCommand(RegistryCommandMode.ImportPrompt, "input.reg")));
    }

    [Fact]
    public void Parse_ImportsArbitraryFilenamesAndPreservesSpacesWithoutRequiringRegExtension()
    {
        foreach (var file in new[] { "backup", "backup.txt", @"folder with spaces\settings export.REG", @"C:\settings backup.data" }) {
            var command = RegistryCommandLine.Parse(new[] { file });
            Assert.Equal(RegistryCommandMode.ImportPrompt, command.Mode);
            Assert.Equal(Path.GetFullPath(file), command.File);
        }
        Assert.Equal("HKEY_CURRENT_USER\\Software\\Key with spaces",
            RegistryCommandLine.Parse(new[] { "/E", "output file.reg", @"HKEY_CURRENT_USER\Software\Key with spaces" }).Path);
    }

    [Fact]
    public void ExportRoots_UsesLocalMachineThenCurrentUserForWholeRegistryAndPreservesExplicitBranch()
    {
        Assert.Equal(new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" },
            RegistryCommandLine.ExportRoots(RegistryCommandLine.Parse(new[] { "/e", "all.reg" })));
        Assert.Equal(new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" },
            RegistryCommandLine.ExportRoots(RegistryCommandLine.Parse(new[] { "/a", "all.reg" })));
        Assert.Equal(new[] { "HKEY_CURRENT_USER\\Software" },
            RegistryCommandLine.ExportRoots(RegistryCommandLine.Parse(new[] { "/e", "part.reg", @"HKCU\Software" })));
    }

    [Fact]
    public void IsSilent_RecognizesMalformedSilentCommandsWithoutTurningExportOrInteractiveErrorsSilent()
    {
        Assert.True(RegistryCommandLine.IsSilent(new[] { "/S" }));
        Assert.True(RegistryCommandLine.IsSilent(new[] { "/m", "/s", "file.reg", "extra" }));
        Assert.False(RegistryCommandLine.IsSilent(Array.Empty<string>()));
        Assert.False(RegistryCommandLine.IsSilent(new[] { "/e", "file.reg" }));
        Assert.False(RegistryCommandLine.IsSilent(new[] { "file.reg" }));
        Assert.True(RegistryCommandLine.IsSilent(new[] { "/unknown", "/s" }));
    }

    [Fact]
    public void Parse_AcceptsDashCombinedRepeatedSwitchesAndImportsFilesInOrder()
    {
        var command = RegistryCommandLine.Parse(new[] { "-m", "/S", "first file.reg", "second.reg" });
        Assert.Equal(RegistryCommandMode.ImportSilent, command.Mode);
        Assert.Equal(new[] { Path.GetFullPath("first file.reg"), Path.GetFullPath("second.reg") },
            RegistryCommandLine.ImportFiles(command));
        Assert.Equal(RegistryCommandMode.Export, RegistryCommandLine.Parse(new[] { "/se", "file.reg", "HKCU" }).Mode);
        Assert.Equal(RegistryCommandMode.ExportLegacy, RegistryCommandLine.Parse(new[] { "-s", "-e", "/a", "file.reg" }).Mode);
        Assert.Equal(RegistryCommandMode.ImportPrompt, RegistryCommandLine.Parse(new[] { "one.reg", "two.reg" }).Mode);
        Assert.True(RegistryCommandLine.IsSilent(new[] { "-ms" }));
        Assert.Equal(RegistryCommandMode.Open, RegistryCommandLine.Parse(new[] { "/v", "/c" }).Mode);
        foreach (var flag in new[] { "/e", "/a", "/L:system", "/r" })
            Assert.Equal(RegistryCommandMode.NoOp, RegistryCommandLine.Parse(new[] { flag }).Mode);
    }
}
