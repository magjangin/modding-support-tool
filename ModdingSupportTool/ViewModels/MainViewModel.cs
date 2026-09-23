using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModdingSupportTool.Core;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Models;
using ModdingSupportTool.Services;

namespace ModdingSupportTool.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DumpService _dumpService;
    private readonly IFilePickerService _filePickerService;
    private CancellationTokenSource? _cts;
    private bool _isDetecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignatureDumperSelected))]
    [NotifyPropertyChangedFor(nameof(IsIlSpySelected))]
    private DumpEngineType _selectedEngine = DumpEngineType.SignatureDumper;

    [ObservableProperty]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _outputPath = @"H:\source\repos\modding support tool\Decompiled";

    [ObservableProperty]
    private string _targetAssemblies = "Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll";

    [ObservableProperty]
    private bool _ilSpyCreateProject = true;

    [ObservableProperty]
    private bool _ilSpyNestedDirectories = true;

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
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "준비 완료";

    [ObservableProperty]
    private string _logOutput = string.Empty;

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

    partial void OnSelectedEngineChanged(DumpEngineType value)
    {
        OnPropertyChanged(nameof(IsSignatureDumperSelected));
        OnPropertyChanged(nameof(IsIlSpySelected));
    }

    public MainViewModel() : this(new DumpService(), new FilePickerService(GetStorageProvider))
    {
    }

    public MainViewModel(DumpService dumpService, IFilePickerService filePickerService)
    {
        _dumpService = dumpService;
        _filePickerService = filePickerService;

        InputPath = PresetService.ResolveInitialPath();
        DetectBackend(InputPath);
    }

    partial void OnInputPathChanged(string value)
    {
        DetectBackend(value);
    }

    public void DetectBackend(string path)
    {
        if (_isDetecting) return;

        if (string.IsNullOrWhiteSpace(path))
        {
            DetectedBackend = UnityBackend.Unknown;
            DetectedBackendText = "경로 미입력";
            DetectionReasonText = string.Empty;
            return;
        }

        try
        {
            _isDetecting = true;
            var result = UnityBackendDetector.Detect(path);
            DetectedBackend = result.Backend;
            DetectionReasonText = result.Reason;

            switch (result.Backend)
            {
                case UnityBackend.Il2Cpp:
                    DetectedBackendText = "Unity IL2CPP";
                    if (IsAutoDetectEnabled)
                    {
                        SelectedEngine = DumpEngineType.SignatureDumper;
                        if (!string.IsNullOrEmpty(result.RecommendedAssemblyPath) &&
                            result.RecommendedAssemblyPath != path &&
                            Directory.Exists(result.RecommendedAssemblyPath))
                        {
                            AppendLog($"[엔진 자동 감지] IL2CPP 감지 ({result.Reason}) -> SignatureDumper (앱 내장) 자동 선택됨 & 어셈블리 경로 설정: {result.RecommendedAssemblyPath}");
                            InputPath = result.RecommendedAssemblyPath;
                        }
                        else
                        {
                            AppendLog($"[엔진 자동 감지] IL2CPP 감지 ({result.Reason}) -> SignatureDumper (앱 내장) 선택됨");
                        }
                    }
                    break;

                case UnityBackend.Mono:
                    DetectedBackendText = "Unity Mono";
                    if (IsAutoDetectEnabled)
                    {
                        SelectedEngine = DumpEngineType.IlSpyCmd;
                        if (!string.IsNullOrEmpty(result.RecommendedAssemblyPath) &&
                            result.RecommendedAssemblyPath != path &&
                            Directory.Exists(result.RecommendedAssemblyPath))
                        {
                            AppendLog($"[엔진 자동 감지] Mono 감지 ({result.Reason}) -> ilspycmd (Mono) 자동 선택됨 & 어셈블리 경로 설정: {result.RecommendedAssemblyPath}");
                            InputPath = result.RecommendedAssemblyPath;
                        }
                        else
                        {
                            AppendLog($"[엔진 자동 감지] Mono 감지 ({result.Reason}) -> ilspycmd (Mono) 선택됨");
                        }
                    }
                    break;

                default:
                    DetectedBackendText = "미감지 (비Unity / 수동 선택 필요)";
                    break;
            }
        }
        finally
        {
            _isDetecting = false;
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
    private void ApplyPreset(string presetKey)
    {
        string? path = PresetService.GetPresetPath(presetKey);
        if (!string.IsNullOrEmpty(path))
        {
            InputPath = path;
        }
    }

    [RelayCommand]
    private void TriggerAutoDetect()
    {
        DetectBackend(InputPath);
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
            var request = new DumpRequest
            {
                Engine = SelectedEngine,
                InputPath = InputPath,
                OutputDirectory = OutputPath,
                TargetAssemblyNames = TargetAssemblies,
                IlSpyCreateProject = IlSpyCreateProject,
                IlSpyNestedDirectories = IlSpyNestedDirectories
            };

            await _dumpService.RunDumpAsync(request, AppendLog, _cts.Token);
            StatusMessage = "덤프 완료";
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
        LogOutput = string.Empty;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        OpenFolderInExplorer(OutputPath);
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
            AppendLog("  - SignatureDumper.csproj (net472;net8.0 지원)");
            AppendLog("  - Program.cs");
            AppendLog("  - SignatureDumper.cs");
            AppendLog("  - README.md");

            StatusMessage = "SignatureDumper 소스 내보내기 완료";
            OpenFolderInExplorer(targetFolder);
        }
        catch (Exception ex)
        {
            AppendLog($"[내보내기 실패] {ex.Message}");
            StatusMessage = "내보내기 실패";
        }
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

    private void AppendLog(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogOutput += (string.IsNullOrEmpty(LogOutput) ? "" : "\n") + message;
        });
    }
}
