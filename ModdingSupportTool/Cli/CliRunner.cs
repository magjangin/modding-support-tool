using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Models;
using ModdingSupportTool.Services;

namespace ModdingSupportTool.Cli;

public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=================================================");
        Console.WriteLine(" Modding Support Tool - 자동 덤프 CLI 모드");
        Console.WriteLine("=================================================");

        var parsed = ParseArguments(args);
        string inputPath = parsed.InputPath;
        string outputPath = parsed.OutputPath;
        string targets = parsed.Targets;

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = PresetService.ResolveInitialPath();

            if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
            {
                Console.Error.WriteLine("[오류] 입력 경로가 지정되지 않았으며 기본 경로를 찾을 수 없습니다.");
                PrintUsage();
                return 1;
            }
        }

        Console.WriteLine($"[입력 경로] {inputPath}");
        Console.WriteLine($"[출력 폴더] {outputPath}");
        Console.WriteLine($"[대상 어셈블리] {targets}");

        // Unity 백엔드 자동 판별
        Console.WriteLine("\n[1] Unity 엔진 백엔드 자동 판별 중...");
        var detection = UnityBackendDetector.Detect(inputPath);
        Console.WriteLine($"  -> 판별 결과: {detection.Backend} ({detection.Reason})");

        DumpEngineType engine;

        if (detection.Backend == UnityBackend.Il2Cpp)
        {
            engine = DumpEngineType.SignatureDumper;
            Console.WriteLine("  -> 엔진 선택: SignatureDumper (앱 내장)");
            if (!string.IsNullOrEmpty(detection.RecommendedAssemblyPath) &&
                detection.RecommendedAssemblyPath != inputPath &&
                Directory.Exists(detection.RecommendedAssemblyPath))
            {
                Console.WriteLine($"  -> 추천 어셈블리 폴더로 자동 보정: {detection.RecommendedAssemblyPath}");
                inputPath = detection.RecommendedAssemblyPath;
            }
        }
        else if (detection.Backend == UnityBackend.Mono)
        {
            engine = DumpEngineType.IlSpyCmd;
            Console.WriteLine("  -> 엔진 선택: ilspycmd (Mono)");
            if (!string.IsNullOrEmpty(detection.RecommendedAssemblyPath) &&
                detection.RecommendedAssemblyPath != inputPath &&
                Directory.Exists(detection.RecommendedAssemblyPath))
            {
                Console.WriteLine($"  -> 추천 어셈블리 폴더로 자동 보정: {detection.RecommendedAssemblyPath}");
                inputPath = detection.RecommendedAssemblyPath;
            }
        }
        else
        {
            engine = DumpEngineType.SignatureDumper;
            Console.WriteLine("  -> 백엔드 미확정: 기본 엔진(SignatureDumper)으로 시도합니다.");
        }

        Console.WriteLine("\n[2] 덤프 작업 시작...");
        var dumpService = new DumpService();
        var request = new DumpRequest
        {
            Engine = engine,
            InputPath = inputPath,
            OutputDirectory = outputPath,
            TargetAssemblyNames = targets,
            IlSpyCreateProject = true,
            IlSpyNestedDirectories = true
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[작업 취소 요청됨]");
        };

        try
        {
            await dumpService.RunDumpAsync(request, line => Console.WriteLine(line), cts.Token);
            Console.WriteLine("\n[성공] 모든 작업이 성공적으로 완료되었습니다!");
            Console.WriteLine($"결과물 위치: {outputPath}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[작업이 취소되었습니다.]");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[오류 발생] {ex.Message}");
            return 3;
        }
    }

    private static (string InputPath, string OutputPath, string Targets) ParseArguments(string[] args)
    {
        string inputPath = string.Empty;
        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Decompiled");
        string targets = "Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll";

        if (args.Length > 0 && !args[0].StartsWith("-"))
        {
            inputPath = args[0];
            if (args.Length > 1 && !args[1].StartsWith("-"))
            {
                outputPath = args[1];
            }
            if (args.Length > 2 && !args[2].StartsWith("-"))
            {
                targets = args[2];
            }
        }
        else
        {
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "-i" || args[i] == "--input") && i + 1 < args.Length)
                    inputPath = args[++i];
                else if ((args[i] == "-o" || args[i] == "--output") && i + 1 < args.Length)
                    outputPath = args[++i];
                else if ((args[i] == "-t" || args[i] == "--targets") && i + 1 < args.Length)
                    targets = args[++i];
            }
        }

        return (inputPath, outputPath, targets);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  ModdingSupportTool.exe [입력경로] [출력폴더] [타겟DLL목록]");
        Console.WriteLine("  ModdingSupportTool.exe --input <경로> --output <폴더> --targets <dll1,dll2>");
    }
}
