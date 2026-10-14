using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Core.Reference;
using ModdingSupportTool.Models;

namespace ModdingSupportTool.Services;

public sealed class DumpRequest
{
    public DumpEngineType Engine { get; set; } = DumpEngineType.SignatureDumper;
    public string InputPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string TargetAssemblyNames { get; set; } = DumpService.DefaultTargets;
    public bool IlSpyCreateProject { get; set; } = true;
    public bool IlSpyNestedDirectories { get; set; } = true;
    public bool GenerateAiOutline { get; set; } = true;
    public bool IncludeInteropMembers { get; set; }

    // true면 덤프가 쓴 스켈레톤/아웃라인 내용을 DumpResult.WrittenSources로 돌려준다. (덤프 직후 색인을 메모리에서 만들 때)
    public bool CaptureWrittenSources { get; set; }

    // reference.json 기록용 (모르면 비워 둔다)
    public UnityBackend Backend { get; set; } = UnityBackend.Unknown;
    public GameInfo? Game { get; set; }
}

public sealed record DumpModuleResult(string Name, string OutputPath, string? OutlinePath, int FileCount);

public sealed record DumpResult(int SucceededCount, int FailedCount, IReadOnlyList<DumpModuleResult> Modules)
{
    /// <summary>CaptureWrittenSources일 때 덤프가 쓴 파일 (전체 경로 → 내용)</summary>
    public IReadOnlyDictionary<string, string>? WrittenSources { get; init; }

    public static readonly DumpResult Empty = new(0, 0, Array.Empty<DumpModuleResult>());

    // 하나 이상 덤프했고 실패가 없어야 성공이다. (대상 파일이 하나도 없으면 실패)
    public bool IsSuccess => SucceededCount > 0 && FailedCount == 0;
}

/// <summary>입력 경로를 보고 정한 덤프 방식. CLI·GUI·MCP가 같은 규칙으로 엔진과 실제 입력 폴더를 고른다.</summary>
public sealed record DumpPlan(UnityDetectionResult Detection, DumpEngineType Engine, string InputPath, GameInfo? Game)
{
    public string OriginalInputPath { get; init; } = InputPath;
    public bool InputPathAdjusted => !string.Equals(InputPath, OriginalInputPath, StringComparison.OrdinalIgnoreCase);
}

public class DumpService
{
    public const string DefaultTargets = "Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll";

    public static DumpEngineType EngineFor(UnityBackend backend) =>
        backend == UnityBackend.Mono ? DumpEngineType.IlSpyCmd : DumpEngineType.SignatureDumper;

    public static string EngineDisplayName(DumpEngineType engine) =>
        engine == DumpEngineType.SignatureDumper ? "SignatureDumper (앱 내장)" : "ilspycmd (Mono)";

