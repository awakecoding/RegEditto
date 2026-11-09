using System;
using System.Collections.Generic;
using System.IO;

namespace RegistryEditor.Presentation.Backend;

public enum RegistryCommandMode { Open, ImportPrompt, ImportSilent, Export, ExportLegacy, NoOp }
public sealed record RegistryCommand(RegistryCommandMode Mode, string? File = null, string Path = "Computer",
    IReadOnlyList<string>? AdditionalFiles = null);

public static class RegistryCommandLine
{
    public static bool IsSilent(IReadOnlyList<string> args)
    {
        foreach (var argument in args) {
            if (!argument.StartsWith('/') && !argument.StartsWith('-')) break;
            if (argument[1..].Contains("s", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static RegistryCommand Parse(IReadOnlyList<string> args)
    {
        var index = 0;
        var silent = false;
        var export = false;
        var legacy = false;
        while (index < args.Count && (args[index].StartsWith('/') || args[index].StartsWith('-'))) {
            var block = args[index++][1..];
            if (block.Equals("regserver", StringComparison.OrdinalIgnoreCase) || block.Equals("unregserver", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Self-registration is not supported; this application does not change Windows shell associations.");
            foreach (var letter in block) {
                switch (char.ToLowerInvariant(letter)) {
                    case 'a': export = true; legacy = true; break;
                    case 'e': export = true; break;
                    case 's': silent = true; break;
                    case 'r': case 'l': return new RegistryCommand(RegistryCommandMode.NoOp);
                }
            }
        }
        var count = args.Count - index;
        if (count == 0) {
            if (export) return new RegistryCommand(RegistryCommandMode.NoOp);
            return new RegistryCommand(RegistryCommandMode.Open);
        }
        var first = args[index];
        if (export) {
            if (count > 2) return new RegistryCommand(RegistryCommandMode.NoOp);
            var path = count == 2 ? RegistryBackend.ParsePath(args[index + 1]).ToString() : "Computer";
            return new RegistryCommand(legacy ? RegistryCommandMode.ExportLegacy : RegistryCommandMode.Export,
                System.IO.Path.GetFullPath(first), path);
        }
        if (silent || count > 1) {
            var additional = new List<string>();
            for (var i = index + 1; i < args.Count; i++) additional.Add(System.IO.Path.GetFullPath(args[i]));
            return new RegistryCommand(silent ? RegistryCommandMode.ImportSilent : RegistryCommandMode.ImportPrompt,
                System.IO.Path.GetFullPath(first), AdditionalFiles: additional.Count == 0 ? null : additional);
        }
        if (first.Equals("Computer", StringComparison.OrdinalIgnoreCase)) return new RegistryCommand(RegistryCommandMode.Open);
        var root = first.Split('\\', 2)[0];
        if (root.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || root.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("HKCR", StringComparison.OrdinalIgnoreCase) || root.Equals("HKU", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("HKCC", StringComparison.OrdinalIgnoreCase) || root.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            return new RegistryCommand(RegistryCommandMode.Open, Path: RegistryBackend.ParsePath(first).ToString());
        return new RegistryCommand(RegistryCommandMode.ImportPrompt, System.IO.Path.GetFullPath(first));
    }

    public static IReadOnlyList<string> ExportRoots(RegistryCommand command)
        => command.Path == "Computer" ? new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" } : new[] { command.Path };
    public static void Execute(RegistryCommand command)
    {
        switch (command.Mode) {
            case RegistryCommandMode.ImportSilent:
                foreach (var file in ImportFiles(command)) RegFile.Apply(RegFile.Parse(RegFile.Read(file)));
                break;
            case RegistryCommandMode.Export: case RegistryCommandMode.ExportLegacy:
                RegFile.ExportToFile(ExportRoots(command), command.File ?? throw new ArgumentException("Export requires a file."), command.Mode == RegistryCommandMode.ExportLegacy); break;
            default: throw new InvalidOperationException("This command requires an interactive application window.");
        }
    }

    public static IEnumerable<string> ImportFiles(RegistryCommand command)
    {
        yield return command.File ?? throw new ArgumentException("Import requires a file.");
        if (command.AdditionalFiles is not null)
            foreach (var file in command.AdditionalFiles) yield return file;
    }
}
