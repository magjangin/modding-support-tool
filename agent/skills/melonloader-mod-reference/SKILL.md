---
name: melonloader-mod-reference
description: >-
  Unity 게임(Mono/Il2Cpp)에 MelonLoader로 주입할 모드 DLL을 만들 때 쓰는 레퍼런스 워크플로. Modding Support Tool
  (MCP 서버 modding-support-tool 또는 ModdingSupportTool.exe CLI)로 게임 어셈블리를 C# 레퍼런스로 덤프하고, 타입·메서드를
  검색해 실제 시그니처로 Harmony 패치와 모드 프로젝트(.csproj)를 만든다. Use when the user wants to make or fix a MelonLoader
  mod, hook/patch a Unity game method, find a class or method in Assembly-CSharp, or asks about Il2Cpp/Mono game internals
  for modding. Unreal Engine 게임(UE4SS)이나 네이티브 바이너리 리버싱에는 쓰지 않는다.
---

# MelonLoader 모드 레퍼런스 워크플로

Modding Support Tool은 **MelonLoader에 주입할 모드 DLL을 만들 때 참고할 C# 레퍼런스**를 만든다.
게임 코드를 직접 바꾸는 도구가 아니고, 에이전트가 "어떤 클래스의 어떤 메서드를 어떤 시그니처로 패치할지"를
추측하지 않고 확인하게 해 주는 도구다.

## 범위 먼저 확인

- 대상: Unity 게임 + MelonLoader. Mono(`*_Data/Managed/Assembly-CSharp.dll`)와 Il2Cpp(`GameAssembly.dll`) 모두.
- 범위 밖이면 이 스킬을 멈추고 알맞은 도구를 안내한다. 자세한 표: [references/scope.md](references/scope.md)
  - Unreal Engine 게임 → **UE4SS** (Lua/C++ 모드, SDK 헤더 덤프)
  - Il2Cpp 메서드 본문(실제 로직), 네이티브 바이너리 → Cpp2IL / Il2CppDumper + Ghidra·IDA
  - 런타임 메모리 값 → Cheat Engine, 에셋 → AssetRipper / UABEA

## 도구 사용 방식

MCP 도구(`modding-support-tool` 서버)가 보이면 그것을 쓴다. 안 보이면 같은 기능의 CLI를 쓴다.
실행 파일 경로는 [references/local-install.md](references/local-install.md)(설치 시 생성)에 있다. 그 파일이 없으면
사용자에게 `ModdingSupportTool.exe` 위치를 묻거나, 사용자가 `ModdingSupportTool.exe agent install`을 실행하게 한다.

| 단계 | MCP 도구 | CLI |
| :--- | :--- | :--- |
| 백엔드 판별 | `detect_unity_backend(path)` | `detect <경로>` |
| 레퍼런스 덤프 | `dump_game_reference(game_path)` | `dump <게임폴더> --out <출력>` |
| 개요 | `describe_reference()` | `refs --ref <출력>` |
| 검색 | `search_reference(query, kind)` | `search <이름> --kind method --ref <출력>` |
| 선언/본문 읽기 | `read_reference_type(type, member)` | `type <타입> --member <멤버> --ref <출력>` |
| Harmony 패치 | `generate_harmony_patch(type, method)` | `patch <타입> <메서드> --ref <출력>` |
| 모드 프로젝트 | `generate_melon_mod_project(game_path, mod_name, output_dir)` | `template <게임폴더> <모드이름> --out <폴더>` |

CLI 명령 예와 출력 해석: [references/workflow.md](references/workflow.md)

## 절차

1. **판별** — `detect_unity_backend`로 백엔드, MelonLoader 설치 여부, Il2CppAssemblies 생성 여부를 본다.
   - MelonLoader가 없거나 Il2Cpp인데 `MelonLoader/Il2CppAssemblies`가 없으면: 사용자에게 MelonLoader를 설치하고
     게임을 한 번 실행해 달라고 한다. 그 전에는 덤프할 수 없다.
2. **덤프** — `dump_game_reference(game_path=<게임 루트>)`. 결과 폴더가 이후 호출의 기본 레퍼런스가 된다.
   - Il2Cpp: 시그니처 스켈레톤만 있다(메서드 본문 없음). Mono: 전체 소스 + 본문을 뺀 `_Outline`.
3. **탐색** — 덤프 폴더를 통째로 읽거나 grep 하지 말고 `search_reference`로 좁힌다.
   - 진입점 후보: 싱글턴(`Instance`), `*Manager`/`*Controller`, `MonoBehaviour`의 `Awake/Start/Update`,
     UI 핸들러(`OnBtn*Click`), 이벤트(`On*`).
   - 타입 전체 시그니처는 `read_reference_type(type)`, 특정 메서드 로직(Mono)은 `read_reference_type(type, member)`.
4. **프로젝트** — `generate_melon_mod_project(game_path, mod_name, output_dir)`로 `.csproj`와 `Core.cs`를 만든다.
   참조 경로·TargetFramework·`MelonGame` 값은 게임 폴더에서 읽어 채워진다. 설명: [references/project-setup.md](references/project-setup.md)
5. **패치** — `generate_harmony_patch(type, method)`가 만든 클래스를 프로젝트에 넣고 Prefix/Postfix 안을 채운다.
   패턴과 함정: [references/harmony-patterns.md](references/harmony-patterns.md)
   - Il2Cpp라면 반드시 [references/il2cpp-interop.md](references/il2cpp-interop.md)를 읽는다 (캐스팅, 컬렉션, 델리게이트, 컴포넌트 주입).
6. **빌드** — 모드 프로젝트 폴더에서 `dotnet build -c Release`. 모르는 타입 오류가 나면 `search_reference`로
   네임스페이스를 찾아 `using`을 고친다.
7. **배포/검증** — 게임 `Mods` 폴더 복사(`-p:CopyToMods=true`)나 게임 실행은 **사용자 확인 후** 한다.
   로드 여부는 `<게임>/MelonLoader/Latest.log`에서 모드 이름과 예외를 확인한다.

## 규칙

- 레퍼런스에 없는 클래스·메서드 이름을 지어내지 않는다. 쓰기 전에 `search_reference`로 확인한다.
- Il2Cpp 덤프의 네임스페이스(`Il2Cpp`, `Il2CppAssets.*` 등)와 타입 이름은 레퍼런스에 나온 그대로 쓴다.
- 게임 파일(`*_Data`, `GameAssembly.dll` 등)은 수정하지 않는다. 모드는 `Mods` 폴더의 DLL로만 배포한다.
- 디컴파일한 게임 코드를 모드 소스에 통째로 복사해 배포하지 않는다. 필요한 부분만 패치로 바꾼다.
