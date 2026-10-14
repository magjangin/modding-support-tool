using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Agent;
using ModdingSupportTool.Agent.Mcp;
using ModdingSupportTool.Core;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Models;
using ModdingSupportTool.Services;

namespace ModdingSupportTool.Cli;

public static class CliRunner
{
    // 종료 코드: 0 성공, 1 입력/인자 오류, 2 취소, 3 덤프·도구 실패
    private const int Ok = 0, BadInput = 1, Cancelled = 2, Failed = 3;

    private static readonly string[] Commands = { "dump", "outline", "detect", "refs", "search", "type", "patch", "template", "mcp", "agent", "help" };

    public static bool IsCommand(string arg) => Commands.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        string first = args.Length > 0 ? args[0] : "";

        // 예전 사용법 호환: --outline/-outline <소스> [출력], 그 밖의 플래그나 경로로 시작하면 덤프
        if (first is "--outline" or "-outline") return await RunOutlineAsync(CliArgs.Parse(args.Skip(1)));
        if (first is "--help" or "-h" or "help") { PrintUsage(); return Ok; }
        if (!IsCommand(first)) return await RunDumpAsync(CliArgs.Parse(args), legacy: true);

        var rest = CliArgs.Parse(args.Skip(1));
        if (rest.Has("help")) { PrintUsage(); return Ok; }

