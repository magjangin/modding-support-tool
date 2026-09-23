# 폴더별 전체 파일 상세 구조도 (Directory & File Architecture)

이 문서는 `Modding Support Tool` 프로젝트의 모든 디렉터리와 파일을 폴더 단위로 세밀하게 분류하여 각 파일의 역할, 의존성 관계, 내부 동작 메커니즘을 상세히 기술합니다.

---

## 📁 1. 루트 디렉터리 (`/`)

프로젝트의 최상위 폴더로, 솔루션 정의 파일과 빠른 실행/빌드 스크립트, 그리고 문서가 위치합니다.

| 파일/폴더명 | 유형 | 역할 및 세부 설명 |
| :--- | :---: | :--- |
| [`ModdingSupportTool.slnx`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool.slnx) | 파일 | Visual Studio 2022 v17.10+ 최신 XML 기반 가벼운 솔루션 선언 파일. |
| [`run_dump.bat`](file:///h:/source/repos/modding%20support%20tool/run_dump.bat) | 스크립트 | **원클릭 및 드래그 앤 드롭 덤프 스크립트**.<br>• 폴더/DLL 드래그 시 즉시 해당 경로 덤프 수행.<br>• 더블 클릭 시 `PresetService` 기본 경로로 자동 실행.<br>• 실행 파일 부재 시 `publish.bat` 자동 연계 빌드. |
| [`publish.bat`](file:///h:/source/repos/modding%20support%20tool/publish.bat) | 스크립트 | **배포 빌드 스크립트**.<br>`dotnet publish`를 실행하여 런타임 종속성 없이 단독 실행 가능한 `Publish/ModdingSupportTool.exe` (.NET 10 self-contained win-x64)를 빌드합니다. |
| [`.gitignore`](file:///h:/source/repos/modding%20support%20tool/.gitignore) | 설정 | `bin/`, `obj/`, 빌드 아티팩트 및 임시 캐시 파일을 Git 추적에서 제외합니다. |
| [`README.md`](file:///h:/source/repos/modding%20support%20tool/README.md) | 문서 | 프로젝트 소개, 빠른 시작, 기능 요약 및 문서 인덱스. |
| [`docs/`](file:///h:/source/repos/modding%20support%20tool/docs) | 폴더 | 세부 기술 문서 및 아키텍처 가이드가 모여 있는 디렉터리. |
| [`ModdingSupportTool/`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool) | 폴더 | C# 메인 애플리케이션 프로젝트 소스 코드. |
| [`Publish/`](file:///h:/source/repos/modding%20support%20tool/Publish) | 폴더 | `publish.bat`에 의해 컴파일된 배포용 바이너리가 저장되는 폴더. |
| [`Decompiled/`](file:///h:/source/repos/modding%20support%20tool/Decompiled) | 폴더 | 덤프된 C# 스켈레톤 소스(`.cs`) 및 디컴파일 결과물이 저장되는 기본 대상 폴더. |

---

## 📁 2. 문서 디렉터리 (`docs/`)

개발자 및 사용자를 위한 기술 설명서가 체계적으로 정리된 폴더입니다.

| 파일명 | 내용 |
| :--- | :--- |
| [`docs/file-structure.md`](file:///h:/source/repos/modding%20support%20tool/docs/file-structure.md) | **(현재 문서)** 프로젝트 내 모든 디렉터리와 파일들의 역할 및 상호작용 상세 안내. |
| [`docs/architecture.md`](file:///h:/source/repos/modding%20support%20tool/docs/architecture.md) | Mermaid 다이어그램 기반의 시스템 구조도, 백엔드 판별 트리, ALC 로딩 및 리플렉션 덤프 메커니즘. |
| [`docs/usage.md`](file:///h:/source/repos/modding%20support%20tool/docs/usage.md) | Avalonia GUI 조작법, CLI 명령줄 옵션, 드래그앤드롭 사용법 및 Standalone SignatureDumper 방출 안내. |

---

## 📁 3. 메인 소스 코드 디렉터리 (`ModdingSupportTool/`)

애플리케이션의 핵심 로직이 구현된 C# .NET 10 프로젝트입니다.

### 📄 프로젝트 루트 파일들
- [`ModdingSupportTool.csproj`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/ModdingSupportTool.csproj):
  - TargetFramework: `net10.0`, OutputType: `WinExe`
  - 의존성: `Avalonia (12.1.2)`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `CommunityToolkit.Mvvm (8.4.2)`
- [`Program.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Program.cs):
  - **GUI / CLI 하이브리드 진입점**.
  - 인자가 전달되면 Win32 `AttachConsole(-1)` 또는 `AllocConsole()`로 현재 콘솔에 터미널을 붙여 [`CliRunner`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Cli/CliRunner.cs)를 실행.
  - 인자가 없으면 `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`를 호출하여 Avalonia GUI 창 실행.
- [`App.axaml`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/App.axaml) / [`App.axaml.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/App.axaml.cs):
  - Avalonia 앱 수명 주기 및 전역 리소스(FluentTheme 다크 모드) 초기화.
  - `MainWindow`를 생성하고 `MainViewModel`을 DataContext로 바인딩.
- [`ViewLocator.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/ViewLocator.cs):
  - MVVM 패턴에 따라 ViewModel 타입명을 View 타입명으로 자동 매핑(DataTemplate).
- [`app.manifest`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/app.manifest):
  - Windows DPI 인식(Per-Monitor V2) 및 Windows 10/11 OS 호환성 매니페스트.

---

### 📂 `ModdingSupportTool/Core/` (핵심 분석 및 덤프 엔진)

게임 엔진 감지 및 Il2Cpp 리플렉션 분석의 심장부입니다.

#### `Core/Detection/` (Unity 백엔드 판별)
- [`UnityBackend.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/Detection/UnityBackend.cs):
  - 백엔드 열거형 (`Unknown = 0`, `Mono = 1`, `Il2Cpp = 2`).
- [`UnityBackendDetector.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/Detection/UnityBackendDetector.cs):
  - **파일 시스템 기반 Unity 백엔드 자동 감지기**.
  - 파일 검사: `global-metadata.dat`, `GameAssembly.dll`, `UnityPlayer.dll`, `Assembly-CSharp.dll`, `MonoBleedingEdge` 등.
  - 폴더 깊이 8단계 스택 기반 재귀 스캔.
  - `MelonLoader/Il2CppAssemblies` 폴더 자동 탐지 및 권장 어셈블리 경로 보정 반환.

#### `Core/` 루트 파일
- [`BuiltinSignatureDumper.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/BuiltinSignatureDumper.cs):
  - **내장 System.Reflection 오프라인 시그니처 덤퍼**.
  - `AssemblyLoadContext(isCollectible: true)`를 사용하여 대상 DLL을 파일 잠금 없이 동적 로드.
  - `alc.Resolving` 핸들러로 MelonLoader 런타임 의존성(`net6`, `net472`, `net35`, `Dependencies/SupportModules`)을 자동 해석.
  - 클래스, 구조체, 인터페이스, 열거형, 델리게이트 식별.
  - 필드, 프로퍼티(get/set 접근자 분리), 메서드 시그니처(`ref`, `out`, `params`, 기본값), 제네릭 완벽 복원.
  - 네임스페이스별 `Decompiled/<ModuleName>/<Namespace>/<TypeName>.cs` 스켈레톤 소스 생성.
- [`SignatureDumperExporter.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/SignatureDumperExporter.cs):
  - 내장 덤퍼 로직을 외부 폴더에 독립 실행형 프로젝트(`net472;net8.0` 멀티타깃 `.csproj` + `Program.cs` + `SignatureDumper.cs`)로 추출/방출하는 툴 생성기.

---

### 📂 `ModdingSupportTool/Cli/` (콘솔 명령줄 모드)

- [`CliRunner.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Cli/CliRunner.cs):
  - 명령줄 인수 파싱 (`-i`, `-o`, `-t` 등).
  - 콘솔 UTF-8 인코딩 세팅 및 실시간 진행 상태 출력.
  - `Ctrl + C` 인터럽트 안전 취소 핸들러 연동.
  - 백엔드 감지 -> 엔진 선택 -> [`DumpService`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Services/DumpService.cs) 실행 파이프라인 총괄.

---

### 📂 `ModdingSupportTool/Services/` (비즈니스 서비스)

- [`DumpService.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Services/DumpService.cs):
  - `DumpRequest` 모델을 받아 실제 덤프 작업을 총괄.
  - `SignatureDumper`: `BuiltinSignatureDumper`를 백그라운드 태스크로 구동.
  - `IlSpyCmd`: 시스템에 설치된 `ilspycmd` 외부 프로세스를 실행하고 표준 출력/에러 스트림을 비동기 가로채기. 취소 시 프로세스 트리 전체 강제 종료.
- [`PresetService.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Services/PresetService.cs):
  - 주로 분석하는 게임들의 경로 프리셋 관리 (*Muse Dash*, *A Short Hike*, *Sixtar Gate*, *MiSide*).
  - 시스템에 존재하는 기본 경로를 우선 탐색하는 `ResolveInitialPath()` 제공.
- [`IFilePickerService.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Services/IFilePickerService.cs) / [`FilePickerService.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Services/FilePickerService.cs):
  - Avalonia 12의 `IStorageProvider`를 추상화하여 폴더 및 DLL 파일 선택 대화상자를 MVVM 패턴에 맞게 비동기 호출.

---

### 📂 `ModdingSupportTool/Models/` (데이터 모델)

- [`DumpEngineType.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Models/DumpEngineType.cs):
  - 덤프 엔진 구분 열거형 (`SignatureDumper`, `IlSpyCmd`).

---

### 📂 `ModdingSupportTool/ViewModels/` (MVVM 뷰모델)

- [`ViewModelBase.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/ViewModels/ViewModelBase.cs):
  - 모든 뷰모델의 기본 클래스 (`CommunityToolkit.Mvvm.ComponentModel.ObservableObject` 상속).
- [`MainViewModel.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/ViewModels/MainViewModel.cs):
  - GUI 전체 상태를 관장하는 메인 뷰모델.
  - 입력 경로 변경 시 디바운스 백엔드 감지(`UnityBackendDetector.Detect`).
  - Il2Cpp <-> Mono에 따른 라디오 버튼 및 옵션 UI 자동 전환.
  - 덤프 시작(`StartDumpCommand`), 취소(`CancelDumpCommand`), 로그 지우기, 탐색기 열기, 프리셋 적용 커맨드 제공.

---

### 📂 `ModdingSupportTool/Views/` (UI 화면 뷰)

- [`MainWindow.axaml`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Views/MainWindow.axaml):
  - Fluent 다크 테마 기반의 XAML 레이아웃.
  - 5개 영역 분할: (1) 헤더 및 엔진 선택 (2) 입력 경로 및 백엔드 감지 상태 (3) 출력 설정 (4) 실행/취소 제어바 (5) 실시간 가상 터미널 로그 뷰어.
- [`MainWindow.axaml.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Views/MainWindow.axaml.cs):
  - 코드 비하인드 (순수 컴포넌트 초기화 담당).

---

### 📂 `ModdingSupportTool/Assets/` (리소스)

- [`app-icon.ico`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Assets/app-icon.ico) / [`app-icon.png`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Assets/app-icon.png):
  - 네온 사이버네틱 렌치와 게임패드, C# 코드 브래킷 모티브의 모딩 툴 공식 전용 아이콘 (창 아이콘, 실행 바이너리 아이콘, README 로고).
- [`avalonia-logo.ico`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Assets/avalonia-logo.ico):
  - 하위 호환성을 위해 새 아이콘과 동일하게 치환 유지된 아이콘.

---

## 📁 4. 배포 및 출력 디렉터리 (`Publish/`, `Decompiled/`)

- [`Publish/`](file:///h:/source/repos/modding%20support%20tool/Publish):
  - `publish.bat` 실행 시 생성되는 자체 완결형 단일 실행 바이너리(`ModdingSupportTool.exe`)가 위치합니다.
- [`Decompiled/`](file:///h:/source/repos/modding%20support%20tool/Decompiled):
  - 덤프 실행 시 결과물이 저장되는 기본 폴더입니다.
  - 예시: `Decompiled/Assembly-CSharp/AchieveModel.cs`, `DBMMusic.cs`, `BeatWave.cs` 등 네임스페이스 및 클래스별 C# 스켈레톤 파일들이 구조화되어 보관됩니다.
