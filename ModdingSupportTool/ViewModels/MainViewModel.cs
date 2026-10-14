using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModdingSupportTool.Agent;
using ModdingSupportTool.Core;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Models;
using ModdingSupportTool.Services;

namespace ModdingSupportTool.ViewModels;

/// <summary>프리셋 버튼 하나. 이 PC에 경로가 없으면 버튼을 끈다.</summary>
public sealed class PresetItem
{
    public PresetItem(PathPreset preset, Action<PathPreset> apply)
    {
        Preset = preset;
        Exists = Directory.Exists(preset.Path);
        ApplyCommand = new RelayCommand(() => apply(preset));
    }

    public PathPreset Preset { get; }
    public string DisplayName => Preset.DisplayName;
    public string ToolTip => Exists ? Preset.Path : $"{Preset.Path} (이 PC에 없음)";
    public bool Exists { get; }
    public IRelayCommand ApplyCommand { get; }
}

public partial class MainViewModel : ViewModelBase
{
    // 로그 창이 무한히 커지면 TextBox 레이아웃이 느려지므로 마지막 부분만 유지한다.
    private const int MaxLogChars = 200_000;

    private readonly DumpService _dumpService;
    private readonly IFilePickerService _filePickerService;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly StringBuilder _logBuffer = new();
    private readonly DispatcherTimer _logTimer;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _detectCts;
    private Task _detectTask = Task.CompletedTask;
    private DumpPlan? _lastPlan;
    private bool _suppressDetection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignatureDumperSelected))]
    [NotifyPropertyChangedFor(nameof(IsIlSpySelected))]
    private DumpEngineType _selectedEngine = DumpEngineType.SignatureDumper;

    [ObservableProperty]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _outputPath = AppPaths.DefaultOutputDirectory;

    [ObservableProperty]
    private string _targetAssemblies = DumpService.DefaultTargets;

    [ObservableProperty]
    private bool _ilSpyCreateProject = true;

    [ObservableProperty]
    private bool _ilSpyNestedDirectories = true;

    [ObservableProperty]
    private bool _generateAiOutline = true;

    [ObservableProperty]
    private bool _includeInteropMembers;

    [ObservableProperty]
    private bool _hasOutlineFolder;

    [ObservableProperty]
    private UnityBackend _detectedBackend = UnityBackend.Unknown;

    [ObservableProperty]
    private string _detectedBackendText = "미감지";

    [ObservableProperty]
    private string _detectionReasonText = string.Empty;

    [ObservableProperty]
    private bool _isAutoDetectEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDumpCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDumpCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallAgentCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "준비 완료";

    [ObservableProperty]
    private string _logOutput = string.Empty;

    public IReadOnlyList<PresetItem> Presets { get; }

    public string McpCommandText => $"\"{AppPaths.ExecutablePath}\" mcp";

    public bool IsSignatureDumperSelected
    {
        get => SelectedEngine == DumpEngineType.SignatureDumper;
        set
        {
            if (value && SelectedEngine != DumpEngineType.SignatureDumper)
            {
                SelectedEngine = DumpEngineType.SignatureDumper;
            }
        }
    }

    public bool IsIlSpySelected
    {
        get => SelectedEngine == DumpEngineType.IlSpyCmd;
        set
        {
            if (value && SelectedEngine != DumpEngineType.IlSpyCmd)
            {
                SelectedEngine = DumpEngineType.IlSpyCmd;
            }
        }
    }

    public MainViewModel() : this(new DumpService(), new FilePickerService(GetStorageProvider))
    {
    }

    public MainViewModel(DumpService dumpService, IFilePickerService filePickerService)
    {
        _dumpService = dumpService;
        _filePickerService = filePickerService;

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        Presets = PresetService.Presets.Select(p => new PresetItem(p, preset => InputPath = preset.Path)).ToList();
        InputPath = PresetService.ResolveInitialPath();
        RefreshHasOutlineFolder();
    }

    partial void OnInputPathChanged(string value)
    {
        if (_suppressDetection) return;
        _detectTask = DetectBackendAsync(value, debounce: true);
    }

    partial void OnOutputPathChanged(string value) => RefreshHasOutlineFolder();

    public void RefreshHasOutlineFolder()
    {
        string outlineDir = DumpService.GetOutlineDirectory(OutputPath);
        HasOutlineFolder = !string.IsNullOrWhiteSpace(outlineDir) && Directory.Exists(outlineDir);
    }

    // 경로를 입력하는 동안 키 입력마다 폴더를 뒤지지 않도록 잠깐 기다렸다가, UI 스레드 밖에서 판별한다.
    // 새 입력이 오면 이전 판별은 취소한다.
    private async Task DetectBackendAsync(string path, bool debounce)
    {
        _detectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detectCts = cts;

        if (string.IsNullOrWhiteSpace(path))
        {
            _lastPlan = null;
            DetectedBackend = UnityBackend.Unknown;
            DetectedBackendText = "경로 미입력";
            DetectionReasonText = string.Empty;
            return;
        }

        try
        {
            if (debounce) await Task.Delay(300, cts.Token);
            DetectedBackendText = "판별 중...";
            var plan = await Task.Run(() => DumpService.Plan(path, null, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) ApplyDetection(plan);
        }
        catch (OperationCanceledException)
        {
            // 더 새로운 입력이 들어와서 이 판별은 버린다.
        }
    }

    private void ApplyDetection(DumpPlan plan)
    {
        _lastPlan = plan;
        var result = plan.Detection;
        DetectedBackend = result.Backend;
        DetectionReasonText = result.Reason;
        DetectedBackendText = result.Backend switch
        {
            UnityBackend.Il2Cpp => "Unity IL2CPP",
            UnityBackend.Mono => "Unity Mono",
            _ => "미감지 (비Unity / 수동 선택 필요)"
        };

        if (result.Backend == UnityBackend.Unknown || !IsAutoDetectEnabled) return;

        SelectedEngine = plan.Engine;
        string label = result.Backend == UnityBackend.Il2Cpp ? "IL2CPP" : "Mono";
        if (plan.InputPathAdjusted)
        {
            AppendLog($"[엔진 자동 감지] {label} 감지 ({result.Reason}) -> {DumpService.EngineDisplayName(plan.Engine)} 자동 선택됨 & 어셈블리 경로 설정: {plan.InputPath}");
            _suppressDetection = true;
            try { InputPath = plan.InputPath; }
            finally { _suppressDetection = false; }
        }
        else
        {
            AppendLog($"[엔진 자동 감지] {label} 감지 ({result.Reason}) -> {DumpService.EngineDisplayName(plan.Engine)} 선택됨");
        }
    }

    private static IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }
        return null;
    }

    [RelayCommand]
    private async Task SelectInputFolderAsync()
    {
        var folder = await _filePickerService.PickFolderAsync(InputPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            InputPath = folder;
        }
    }

    [RelayCommand]
    private async Task SelectInputFileAsync()
    {
        var file = await _filePickerService.PickFileAsync("대상 어셈블리 DLL 선택", new[] { "*.dll" });
        if (!string.IsNullOrWhiteSpace(file))
        {
            InputPath = file;
        }
    }

    [RelayCommand]
    private async Task SelectOutputFolderAsync()
    {
        var folder = await _filePickerService.PickFolderAsync(OutputPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputPath = folder;
        }
    }

    [RelayCommand]
    private void TriggerAutoDetect()
    {
        _detectTask = DetectBackendAsync(InputPath, debounce: false);
    }

    private bool CanStartDump() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartDump))]
    private async Task StartDumpAsync()
    {
        IsBusy = true;
        StatusMessage = "덤프 작업 진행 중...";
        _cts = new CancellationTokenSource();

        AppendLog($"[{DateTime.Now:HH:mm:ss}] 덤프 작업을 시작합니다...");

        try
        {
            // 경로를 막 바꿨다면 판별이 끝나야 엔진·입력 폴더가 맞는다.
            await _detectTask;

            var request = new DumpRequest
            {
                Engine = SelectedEngine,
                InputPath = InputPath,
                OutputDirectory = OutputPath,
                TargetAssemblyNames = TargetAssemblies,
                IlSpyCreateProject = IlSpyCreateProject,
                IlSpyNestedDirectories = IlSpyNestedDirectories,
                GenerateAiOutline = GenerateAiOutline,
                IncludeInteropMembers = IncludeInteropMembers,
                Backend = _lastPlan?.Detection.Backend ?? UnityBackend.Unknown,
                Game = _lastPlan?.Game
            };

            var result = await _dumpService.RunDumpAsync(request, AppendLog, _cts.Token);
            StatusMessage = result.IsSuccess ? "덤프 완료" : "덤프 실패 (로그 확인)";
            RefreshHasOutlineFolder();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "작업 취소됨";
            AppendLog("[알림] 작업이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            StatusMessage = "덤프 실패";
            AppendLog($"[오류 발생] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancelDump() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancelDump))]
    private void CancelDump()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void ClearLog()
    {
        while (_pendingLog.TryDequeue(out _)) { }
        _logBuffer.Clear();
        LogOutput = string.Empty;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        OpenFolderInExplorer(OutputPath);
    }

    [RelayCommand]
    private void OpenOutlineFolder()
    {
        OpenFolderInExplorer(DumpService.GetOutlineDirectory(OutputPath));
    }

    [RelayCommand]
    private async Task ExportSignatureDumperAsync()
    {
        AppendLog("[내보내기] SignatureDumper 소스 프로젝트를 내보낼 대상 폴더를 선택하세요...");

        var targetFolder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            AppendLog("[내보내기 취소] 폴더가 선택되지 않았습니다.");
            return;
        }

        try
        {
            await SignatureDumperExporter.ExportToDirectoryAsync(targetFolder);
            AppendLog($"[내보내기 성공] SignatureDumper 프로젝트가 다음 경로에 생성되었습니다:\n  {targetFolder}");
            foreach (var file in SignatureDumperExporter.ExportedFiles) AppendLog($"  - {file}");

            StatusMessage = "SignatureDumper 소스 내보내기 완료";
            OpenFolderInExplorer(targetFolder);
        }
        catch (Exception ex)
        {
            AppendLog($"[내보내기 실패] {ex.Message}");
            StatusMessage = "내보내기 실패";
        }
    }

    // ───────────────────────────── AI 에이전트 연동 ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartDump))]
    private async Task InstallAgentAsync()
    {
        IsBusy = true;
        StatusMessage = "AI 에이전트 연동 설치 중...";
        try
        {
            // 이 PC에서 감지된 에이전트(Claude Code, Antigravity, Gemini CLI)에 스킬 설치 + MCP 등록
            bool ok = await Task.Run(() => AgentInstaller.InstallAsync(new AgentInstallOptions { UserScope = true, RegisterMcp = true }, AppendLog));
            StatusMessage = ok ? "AI 에이전트 연동 완료 (에이전트 재시작 필요)" : "AI 에이전트 연동 일부 실패 (로그 확인)";
        }
        catch (Exception ex)
        {
            AppendLog($"[에이전트 설치 오류] {ex.Message}");
            StatusMessage = "AI 에이전트 연동 실패";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowAgentStatus()
    {
        AgentInstaller.PrintStatus(new AgentInstallOptions(), AppendLog);
        AppendLog($"MCP 서버 명령: {McpCommandText}");
    }

    private void OpenFolderInExplorer(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[폴더 열기 실패] {ex.Message}");
            }
        }
        else
        {
            AppendLog($"[경고] 대상 폴더가 존재하지 않습니다: {path}");
        }
    }

    // 어느 스레드에서 불러도 된다. 실제 화면 반영은 FlushLog가 100ms마다 모아서 한 번에 한다.
    private void AppendLog(string message) => _pendingLog.Enqueue(message);

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;

        while (_pendingLog.TryDequeue(out var line))
        {
            if (_logBuffer.Length > 0) _logBuffer.Append('\n');
            _logBuffer.Append(line);
        }

        if (_logBuffer.Length > MaxLogChars)
        {
            int cut = _logBuffer.Length - MaxLogChars;
            int newline = _logBuffer.ToString(cut, Math.Min(4096, _logBuffer.Length - cut)).IndexOf('\n');
            _logBuffer.Remove(0, newline >= 0 ? cut + newline + 1 : cut);
        }

        LogOutput = _logBuffer.ToString();
    }
}
