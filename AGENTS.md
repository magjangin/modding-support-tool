# AGENTS.md — Modding Support Tool 인수인계

MelonLoader 모드(DLL) 작성용 C# 레퍼런스 생성기(Unity Mono/Il2Cpp). GUI(Avalonia) + CLI + MCP 서버가 같은 구현(`Agent/ModdingToolkit.cs`)을 쓴다.
구조·사용법 상세는 [docs/](docs/) (`architecture.md`, `file-structure.md`, `core-logic.md`, `usage.md`).

## ① 검증에 쓴 게임·버전 (도구가 고정하지는 않음)
도구는 특정 게임 버전에 묶이지 않는다. `closures`(2026-11-09) 검증 환경:

| 대상 | 버전 | 입력 |
| :--- | :--- | :--- |
| Sixtar Gate STARTRAIL (Steam 1802720) | Build ID 26455233, 고정 안 함(.acf 패치 차단 없음) | MelonLoader v0.7.3 Open-Beta, Cpp2IL 2022.1.0-pre-release.21 `cpp2il_out` |
| DankaguPL | Il2CppDumper 출력 (32비트) | `Il2CppDumperOut/DankaguPL/DummyDll` (git 제외) |
| 2D Neon Cube | Mono | `Managed` 폴더 → DummyDll 아님 오류 경로 확인용 |

게임이 업데이트되면 RVA가 바뀐다. MelonLoader가 다음 실행 때 `cpp2il_out`을 다시 만들므로 `closures`는 다시 조회하면 된다.

## ② 런타임 백엔드와 입력 경로
- **Mono**: `*_Data/Managed` → ilspycmd 전체 디컴파일 + Roslyn 아웃라인.
- **Il2Cpp**: `MelonLoader/Il2CppAssemblies`(Il2CppInterop) → 내장 SignatureDumper 시그니처 스켈레톤(본문 없음).
- **Il2Cpp 주소 조회(`closures`)**: `MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out`(MelonLoader가 자동 생성) 또는 Il2CppDumper `DummyDll`.
- 출력 기본 위치: `Decompiled/<게임이름>/` (+ `reference.json` 매니페스트).

## ③ 설계 의도 (코드에 안 드러나는 것)
- **기본 덤프는 Il2CppInterop 이름 그대로 둔다.** 모드 코드·`generate_harmony_patch`가 `Il2Cpp…` 이름을 쓰기 때문. DummyDll(원래 이름)로 기본 덤프를 바꾸지 않는다.
- **주소 정보는 필요할 때만 조회한다(`closures` / `inspect_closures`).** 전체 덤프에 RVA를 붙이면 공용 코드 ICF 경고가 수천 줄 생긴다(STARTRAIL 빈 생성자 `0x449260` ×1398).
- ICF 경고가 필요한 이유: Il2CppInterop의 Harmony 패치는 네이티브 함수 주소에 디투어를 걸어서, 같은 기계어를 공유하는 메서드가 모두 패치된다. 공유 20곳 이상은 "공용 코드"로 묶고 상대를 나열하지 않는다.
- ICF 색인은 `System.Reflection.Metadata`로 전 어셈블리를 병렬로 읽는다(ICF는 GameAssembly.dll 전체 범위라 mscorlib·UnityEngine과도 겹침). 타입 상세는 MetadataLoadContext로 읽는다.
- DummyDll은 MetadataLoadContext + 단순 이름 리졸버로 연다. 더미 mscorlib 공개 키 토큰이 비어 있어 `PathAssemblyResolver`가 거부하고, 런타임 로드는 명시적 인터페이스 구현 정보가 빠져 상태 머신 타입이 로드 거부된다.
- Interop 이름은 **토큰으로 확정**한다: Il2CppInterop은 각 타입의 정적 생성자에서 `NativeMethodInfoPtr_X = IL2CPP.GetIl2CppMethodByToken(classPtr, 원래토큰)`을 만든다. 더미 메서드의 `[Token]` 값으로 같은 게임의 `MelonLoader/Il2CppAssemblies`를 찾으면 정확한 이름이 나온다(`InteropTokenMap`). 규칙 추정으로는 로컬 함수 이름을 맞출 수 없다(아래 ⑤).
- 이름 확인 우선순위: ① Il2CppAssemblies 토큰 매핑 ② 같은 게임(`reference.json`의 GameRoot 일치) 레퍼런스 덤프 ③ 규칙 추정. 다른 게임 덤프로 확인하면 틀린 "없음"이 나오므로 쓰지 않는다.
- asmdef로 나뉜 게임(PLATiNA LAB, Transiruby)은 `Assembly-CSharp.dll`이 없고, Interop 파일 이름에 `Il2Cpp` 접두사가 붙는다(`Alchemy.dll` → `Il2CppAlchemy.dll`).
- 범위 밖: Il2Cpp 메서드 본문(ISIL/디스어셈블리), Unreal(UE4SS). 본문 분석은 Ghidra/IDA/Cpp2IL 몫.

