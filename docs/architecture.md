# 아키텍처 및 동작 파이프라인 (Architecture & Pipeline)

이 문서는 `Modding Support Tool`의 구조와 파이프라인을 다이어그램으로 설명합니다. 각 단계의 세부 규칙은 [core-logic.md](core-logic.md)에 있습니다.

> 범위: Unity(Mono/Il2Cpp) 게임에 **MelonLoader로 주입할 모드 DLL**을 만들 때 참고할 C# 레퍼런스를 만듭니다. Unreal Engine은 UE4SS, 네이티브 리버싱은 Ghidra/IDA 등 다른 도구를 쓰세요.

---

## 1. 전체 시스템 구조도

```mermaid
flowchart TD
    subgraph Entry ["진입점 (Program.cs)"]
        GUI["Avalonia GUI<br/>(MainWindow / MainViewModel)"]
        CLI["CLI 하위 명령<br/>(CliRunner)"]
        MCP["MCP 서버 stdio<br/>(McpServer)"]
    end

    subgraph Agents ["AI 에이전트"]
        Claude["Claude Code"]
        Agy["Antigravity (agy)"]
        Gemini["Gemini CLI"]
    end

    subgraph Toolkit ["공용 툴킷 (ModdingToolkit)"]
        Plan["DumpService.Plan<br/>판별 · 엔진 · 입력 보정"]
        Dump["DumpService<br/>덤프 실행"]
        Index["ReferenceIndex<br/>색인 · 검색 · 선언 읽기"]
        Patch["HarmonyPatchGenerator"]
        Template["MelonModTemplate"]
    end

    subgraph Engines ["덤프 엔진"]
        Sig["BuiltinSignatureDumper<br/>(Il2Cpp 스켈레톤)"]
        Ilspy["ilspycmd<br/>(Mono 전체 소스)"]
        Outliner["RoslynOutliner<br/>(_Outline)"]
    end

    subgraph Output ["출력 (Decompiled/...)"]
        Files["#60;모듈#62;/#60;Namespace#62;/#60;Type#62;.cs"]
        OutlineFiles["*_Outline/#60;모듈#62;/..."]
        Manifest["reference.json"]
    end

    Installer["AgentInstaller<br/>스킬 MD 설치 + MCP 등록"]

    Claude & Agy & Gemini -- "stdio JSON-RPC" --> MCP
    GUI --> Plan
    CLI --> Toolkit
    MCP --> Toolkit
    Plan --> Dump
    Dump --> Sig --> Files
    Dump --> Ilspy --> Files
    Ilspy --> Outliner --> OutlineFiles
    Dump --> Manifest
    Files & OutlineFiles & Manifest --> Index
    Index --> Patch
    GUI -- "AI 에이전트에 설치" --> Installer
    CLI -- "agent install" --> Installer
    Installer -.-> Claude & Agy & Gemini
```

---

## 2. Unity 백엔드 판별 ([`UnityBackendDetector.cs`](../ModdingSupportTool/Core/Detection/UnityBackendDetector.cs))

```mermaid
flowchart TD
    Start["입력 경로 (파일 or 폴더)"] --> CheckPath{"단일 파일인가?"}

    CheckPath -- "DLL" --> CheckParent["경로에 Il2CppAssemblies 포함?<br/>아니면 상위 폴더(Game_Data, 게임 루트)로 위임"]
    CheckPath -- "폴더" --> ScanDir["디렉터리 분석"]
    CheckParent --> ScanDir

    ScanDir --> Check1{"MelonLoader/Il2CppAssemblies?"}
    Check1 -- Yes --> Il2CppFound["Il2Cpp 확정<br/>(그 폴더를 덤프)"]
    Check1 -- No --> Check2{"*_Data/il2cpp_data/Metadata/<br/>global-metadata.dat?"}
    Check2 -- Yes --> Il2CppFound2["Il2Cpp 확정<br/>(MelonLoader/Il2CppAssemblies로 보정)"]
    Check2 -- No --> Check3{"GameAssembly.dll +<br/>UnityPlayer.dll?"}
    Check3 -- Yes --> Il2CppFound2
    Check3 -- No --> Check4{"*_Data/Managed/<br/>Assembly-CSharp.dll?"}
    Check4 -- Yes --> MonoFound["Mono 확정<br/>(ilspycmd, Managed로 보정)"]
    Check4 -- No --> Check5{"MonoBleedingEdge?"}
    Check5 -- Yes --> MonoFound
    Check5 -- No --> Unknown["Unknown<br/>(툴킷: Unreal이면 UE4SS 안내)"]
```

- 게임 루트 스캔은 최대 깊이 8, **최대 4,000개 폴더**에서 멈추고 `CancellationToken`으로 취소할 수 있습니다.
- GUI는 경로 입력을 300ms 디바운스한 뒤 백그라운드에서 판별합니다.

---

