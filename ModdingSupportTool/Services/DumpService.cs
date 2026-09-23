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
}

public class DumpService
{
    public async Task RunDumpAsync(DumpRequest request, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            log("[오류] 입력 경로가 지정되지 않았습니다.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            log("[오류] 출력 폴더가 지정되지 않았습니다.");
            return;
        }

        bool isFile = File.Exists(request.InputPath);
        bool isDir = Directory.Exists(request.InputPath);

        if (!isFile && !isDir)
        {
            log($"[오류] 지정된 입력 경로가 존재하지 않습니다: {request.InputPath}");
            return;
        }

        Directory.CreateDirectory(request.OutputDirectory);

        log($"=== 덤프 작업 시작 ===");
        log($"엔진: {(request.Engine == DumpEngineType.SignatureDumper ? "SignatureDumper (앱 내장 엔진)" : "ilspycmd (Mono)")}");
        log($"입력: {request.InputPath}");
        log($"출력: {request.OutputDirectory}");
        log($"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log("----------------------------------------");

        if (request.Engine == DumpEngineType.SignatureDumper)
        {
            await RunSignatureDumperAsync(request, isFile, log, ct);
        }
        else
        {
            await RunIlSpyCmdAsync(request, isFile, log, ct);
        }

        log("----------------------------------------");
        log($"=== 덤프 작업 완료 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
    }

    private async Task RunSignatureDumperAsync(DumpRequest request, bool isFile, Action<string> log, CancellationToken ct)
    {
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
            if (Directory.Exists(moduleOutputDir))
            {
                try { Directory.Delete(moduleOutputDir, recursive: true); } catch { }
            }

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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"  [실패] {targetName}: {ex.Message}");
            }
        }
    }

    private async Task RunIlSpyCmdAsync(DumpRequest request, bool isFile, Action<string> log, CancellationToken ct)
    {
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
                return;
            }
        }

        foreach (var dllPath in targetFiles)
        {
            ct.ThrowIfCancellationRequested();

            string moduleName = Path.GetFileNameWithoutExtension(dllPath);
            string moduleOutDir = Path.Combine(request.OutputDirectory, moduleName);
            Directory.CreateDirectory(moduleOutDir);

            string flags = "";
            if (request.IlSpyCreateProject) flags += " -p";
            if (request.IlSpyNestedDirectories) flags += " --nested-directories";

            string arguments = $"{flags} -o \"{moduleOutDir}\" \"{dllPath}\"";
            log($"[ilspycmd 실행] {dllPath} -> {moduleOutDir}");

            await ExecuteProcessAsync("ilspycmd", arguments, log, ct);
        }
    }

    private async Task ExecuteProcessAsync(string fileName, string arguments, Action<string> log, CancellationToken ct)
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
            }
        }
        catch (OperationCanceledException)
        {
            log("[작업이 사용자에 의해 취소되었습니다.]");
            throw;
        }
        catch (Exception ex)
        {
            log($"[프로세스 실행 실패] {ex.Message}");
        }
    }
}
