# 코드 스니펫 (Code Snippets)

Modding Support Tool을 쓰거나 고칠 때 바로 참고할 수 있는 코드 조각 모음입니다. 동작 원리는 [core-logic.md](core-logic.md)에 있습니다.
아래 출력 예시는 Muse Dash(Il2Cpp)와 KALPA(Mono)로 실제 실행한 결과를 줄인 것입니다.

> 이 도구는 **MelonLoader로 주입할 모드 DLL**을 만들 때 쓰는 레퍼런스 도구입니다. Unreal Engine 게임은 UE4SS, 네이티브 리버싱은 Ghidra/IDA 등을 쓰세요.

목차

1. [CLI](#1-cli)
2. [MCP 연결 설정](#2-mcp-연결-설정)
3. [MCP JSON-RPC 주고받기](#3-mcp-json-rpc-주고받기)
4. [출력물 예시](#4-출력물-예시)
5. [생성되는 모드 코드](#5-생성되는-모드-코드)
6. [내부 구현 핵심 코드](#6-내부-구현-핵심-코드)
7. [확장하기](#7-확장하기)

---

## 1. CLI

```powershell
$exe = ".\Publish\ModdingSupportTool.exe"

# 덤프 (예전 형식도 그대로 동작: [입력] [출력] [대상], -i/-o/-t, --cli, --outline)
& $exe dump "H:\muse dash hwa" ".\Decompiled\MuseDash"
& $exe dump "D:\Games\MonoGame" ".\Decompiled\MonoGame" --no-outline
& $exe dump "D:\Games\Il2CppGame" --keep-interop            # Il2CppInterop 내부 멤버까지

# 레퍼런스 조회
& $exe detect "H:\muse dash hwa"
& $exe refs   ".\Decompiled\MuseDash"
& $exe search "AccountCanvas.OnBtnLoginClick" --ref ".\Decompiled\MuseDash"
& $exe search "*Score*" --kind method --ns Il2CppAssets --limit 20 --ref ".\Decompiled\MuseDash"
& $exe type   "KalpaPlayerInputManager" --member "InputInvoke" --ref ".\Decompiled\KALPA"

# 코드 생성
& $exe patch    "AccountCanvas" "OnBtnLoginClick" --kind postfix --ref ".\Decompiled\MuseDash"
& $exe template "H:\muse dash hwa" "MyFirstMod" --author "me" --out "D:\Mods\MyFirstMod"

# 에이전트 연동
& $exe agent install                                   # 감지된 에이전트 전부, 사용자 전역
& $exe agent install --client claude,antigravity --scope project --project "D:\Mods\MyFirstMod"
& $exe agent install --source github                   # GitHub 최신 스킬 MD 다운로드
& $exe agent install --dry-run                         # 무엇을 쓸지 미리보기
& $exe agent status
& $exe agent uninstall --client claude
```

종료 코드: `0` 성공 · `1` 입력/인자 오류 · `2` 취소 · `3` 덤프·도구 실패. 에이전트가 파이프로 실행해도 출력은 그 파이프로 나옵니다.

---

## 2. MCP 연결 설정

`agent install`이 아래 설정을 대신 써 줍니다. 직접 등록할 때의 형태입니다. (`args`의 `--ref`는 선택)

**Claude Code** — 사용자 전역

```powershell
claude mcp add --scope user modding-support-tool -- "D:\Tools\ModdingSupportTool.exe" mcp
```

**Claude Code** — 프로젝트 `.mcp.json`

```json
{
  "mcpServers": {
    "modding-support-tool": {
      "type": "stdio",
      "command": "D:\\Tools\\ModdingSupportTool.exe",
      "args": ["mcp", "--ref", "D:\\Refs\\MuseDash"]
    }
  }
}
```

**Antigravity** — `~/.gemini/config/mcp_config.json` (또는 `agy mcp add modding-support-tool "D:\Tools\ModdingSupportTool.exe" mcp`)

```json
{
  "mcpServers": {
    "modding-support-tool": { "command": "D:\\Tools\\ModdingSupportTool.exe", "args": ["mcp"] }
  }
}
```

**Gemini CLI** — `gemini mcp add --scope user modding-support-tool "D:\Tools\ModdingSupportTool.exe" mcp`

---

## 3. MCP JSON-RPC 주고받기

한 줄에 JSON 메시지 하나(stdio). 클라이언트 → 서버:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"demo","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"dump_game_reference","arguments":{"game_path":"H:\\muse dash hwa"},"_meta":{"progressToken":"dump-1"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_reference","arguments":{"query":"OnBtnLoginClick","kind":"method"}}}
```

서버 → 클라이언트 (요약):

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"modding-support-tool","version":"1.1.0"},"instructions":"..."}}
{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"dump-1","progress":12,"message":"[내장 SignatureDumper] 4157개 타입 정적 분석 시작..."}}
{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"'OnBtnLoginClick' 검색 결과 1건 ...\n[method] Il2Cpp.AccountCanvas.OnBtnLoginClick  — public void OnBtnLoginClick()\n    Assembly-CSharp\\Il2Cpp\\AccountCanvas.cs:165"}],"isError":false}}
```

취소: `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}`

### 최소 테스트 클라이언트 (Python)

```python
import json, subprocess
p = subprocess.Popen([r"D:\Tools\ModdingSupportTool.exe", "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)

def rpc(i, method, params=None):
    p.stdin.write((json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params or {}}) + "\n").encode())
    p.stdin.flush()
    while True:
        msg = json.loads(p.stdout.readline())
        if msg.get("id") == i:
            return msg

print(rpc(1, "initialize", {"protocolVersion": "2025-06-18"})["result"]["serverInfo"])
print([t["name"] for t in rpc(2, "tools/list")["result"]["tools"]])
print(rpc(3, "tools/call", {"name": "detect_unity_backend", "arguments": {"path": r"H:\muse dash hwa"}})["result"]["content"][0]["text"])
p.stdin.close()
```

---

## 4. 출력물 예시

### 4.1 Il2Cpp 스켈레톤 — Il2CppInterop 내부 멤버 생략 전/후

```csharp
// 이전 (모든 멤버 출력)
namespace Il2Cpp
{
    // Nested in: AccountCanvas
    public sealed class MDAccountProtocolData : Il2CppSystem.ValueType
    {
        // Fields
        private static readonly System.IntPtr NativeFieldInfoPtr_isFirstAgree;
        private static readonly System.IntPtr NativeFieldInfoPtr_isAgree;
        ...
        // Constructors
        static MDAccountProtocolData();
        public MDAccountProtocolData(System.IntPtr pointer);
        public MDAccountProtocolData();
    }
}
```

```csharp
// 이후 (기본값) — 모드에서 쓰는 것만 남는다
using Il2CppSystem;

namespace Il2Cpp
{
    // Nested in: AccountCanvas
    public sealed class MDAccountProtocolData : ValueType
    {
        // Properties
        public bool isFirstAgree { get; set; }
        public bool isAgree { get; set; }
        public string agreementVersion { get; set; }
        public bool isAgreeChange { get; set; }

        // Constructors
        public MDAccountProtocolData();
    }
}
```

`System.IntPtr` 참조가 사라지면서 `using System;`도 빠지고, `ValueType`이 더는 모호하지 않아 짧게 표기됩니다.

### 4.2 Mono AI 아웃라인 — 원본 / 이전 / 이후

```csharp
// ilspycmd 원본
public static bool IsPreventInput => singleton.isPreventInput;

private void Awake()
{
    singleton = this;
}
```

```csharp
// 이전 아웃라인: 스텁이 두 줄로 쪼개지고 접근자에 공백이 없음
public static bool IsPreventInput {get;}
private void Awake()
 {/* ... */ }
```

```csharp
// 이후 아웃라인: 스켈레톤과 같은 "선언;" 형태
// [AI outline] 메서드 본문 생략됨 · 전체 소스는 _Outline이 없는 폴더의 같은 경로
public static bool IsPreventInput { get; }

private void Awake();
```

### 4.3 `reference.json`

```json
{
  "schema": 1,
  "tool": "ModdingSupportTool",
  "toolVersion": "1.1.0",
  "purpose": "MelonLoader 모드(DLL) 작성용 C# 레퍼런스",
  "gameName": "MuseDash",
  "gameRoot": "H:\\muse dash hwa",
  "company": "PeroPeroGames",
  "product": "MuseDash",
  "backend": "Il2Cpp",
  "modules": [
    {
      "name": "Assembly-CSharp",
      "engine": "SignatureDumper",
      "source": "H:\\muse dash hwa\\MelonLoader\\Il2CppAssemblies\\Assembly-CSharp.dll",
      "path": "Assembly-CSharp",
      "hasMethodBodies": false,
      "files": 4157,
      "omittedInteropMembers": 52438
    }
  ]
}
```

### 4.4 검색 결과

```text
'KalpaPlayerInputManager' 검색 결과 8건 · 레퍼런스 D:\Refs\KALPA (경로는 이 폴더 기준)
[class] KalpaCosmicSymphony.KalpaPlayerInputManager  — public class KalpaPlayerInputManager : MonoBehaviour
    Assembly-CSharp\KalpaCosmicSymphony\KalpaPlayerInputManager.cs:14 · 멤버 99개
[method] KalpaCosmicSymphony.KalpaPlayerInputManager.InputInvoke  — private void InputInvoke(NewInputActionType newInputActionType, InputAction.CallbackContext context)
    Assembly-CSharp\KalpaCosmicSymphony\KalpaPlayerInputManager.cs:48
```

---

## 5. 생성되는 모드 코드

### 5.1 Harmony 패치 — Il2Cpp (public, `nameof`)

```csharp
// 대상: Il2Cpp.AccountCanvas → public void OnBtnLoginClick()
[HarmonyPatch(typeof(AccountCanvas), nameof(AccountCanvas.OnBtnLoginClick))]
internal static class AccountCanvas_OnBtnLoginClick_Patch
{
    // 원본 실행 전에 호출됩니다. false를 반환하면 원본을 건너뜁니다.
    private static bool Prefix(AccountCanvas __instance)
    {
        return true;
    }

    // 원본 실행 후에 호출됩니다.
    private static void Postfix(AccountCanvas __instance)
    {
    }
}
```

### 5.2 Harmony 패치 — Mono (private 메서드, 문자열 이름 + 원래 파라미터)

```csharp
// 대상: KalpaCosmicSymphony.KalpaPlayerInputManager → private void InputInvoke(NewInputActionType newInputActionType, InputAction.CallbackContext context)
[HarmonyPatch(typeof(KalpaPlayerInputManager), "InputInvoke")]
internal static class KalpaPlayerInputManager_InputInvoke_Patch
{
    private static bool Prefix(KalpaPlayerInputManager __instance, NewInputActionType newInputActionType, InputAction.CallbackContext context)
    {
        return true;
    }

    private static void Postfix(KalpaPlayerInputManager __instance, NewInputActionType newInputActionType, InputAction.CallbackContext context)
    {
    }
}
```

### 5.3 오버로드·ref·프로퍼티·생성자일 때 붙는 속성

```csharp
[HarmonyPatch(typeof(Note), nameof(Note.Judge), new Type[] { typeof(float), typeof(int) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref })]
[HarmonyPatch(typeof(Player), nameof(Player.Speed), MethodType.Getter)]      // Postfix(Player __instance, ref float __result)
[HarmonyPatch(typeof(Player), MethodType.Constructor, new Type[] { typeof(string) })]

// 접근할 수 없는 타입
[HarmonyPatch]
internal static class Hidden_Run_Patch
{
    private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("Game.Outer+Hidden"), "Run", new Type[] { typeof(int) });
    private static void Postfix(object __instance, int count) { }
}
```

### 5.4 모드 프로젝트 (Il2Cpp)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>TestMod</AssemblyName>
    <GameDir>H:\muse dash hwa</GameDir>
    <MelonLoaderDir>$(GameDir)\MelonLoader</MelonLoaderDir>
    <CopyToMods>false</CopyToMods>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="$(MelonLoaderDir)\net6\MelonLoader.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\net6\0Harmony.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\net6\Il2CppInterop.Runtime.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\net6\Il2CppInterop.Common.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\Il2CppAssemblies\Il2Cpp*.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\Il2CppAssemblies\UnityEngine*.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\Il2CppAssemblies\Unity.*.dll" Private="false" />
    <Reference Include="$(MelonLoaderDir)\Il2CppAssemblies\Assembly-CSharp*.dll" Private="false" />
  </ItemGroup>
  <Target Name="CopyToMods" AfterTargets="Build" Condition="'$(CopyToMods)' == 'true'">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameDir)\Mods" />
  </Target>
</Project>
```

Mono(KALPA)는 `net472` + `MelonLoader\net472\*.dll` + `KALPA CosmicSymphony_Data\Managed\{UnityEngine*,Unity.*,Assembly-CSharp*}.dll`로 바뀝니다.

```csharp
using MelonLoader;

[assembly: MelonInfo(typeof(TestMod.Core), "TestMod", "1.0.0", "tester")]
[assembly: MelonGame("PeroPeroGames", "MuseDash")]

namespace TestMod
{
    public sealed class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("TestMod 로드됨");
        }
    }
}
```

---

## 6. 내부 구현 핵심 코드

### 6.1 DLL을 잠그지 않는 로드 + 의존성 캐시 (`BuiltinSignatureDumper`)

```csharp
var resolved = new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);
var alc = new AssemblyLoadContext($"SignatureDumper_{Guid.NewGuid()}", isCollectible: true);
alc.Resolving += (context, name) => ResolveDependency(name.Name, dependencyDirs, resolved, path => LoadFromStream(context, path));
try
{
    return DumpLoaded(LoadFromStream(alc, assemblyPath), ct);
}
finally
{
    Reset();
    alc.Unload();
}

static Assembly LoadFromStream(AssemblyLoadContext context, string path)
{
    using var stream = File.OpenRead(path);   // 스트림으로 올리면 파일 핸들을 쥐지 않는다
    return context.LoadFromStream(stream);
}
```

### 6.2 Il2CppInterop 배관 멤버 걸러내기

```csharp
private static bool IsInteropPlumbingField(FieldInfo f) =>
    f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal) ||
    f.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal);

