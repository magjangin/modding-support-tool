using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;
using ModdingSupportTool.Models;

namespace ModdingSupportTool.Services;

public class DumpRequest
{
    public DumpEngineType Engine { get; set; } = DumpEngineType.SignatureDumper;
    public string InputPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string TargetAssemblyNames { get; set; } = "Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll";
    public bool IlSpyCreateProject { get; set; } = true;
    public bool IlSpyNestedDirectories { get; set; } = true;
    public bool GenerateAiOutline { get; set; } = true;
}

public sealed record DumpResult(int SucceededCount, int FailedCount)
{
    // 하나 이상 덤프했고 실패가 없어야 성공이다. (대상 파일이 하나도 없으면 실패)
    public bool IsSuccess => SucceededCount > 0 && FailedCount == 0;
}

public class DumpService
{
    public async Task<DumpResult> RunDumpAsync(DumpRequest request, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            log("[오류] 입력 경로가 지정되지 않았습니다.");
            return new DumpResult(0, 0);
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            log("[오류] 출력 폴더가 지정되지 않았습니다.");
            return new DumpResult(0, 0);
        }

        bool isFile = File.Exists(request.InputPath);
        bool isDir = Directory.Exists(request.InputPath);

        if (!isFile && !isDir)
        {
            log($"[오류] 지정된 입력 경로가 존재하지 않습니다: {request.InputPath}");
            return new DumpResult(0, 0);
        }

        Directory.CreateDirectory(request.OutputDirectory);

        log($"=== 덤프 작업 시작 ===");
        log($"엔진: {(request.Engine == DumpEngineType.SignatureDumper ? "SignatureDumper (앱 내장 엔진)" : "ilspycmd (Mono)")}");
        log($"입력: {request.InputPath}");
        log($"출력: {request.OutputDirectory}");
        log($"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log("----------------------------------------");

        DumpResult result = request.Engine == DumpEngineType.SignatureDumper
            ? await RunSignatureDumperAsync(request, isFile, log, ct)
            : await RunIlSpyCmdAsync(request, isFile, log, ct);

        log("----------------------------------------");
        log($"=== 덤프 작업 완료 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        log($"결과: 성공 {result.SucceededCount}개, 실패 {result.FailedCount}개");
        if (result.SucceededCount == 0 && result.FailedCount == 0)
        {
            log("[경고] 덤프된 어셈블리가 없습니다. 입력 폴더에 대상 어셈블리가 있는지 확인하세요.");
        }

        return result;
    }

    private async Task<DumpResult> RunSignatureDumperAsync(DumpRequest request, bool isFile, Action<string> log, CancellationToken ct)
    {
        int succeeded = 0;
        int failed = 0;
        string inputDir = isFile ? Path.GetDirectoryName(request.InputPath)! : request.InputPath;
        string[] targets;

        if (isFile)
        {
            targets = new[] { Path.GetFileName(request.InputPath) };
        }
        else
        {
            targets = request.TargetAssemblyNames
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToArray();
        }

        foreach (string targetName in targets)
        {
            ct.ThrowIfCancellationRequested();

            string dllPath = Path.Combine(inputDir, targetName);
            if (!File.Exists(dllPath))
            {
                log($"[건너뜀] 대상 어셈블리 파일이 존재하지 않습니다: {dllPath}");
                continue;
            }

            string moduleOutputDir = Path.Combine(request.OutputDirectory, Path.GetFileNameWithoutExtension(targetName));
            ClearModuleOutput(moduleOutputDir, log);

            log($"[내장 SignatureDumper 실행] {targetName} -> {moduleOutputDir}");

            var options = new SignatureDumperOptions
            {
                TargetAssemblyPath = dllPath,
                SearchDirectory = inputDir,
                OutputDirectory = moduleOutputDir
            };

            try
            {
                await Task.Run(() =>
                {
                    var dumper = new BuiltinSignatureDumper(options, log);
                    dumper.Dump(ct);
                }, ct);

                log($"  [완료] {targetName}");
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"  [실패] {targetName}: {ex.Message}");
                failed++;
            }
        }

        return new DumpResult(succeeded, failed);
    }

    private async Task<DumpResult> RunIlSpyCmdAsync(DumpRequest request, bool isFile, Action<string> log, CancellationToken ct)
    {
        int succeeded = 0;
        int failed = 0;
        string[] targetFiles;

        if (isFile)
        {
            targetFiles = new[] { request.InputPath };
        }
        else
        {
            var targets = request.TargetAssemblyNames
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToArray();

            targetFiles = targets
                .Select(t => Path.Combine(request.InputPath, t))
                .Where(File.Exists)
                .ToArray();

            if (targetFiles.Length == 0)
            {
                log($"[경고] 지정된 폴더에서 일치하는 타겟 어셈블리를 찾을 수 없습니다: {request.TargetAssemblyNames}");
                return new DumpResult(0, 0);
            }
        }

        foreach (var dllPath in targetFiles)
        {
            ct.ThrowIfCancellationRequested();

            string moduleName = Path.GetFileNameWithoutExtension(dllPath);
            string moduleOutDir = Path.Combine(request.OutputDirectory, moduleName);
            // ilspycmd는 기존 파일을 지우지 않고, 생성된 .csproj는 폴더 안의 .cs를 전부 포함하므로 먼저 비운다.
            ClearModuleOutput(moduleOutDir, log);
            Directory.CreateDirectory(moduleOutDir);

            string flags = "";
            if (request.IlSpyCreateProject) flags += " -p";
            if (request.IlSpyNestedDirectories) flags += " --nested-directories";

            string arguments = $"{flags} -o \"{moduleOutDir}\" \"{dllPath}\"";
            log($"[ilspycmd 실행] {dllPath} -> {moduleOutDir}");

            if (await ExecuteProcessAsync("ilspycmd", arguments, log, ct))
            {
                succeeded++;

                if (request.GenerateAiOutline)
                {
                    await GenerateModuleOutlineAsync(moduleOutDir, request.OutputDirectory, moduleName, log, ct);
                }
            }
            else
            {
                failed++;
            }
        }

        return new DumpResult(succeeded, failed);
    }

    public static string GetOutlineDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) return string.Empty;
        return outputDirectory.TrimEnd('\\', '/') + "_Outline";
    }

    private async Task GenerateModuleOutlineAsync(
        string moduleSourceDir,
        string baseOutputDir,
        string moduleName,
        Action<string> log,
        CancellationToken ct)
    {
        string outlineRootDir = GetOutlineDirectory(baseOutputDir);
        string moduleOutlineDir = Path.Combine(outlineRootDir, moduleName);

        log($"----------------------------------------");
        log($"[AI 아웃라인 생성 시작] {moduleName} -> {moduleOutlineDir}");

        try
        {
            ClearModuleOutput(moduleOutlineDir, log);
            var stats = await RoslynOutliner.ProcessDirectoryAsync(moduleSourceDir, moduleOutlineDir, log, ct);

            log($"  [AI 아웃라인 생성 완료] {stats.ProcessedFiles}개 파일 변환 완료 (실패 {stats.FailedFiles}개)");
            log($"  -> 라인 수 절감: {stats.OriginalLines:N0}줄 -> {stats.OutlineLines:N0}줄 ({stats.LineReductionPercent}% 토큰 절감 효과)");
            log($"  -> 아웃라인 폴더: {moduleOutlineDir}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"  [경고] AI 아웃라인 생성 중 오류: {ex.Message}");
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
    private async Task<bool> ExecuteProcessAsync(string fileName, string arguments, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) log(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) log($"[ERR] {e.Data}");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
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
            return false;
        }
    }
}
