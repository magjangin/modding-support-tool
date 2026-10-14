# 이 도구의 범위

Modding Support Tool은 **Unity 게임에 MelonLoader로 주입할 모드 DLL을 만들 때, 에이전트가 참고할 C# 레퍼런스를 만드는 도구**다.
게임 어셈블리의 타입·필드·프로퍼티·메서드 시그니처(Mono는 메서드 본문까지)를 C# 파일로 덤프하고, 그 레퍼런스에서
검색·조회·Harmony 패치 생성·모드 프로젝트 생성을 한다.

## 할 수 있는 것

| 대상 | 결과 |
| :--- | :--- |
| Unity Mono 게임 (`*_Data/Managed/Assembly-CSharp.dll`) | ilspycmd 전체 디컴파일(본문 포함) + 본문을 뺀 AI 아웃라인 |
| Unity Il2Cpp 게임 (`GameAssembly.dll` + `global-metadata.dat`) | MelonLoader가 만든 `MelonLoader/Il2CppAssemblies`의 시그니처 스켈레톤 |
| 레퍼런스 활용 | 이름 검색, 타입/멤버 선언 읽기, HarmonyX 패치 코드, MelonLoader `.csproj` + `Core.cs` |

## 할 수 없는 것 → 다른 도구

| 하고 싶은 것 | 쓸 도구 |
| :--- | :--- |
| Unreal Engine(UE4/UE5) 게임 모딩, SDK/헤더 덤프 | **UE4SS** (Lua·C++ 모드, Live View, CXX 헤더 덤프) |
| Il2Cpp 메서드의 실제 로직(본문) 읽기 | Cpp2IL, Il2CppDumper + Ghidra / IDA (네이티브 `GameAssembly.dll` 분석) |
| 네이티브 C/C++ 게임, 엔진 바이너리 분석 | Ghidra, IDA, x64dbg |
| 실행 중 메모리 값 찾기 | Cheat Engine |
| 텍스처·메시·씬 같은 에셋 추출/수정 | AssetRipper, UABEA (Unity) |
| 렌더 패스·셰이더 분석 | RenderDoc |
| BepInEx 플러그인 | 레퍼런스(덤프·검색)는 그대로 쓸 수 있지만 템플릿·패치 안내는 MelonLoader 기준이다. |
| Godot, GameMaker, RPG Maker 등 다른 엔진 | 각 엔진 전용 도구 |

## 판별 신호

- Unity: `<게임>_Data` 폴더, `UnityPlayer.dll`
  - Mono: `<게임>_Data/Managed/Assembly-CSharp.dll`, `MonoBleedingEdge`
  - Il2Cpp: `GameAssembly.dll`, `<게임>_Data/il2cpp_data/Metadata/global-metadata.dat`
- Unreal: `Engine` 폴더, `<프로젝트>/Content/Paks/*.pak`, `<프로젝트>/Binaries/Win64/*-Win64-Shipping.exe`
- `detect_unity_backend`가 Unity가 아니라고 하면 이 스킬을 멈추고 위 표의 도구를 안내한다.