    /// <summary>
    /// 백엔드를 판별해 엔진을 정하고, 감지기가 추천한 어셈블리 폴더(Il2CppAssemblies, *_Data/Managed)가 있으면 입력을 그쪽으로 보정한다.
    /// </summary>
    public static DumpPlan Plan(string inputPath, DumpEngineType? forcedEngine = null, CancellationToken ct = default)
    {
        inputPath = NormalizePath(inputPath);
        var detection = UnityBackendDetector.Detect(inputPath, ct);
        string effective = inputPath;

        if (detection.Backend != UnityBackend.Unknown &&
            !string.IsNullOrEmpty(detection.RecommendedAssemblyPath) &&
            !string.Equals(detection.RecommendedAssemblyPath, inputPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(detection.RecommendedAssemblyPath))
        {
            effective = detection.RecommendedAssemblyPath;
        }

        return new DumpPlan(detection, forcedEngine ?? EngineFor(detection.Backend), effective, GameLocator.Find(inputPath))
        {
            OriginalInputPath = inputPath
        };
    }

    // "H:/game\Data" 처럼 섞인 구분자를 정리해서 로그·reference.json 경로를 일관되게 한다.
    private static string NormalizePath(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    public static string[] ParseTargets(string? targets) =>
        (targets ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();

    public static string GetOutlineDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) return string.Empty;
        return outputDirectory.TrimEnd('\\', '/') + "_Outline";
    }

    public async Task<DumpResult> RunDumpAsync(DumpRequest request, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            log("[오류] 입력 경로가 지정되지 않았습니다.");
            return DumpResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            log("[오류] 출력 폴더가 지정되지 않았습니다.");
            return DumpResult.Empty;
        }

        bool isFile = File.Exists(request.InputPath);
        if (!isFile && !Directory.Exists(request.InputPath))
        {
            log($"[오류] 지정된 입력 경로가 존재하지 않습니다: {request.InputPath}");
            return DumpResult.Empty;
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var watch = Stopwatch.StartNew();

        log("=== 덤프 작업 시작 ===");
        log($"엔진: {EngineDisplayName(request.Engine)}");
        log($"입력: {request.InputPath}");
        log($"출력: {request.OutputDirectory}");
        log($"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log("----------------------------------------");

        var dllPaths = ResolveTargetFiles(request, isFile, log);
        var written = request.CaptureWrittenSources ? new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
        Action<string, string>? onWritten = written != null ? (path, content) => written[Path.GetFullPath(path)] = content : null;
        var modules = new List<DumpModuleResult>();
        int failed = 0;

        foreach (string dllPath in dllPaths)
        {
            ct.ThrowIfCancellationRequested();

            var module = request.Engine == DumpEngineType.SignatureDumper
                ? await RunSignatureDumperAsync(request, dllPath, log, onWritten, ct)
                : await RunIlSpyCmdAsync(request, dllPath, log, onWritten, ct);

            if (module != null) modules.Add(module);
            else failed++;
        }

        log("----------------------------------------");
        log($"=== 덤프 작업 완료 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}, {watch.Elapsed.TotalSeconds:0.0}초) ===");
        log($"결과: 성공 {modules.Count}개, 실패 {failed}개");
        if (modules.Count == 0 && failed == 0)
        {
            log("[경고] 덤프된 어셈블리가 없습니다. 입력 폴더에 대상 어셈블리가 있는지 확인하세요.");
        }
        else if (modules.Count > 0)
        {
            log($"레퍼런스 정보: {Path.Combine(request.OutputDirectory, ReferenceManifest.FileName)}");
        }

        return new DumpResult(modules.Count, failed, modules) { WrittenSources = written };
    }

    private static List<string> ResolveTargetFiles(DumpRequest request, bool isFile, Action<string> log)
    {
        if (isFile)
        {
            return new List<string> { request.InputPath };
        }

        var files = new List<string>();
        foreach (string target in ParseTargets(request.TargetAssemblyNames))
        {
            string dllPath = Path.Combine(request.InputPath, target);
            if (File.Exists(dllPath)) files.Add(dllPath);
            else log($"[건너뜀] 대상 어셈블리 파일이 존재하지 않습니다: {dllPath}");
        }
        return files;
    }

    private static async Task<DumpModuleResult?> RunSignatureDumperAsync(DumpRequest request, string dllPath, Action<string> log, Action<string, string>? onWritten, CancellationToken ct)
    {
        string moduleName = Path.GetFileNameWithoutExtension(dllPath);
        string moduleOutputDir = Path.Combine(request.OutputDirectory, moduleName);
        ClearModuleOutput(moduleOutputDir, log);

        var options = new SignatureDumperOptions
        {
            TargetAssemblyPath = dllPath,
            SearchDirectory = Path.GetDirectoryName(dllPath),
            OutputDirectory = moduleOutputDir,
            IncludeInteropMembers = request.IncludeInteropMembers,
            FileWritten = onWritten
        };

        try
        {
            var result = await Task.Run(() => new BuiltinSignatureDumper(options, log).Dump(ct), ct);
            log($"  [완료] {Path.GetFileName(dllPath)}");

            RecordModule(request, new ReferenceModule
            {
                Name = moduleName,
                Engine = "SignatureDumper",
                Source = dllPath,
                Path = moduleName,
                HasMethodBodies = false,
                Files = result.FileCount,
                OmittedInteropMembers = result.IsIl2CppInterop && !request.IncludeInteropMembers ? result.OmittedInteropMembers : null,
                DumpedAt = DateTime.Now
            }, log);

            return new DumpModuleResult(moduleName, moduleOutputDir, null, result.FileCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"  [실패] {Path.GetFileName(dllPath)}: {ex.Message}");
            return null;
        }
    }

    private async Task<DumpModuleResult?> RunIlSpyCmdAsync(DumpRequest request, string dllPath, Action<string> log, Action<string, string>? onWritten, CancellationToken ct)
    {
        string moduleName = Path.GetFileNameWithoutExtension(dllPath);
        string moduleOutDir = Path.Combine(request.OutputDirectory, moduleName);

        // ilspycmd는 기존 파일을 지우지 않고, 생성된 .csproj는 폴더 안의 .cs를 전부 포함하므로 먼저 비운다.
        ClearModuleOutput(moduleOutDir, log);
        Directory.CreateDirectory(moduleOutDir);

        var arguments = new List<string>();
        if (request.IlSpyCreateProject) arguments.Add("-p");
        if (request.IlSpyNestedDirectories) arguments.Add("--nested-directories");
        arguments.AddRange(new[] { "-o", moduleOutDir, dllPath });

        log($"[ilspycmd 실행] {dllPath} -> {moduleOutDir}");
        if (!await ExecuteProcessAsync("ilspycmd", arguments, log, ct))
        {
            return null;
        }

        string? outlineDir = null;
        int fileCount;
        if (request.GenerateAiOutline)
        {
            outlineDir = Path.Combine(GetOutlineDirectory(request.OutputDirectory), moduleName);
            var stats = await GenerateModuleOutlineAsync(moduleOutDir, outlineDir, moduleName, log, onWritten, ct);
            fileCount = stats?.TotalFiles ?? RoslynOutliner.EnumerateSourceFiles(moduleOutDir).Count();
            if (stats == null) outlineDir = null;
        }
        else
        {
            fileCount = RoslynOutliner.EnumerateSourceFiles(moduleOutDir).Count();
        }

        RecordModule(request, new ReferenceModule
        {
            Name = moduleName,
            Engine = "ilspycmd",
            Source = dllPath,
            Path = moduleName,
            OutlinePath = outlineDir != null ? Path.GetRelativePath(request.OutputDirectory, outlineDir) : null,
            HasMethodBodies = true,
            Files = fileCount,
            DumpedAt = DateTime.Now
        }, log);

        return new DumpModuleResult(moduleName, moduleOutDir, outlineDir, fileCount);
    }

    private static async Task<OutlineStats?> GenerateModuleOutlineAsync(
        string moduleSourceDir,
        string moduleOutlineDir,
        string moduleName,
        Action<string> log,
        Action<string, string>? onWritten,
        CancellationToken ct)
    {
        log("----------------------------------------");
        log($"[AI 아웃라인 생성 시작] {moduleName} -> {moduleOutlineDir}");

        try
        {
            ClearModuleOutput(moduleOutlineDir, log);
            var watch = Stopwatch.StartNew();
            var stats = await RoslynOutliner.ProcessDirectoryAsync(moduleSourceDir, moduleOutlineDir, log, ct, onWritten);

            log($"  [AI 아웃라인 생성 완료] {stats.ProcessedFiles}개 파일 변환 완료 (실패 {stats.FailedFiles}개, {watch.ElapsedMilliseconds}ms)");
            log($"  -> 라인 수: {stats.OriginalLines:N0}줄 -> {stats.OutlineLines:N0}줄 ({stats.LineReductionPercent}% 절감)");
            log($"  -> 글자 수(토큰 근사): {stats.OriginalChars:N0} -> {stats.OutlineChars:N0} ({stats.CharReductionPercent}% 절감)");
            return stats;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"  [경고] AI 아웃라인 생성 중 오류: {ex.Message}");
            return null;
        }
    }

    private static void RecordModule(DumpRequest request, ReferenceModule module, Action<string> log)
    {
        try
        {
            ReferenceManifest.Record(request.OutputDirectory, request.Game, request.Backend, module);
        }
        catch (Exception ex)
        {
            log($"[경고] {ReferenceManifest.FileName} 기록 실패: {ex.Message}");
        }
    }

    // 이전 덤프 결과(게임 업데이트로 사라진 타입 등)가 섞이지 않도록 모듈 출력 폴더를 비운다.
    private static void ClearModuleOutput(string moduleOutputDir, Action<string> log)
    {
        if (!Directory.Exists(moduleOutputDir)) return;

        try
        {
            Directory.Delete(moduleOutputDir, recursive: true);
        }
        catch (Exception ex)
        {
            log($"[경고] 이전 출력 폴더를 지우지 못해 예전 파일이 남을 수 있습니다: {moduleOutputDir} ({ex.Message})");
        }
    }

    // 프로세스가 정상 실행되어 종료 코드 0으로 끝나면 true
    private static async Task<bool> ExecuteProcessAsync(string fileName, IEnumerable<string> arguments, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log($"[ERR] {e.Data}"); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* 이미 종료됨 */ }
            }))
            {
                await process.WaitForExitAsync(ct);
            }

            if (process.ExitCode != 0)
            {
                log($"[프로세스 종료 코드: {process.ExitCode}]");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            log("[작업이 사용자에 의해 취소되었습니다.]");
            throw;
        }
        catch (Exception ex)
        {
            log($"[프로세스 실행 실패] {ex.Message}");
            if (fileName == "ilspycmd")
            {
                log("  -> ilspycmd가 설치되어 있는지 확인하세요: dotnet tool install -g ilspycmd");
            }
            return false;
        }
    }
}
