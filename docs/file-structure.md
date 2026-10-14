# 폴더별 파일 구조 (Directory & File Architecture)

프로젝트의 폴더와 파일을 역할별로 정리했습니다. 동작 원리는 [core-logic.md](core-logic.md), 다이어그램은 [architecture.md](architecture.md)를 보세요.

---

## 📁 1. 루트 (`/`)

| 파일/폴더 | 역할 |
| :--- | :--- |
| [`ModdingSupportTool.slnx`](../ModdingSupportTool.slnx) | Visual Studio 2022 v17.10+ XML 솔루션 파일. `AppPaths`가 저장소 루트를 찾는 표식으로도 쓴다. |
| [`run_dump.bat`](../run_dump.bat) | 드래그 앤 드롭/더블 클릭 덤프. exe가 없으면 `publish.bat`을 먼저 실행. |
| [`publish.bat`](../publish.bat) | `Publish/ModdingSupportTool.exe` 단일 파일(win-x64, framework-dependent) 게시. |
| [`.claude-plugin/marketplace.json`](../.claude-plugin/marketplace.json) | Claude Code 플러그인 마켓플레이스. `agent/` 플러그인을 가리킨다. |
| [`agent/`](../agent) | AI 에이전트 플러그인 (아래 2장). |
| [`docs/`](.) | 문서. |
| [`ModdingSupportTool/`](../ModdingSupportTool) | C# 애플리케이션. |
| `Publish/`, `Decompiled/` | 게시 결과, 기본 덤프 출력 (Git 제외). |

---

## 📁 2. 에이전트 플러그인 (`agent/`)

스킬 MD의 **단일 원본**입니다. Claude Code·Antigravity 플러그인으로 바로 설치할 수 있고, 빌드 시 `agent/skills/**`가 exe에 임베드되어 `agent install`이 이 파일들을 각 에이전트 스킬 폴더에 씁니다.

| 파일 | 역할 |
| :--- | :--- |
| [`.claude-plugin/plugin.json`](../agent/.claude-plugin/plugin.json) | Claude Code 플러그인 매니페스트 |
| [`plugin.json`](../agent/plugin.json) | Antigravity 플러그인 매니페스트 (`agy plugin install`) |
| [`README.md`](../agent/README.md) | 플러그인 설치 안내 |
| [`skills/melonloader-mod-reference/SKILL.md`](../agent/skills/melonloader-mod-reference/SKILL.md) | 스킬 본문: 범위 확인 → 판별 → 덤프 → 검색 → 프로젝트/패치 → 빌드 절차 |
| `references/workflow.md` | MCP 도구·CLI 명령 상세, 출력 폴더 구조, 검증 로그 |
| `references/scope.md` | 이 도구의 범위와 다른 도구(UE4SS, Ghidra 등) 안내 |
| `references/harmony-patterns.md` | HarmonyX 패턴, 주입 인자, Il2Cpp 차이, 오류 해결 |
| `references/il2cpp-interop.md` | Il2Cpp 이름 규칙, 캐스팅, 컬렉션, 델리게이트, 컴포넌트 주입 |
| `references/project-setup.md` | MelonMod 수명 주기, 설정·로그, `.csproj` 요점, 빌드 문제 |
| (설치 시 생성) `references/local-install.md` | exe 경로, MCP 이름, CLI 대체 명령 |

---

## 📁 3. 애플리케이션 (`ModdingSupportTool/`)

### 루트 파일
- [`ModdingSupportTool.csproj`](../ModdingSupportTool/ModdingSupportTool.csproj) — `net10.0`, `WinExe`, 버전 1.1.0. Avalonia 12, CommunityToolkit.Mvvm, Microsoft.CodeAnalysis.CSharp. 임베디드 리소스: 덤퍼 코어 소스(`Export/SignatureDumper.cs`), 스킬 파일(`agent/skills/**`).
- [`Program.cs`](../ModdingSupportTool/Program.cs) — 인자 없음 → GUI, `mcp` → MCP 서버(콘솔 미연결), 그 밖 → CLI. 출력이 리디렉션되지 않았을 때만 콘솔에 붙는다.
- `App.axaml(.cs)`, `ViewLocator.cs`, `app.manifest` — Avalonia 앱 수명 주기, 뷰 매핑, DPI 매니페스트.

### 📂 `Agent/` (AI 에이전트 연동)
- [`ModdingToolkit.cs`](../ModdingSupportTool/Agent/ModdingToolkit.cs) — MCP 도구와 CLI 하위 명령이 함께 쓰는 기능(판별·덤프·개요·검색·선언 읽기·패치·템플릿), 레퍼런스 폴더 결정, 범위 밖(Unreal 등) 안내.
- [`AgentInstaller.cs`](../ModdingSupportTool/Agent/AgentInstaller.cs) — 스킬 설치(내장/URL/폴더 출처), MCP 등록(공식 CLI 또는 JSON 병합), 제거, 상태.
- [`Mcp/McpServer.cs`](../ModdingSupportTool/Agent/Mcp/McpServer.cs) — stdio JSON-RPC 2.0 MCP 서버 (버전 협상, 동시 처리, 취소, 진행률).
- [`Mcp/McpTools.cs`](../ModdingSupportTool/Agent/Mcp/McpTools.cs) — 도구 7개의 이름·설명·입력 스키마와 핸들러.