        var toolkit = new ModdingToolkit { DefaultReferenceDirectory = rest.Get("ref") };
        switch (first.ToLowerInvariant())
        {
            case "dump": return await RunDumpAsync(rest, legacy: false);
            case "outline": return await RunOutlineAsync(rest);
            case "mcp": return await McpServer.RunStdioAsync(rest.Get("ref"));
            case "agent": return await RunAgentAsync(rest);
            case "detect": return Print(toolkit.Detect(rest.Positional(0) ?? rest.Get("input") ?? ""));
            case "refs": return Print(toolkit.DescribeReference(rest.Positional(0) ?? rest.Get("ref")));
            case "search":
                return Print(toolkit.Search(string.Join(" ", rest.Positionals), rest.Get("ref"), rest.Get("kind"), rest.Get("ns", "namespace"), rest.GetInt("limit", 30)));
            case "type":
                return Print(toolkit.ReadType(rest.Positional(0) ?? "", rest.Get("ref"), rest.Get("member"), rest.Has("full"), rest.GetInt("max-chars", 40000)));
            case "patch":
                return Print(toolkit.HarmonyPatch(rest.Positional(0) ?? "", rest.Positional(1) ?? rest.Get("member") ?? "", rest.Get("ref"), rest.Get("kind")));
            case "template":
                return Print(toolkit.ModTemplate(rest.Positional(0) ?? "", rest.Positional(1) ?? rest.Get("name") ?? "", rest.Get("author"), rest.Get("out", "output"), rest.Has("force") || rest.Has("overwrite")));
            default:
                PrintUsage();
                return Ok;
        }
    }

    private static int Print(ToolResult result)
    {
        if (result.IsError) Console.Error.WriteLine(result.Text);
        else Console.WriteLine(result.Text);
        return result.IsError ? Failed : Ok;
    }

    private static void PrintBanner(string title)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine($" Modding Support Tool {AppPaths.Version} - {title}");
        Console.WriteLine("=================================================");
    }

    // ───────────────────────────── dump ─────────────────────────────

    private static async Task<int> RunDumpAsync(CliArgs a, bool legacy)
    {
        PrintBanner("자동 덤프 CLI 모드");

        // 위치 인자: [입력경로] [출력폴더] [타겟DLL목록]
        string inputPath = a.Get("input") ?? a.Positional(0) ?? "";
        string outputPath = a.Get("output", "out") ?? a.Positional(1) ?? AppPaths.DefaultOutputDirectory;
        string targets = a.Get("targets") ?? a.Positional(2) ?? DumpService.DefaultTargets;
        bool generateOutline = !a.Has("no-outline");

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = PresetService.ResolveInitialPath();
            if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
            {
                Console.Error.WriteLine("[오류] 입력 경로가 지정되지 않았으며 기본 경로를 찾을 수 없습니다.");
                PrintUsage();
                return BadInput;
            }
        }
        else if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"[오류] 지정된 입력 경로가 존재하지 않습니다: {inputPath}");
            PrintUsage();
            return BadInput;
        }

        DumpEngineType? forced = (a.Get("engine") ?? "auto").ToLowerInvariant() switch
        {
            "signature" => DumpEngineType.SignatureDumper,
            "ilspy" => DumpEngineType.IlSpyCmd,
            _ => null
        };

        Console.WriteLine($"[입력 경로] {inputPath}");
        Console.WriteLine($"[출력 폴더] {outputPath}");
        Console.WriteLine($"[대상 어셈블리] {targets}");
        Console.WriteLine($"[AI 아웃라인 동시 생성] {(generateOutline ? "활성화" : "비활성화")}");

        Console.WriteLine("\n[1] Unity 엔진 백엔드 자동 판별 중...");
        var plan = DumpService.Plan(inputPath, forced);
        Console.WriteLine($"  -> 판별 결과: {plan.Detection.Backend} ({plan.Detection.Reason})");
        Console.WriteLine(plan.Detection.Backend == UnityBackend.Unknown && forced == null
            ? "  -> 백엔드 미확정: 기본 엔진(SignatureDumper)으로 시도합니다."
            : $"  -> 엔진 선택: {DumpService.EngineDisplayName(plan.Engine)}");
        if (plan.InputPathAdjusted)
        {
            Console.WriteLine($"  -> 추천 어셈블리 폴더로 자동 보정: {plan.InputPath}");
        }

        Console.WriteLine("\n[2] 덤프 작업 시작...");
        var request = new DumpRequest
        {
            Engine = plan.Engine,
            InputPath = plan.InputPath,
            OutputDirectory = outputPath,
            TargetAssemblyNames = targets,
            IlSpyCreateProject = true,
            IlSpyNestedDirectories = true,
            GenerateAiOutline = generateOutline,
            IncludeInteropMembers = a.Has("keep-interop"),
            Backend = plan.Detection.Backend,
            Game = plan.Game
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
            var result = await new DumpService().RunDumpAsync(request, Console.WriteLine, cts.Token);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine($"\n[실패] 덤프에 실패했습니다. (성공 {result.SucceededCount}개, 실패 {result.FailedCount}개) 위 로그를 확인하세요.");
                return Failed;
            }

            Console.WriteLine("\n[성공] 모든 작업이 성공적으로 완료되었습니다!");
            Console.WriteLine($"결과물 위치: {outputPath}");
            return Ok;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[작업이 취소되었습니다.]");
            return Cancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[오류 발생] {ex.Message}");
            return Failed;
        }
    }

    // ───────────────────────────── outline ─────────────────────────────

    // 독립 아웃라인 변환: outline <소스폴더> [출력폴더]
    private static async Task<int> RunOutlineAsync(CliArgs a)
    {
        PrintBanner("AI 아웃라인 생성");
        string sourceDir = Path.GetFullPath(a.Positional(0) ?? a.Get("input") ?? AppPaths.DefaultOutputDirectory);
        string outDir = Path.GetFullPath(a.Positional(1) ?? a.Get("output", "out") ?? DumpService.GetOutlineDirectory(sourceDir));

        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"[오류] 대상 폴더가 존재하지 않습니다: {sourceDir}");
            return BadInput;
        }

        Console.WriteLine($"  -> 원본 폴더: {sourceDir}");
        Console.WriteLine($"  -> 아웃라인 폴더: {outDir}");

        try
        {
            var stats = await RoslynOutliner.ProcessDirectoryAsync(sourceDir, outDir, Console.WriteLine, CancellationToken.None);
            Console.WriteLine($"\n[성공] 총 {stats.ProcessedFiles}개 파일 변환 완료! (실패 {stats.FailedFiles}개)");
            Console.WriteLine($"  -> 라인 수: {stats.OriginalLines:N0}줄 -> {stats.OutlineLines:N0}줄 ({stats.LineReductionPercent}% 절감)");
            Console.WriteLine($"  -> 글자 수(토큰 근사): {stats.OriginalChars:N0} -> {stats.OutlineChars:N0} ({stats.CharReductionPercent}% 절감)");
            Console.WriteLine($"  -> 저장 위치: {outDir}");
            return stats.FailedFiles == 0 ? Ok : Failed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] 아웃라인 생성 실패: {ex.Message}");
            return BadInput;
        }
    }

    // ───────────────────────────── agent ─────────────────────────────

    private static async Task<int> RunAgentAsync(CliArgs a)
    {
        string action = (a.Positional(0) ?? "status").ToLowerInvariant();
        if (!AgentInstaller.TryParseClients(a.Get("client"), out var clients, out var error))
        {
            Console.Error.WriteLine(error);
            return BadInput;
        }

        string scope = (a.Get("scope") ?? (a.Has("project") ? "project" : "user")).ToLowerInvariant();
        if (scope is not ("user" or "project"))
        {
            Console.Error.WriteLine($"알 수 없는 범위: {scope} (user | project)");
            return BadInput;
        }

        var options = new AgentInstallOptions
        {
            Clients = clients,
            UserScope = scope == "user",
            ProjectDirectory = a.Get("project"),
            RegisterMcp = !a.Has("no-mcp"),
            Source = a.Get("source"),
            HomeDirectory = a.Get("home"),
            DryRun = a.Has("dry-run"),
            Force = a.Has("force")
        };

        switch (action)
        {
            case "install":
                return await AgentInstaller.InstallAsync(options, Console.WriteLine) ? Ok : Failed;
            case "uninstall":
                return await AgentInstaller.UninstallAsync(options, Console.WriteLine) ? Ok : Failed;
            case "status":
                AgentInstaller.PrintStatus(options, Console.WriteLine);
                return Ok;
            default:
                Console.Error.WriteLine($"알 수 없는 agent 동작: {action} (install | uninstall | status)");
                return BadInput;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
Modding Support Tool {AppPaths.Version} — MelonLoader 모드(DLL) 작성용 C# 레퍼런스 생성기 (Unity Mono/Il2Cpp 전용)
※ Unreal Engine 게임은 UE4SS, 네이티브 바이너리는 Ghidra/IDA 등 다른 도구를 쓰세요.

사용법: ModdingSupportTool.exe <명령> [인자] [옵션]   (인자 없이 실행하면 GUI)

덤프
  dump [입력] [출력] [대상DLL목록]      게임/어셈블리를 C# 레퍼런스로 덤프 (예전 형식: [입력] [출력] [대상], -i/-o/-t, --cli)
       --engine auto|signature|ilspy   엔진 강제 선택 (기본: 백엔드에 맞춤)
       --no-outline                    Mono 덤프 시 AI 아웃라인 생략
       --keep-interop                  Il2CppInterop 내부 포인터 멤버도 출력
  outline <소스폴더> [출력폴더]          기존 디컴파일 폴더를 AI 아웃라인으로 변환 (예전 형식: --outline)

에이전트용 레퍼런스 조회 (--ref <덤프폴더>, 생략 시 기본 출력 폴더)
  detect <경로>                         Unity 백엔드·MelonLoader 상태 판별
  refs [덤프폴더]                        레퍼런스 개요 (게임·모듈·네임스페이스)
  search <이름> [--kind type|method|property|field] [--ns 네임스페이스] [--limit N]
  type <타입> [--member 멤버] [--full]   선언 읽기 (member는 본문 포함)
  patch <타입> <메서드> [--kind prefix|postfix|both]   Harmony 패치 코드 생성
  template <게임폴더> <모드이름> [--author 이름] [--out 폴더] [--force]   MelonLoader 모드 프로젝트 생성

AI 에이전트 연동
  mcp [--ref 덤프폴더]                    MCP 서버(stdio)로 실행 — 에이전트가 직접 띄웁니다
  agent install [--client claude,antigravity,gemini|all] [--scope user|project] [--project 폴더]
                [--source github|<URL>|<폴더>] [--no-mcp] [--dry-run] [--force]
                                        스킬 MD 파일 설치 + MCP 서버 등록
  agent uninstall [같은 옵션]             설치한 스킬·MCP 등록 제거
  agent status                           설치 상태 확인

종료 코드: 0 성공, 1 입력/인자 오류, 2 취소, 3 덤프·도구 실패
""");
    }
}