## 3. Il2Cpp: 내장 SignatureDumper 파이프라인 ([`BuiltinSignatureDumper.cs`](../ModdingSupportTool/Core/BuiltinSignatureDumper.cs))

```mermaid
flowchart LR
    A["Il2CppAssemblies/<br/>Assembly-CSharp.dll"] --> B["수집 가능한 ALC에<br/>LoadFromStream<br/>(파일 잠금 없음)"]
    B --> C["의존성 해석<br/>net6 → net472 → net35 → SupportModules<br/>(이름별 캐시)"]
    C --> D["GetTypes()<br/>MethodInfoStoreGeneric_* 제외"]
    D --> E["타입별 병렬 분석<br/>NativeField/MethodInfoPtr_*,<br/>IntPtr 생성자 제외"]
    E --> F["using 생성 · 이름 충돌 시<br/>네임스페이스 표기"]
    F --> G["경로 순차 결정 →<br/>병렬 쓰기"]
    G --> H["#60;모듈#62;/#60;Namespace#62;/#60;Type#62;.cs<br/>+ reference.json"]
    G -. "FileWritten 콜백" .-> I["메모리 색인 (MCP)"]
```

- Il2CppInterop 어셈블리는 모든 멤버를 `public`으로 노출하므로 원래 접근 제어자는 알 수 없습니다.
- 이름 충돌 판단은 덤프를 실행한 .NET 런타임의 기본 라이브러리 기준입니다. 그래서 앱(.NET 10)과 독립 SignatureDumper(.NET 8)의 표기가 드물게 다를 수 있습니다. (같은 런타임이면 출력이 같음)
- 덤프 전에 `<출력>/<모듈>` 폴더를 비워 게임 업데이트로 사라진 타입의 예전 파일이 남지 않게 합니다.

---

## 4. Mono: ilspycmd + AI 아웃라인

```mermaid
flowchart LR
    A["*_Data/Managed/<br/>Assembly-CSharp.dll"] --> B["ilspycmd -p --nested-directories<br/>(출력 폴더 먼저 비움)"]
    B --> C["#60;모듈#62;/ 전체 소스 + .csproj"]
    C --> D["RoslynOutliner<br/>(파일 병렬, 본문 → ;)"]
    D --> E["*_Outline/#60;모듈#62;/ 아웃라인"]
    C & E --> F["reference.json<br/>(hasMethodBodies: true)"]
```

- ilspycmd 표준 출력/에러는 비동기로 로그에 흘려보내고, 취소하면 프로세스 트리 전체를 종료합니다.
- 인자는 `ProcessStartInfo.ArgumentList`로 넘겨 공백·따옴표가 든 경로도 안전합니다.

---

## 5. 에이전트 ↔ MCP 서버 흐름

```mermaid
sequenceDiagram
    participant A as AI 에이전트
    participant S as ModdingSupportTool.exe mcp
    participant G as 게임 폴더
    A->>S: initialize (protocolVersion)
    S-->>A: serverInfo, tools capability, instructions
    A->>S: tools/call detect_unity_backend(path)
    S->>G: 판별 (Il2CppAssemblies, app.info)
    S-->>A: Il2Cpp · MelonLoader 설치됨 · 덤프 입력/엔진
    A->>S: tools/call dump_game_reference (progressToken)
    S-->>A: notifications/progress ...
    S-->>A: 출력 폴더 · 색인 요약 (기본 레퍼런스로 기억)
    A->>S: search_reference / read_reference_type
    S-->>A: 한 줄 시그니처 + 파일:줄 / 선언 코드
    A->>S: generate_harmony_patch · generate_melon_mod_project
    S-->>A: 패치 코드 · .csproj + Core.cs
    Note over A: dotnet build → (사용자 확인 후) Mods 복사 → Latest.log 확인
```

---

## 6. 에이전트 설치 흐름 ([`AgentInstaller.cs`](../ModdingSupportTool/Agent/AgentInstaller.cs))

```mermaid
flowchart TD
    Cmd["agent install / GUI 버튼"] --> Src{"스킬 출처"}
    Src -- "기본" --> Emb["exe 내장 리소스<br/>(agent/skills/**)"]
    Src -- "--source github / URL" --> Http["HTTP 다운로드"]
    Src -- "--source #60;폴더#62;" --> Dir["로컬 폴더"]
    Emb & Http & Dir --> Clients["대상 에이전트<br/>(지정 또는 자동 감지)"]
    Clients --> Skill["스킬 폴더에 SKILL.md + references 쓰기<br/>표식(.modding-support-tool.json) + local-install.md"]
    Clients --> Mcp{"MCP 등록"}
    Mcp -- "Claude 사용자" --> ClaudeCli["claude mcp add --scope user"]
    Mcp -- "Gemini 사용자" --> GeminiCli["gemini mcp add --scope user"]
    Mcp -- "Antigravity / 프로젝트" --> Merge["JSON 병합<br/>(다른 키 보존 · 백업 · 원자적 교체)"]
```
