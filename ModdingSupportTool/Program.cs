using System;
using System.Runtime.InteropServices;
using Avalonia;
using ModdingSupportTool.Cli;

namespace ModdingSupportTool;

sealed class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private const int AttachParentProcess = -1;

    private static readonly string[] CliFlags =
        { "--cli", "--auto", "-i", "--input", "-o", "--output", "-t", "--targets" };

    [STAThread]
    public static int Main(string[] args)
    {
        // CLI 인자가 전달되었을 경우 콘솔 자동 실행 모드로 진입
        if (args.Length > 0 && (Array.IndexOf(CliFlags, args[0]) >= 0 || !args[0].StartsWith("-")))
        {
            if (!AttachConsole(AttachParentProcess))
            {
                AllocConsole();
            }

            return CliRunner.RunAsync(args).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
