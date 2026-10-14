using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using ModdingSupportTool.Cli;

namespace ModdingSupportTool;

sealed class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const uint FileTypeDisk = 0x0001, FileTypePipe = 0x0003;

    [STAThread]
    public static int Main(string[] args)
    {
        // 인자가 없으면 GUI, 있으면 CLI(하위 명령 또는 예전 덤프 인자)
        if (args.Length == 0)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        // MCP 모드는 부모(에이전트)가 넘겨준 stdin/stdout 파이프를 그대로 써야 하므로 콘솔을 붙이지 않는다.
        if (!args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            PrepareConsole();
        }

        return CliRunner.RunAsync(args).GetAwaiter().GetResult();
    }

    // WinExe는 콘솔이 없으므로, 출력이 파이프/파일로 리디렉션되지 않았을 때만 부모 콘솔에 붙거나 새로 만든다.
    // (에이전트가 파이프로 실행했는데 AllocConsole을 하면 출력이 새 창으로 가 버린다)
    private static void PrepareConsole()
    {
        if (!IsOutputRedirected() && !AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        var utf8 = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    private static bool IsOutputRedirected()
    {
        if (!OperatingSystem.IsWindows()) return true;
        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        uint type = GetFileType(handle);
        return type == FileTypeDisk || type == FileTypePipe;
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