### 📂 `Cli/`
- [`CliRunner.cs`](../ModdingSupportTool/Cli/CliRunner.cs) — 하위 명령(`dump`, `outline`, `detect`, `refs`, `search`, `type`, `patch`, `template`, `mcp`, `agent`)과 예전 인자 형식 처리, 사용법·종료 코드.
- [`CliArgs.cs`](../ModdingSupportTool/Cli/CliArgs.cs) — 위치 인자·옵션·플래그 파서 (`-i/-o/-t` 별칭).

### 📂 `Core/`
- [`AppPaths.cs`](../ModdingSupportTool/Core/AppPaths.cs) — 버전, exe 경로, 저장소 루트, 기본 출력 폴더, MCP 서버 이름.
- [`BuiltinSignatureDumper.cs`](../ModdingSupportTool/Core/BuiltinSignatureDumper.cs) — 리플렉션 시그니처 덤퍼 (앱·독립 프로젝트 공용 소스, net472 호환). 스트림 로드, 의존성 캐시, Il2CppInterop 멤버 생략, 병렬 분석/쓰기.
- [`SignatureDumperExporter.cs`](../ModdingSupportTool/Core/SignatureDumperExporter.cs) — 위 파일을 리소스에서 꺼내 독립 프로젝트로 내보냄.
- [`RoslynOutliner.cs`](../ModdingSupportTool/Core/RoslynOutliner.cs) — 메서드·접근자 본문을 지운 AI 아웃라인 (병렬).
- `Detection/`
  - [`UnityBackend.cs`](../ModdingSupportTool/Core/Detection/UnityBackend.cs) — `Unknown`, `Mono`, `Il2Cpp`
  - [`UnityBackendDetector.cs`](../ModdingSupportTool/Core/Detection/UnityBackendDetector.cs) — 파일 구조 기반 판별 (탐색 상한·취소)
  - [`GameLocator.cs`](../ModdingSupportTool/Core/Detection/GameLocator.cs) — 게임 루트, `*_Data`, MelonLoader 폴더, `app.info` 회사명·제품명
- `Reference/`
  - [`ReferenceManifest.cs`](../ModdingSupportTool/Core/Reference/ReferenceManifest.cs) — `reference.json` 읽기/기록
  - [`ReferenceIndex.cs`](../ModdingSupportTool/Core/Reference/ReferenceIndex.cs) — Roslyn 색인, 검색 점수, 타입 노드 찾기
  - [`HarmonyPatchGenerator.cs`](../ModdingSupportTool/Core/Reference/HarmonyPatchGenerator.cs) — 실제 시그니처 기반 HarmonyX 패치 생성
  - [`MelonModTemplate.cs`](../ModdingSupportTool/Core/Reference/MelonModTemplate.cs) — MelonLoader 모드 `.csproj` + `Core.cs` 생성

### 📂 `Services/`
- [`DumpService.cs`](../ModdingSupportTool/Services/DumpService.cs) — 덤프 계획(`Plan`), 대상 DLL 결정, SignatureDumper/ilspycmd 실행, 아웃라인, `reference.json` 기록, 덤프한 내용 전달(`CaptureWrittenSources`).
- [`PresetService.cs`](../ModdingSupportTool/Services/PresetService.cs) — 리듬게임 경로 프리셋 (GUI 버튼과 `--cli` 기본 경로가 같은 목록을 씀).
- [`IFilePickerService.cs`](../ModdingSupportTool/Services/IFilePickerService.cs) / [`FilePickerService.cs`](../ModdingSupportTool/Services/FilePickerService.cs) — Avalonia 파일/폴더 선택 추상화.

### 📂 `Models/`
- [`DumpEngineType.cs`](../ModdingSupportTool/Models/DumpEngineType.cs) — `SignatureDumper`, `IlSpyCmd`.

### 📂 `ViewModels/` · `Views/`
- [`MainViewModel.cs`](../ModdingSupportTool/ViewModels/MainViewModel.cs) — 디바운스·백그라운드 판별, 로그 묶음 갱신, 프리셋 항목, 덤프·취소·내보내기·에이전트 설치 명령.
- [`MainWindow.axaml`](../ModdingSupportTool/Views/MainWindow.axaml) — 헤더/엔진 선택, 입력·프리셋, 출력·옵션, AI 에이전트 연동, 실행 제어, 로그.
- [`MainWindow.axaml.cs`](../ModdingSupportTool/Views/MainWindow.axaml.cs) — 새 로그가 오면 맨 아래로 스크롤.

### 📂 `Assets/`
- `app-icon.ico` / `app-icon.png` — 앱 아이콘. `avalonia-logo.ico` — 하위 호환용 동일 아이콘.

---

## 📁 4. 출력 폴더 (`Decompiled/`)

```
Decompiled/                         # GUI/CLI 기본 출력 (에이전트 덤프는 Decompiled/<게임 이름>/)
├── reference.json                  # 게임·백엔드·모듈 정보
├── Assembly-CSharp/<Namespace>/<Type>.cs
└── Assembly-CSharp-firstpass/...
Decompiled_Outline/                 # Mono(ilspycmd)일 때만: 본문을 지운 아웃라인
```
