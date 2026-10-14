# Harmony 패치 패턴 (MelonLoader = HarmonyX)

MelonLoader는 HarmonyX(`MelonLoader/net6|net35/0Harmony.dll`)를 쓰고, 모드 어셈블리 안의 `[HarmonyPatch]` 클래스를
**자동으로 적용**한다. 직접 적용하려면 `[assembly: HarmonyDontPatchAll]`을 붙이고 `HarmonyInstance.PatchAll()` 등을 호출한다.

`generate_harmony_patch`가 레퍼런스의 실제 시그니처로 아래 형태를 만들어 준다. 직접 쓸 때도 이름·타입은 레퍼런스에서 확인한다.

## 기본형

```csharp
using HarmonyLib;

[HarmonyPatch(typeof(ScoreManager), nameof(ScoreManager.AddScore))]
internal static class ScoreManager_AddScore_Patch
{
    // 원본 전에 실행. false를 반환하면 원본을 건너뛴다 (다른 Prefix·Postfix는 실행됨).
    private static bool Prefix(ScoreManager __instance, ref int amount)
    {
        amount *= 2;          // ref 로 받으면 인자를 바꿀 수 있다
        return true;
    }

    // 원본 후에 실행. __result로 반환값을 읽고 바꾼다.
    private static void Postfix(ScoreManager __instance, int amount, ref bool __result)
    {
        __result = true;
    }
}
```

## 주입 인자 이름 (이름으로 매칭된다)

| 인자 | 뜻 |
| :--- | :--- |
| `__instance` | 대상 인스턴스 (static 메서드엔 없음) |
| `__result` | 반환값. 바꾸려면 `ref` |
| `__state` | Prefix에서 `out T __state`로 만든 값을 Postfix에서 `T __state`로 받음 |
| 원래 파라미터 이름 | 같은 이름으로 받음. 바꾸려면 `ref` |
| `___필드명` (언더바 3개) | private 필드 주입 (**Mono만**, 바꾸려면 `ref`) |
| `__originalMethod` | 패치된 `MethodBase` |
| `__exception` | Finalizer에서 원본이 던진 예외 |

## 대상 지정이 까다로울 때

```csharp
// 오버로드: 인자 타입 배열로 고른다 (ref/out은 ArgumentType으로)
[HarmonyPatch(typeof(Note), nameof(Note.Judge), new[] { typeof(float), typeof(int) }, new[] { ArgumentType.Normal, ArgumentType.Ref })]

// 프로퍼티
[HarmonyPatch(typeof(Player), nameof(Player.Speed), MethodType.Getter)]
[HarmonyPatch(typeof(Player), nameof(Player.Speed), MethodType.Setter)]   // 새 값은 'value' 인자

// 생성자 (파라미터가 있으면 타입 배열 필수)
[HarmonyPatch(typeof(Player), MethodType.Constructor, new[] { typeof(string) })]

// private 메서드 (Mono): nameof 대신 문자열
[HarmonyPatch(typeof(Player), "UpdateInternal")]

// 접근할 수 없는 타입·제네릭 메서드: TargetMethod로 직접 반환
[HarmonyPatch]
internal static class Hidden_Patch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("Game.Internal.Hidden"), "Run", new[] { typeof(int) });

    private static void Postfix(object __instance) { }
}

// 제네릭 메서드는 닫힌 형태만 패치 가능
private static MethodBase TargetMethod() =>
    AccessTools.Method(typeof(Pool), nameof(Pool.Get)).MakeGenericMethod(typeof(Bullet));
```

## 필드·메서드 리플렉션 (Mono)

```csharp
var field = AccessTools.Field(typeof(Player), "hp");
int hp = (int)field.GetValue(player);

int hp2 = Traverse.Create(player).Field("hp").GetValue<int>();
Traverse.Create(player).Method("Heal", 10).GetValue();
```

## 수동 패치

```csharp
public override void OnInitializeMelon()
{
    var original = AccessTools.Method(typeof(Player), nameof(Player.Die));
    var prefix = new HarmonyMethod(typeof(Core), nameof(BeforeDie));
    HarmonyInstance.Patch(original, prefix: prefix);
}

private static bool BeforeDie() => false; // 죽지 않게
```

## Il2Cpp에서 달라지는 점

- 패치는 Il2CppInterop가 네이티브 메서드에 연결한다. **Transpiler는 쓸 수 없다** (IL이 없음).
- 아주 짧아 컴파일러가 인라인한 메서드는 Prefix/Postfix가 호출되지 않을 수 있다 → 호출하는 쪽 메서드를 패치한다.
- 필드는 프로퍼티로 노출되므로 `___필드` 주입 대신 `__instance.필드명`을 읽고 쓴다.
- `__instance`, 파라미터는 Il2CppInterop 프록시 객체다. 하위 타입으로 바꿀 땐 `TryCast<T>()`를 쓴다 (C# `as`/`is` 대신).
- 매 프레임 호출되는 메서드(`Update` 등)에 무거운 패치를 걸면 네이티브↔관리 전환 비용으로 프레임이 떨어진다.

## 자주 나는 오류

| 증상 | 원인 / 해결 |
| :--- | :--- |
| `HarmonyException: Patching exception ... Undefined target method` | 이름·오버로드가 틀림 → `search_reference`로 시그니처 확인, 인자 타입 배열 지정 |
| `Parameter "x" not found` | 패치 인자 이름이 원본과 다름 (이름으로 매칭됨) |
| Prefix가 호출되지 않음 (Il2Cpp) | 인라인된 메서드 → 상위 호출자 패치 |
| `InvalidCastException` (Il2Cpp) | C# 캐스트 대신 `obj.TryCast<T>()` / `Cast<T>()` |