private static bool IsInteropPlumbingConstructor(ConstructorInfo c)
{
    if (c.IsStatic) return true;
    var parameters = c.GetParameters();
    return parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr);
}
```

### 6.3 병렬 분석, 순차 경로 결정, 병렬 쓰기

```csharp
var analyzed = new TypeSignatureInfo?[sorted.Length];
Parallel.For(0, sorted.Length, parallel, i => analyzed[i] = AnalyzeType(sorted[i]));   // 순서 보존

for (int i = 0; i < infos.Count; i++)                                                   // 대소문자 충돌 접미사는 순서 의존
{
    string filePath = Path.Combine(targetDir, safeFileName + ".cs");
    for (int suffix = 2; !usedPaths.Add(filePath); suffix++)
        filePath = Path.Combine(targetDir, $"{safeFileName}_{suffix}.cs");
    filePaths[i] = filePath;
}

Parallel.For(0, infos.Count, parallel, i =>
{
    string content = RenderType(infos[i]);
    File.WriteAllText(filePaths[i], content, Utf8NoBom);
    fileWritten?.Invoke(filePaths[i], content);   // 덤프 직후 색인용
});
```

### 6.4 아웃라인: 본문을 방문하지 않고 `;`로 바꾸기 (`RoslynOutliner`)

```csharp
public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => StubBody(node);

