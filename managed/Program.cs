using System;
using System.Linq;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RegistryEditor.Presentation.Backend;
using RegistryEditor.Presentation.Views;

namespace RegistryEditor.Presentation;

internal static class Program
{
    internal static RegistryCommand? Command { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegEditto");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"), $"{DateTimeOffset.Now:O}\n{e.ExceptionObject}\n");
            Console.Error.WriteLine(e.ExceptionObject);
        };
        try {
            Command = RegistryCommandLine.Parse(args);
            if (Command.Mode == RegistryCommandMode.NoOp) return 0;
            if (Command.Mode is RegistryCommandMode.ImportSilent or RegistryCommandMode.Export or RegistryCommandMode.ExportLegacy) {
                RegistryCommandLine.Execute(Command);
                return 0;
            }
            AppBuilder.Configure<RegistryApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or Win32Exception or UnauthorizedAccessException or FormatException or OverflowException) {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegEditto");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"), $"{DateTimeOffset.Now:O}\n{error}\n");
            Console.Error.WriteLine(error.Message);
            if (!RegistryCommandLine.IsSilent(args)) MessageBox(IntPtr.Zero, error.Message, "RegEditto", 0x10);
            return 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}

public sealed class RegistryApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.Command?.Mode == RegistryCommandMode.Open && Program.Command.Path != "Computer" ? Program.Command.Path : null,
                initialImports: Program.Command?.Mode == RegistryCommandMode.ImportPrompt ? RegistryCommandLine.ImportFiles(Program.Command).ToArray() : null);
        base.OnFrameworkInitializationCompleted();
    }
}
