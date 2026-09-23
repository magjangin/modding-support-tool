# Modding Support Tool (Assembly C# Dumper)

<p align="center">
  <img src="ModdingSupportTool/Assets/avalonia-logo.ico" alt="Logo" width="96" height="96" />
</p>

<p align="center">
  <strong>Unity 게임(Mono 및 Il2Cpp)의 어셈블리를 자동 판별하여 모딩 분석용 C# 소스 코드 및 시그니처 스켈레톤을 추출하는 일체형 모딩 도구</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/UI-Avalonia%2012-8A2BE2?logo=avalonia&logoColor=white" alt="Avalonia UI" /></a>
  <a href="https://github.com/magjangin/modding-support-tool/stargazers"><img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows&logoColor=white" alt="Windows" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License: MIT" /></a>
</p>

---

## 🌟 개요 (Overview)

**Modding Support Tool**은 Unity 엔진 기반 게임의 모딩과 리버스 엔지니어링 분석을 가속화하기 위해 제작된 데스크톱 GUI 및 CLI 툴입니다.

분석하고자 하는 게임의 설치 경로 또는 어셈블리(`.dll`)를 지정하면, 도구가 **Mono인지 Il2Cpp인지 백엔드를 100% 자동 판별**하여 최적화된 엔진으로 C# 소스 코드 또는 시그니처 스켈레톤을 한 번에 뽑아냅니다.

### 🔄 지원 백엔드별 처리 전략

| 구분 | Mono 백엔드 게임 | Il2Cpp 백엔드 게임 |
| :--- | :--- | :--- |
| **판별 지표** | `Managed/Assembly-CSharp.dll`, `MonoBleedingEdge` | `global-metadata.dat`, `GameAssembly.dll`, `Il2CppAssemblies` |
| **사용 엔진** | **`ilspycmd` (외부 CLI 연동)** | **`BuiltinSignatureDumper` (앱 내장 Reflection 코어)** |
| **분석 방식** | 바이트코드 IL 전체 디컴파일 | MelonLoader 더미 어셈블리 오프라인 정적 분석 |
| **추출 결과** | 완벽한 C# 소스 로직 + `.csproj` 프로젝트 파일 | 네임스페이스별 클래스/프로퍼티/메서드 C# 스켈레톤(`.cs`) |
| **추천 용도** | 소스 로직 전체 파악, Mono 패치 모드 개발 | MelonLoader 훅 시그니처 추출, 타입 메타데이터 분석 |

---

## ✨ 핵심 기능 (Key Features)

- 🔍 **스마트 백엔드 자동 감지기 (`UnityBackendDetector`)**:
  - 게임 루트, `*_Data` 폴더 구조를 최대 8단계까지 스택 탐색하여 엔진 백엔드를 스스로 진단합니다.
  - `MelonLoader/Il2CppAssemblies` 폴더가 존재하면 자동으로 분석 타겟 경로를 보정합니다.
- ⚡ **외부 의존성 제로 내장 시그니처 덤퍼 (`BuiltinSignatureDumper`)**:
  - `AssemblyLoadContext(isCollectible: true)`를 활용하여 분석 중인 DLL 파일 잠금(Lock) 없이 안전하게 메모리에 로드 후 언로드합니다.
  - 접근 제어자(`public`/`private`/`protected`), 클래스/구조체/인터페이스/열거형, 상속, 프로퍼티(Getter/Setter), 파라미터(`ref`/`out`/`params`/기본값) 완벽 복원.
  - 단일 파일 덤프가 아닌 네임스페이스 경로별 분할 C# 소스 파일(`Decompiled/<ModuleName>/<Namespace>/<Type>.cs`) 생성.
- 🎨 **모던 Avalonia 다크 테마 GUI**:
  - Visual Studio 감성의 직관적인 Dark Fluent 인터페이스.
  - 백엔드 감지 상태 뱃지, 실시간 가상 콘솔 터미널 로그 뷰어, 작업 취소(`CancellationToken`) 지원.
- 🚀 **드래그 앤 드롭 원클릭 자동 덤프 (`run_dump.bat`)**:
  - GUI를 열 필요 없이 탐색기에서 게임 폴더나 DLL을 배치 파일 위로 끌어다 놓기만 하면 즉시 덤프 진행.
- 📦 **독립 실행형 SignatureDumper 프로젝트 내보내기**:
  - 앱 내장 시그니처 덤퍼를 `net472;net8.0` 멀티타깃의 독립 콘솔 프로젝트 소스로 어디서든 내보내기(Export) 가능.
- 🎮 **모딩 타겟 원클릭 프리셋 내장**:
  - *Muse Dash (Il2Cpp)*, *Sixtar Gate: STARTRAIL (Il2Cpp)*, *A Short Hike (Mono)*, *MiSide (Il2Cpp)* 등 주요 모딩 대상 게임 경로 기본 탑재.

---