private static SyntaxNode StubBody(BaseMethodDeclarationSyntax node)
{
    if (node.Body == null && node.ExpressionBody == null) return node;

    var trailing = node.GetLastToken().TrailingTrivia;   // 선언 끝 줄바꿈은 ; 뒤로 옮긴다
    var trimmed = TrimBeforeBody(node, node.Body?.OpenBraceToken ?? node.ExpressionBody!.ArrowToken);
    return trimmed
        .WithBody(null)
        .WithExpressionBody(null)
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(trailing));
}
```

### 6.5 덤프 직후 메모리 색인

```csharp
var request = new DumpRequest { /* ... */ CaptureWrittenSources = true };
var result = await _dumpService.RunDumpAsync(request, Log, ct);
var index = ReferenceIndex.Load(output, ct, result.WrittenSources);   // 디스크 재읽기 없음

// ReferenceIndex.Build 안
string text = preloaded != null && preloaded.TryGetValue(parsePath, out var cached) ? cached : File.ReadAllText(parsePath);
```

### 6.6 GUI 로그 묶음 갱신

```csharp
private void AppendLog(string message) => _pendingLog.Enqueue(message);   // 아무 스레드에서나

private void FlushLog()   // DispatcherTimer 100ms
{
    if (_pendingLog.IsEmpty) return;
    while (_pendingLog.TryDequeue(out var line))
    {
        if (_logBuffer.Length > 0) _logBuffer.Append('\n');
        _logBuffer.Append(line);
    }
    if (_logBuffer.Length > MaxLogChars) { /* 앞부분을 줄 단위로 잘라 낸다 */ }
    LogOutput = _logBuffer.ToString();
}
```

### 6.7 경로 입력 디바운스 + 백그라운드 판별

```csharp
partial void OnInputPathChanged(string value)
{
    if (_suppressDetection) return;
    _detectTask = DetectBackendAsync(value, debounce: true);
}

