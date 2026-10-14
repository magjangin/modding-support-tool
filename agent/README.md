# Modding Support Tool — AI 에이전트 플러그인

Unity(Mono/Il2Cpp) 게임에 **MelonLoader로 주입할 모드 DLL을 만들 때**, 에이전트가 게임 코드의 실제 타입·메서드
시그니처를 확인하며 작업하도록 돕는 스킬 묶음입니다. Unreal Engine 게임은 이 도구 대신 **UE4SS**를 쓰세요.

- 스킬: [`skills/melonloader-mod-reference`](skills/melonloader-mod-reference/SKILL.md)
- MCP 서버: `ModdingSupportTool.exe mcp` (도구 7개: 판별·덤프·개요·검색·선언 읽기·Harmony 패치·모드 프로젝트)

## 설치

가장 쉬운 방법은 exe가 직접 설치하는 것입니다. 스킬 MD 파일을 각 에이전트 스킬 폴더에 쓰고 MCP 서버도 등록합니다.

```powershell
ModdingSupportTool.exe agent install                        # 이 PC에서 감지된 에이전트 전부 (사용자 전역)
ModdingSupportTool.exe agent install --client claude        # Claude Code만
ModdingSupportTool.exe agent install --client antigravity   # Antigravity만
ModdingSupportTool.exe agent install --scope project --project D:\Mods\MyMod   # 프로젝트 폴더에만
ModdingSupportTool.exe agent install --source github        # GitHub의 최신 스킬 파일을 내려받아 설치
ModdingSupportTool.exe agent status                         # 설치 상태
```

| 에이전트 | 사용자 스킬 폴더 | 프로젝트 스킬 폴더 | MCP 등록 |
| :--- | :--- | :--- | :--- |
| Claude Code | `~/.claude/skills/` | `.claude/skills/` | `claude mcp add --scope user` / `.mcp.json` |
| Antigravity (agy) | `~/.gemini/config/skills/` | `.agents/skills/` | `~/.gemini/config/mcp_config.json` |
| Gemini CLI 등 (Agent Skills) | `~/.agents/skills/` | `.agents/skills/` | `gemini mcp add --scope user` / `.gemini/settings.json` |

플러그인으로 설치할 수도 있습니다. (이 경우 스킬만 들어가므로 MCP는 위의 `agent install`로 따로 등록합니다)

```text
# Claude Code
/plugin marketplace add magjangin/modding-support-tool
/plugin install modding-support-tool@modding-support-tool

# Antigravity CLI (저장소를 받은 뒤 이 폴더 지정)
agy plugin install <저장소>/agent

# Gemini CLI
gemini skills install https://github.com/magjangin/modding-support-tool --path agent/skills/melonloader-mod-reference
```
