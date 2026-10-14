# 사용법 가이드 (Usage Guide)

`Modding Support Tool`을 쓰는 방법(GUI, 드래그 앤 드롭, CLI, AI 에이전트 연동, 독립 프로젝트 내보내기)을 안내합니다.

> 이 도구는 Unity(Mono/Il2Cpp) 게임에 **MelonLoader로 주입할 모드 DLL**을 만들 때 참고할 레퍼런스를 만듭니다.
> Unreal Engine 게임은 **UE4SS**, Il2Cpp 메서드 본문·네이티브 바이너리 분석은 Cpp2IL / Il2CppDumper + Ghidra·IDA 등을 쓰세요.

---

## 1. GUI

### 실행
- `Publish\ModdingSupportTool.exe`를 인자 없이 실행하거나, 개발 중엔 `dotnet run --project ModdingSupportTool`.

### 순서
1. **입력 경로 지정** — `[폴더 선택]`/`[DLL 파일 선택]` 또는 리듬게임 프리셋 버튼. (이 PC에 없는 프리셋은 비활성화되고, 마우스를 올리면 경로가 보입니다)
2. **엔진 판별 확인** — 입력을 멈추면 잠시 뒤 백그라운드에서 판별해 `Unity IL2CPP`/`Unity Mono` 배지와 근거를 보여 주고, 엔진과 입력 폴더(예: `MelonLoader/Il2CppAssemblies`, `*_Data/Managed`)를 자동으로 맞춥니다.
3. **옵션**
   - SignatureDumper: `Il2CppInterop 내부 멤버도 출력` (기본 꺼짐 — 모드 작성엔 필요 없고 출력이 두 배 가까이 커짐)
   - ilspycmd: 프로젝트 파일(`-p`), 네임스페이스 폴더 분할, **AI 아웃라인 동시 생성**
4. **[덤프 시작]** — 로그 창에 진행 상황이 나오고 끝나면 출력 폴더에 결과와 `reference.json`이 생깁니다.
   - 기본 출력: 저장소 안에서 실행하면 `<저장소>/Decompiled`, 배포본만 있으면 exe 옆 `Decompiled`

### AI 에이전트 연동 영역
- **[🤖 AI 에이전트에 설치]** — 이 PC에서 감지된 Claude Code·Antigravity·Gemini CLI에 스킬을 설치하고 MCP 서버를 등록합니다. 에이전트를 다시 시작하면 적용됩니다.
- **[연동 상태]** — 에이전트별 스킬·MCP 등록 상태를 로그에 출력합니다.
- **MCP 명령** — 다른 MCP 클라이언트에 직접 등록할 때 복사해서 쓰는 명령입니다.

---

## 2. 드래그 앤 드롭 / 더블 클릭 ([`run_dump.bat`](../run_dump.bat))

- 게임 설치 폴더나 `Assembly-CSharp.dll`을 `run_dump.bat` 위에 끌어다 놓으면 판별 후 `Decompiled/`에 덤프합니다.
- 더블 클릭하면 이 PC에 있는 첫 번째 프리셋 경로로 덤프합니다.
- `run_dump.bat [입력경로] [출력폴더] [타겟DLL목록]`으로도 실행할 수 있습니다.

---

## 3. CLI

```powershell
ModdingSupportTool.exe help
```

### 덤프
```powershell
# 새 형식
ModdingSupportTool.exe dump "H:\muse dash hwa" ".\Decompiled\MuseDash"
ModdingSupportTool.exe dump "D:\Games\MyGame" --engine ilspy --no-outline
ModdingSupportTool.exe dump "D:\Games\Il2CppGame" --keep-interop

# 예전 형식 (그대로 동작)
ModdingSupportTool.exe --cli
ModdingSupportTool.exe "H:\muse dash hwa\MelonLoader\Il2CppAssemblies" ".\MyOutput" "Assembly-CSharp.dll"
ModdingSupportTool.exe -i "H:\steam\steamapps\common\A Short Hike" -o ".\Decompiled" -t "Assembly-CSharp.dll"
ModdingSupportTool.exe --outline ".\Decompiled\Assembly-CSharp" ".\Decompiled_Outline\Assembly-CSharp"
```

### 레퍼런스 조회와 코드 생성 (`--ref <덤프 폴더>`, 생략하면 기본 출력 폴더)
```powershell
ModdingSupportTool.exe detect "H:\muse dash hwa"
ModdingSupportTool.exe refs ".\Decompiled\MuseDash"
ModdingSupportTool.exe search "PlayerController" --ref ".\Decompiled\MuseDash"
ModdingSupportTool.exe search "*Score*" --kind method --ns Il2CppAssets --ref ".\Decompiled\MuseDash"
ModdingSupportTool.exe type "AccountCanvas" --member "OnBtnLoginClick" --ref ".\Decompiled\MuseDash"
ModdingSupportTool.exe patch "AccountCanvas" "OnBtnLoginClick" --kind postfix --ref ".\Decompiled\MuseDash"
ModdingSupportTool.exe template "H:\muse dash hwa" "MyFirstMod" --author "me" --out "D:\Mods\MyFirstMod"
```

