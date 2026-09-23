# 아키텍처 및 동작 파이프라인 (Architecture & Pipeline)

이 문서는 `Modding Support Tool`의 전반적인 시스템 아키텍처와 게임 백엔드 판별, 디컴파일/시그니처 덤프 파이프라인의 내부 동작 원리를 설명합니다.

---

## 1. 전체 시스템 구조도

```mermaid
flowchart TD
    subgraph UI_CLI ["입력 인터페이스"]
        GUI["Avalonia Desktop GUI\n(MainWindow.axaml)"]
        CLI["CLI Runner / run_dump.bat\n(CliRunner.cs)"]
    end

    subgraph CoreEngine ["분석 및 감지 코어"]
        Detector["UnityBackendDetector\n(파일 구조 & 메타데이터 분석)"]
        Presets["PresetService\n(주요 게임 경로 프리셋)"]
    end

    subgraph ExecutionLayer ["덤프 실행 서비스 (DumpService)"]
        Dispatcher{"엔진 결정\n(Il2Cpp vs Mono)"}
        Il2CppBranch["BuiltinSignatureDumper\n(In-Process 정적 분석)"]
        MonoBranch["ilspycmd Process\n(외부 CLI 도구 호출)"]
    end

    subgraph OutputLayer ["출력 디렉터리 (Decompiled/)"]
        SkeletonFiles["C# 스켈레톤 소스 (*.cs)\n(Decompiled/<Asm>/<Namespace>/<Type>.cs)"]
        FullDecompiled["전체 디컴파일 C# 소스 & .csproj\n(Decompiled/<Asm>/...)"]
    end

    GUI --> Detector
    CLI --> Detector
    Presets -.-> Detector

    Detector --> Dispatcher
    Dispatcher -- "Il2Cpp 감지됨" --> Il2CppBranch
    Dispatcher -- "Mono 감지됨" --> MonoBranch

    Il2CppBranch --> SkeletonFiles
    MonoBranch --> FullDecompiled
```

---

## 2. Unity 백엔드 판별 로직 상세 ([`UnityBackendDetector.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/Detection/UnityBackendDetector.cs))

Unity 게임은 빌드 시점에 어떤 백엔드를 사용했는지에 따라 파일 구조가 확연히 다릅니다. 본 도구는 이를 휴리스틱 및 시그니처 파일 체크를 통해 100% 자동 판별합니다.

```mermaid
flowchart TD
    Start["입력 경로 수신 (파일 or 폴더)"] --> CheckPath{"단일 파일인가,\n폴더인가?"}
    
    CheckPath -- "단일 파일 (*.dll)" --> CheckParent["경로 내 'Il2CppAssemblies' 포함 확인\n또는 상위 폴더(Game_Data)로 스캔 위임"]
    CheckPath -- "디렉터리" --> ScanDir["디렉터리 구조 분석"]
    
    CheckParent --> ScanDir

    ScanDir --> Check1{"MelonLoader/Il2CppAssemblies\n폴더인가?"}
    Check1 -- Yes --> Il2CppFound["Il2Cpp 백엔드 확정\n(타겟 어셈블리 직접 분석)"]
    Check1 -- No --> Check2{"*_Data/il2cpp_data/Metadata/\nglobal-metadata.dat 존재?"}
    
    Check2 -- Yes --> Il2CppFound2["Il2Cpp 백엔드 확정\n(MelonLoader 폴더 보정 추천)"]
    Check2 -- No --> Check3{"GameAssembly.dll +\nUnityPlayer.dll 존재?"}
    
    Check3 -- Yes --> Il2CppFound2
    Check3 -- No --> Check4{"*_Data/Managed/ 내\nAssembly-CSharp.dll 존재?"}
    
    Check4 -- Yes --> MonoFound["Mono 백엔드 확정\n(ilspycmd 엔진 선택)"]
    Check4 -- No --> Check5{"MonoBleedingEdge\n런타임 폴더 존재?"}
    
    Check5 -- Yes --> MonoFound
    Check5 -- No --> Unknown["백엔드 미확정\n(기본 SignatureDumper로 시도)"]
```

---

## 3. 내장 SignatureDumper 동작 파이프라인 ([`BuiltinSignatureDumper.cs`](file:///h:/source/repos/modding%20support%20tool/ModdingSupportTool/Core/BuiltinSignatureDumper.cs))

Il2Cpp 게임의 경우 MelonLoader가 생성한 더미 어셈블리(`.dll`)를 런타임 게임 실행 없이 오프라인에서 읽어들입니다.

### 1) 격리 로딩 (`AssemblyLoadContext`)
- 분석 대상 어셈블리가 프로세스에 영구 잠금되는 문제를 방지하기 위해 매 덤프 시마다 `isCollectible: true` 속성의 임시 ALC를 생성합니다.
- 어셈블리가 참조하는 MelonLoader 기본 의존성(`net6`, `net472`, `net35`, `Dependencies/SupportModules`)을 `alc.Resolving` 이벤트 핸들러에서 자동 해결합니다.

### 2) 리플렉션 정적 분석 (`System.Reflection`)
- `asm.GetTypes()`로 로드된 타입 메타데이터를 순회하며 다음 정보들을 추출하여 `TypeSignatureInfo` 모델을 빌드합니다:
  - **타입 원형**: `class`, `struct`, `interface`, `enum`, `delegate` 구분.
  - **상속 및 구현**: 베이스 클래스 및 모든 인터페이스 목록.
  - **필드 & 상수**: `const`, `static`, `readonly`, 접근 제어자, 기본 리터럴 값.
  - **프로퍼티**: `get;`, `set;`의 개별 접근 제어자(예: `public int Value { get; private set; }`).
  - **생성자 & 메서드**: 리턴 타입, 제네릭 인자, 파라미터(`ref`, `out`, `params`, 디폴트 인자).

### 3) 소스 파일 분할 생성
- 전체 클래스가 한 파일에 뭉쳐 있으면 검색 및 Git 형상 관리가 어렵기 때문에, 각 타입별로 독립된 `.cs` 파일을 생성합니다:
  - **저장 규칙**: `<OutputDirectory>/<Namespace>/<TypeName>.cs`
  - 네임스페이스가 없는 글로벌 타입은 `<OutputDirectory>/<TypeName>.cs`에 직접 저장됩니다.
- 분석이 끝나면 ALC를 언로드(`alc.Unload()`)하여 파일 핸들과 메모리를 즉시 반환합니다.

---

## 4. Mono 디컴파일 파이프라인 (`ilspycmd`)

- Mono 백엔드로 감지되었거나 사용자가 `ilspycmd`를 선택한 경우:
  - 대상 어셈블리(`Assembly-CSharp.dll` 등)에 대해 `ilspycmd` CLI를 백그라운드 프로세스로 호출합니다.
  - 실행 인수:
    - `-p`: C# 프로젝트 파일(`.csproj`) 생성
    - `--nested-directories`: 네임스페이스에 맞춘 폴더 분할
    - `-o "<OutputDir>"`: 디컴파일 결과 출력 위치
  - 표준 출력(stdout) 및 표준 에러(stderr) 스트림을 비동기 가로채어 GUI/CLI 실시간 터미널 화면에 뿌려줍니다.
  - 작업 취소 시 프로세스 트리 전체를 정리(`Kill(entireProcessTree: true)`)하여 좀비 프로세스를 방지합니다.
