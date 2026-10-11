using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;
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

        // 독립 아웃라인 변환 모드: ModdingSupportTool.exe --outline <소스폴더> [출력폴더]
        if (args.Length > 0 && (args[0] == "--outline" || args[0] == "-outline"))
        {
            string sourceDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("Decompiled");
            string outDir = args.Length > 2 ? Path.GetFullPath(args[2]) : DumpService.GetOutlineDirectory(sourceDir);

            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"[오류] 대상 폴더가 존재하지 않습니다: {sourceDir}");
                return 1;
            }

            Console.WriteLine($"[독립 AI 아웃라인 생성 모드]");
            Console.WriteLine($"  -> 원본 폴더: {sourceDir}");
            Console.WriteLine($"  -> 아웃라인 폴더: {outDir}");

            try
            {
                var stats = await RoslynOutliner.ProcessDirectoryAsync(sourceDir, outDir, line => Console.WriteLine(line), CancellationToken.None);
                Console.WriteLine($"\n[성공] 총 {stats.ProcessedFiles}개 파일 변환 완료!");
                Console.WriteLine($"  -> 라인 수: {stats.OriginalLines:N0}줄 -> {stats.OutlineLines:N0}줄 ({stats.LineReductionPercent}% 토큰 절감)");
                Console.WriteLine($"  -> 저장 위치: {outDir}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[오류] 아웃라인 생성 실패: {ex.Message}");
                return 1;
            }
        }

        var parsed = ParseArguments(args);
        string inputPath = parsed.InputPath;
        string outputPath = parsed.OutputPath;
        string targets = parsed.Targets;
        bool generateOutline = parsed.GenerateOutline;

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
        else if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"[오류] 지정된 입력 경로가 존재하지 않습니다: {inputPath}");
            PrintUsage();
            return 1;
        }

        Console.WriteLine($"[입력 경로] {inputPath}");
        Console.WriteLine($"[출력 폴더] {outputPath}");
        Console.WriteLine($"[대상 어셈블리] {targets}");
        Console.WriteLine($"[AI 아웃라인 동시 생성] {(generateOutline ? "활성화" : "비활성화")}");

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
            IlSpyNestedDirectories = true,
            GenerateAiOutline = generateOutline
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
            var result = await dumpService.RunDumpAsync(request, line => Console.WriteLine(line), cts.Token);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine($"\n[실패] 덤프에 실패했습니다. (성공 {result.SucceededCount}개, 실패 {result.FailedCount}개) 위 로그를 확인하세요.");
                return 3;
            }

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

    private static (string InputPath, string OutputPath, string Targets, bool GenerateOutline) ParseArguments(string[] args)
    {
        string inputPath = string.Empty;
        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Decompiled");
        string targets = "Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll";
        bool generateOutline = true;

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
                else if (args[i] == "--no-outline")
                    generateOutline = false;
                else if (args[i] == "--outline")
                    generateOutline = true;
            }
        }

        return (inputPath, outputPath, targets, generateOutline);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  1. 일반 덤프 모드:");
        Console.WriteLine("     ModdingSupportTool.exe [입력경로] [출력폴더] [타겟DLL목록]");
        Console.WriteLine("     ModdingSupportTool.exe --input <경로> --output <폴더> --targets <dll1,dll2> [--no-outline]");
        Console.WriteLine("  2. 기존 디컴파일 폴더 독립 아웃라인 생성 모드:");
        Console.WriteLine("     ModdingSupportTool.exe --outline <디컴파일폴더경로> [출력아웃라인폴더]");
    }
}