### 종료 코드
- `0`: 성공
- `1`: 입력/인자 오류 (경로 없음, 기본 경로 없음, 알 수 없는 옵션 값)
- `2`: 사용자 취소 (`Ctrl + C`)
- `3`: 덤프·도구 실패 (대상 어셈블리를 하나도 못 덤프했거나 일부 실패, `type`/`patch`의 타입·멤버를 찾지 못함 등. 검색 결과 0건은 실패가 아님)
  - 예: `ilspycmd` 미설치, Il2Cpp 게임인데 `MelonLoader/Il2CppAssemblies`가 아직 없음

출력이 파이프/파일로 리디렉션되면 그대로 그쪽으로 나갑니다. (에이전트가 실행해도 출력을 받을 수 있음)

---

## 4. AI 에이전트 연동

### 설치 (명령 한 번)
```powershell
ModdingSupportTool.exe agent install                                   # 감지된 에이전트 전부, 사용자 전역
ModdingSupportTool.exe agent install --client claude                   # claude | antigravity | gemini | all (쉼표로 여러 개)
ModdingSupportTool.exe agent install --scope project --project "D:\Mods\MyMod"
ModdingSupportTool.exe agent install --source github                   # GitHub 최신 스킬 MD를 내려받아 설치
ModdingSupportTool.exe agent install --source "https://example.com/agent"   # 같은 구조의 다른 URL
ModdingSupportTool.exe agent install --source ".\agent"                # 로컬 폴더(빌드 없이 스킬 수정 시험)
ModdingSupportTool.exe agent install --no-mcp                          # 스킬만
ModdingSupportTool.exe agent install --dry-run                         # 미리보기
ModdingSupportTool.exe agent status
ModdingSupportTool.exe agent uninstall --client claude
```

| 에이전트 | 사용자 스킬 | 프로젝트 스킬 | MCP 등록 |
| :--- | :--- | :--- | :--- |
| Claude Code | `~/.claude/skills/melonloader-mod-reference` | `.claude/skills/...` | `claude mcp add --scope user` / `.mcp.json` |
| Antigravity | `~/.gemini/config/skills/...` | `.agents/skills/...` | `~/.gemini/config/mcp_config.json` (전역만) |
| Gemini CLI 등 | `~/.agents/skills/...` | `.agents/skills/...` | `gemini mcp add --scope user` / `.gemini/settings.json` |

- 설치되는 파일: `SKILL.md`, `references/{workflow,scope,harmony-patterns,il2cpp-interop,project-setup}.md`, 설치 시 생성되는 `references/local-install.md`(exe 경로·CLI 대체 명령)
- 이미 같은 이름의 스킬이 있는데 이 도구가 설치한 것이 아니면 건드리지 않습니다(`--force`로 덮어쓰기).
- JSON 설정은 다른 항목을 그대로 두고 `modding-support-tool` 항목만 바꾸며, 처음 고칠 때 `*.mst-backup`을 남깁니다.
- **MCP에 등록되는 exe 경로**는 지금 실행 중인 exe입니다. `bin\Debug`가 아니라 `publish.bat`으로 만든 `Publish\ModdingSupportTool.exe`에서 설치하세요. (exe를 옮겼다면 다시 `agent install`)

### 플러그인으로 설치 (스킬만)
```text
/plugin marketplace add magjangin/modding-support-tool          # Claude Code
/plugin install modding-support-tool@modding-support-tool
agy plugin install <저장소>/agent                                # Antigravity
gemini skills install https://github.com/magjangin/modding-support-tool --path agent/skills/melonloader-mod-reference
```

### 에이전트에게 이렇게 말해 보세요
- "H:\muse dash hwa 게임이 Il2Cpp인지 확인하고 MelonLoader 레퍼런스를 덤프해 줘"
- "레퍼런스에서 노트 판정 메서드를 찾아서 Harmony Postfix로 판정 결과를 로그에 찍는 모드를 만들어 줘"
- "이 게임용 MelonLoader 모드 프로젝트를 만들고 빌드까지 해 줘 (Mods 복사는 묻고 해)"

에이전트는 스킬(`melonloader-mod-reference`)에 따라 판별 → 덤프 → 검색 → 패치/템플릿 → 빌드 순서로 진행하고, Unreal 게임이면 UE4SS를 안내합니다.

---

## 5. 독립 실행형 SignatureDumper 내보내기

모드 프로젝트와 분리된 가벼운 콘솔 도구가 필요할 때 씁니다.

1. GUI 오른쪽 위 `[📦 SignatureDumper 소스 내보내기]`를 누르고 빈 폴더를 고릅니다.
2. 생성 파일:
   - `SignatureDumper.csproj` (`net472;net8.0`)
   - `Program.cs` (진입점)
   - `SignatureDumper.cs` — **앱에 내장된 덤퍼 코어와 같은 파일**
   - `README.md`
3. 실행:
   ```powershell
   dotnet run --framework net8.0 -- "<어셈블리폴더>" ".\Decompiled" "Assembly-CSharp.dll" [--keep-interop]
   ```
   - Il2Cpp(MelonLoader `Il2CppAssemblies`)는 **net8.0**으로 실행하세요. net472 빌드는 .NET 6 대상인 Il2CppInterop 어셈블리를 읽을 수 없어 원인을 알려 주고 실패(종료 코드 3)합니다. net472는 Mono 게임용입니다.
   - 종료 코드: `0` 성공, `1` 인자 없음/입력 폴더 없음, `3` 덤프 실패
