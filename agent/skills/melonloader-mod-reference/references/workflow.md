# 워크플로 상세 (MCP / CLI)

모든 명령은 MCP 도구와 CLI가 같은 구현을 쓴다. CLI는 `ModdingSupportTool.exe <명령>` 형식이며
실행 파일 경로는 `references/local-install.md`에 있다. CLI 종료 코드: `0` 성공, `1` 입력 오류, `2` 취소, `3` 실패.

## 1. 판별

```powershell
& $exe detect "D:\Games\MyGame"
```

출력에서 볼 것:

- `Unity 백엔드: Il2Cpp | Mono | Unknown`
- `MelonLoader: 설치됨/설치되지 않음`
- Il2Cpp면 `Il2CppAssemblies: 있음/없음` — 없으면 MelonLoader 설치 후 게임을 한 번 실행해야 한다.
- `덤프 입력`, `덤프 엔진`, `기본 출력 폴더`

## 2. 덤프

```powershell
& $exe dump "D:\Games\MyGame" --out "D:\Refs\MyGame"
```

- MCP의 `dump_game_reference`는 `output_dir`를 생략하면 `<기본 출력>\<게임 이름>`에 쓴다.
- 기본 대상은 `Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll`. 다른 DLL이 필요하면 `targets`(CLI `--targets`).
- Mono는 `ilspycmd`가 필요하다 (`dotnet tool install -g ilspycmd`). 없으면 덤프가 실패하고 설치 안내가 나온다.
- Il2Cpp 덤프는 Il2CppInterop 내부 멤버(`NativeFieldInfoPtr_*`, `NativeMethodInfoPtr_*`, `(IntPtr pointer)` 생성자 등)를 생략한다.
  모드에선 쓰지 않는다. 꼭 필요하면 `include_interop_members=true`(CLI `--keep-interop`).

### 출력 폴더 구조

```
<출력>/
├── reference.json                    # 게임·백엔드·모듈 정보 (도구가 읽음)
├── Assembly-CSharp/                  # Il2Cpp: 시그니처 스켈레톤 / Mono: ilspycmd 전체 소스(+ .csproj)
│   └── <Namespace 경로>/<Type>.cs    # Il2Cpp 중첩 타입은 <Outer>.<Inner>.cs (파일 안에 // Nested in: Outer)
└── Assembly-CSharp-firstpass/
<출력>_Outline/                        # Mono만: 메서드 본문을 지운 아웃라인 (같은 상대 경로)
```

## 3. 탐색

```powershell
& $exe refs --ref "D:\Refs\MyGame"                                 # 네임스페이스별 타입 수
& $exe search "PlayerController" --ref "D:\Refs\MyGame"            # 타입·멤버 이름
& $exe search "*Score*" --kind method --ref "D:\Refs\MyGame"       # 와일드카드
& $exe search "note judge" --ref "D:\Refs\MyGame"                  # 여러 단어 모두 포함
& $exe search "GameManager.Instance" --ref "D:\Refs\MyGame"        # Type.Member
& $exe type "GameManager" --ref "D:\Refs\MyGame"                   # 선언 전체 (아웃라인)
& $exe type "GameManager" --member "AddScore" --ref "D:\Refs\MyGame"  # 멤버만 (Mono는 본문 포함)
```

검색 결과 한 줄 형식: `[종류] 네임스페이스.타입.멤버 — 시그니처` 다음 줄에 `상대경로:줄`.
같은 단순 이름의 타입이 여러 개면 `type`이 후보를 보여 주므로 전체 이름(`Il2CppAssets.Scripts.X`)으로 다시 부른다.

## 4. 프로젝트와 패치

```powershell
& $exe template "D:\Games\MyGame" "MyFirstMod" --author "me" --out "D:\Mods\MyFirstMod"
& $exe patch "GameManager" "AddScore" --kind postfix --ref "D:\Refs\MyGame"
```

- 생성된 패치 코드의 `namespace YourMod`를 모드 네임스페이스로 바꿔 `.cs` 파일로 저장한다.
- 오버로드가 여러 개면 오버로드마다 클래스가 하나씩 나온다. 필요 없는 것은 지운다.

## 5. 빌드·검증

```powershell
cd "D:\Mods\MyFirstMod"
dotnet build -c Release                       # bin/Release/<tfm>/MyFirstMod.dll
dotnet build -c Release -p:CopyToMods=true    # 사용자 확인 후: <게임>/Mods 로 복사
```

게임 실행 후 `<게임>/MelonLoader/Latest.log`에서 확인:

- `Melon Assembly loaded: '.\Mods\MyFirstMod.dll'` → `MyFirstMod v1.0.0` → `by <작성자>` → `N Mod loaded.` 순서로 나오면 로드 성공
- 모드의 `LoggerInstance.Msg` 출력은 `[MyFirstMod] 메시지` 형태로 찍힌다
- `[ERROR]` / `HarmonyException` / `MissingMethodException` / `TypeLoadException` 등 예외
  - 모드가 목록에 없으면 `MelonGame` 회사명·게임명이 `<게임>_Data/app.info`와 같은지 확인한다

## MCP 도구 인자 요약

| 도구 | 필수 | 선택 |
| :--- | :--- | :--- |
| `detect_unity_backend` | `path` | |
| `dump_game_reference` | `game_path` | `output_dir`, `targets`, `engine`(auto/signature/ilspy), `include_interop_members`, `outline` |
| `describe_reference` | | `reference_dir` |
| `search_reference` | `query` | `kind`(all/type/member/method/property/field), `namespace`, `limit`, `reference_dir` |
| `read_reference_type` | `type` | `member`, `include_bodies`, `max_chars`, `reference_dir` |
| `generate_harmony_patch` | `type`, `method` | `patch`(both/prefix/postfix), `reference_dir` |
| `generate_melon_mod_project` | `game_path`, `mod_name` | `author`, `output_dir`, `overwrite` |

`reference_dir`를 생략하면: 이번 세션의 마지막 덤프 → `mcp --ref` 값 → 환경 변수 `MST_REFERENCE_DIR` → 기본 출력 폴더 순으로 찾는다.
기본 출력 폴더 아래에 게임별 레퍼런스가 여러 개면 목록을 보여 주므로 하나를 골라 `reference_dir`로 넘긴다.