## ④ 핵심 진입점과 파이프라인
- 덤프: `Services/DumpService.RunDumpAsync` → Mono `ilspycmd` / Il2Cpp `Core/BuiltinSignatureDumper` → `Core/Reference/ReferenceIndex`(Roslyn 색인).
- 조회: `ModdingToolkit.Search / ReadType / HarmonyPatch / Closures` ← CLI `Cli/CliRunner.cs`, MCP `Agent/Mcp/McpTools.cs`.
- `closures`: `ModdingToolkit.Closures` → `ClosureInspector.FindDummyDirectory` → `ClosureInspector.Use`(폴더별 캐시) → `Inspect`(타입 찾기 → ICF 색인 → `ReportWriter`).
  - 람다 부모 메서드는 이름 규칙으로 찾는다: `<X>b__N_M`, `<X>d__N`, `<X>g__Name|N_M`, 중첩 `<<X>g__…>b__…` → `X`.
  - Interop 이름: `InteropTokenMap`(Il2CppAssemblies .cctor IL에서 토큰 → `NativeMethodInfoPtr_` 필드 → 그 필드를 읽는 메서드). 없을 때만 규칙 추정(식별자 밖 문자 `< >` → `_`, 네임스페이스 앞에 `Il2Cpp`).

## ⑤ 시도했다가 버린 것 (Dead Ends)
- **전체 덤프에 RVA·필드 오프셋·ICF 주석 붙이기** (`experiment/dummydll-offsets`, 커밋 9703453, 삭제됨): 공용 코드 경고 소음, 원래 이름이라 Interop 이름과 안 맞음. → `closures`로 대체.
- **MetadataLoadContext로 메서드마다 속성 읽어 ICF 색인**: STARTRAIL 93개 DLL에서 15.8초. → System.Reflection.Metadata 병렬로 0.6초.
- **ISIL(Cpp2IL 중간 표현) 도입 검토**: 본문 분석이라 범위 밖. 하지 않는다.
- **"로컬 함수는 Interop에 없다"는 판단 (틀렸음)**: `_g__` 문자열이 0건이라 없다고 봤지만, 실제로는 이름에 `|`가 있어 Il2CppInterop이 난독화된 이름으로 보고 `Method_Internal_Static_Void_Int32_PDM_0`처럼 **시그니처 기반 이름으로 바꾼 것**이었다. `_PDM`·끝 번호는 생성 시점 분석에 달려 있어 규칙으로 못 맞춘다 → 토큰 매핑으로 해결. 2026-11-09 11개 게임(Muse Dash, PLATiNA LAB, STARTRAIL, STARGAZER, Transiruby, KAMITSUBAKI, TurretGirls, Food Wagon, O2Jam, Yasha, MiSide)에서 로컬 함수 968개 전부 이름 확인, 없음 0.
- **로컬 함수 이름 규칙 추정**: 위 이유로 거의 항상 틀린다. Il2CppAssemblies 없이 조회하면 `⚠ 추정` 경고를 붙인다.
- 참고로 람다(`<X>b__N`)는 규칙 추정이 거의 맞는다: 같은 11개 게임 람다 16,593개 중 16,590개 일치. 다른 3개는 같은 타입 안 이름 충돌로 Il2CppInterop이 시그니처를 덧붙인 경우(`_GetValue_b__5_0_Private_Void_IValue_0`). 람다는 `<>c`·`<>c__DisplayClass` 말고 소유 타입에 직접 생기는 것(`this`만 캡처)도 약 18%라 출력 범위를 두 가지로 줄이지 않는다. 람다 조회가 실제 모딩에서 얼마나 쓸모 있는지는 아직 판단 전.

## ⑥ 빌드와 배포
```bash
dotnet build ModdingSupportTool/ModdingSupportTool.csproj -c Debug
```
- 릴리스: `publish.bat` → `Publish/ModdingSupportTool.exe` (단일 파일, win-x64, .NET 10 런타임 필요) + `run_dump.bat`.
- 이 도구 자체는 게임 `Mods/`에 배포하지 않는다. 도구가 만든 모드 프로젝트는 `dotnet build -c Release` 후 `<게임>/Mods/`에 DLL을 넣는다(`-p:CopyToMods=true`).
- 에이전트 연동: `ModdingSupportTool.exe agent install` (스킬 `agent/skills` + MCP 등록).
- git 푸시는 `origin`(GitHub)으로 한다.
