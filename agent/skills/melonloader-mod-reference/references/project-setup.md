# MelonLoader 모드 프로젝트

`generate_melon_mod_project`(CLI `template`)는 게임 폴더를 보고 아래 형태의 `<모드>.csproj`와 `Core.cs`를 만든다.

## Core.cs

```csharp
using MelonLoader;

[assembly: MelonInfo(typeof(MyFirstMod.Core), "MyFirstMod", "1.0.0", "me")]
[assembly: MelonGame("PeroPeroGames", "MuseDash")]   // <게임>_Data/app.info 의 1·2번째 줄

namespace MyFirstMod
{
    public sealed class Core : MelonMod
    {
        public override void OnInitializeMelon() => LoggerInstance.Msg("MyFirstMod 로드됨");
    }
}
```

`MelonGame`의 회사명·게임명이 실제와 다르면 MelonLoader가 모드를 이 게임용이 아니라고 보고 로드하지 않는다.

## MelonMod 수명 주기 (자주 쓰는 것)

| 메서드 | 시점 |
| :--- | :--- |
| `OnInitializeMelon()` | 모드 로드 직후 (설정 만들기, 수동 패치) |
| `OnLateInitializeMelon()` | 모든 모드 초기화 후, 게임 첫 프레임 전후 |
| `OnSceneWasLoaded(int buildIndex, string sceneName)` | 씬 로드 직후 (객체 찾기·캐시) |
| `OnSceneWasInitialized(int buildIndex, string sceneName)` | 씬의 Start까지 끝난 뒤 |
| `OnUpdate()` / `OnFixedUpdate()` / `OnLateUpdate()` | Unity 프레임 루프 |
| `OnGUI()` | IMGUI 그리기 |
| `OnDeinitializeMelon()` | 게임 종료/모드 언로드 |

## 설정과 로그

```csharp
private MelonPreferences_Category _category;
private MelonPreferences_Entry<bool> _enabled;

public override void OnInitializeMelon()
{
    _category = MelonPreferences.CreateCategory("MyFirstMod");
    _enabled = _category.CreateEntry("Enabled", true, "모드 사용");   // UserData/MelonPreferences.cfg 에 저장
    LoggerInstance.Msg($"Enabled = {_enabled.Value}");
    LoggerInstance.Warning("경고");
    LoggerInstance.Error("오류");
}
```

## .csproj 요점

| 항목 | Il2Cpp | Mono |
| :--- | :--- | :--- |
| `TargetFramework` | `net6.0` | `net472` (MelonLoader/net472 가 있을 때) 또는 `net35` |
| MelonLoader·Harmony | `MelonLoader/net6/MelonLoader.dll`, `0Harmony.dll` | `MelonLoader/net472|net35/MelonLoader.dll`, `0Harmony.dll` |
| Il2CppInterop | `MelonLoader/net6/Il2CppInterop.Runtime.dll`, `Il2CppInterop.Common.dll` | — |
| 게임·Unity 어셈블리 | `MelonLoader/Il2CppAssemblies/Il2Cpp*.dll`, `UnityEngine*.dll`, `Unity.*.dll`, `Assembly-CSharp*.dll` | `<게임>_Data/Managed/UnityEngine*.dll`, `Unity.*.dll`, `Assembly-CSharp*.dll` |

- 모든 참조는 `Private="false"`: 게임에 이미 있는 DLL을 모드 출력에 복사하지 않는다.
- Mono의 `mscorlib`·`System*`·`netstandard`는 참조하지 않는다 (대상 프레임워크가 제공, 중복 정의 오류 방지).
- `GameDir` 속성 하나만 바꾸면 다른 PC 경로로 옮길 수 있다.
- `CopyToMods`가 `true`면 빌드 후 `$(GameDir)\Mods`로 복사한다 (기본 `false`, `dotnet build -p:CopyToMods=true`).
- Mono의 `TargetFramework`는 `MelonLoader/Latest.log`의 `Runtime Type`과 맞춘다.

## 빌드 문제 해결

| 오류 | 해결 |
| :--- | :--- |
| `CS0246 형식 또는 네임스페이스 이름을 찾을 수 없습니다` | `search_reference "<타입>" --kind type`으로 네임스페이스 확인 → `using` 추가. 레퍼런스에 없으면 다른 DLL에 있음 → 덤프 `targets`에 그 DLL 추가 |
| `CS0012 ... 어셈블리에 정의되어 있습니다` | 참조가 빠짐 → `.csproj`에 해당 DLL `Reference` 추가 |
| `NETSDK1138 net6.0은 지원 종료` 경고 | 무시해도 됨 (MelonLoader Il2Cpp 런타임이 net6) |
| 게임에서 `TypeLoadException` | 게임 업데이트로 시그니처가 바뀜 → 다시 덤프 후 패치 대상 확인 |