## 🚀 빠른 시작 (Quick Start)

### 방법 1: 배치 파일로 드래그 앤 드롭 (가장 권장)
1. 탐색기에서 분석하려는 게임 설치 폴더 또는 `.dll` 파일을 선택합니다.
2. 루트에 위치한 [`run_dump.bat`](run_dump.bat) 위로 마우스로 **드래그 앤 드롭**합니다.
3. 자동으로 백엔드를 감지하고 `Decompiled/` 폴더에 C# 소스/스켈레톤 파일들이 구조화되어 추출됩니다.

### 방법 2: Avalonia GUI 앱 실행
```powershell
# 개발 환경에서 프로젝트 직접 실행
dotnet run --project ModdingSupportTool
```
1. `[폴더 선택]` 또는 빠른 프리셋 버튼을 눌러 게임 경로를 지정합니다.
2. 엔진 판별 뱃지에 `Il2Cpp` 또는 `Mono`가 표시되는 것을 확인합니다.
3. **`[덤프 시작]`** 버튼을 누르면 하단 터미널에 진행 로그가 출력되며 완료됩니다.

### 방법 3: CLI (명령줄) 모드
자동화 스크립트나 터미널에서 헤드리스 모드로 실행할 수 있습니다:
```powershell
# 기본 프리셋 경로로 덤프
.\Publish\ModdingSupportTool.exe --cli

# 대상 폴더 및 출력 폴더 지정
.\Publish\ModdingSupportTool.exe -i "D:\Games\MyUnityGame" -o ".\Decompiled" -t "Assembly-CSharp.dll"
```

---

## 📂 저장소 디렉터리 구조

```
modding-support-tool/
├── docs/                           # 📚 세부 기술 문서
│   ├── file-structure.md           # 폴더별/파일별 상세 역할 안내
│   ├── architecture.md             # 시스템 아키텍처 및 파이프라인 (Mermaid 다이어그램)
│   └── usage.md                    # GUI, CLI, 드래그앤드롭 사용법 가이드
│
├── ModdingSupportTool/             # 💻 메인 C# 애플리케이션 (.NET 10)
│   ├── Core/                       # 백엔드 감지 및 내장 덤퍼 코어
│   │   ├── Detection/              # UnityBackendDetector.cs (휴리스틱 분석)
│   │   ├── BuiltinSignatureDumper.cs # System.Reflection 기반 스켈레톤 추출기
│   │   └── SignatureDumperExporter.cs# 독립 프로젝트 생성기
│   ├── Cli/                        # CliRunner.cs (터미널 오케스트레이터)
│   ├── Services/                   # DumpService, PresetService, FilePickerService
│   ├── ViewModels/                 # MainViewModel, ViewModelBase (MVVM)
│   ├── Views/                      # MainWindow.axaml, MainWindow.axaml.cs
│   ├── Assets/                     # 애플리케이션 아이콘 및 리소스
│   └── Program.cs                  # GUI/CLI 하이브리드 진입점
│
├── run_dump.bat                    # ⚡ 원클릭 & 드래그 앤 드롭 실행 스크립트
├── publish.bat                     # 🔨 단일 실행 파일(.exe) 배포 빌드 스크립트
├── ModdingSupportTool.slnx         # Visual Studio 2022+ 솔루션 파일
└── README.md
```

---

## 📚 상세 기술 문서 바로가기

더 깊이 있는 아키텍처와 상세 소스 코드 분석은 `docs/` 디렉터리의 문서를 참조하세요:

- 📖 [**폴더별 전체 파일 상세 구조도 (`docs/file-structure.md`)**](docs/file-structure.md)
  - 모든 폴더와 개별 C# 파일들의 설계 의도, 메서드, 의존성 관계 정리.
- 📐 [**아키텍처 및 파이프라인 심층 분석 (`docs/architecture.md`)**](docs/architecture.md)
  - 감지 트리 흐름도, `AssemblyLoadContext`를 통한 어셈블리 격리 로드 및 스켈레톤 복원 알고리즘.
- 🎮 [**전체 기능 사용법 가이드 (`docs/usage.md`)**](docs/usage.md)
  - GUI 세부 옵션, CLI 커맨드 인자, Standalone 시그니처 덤퍼 내보내기 방법 안내.

---

## 🛠️ 요구 사항 및 빌드

- **런타임 / SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download) 이상
- **지원 OS**: Windows 10 / 11 (x64)
- **Mono 디컴파일 선택 사항**: `ilspycmd` 도구 (`dotnet tool install -g ilspycmd`)

### 배포 바이너리 빌드
```powershell
# publish.bat 실행 또는 아래 명령어 수행
dotnet publish ModdingSupportTool/ModdingSupportTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish
```

---

## 📄 라이선스 (License)

이 프로젝트는 [MIT License](LICENSE)에 따라 자유롭게 사용, 수정, 배포할 수 있습니다.
