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

    [STAThread]
    public static int Main(string[] args)
    {
        // CLI 인자가 전달되었을 경우 콘솔 자동 실행 모드로 진입
        if (args.Length > 0 && (args[0] == "--cli" || args[0] == "--auto" || !args[0].StartsWith("-")))
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
