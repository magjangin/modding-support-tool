# Il2Cpp 게임 모딩 메모 (MelonLoader 0.6+ / Il2CppInterop)

Il2Cpp 게임은 C# 코드가 네이티브(`GameAssembly.dll`)로 컴파일되어 있다. MelonLoader는 첫 실행 때
`MelonLoader/Il2CppAssemblies`에 **프록시(더미) 어셈블리**를 만들고, 모드는 이 프록시를 통해 네이티브 객체를 다룬다.
그래서 이 도구의 Il2Cpp 레퍼런스에는 **시그니처만 있고 메서드 본문이 없다.** 로직을 봐야 하면 Cpp2IL/Ghidra 같은 별도 도구가 필요하다.

## 이름 규칙

- MelonLoader 0.6+는 게임 쪽 네임스페이스 앞에 `Il2Cpp`를 붙인다. 네임스페이스가 없던 타입은 `Il2Cpp` 네임스페이스에 들어간다.
  예: 원래 `Assets.Scripts.UI.PnlMain` → `Il2CppAssets.Scripts.UI.PnlMain`, 원래 전역 `GameManager` → `Il2Cpp.GameManager`
- BCL 대응 타입은 `Il2CppSystem.*` (예: `Il2CppSystem.Collections.Generic.List<T>`, `Il2CppSystem.Object`)
- `UnityEngine.*`는 이름이 그대로다 (`UnityEngine.GameObject`).
- **레퍼런스에 나온 이름을 그대로 쓴다.** 덤프는 `System`/`Il2CppSystem`처럼 겹치는 이름에 네임스페이스를 붙여 준다.

## 캐스팅

```csharp
using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase

// C#의 as / is / (T) 캐스트는 프록시 타입 계층을 모른다 → TryCast / Cast
var enemy = component.TryCast<Il2Cpp.Enemy>();   // 실패하면 null
if (enemy != null) { /* ... */ }
var boss = obj.Cast<Il2Cpp.Boss>();             // 실패하면 예외
```

## 문자열·컬렉션

- `string` 파라미터/반환값은 자동 변환된다.
- `Il2CppSystem.Collections.Generic.List<T>` / `Dictionary<K,V>`는 `Count`·인덱서·`Add`·`ContainsKey`를 쓴다.
  `System.Linq`는 바로 안 되므로 for 루프로 돈다.

```csharp
var notes = chart.notes;                       // Il2CppSystem.Collections.Generic.List<NoteData>
for (int i = 0; i < notes.Count; i++)
{
    var note = notes[i];
}
```

- 배열은 `Il2CppReferenceArray<T>`(참조형) / `Il2CppStructArray<T>`(값형) 등으로 나온다. `Length`와 인덱서를 쓴다.

## 델리게이트·이벤트

```csharp
using Il2CppInterop.Runtime;   // DelegateSupport

// 관리 델리게이트 → Il2Cpp 델리게이트
button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new System.Action(OnClick)));
// 많은 경우 아래처럼 System.Action에서 암시적 변환도 된다
button.onClick.AddListener((System.Action)OnClick);
```

## 직접 만든 MonoBehaviour 붙이기

```csharp
using System;
using MelonLoader;
using UnityEngine;

[RegisterTypeInIl2Cpp]                       // MelonLoader가 Il2Cpp 도메인에 등록 (또는 ClassInjector.RegisterTypeInIl2Cpp<T>())
public class ScoreOverlay : MonoBehaviour
{
    public ScoreOverlay(IntPtr ptr) : base(ptr) { }   // Il2Cpp가 객체를 만들 때 쓰는 생성자 (필수)

    private void Update() { }
}

// 사용
var go = new GameObject("ScoreOverlay");
UnityEngine.Object.DontDestroyOnLoad(go);
go.AddComponent<ScoreOverlay>();
```

- 주입 클래스의 필드는 Il2Cpp 쪽에서 보이지 않는다(인스펙터·직렬화 안 됨). 관리 코드 안에서만 쓴다.

## 코루틴

```csharp
using System.Collections;
MelonCoroutines.Start(Blink());

private IEnumerator Blink()
{
    yield return new WaitForSeconds(1f);   // UnityEngine.WaitForSeconds
}
```

## 객체 찾기

```csharp
var manager = UnityEngine.Object.FindObjectOfType<Il2Cpp.GameManager>();
var all = Resources.FindObjectsOfTypeAll<Il2Cpp.Enemy>();
var go = GameObject.Find("Canvas/PnlMain");
```

- 싱글턴(`Instance` 정적 프로퍼티)이 있으면 그게 가장 싸다 → `search_reference "Instance" --kind property`.

## 주의

- Il2Cpp 객체는 네이티브 GC가 관리한다. 오래 들고 있을 관리 참조는 씬 전환 후 파괴(`== null`)됐는지 확인한다.
- `Update`처럼 매 프레임 불리는 곳에서 `FindObjectOfType`을 반복 호출하지 않는다 (씬 로드 때 한 번 찾아 캐시).
- 게임이 새 Input System만 쓰면 `UnityEngine.Input.GetKeyDown`이 동작하지 않을 수 있다 → 레퍼런스에서 `InputSystem` 사용 여부 확인.