private async Task DetectBackendAsync(string path, bool debounce)
{
    _detectCts?.Cancel();
    var cts = _detectCts = new CancellationTokenSource();
    try
    {
        if (debounce) await Task.Delay(300, cts.Token);
        var plan = await Task.Run(() => DumpService.Plan(path, null, cts.Token), cts.Token);
        if (!cts.IsCancellationRequested) ApplyDetection(plan);
    }
    catch (OperationCanceledException) { /* 더 새 입력이 있음 */ }
}
```

### 6.8 설정 파일 병합 (다른 키 보존 + 백업 + 원자적 교체)

```csharp
var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
servers["modding-support-tool"] = new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp") };
root["mcpServers"] = servers;

if (File.Exists(file) && !File.Exists(file + ".mst-backup")) File.Copy(file, file + ".mst-backup");
File.WriteAllText(file + ".mst-tmp", root.ToJsonString(IndentedJson));
File.Move(file + ".mst-tmp", file, overwrite: true);
```

---

## 7. 확장하기

### MCP 도구 추가 (`Agent/Mcp/McpTools.cs`)

1. `ModdingToolkit`에 `ToolResult`를 돌려주는 메서드를 추가합니다. (CLI와 MCP가 같이 씁니다)
2. `McpTools.Create`에 항목을 추가합니다.

```csharp
new()
{
    Name = "list_mod_folder",
    Title = "Mods 폴더 보기",
    Description = "게임 Mods 폴더의 DLL 목록을 보여 줍니다.",
    ReadOnly = true,
    Properties = new[] { new McpProperty("game_path", "string", "게임 설치 폴더", Required: true) },
    Handler = (a, _) => Task.FromResult(toolkit.ListMods(a.Required("game_path")))
}
```

3. 필요하면 `CliRunner`에 같은 이름의 하위 명령을 추가하고, 스킬의 `references/workflow.md` 표를 갱신합니다.

### 에이전트(클라이언트) 추가 (`Agent/AgentInstaller.cs`)

1. `AgentClient` 열거형에 값 추가, `DisplayName`·`TryParseClients`에 이름 추가
2. `SkillsRoot`에 사용자/프로젝트 스킬 폴더 규칙 추가
3. `RegisterMcpAsync`/`UninstallAsync`/`PrintStatus`에 MCP 설정 위치 추가 (공식 CLI가 있으면 `RegisterWithCliAsync`, 작은 JSON 파일이면 `MergeMcpEntry`)

### 스킬 문서 고치기

`agent/skills/melonloader-mod-reference/` 아래 MD를 고치면 다음 빌드부터 exe에 내장됩니다. 빌드 없이 바로 시험하려면:

```powershell
& $exe agent install --source ".\agent" --scope project --project "D:\Mods\Test"
```
