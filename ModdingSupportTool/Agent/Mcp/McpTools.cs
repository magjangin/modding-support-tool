using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ModdingSupportTool.Agent.Mcp;

public sealed record McpCallContext(CancellationToken CancellationToken, Action<string>? Progress);

public sealed record McpProperty(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<McpProperty> Properties { get; init; }
    public bool ReadOnly { get; init; }
    public required Func<McpArguments, McpCallContext, Task<ToolResult>> Handler { get; init; }

    public JsonObject ToJson()
    {
        var properties = new JsonObject();
        foreach (var p in Properties)
        {
            var schema = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null) schema["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)e).ToArray());
            properties[p.Name] = schema;
        }

        return new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(Properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray())
            },
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = ReadOnly,
                ["openWorldHint"] = false
            }
        };
    }
}

/// <summary>tools/call의 arguments를 타입별로 꺼낸다. (클라이언트가 숫자·불리언을 문자열로 보내도 받아 준다)</summary>
public sealed class McpArguments
{
    private readonly JsonObject _args;

    public McpArguments(JsonObject args) => _args = args;

    public string? String(string name)
    {
        var node = _args[name];
        if (node == null) return null;
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    public string Required(string name) =>
        String(name) is { Length: > 0 } value ? value : throw new McpException(-32602, $"필수 인자 누락: {name}");

    public bool Bool(string name, bool fallback)
    {
        var node = _args[name];
        if (node == null) return fallback;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(node.GetValue<string>(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    public int Int(string name, int fallback)
    {
        var node = _args[name];
        if (node == null) return fallback;
        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>() is var d ? (int)d : fallback,
            JsonValueKind.String => int.TryParse(node.GetValue<string>(), out var i) ? i : fallback,
            _ => fallback
        };
    }
}

internal static class McpTools
{
    private const string RefDirDescription = "레퍼런스(덤프) 폴더. 생략하면 마지막 덤프 폴더 또는 기본 출력 폴더를 씁니다.";

    public static IReadOnlyList<McpTool> Create(ModdingToolkit toolkit) => new List<McpTool>
    {
        new()
        {
            Name = "detect_unity_backend",
            Title = "Unity 백엔드 판별",
            Description = "게임 폴더/DLL이 Unity Mono인지 Il2Cpp인지, MelonLoader와 Il2CppAssemblies가 준비됐는지, 덤프에 쓸 입력 폴더와 엔진을 알려 줍니다. " +
                          "Unity가 아니면(Unreal 등) UE4SS 같은 다른 도구를 안내합니다. 모딩 작업의 첫 단계로 호출하세요.",
            ReadOnly = true,
            Properties = new[] { new McpProperty("path", "string", "게임 설치 폴더, *_Data, Managed, MelonLoader/Il2CppAssemblies 또는 DLL 경로", Required: true) },
            Handler = (a, _) => Task.FromResult(toolkit.Detect(a.Required("path")))
        },
        new()
        {
            Name = "dump_game_reference",
            Title = "게임 레퍼런스 덤프",
            Description = "게임 어셈블리(Assembly-CSharp 등)를 C# 레퍼런스로 덤프합니다. Il2Cpp는 MelonLoader가 만든 Il2CppAssemblies에서 시그니처 스켈레톤을, " +
                          "Mono는 ilspycmd로 전체 소스 + 본문을 뺀 AI 아웃라인을 만듭니다. 출력 폴더와 색인 요약을 돌려주며 이후 도구의 기본 레퍼런스가 됩니다.",
            Properties = new[]
            {
                new McpProperty("game_path", "string", "게임 설치 폴더 또는 어셈블리 폴더/DLL", Required: true),
                new McpProperty("output_dir", "string", "출력 폴더. 생략하면 Decompiled/<게임이름>"),
                new McpProperty("targets", "string", "쉼표로 구분한 대상 DLL (기본: Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll)"),
                new McpProperty("engine", "string", "auto(백엔드에 맞춤) | signature(내장 시그니처 덤퍼) | ilspy(ilspycmd 전체 디컴파일)", Enum: new[] { "auto", "signature", "ilspy" }),
                new McpProperty("include_interop_members", "boolean", "Il2CppInterop 내부 포인터 멤버(NativeFieldInfoPtr_ 등)도 포함 (기본 false, 모드 작성엔 불필요)"),
                new McpProperty("outline", "boolean", "Mono(ilspy) 덤프 시 AI 아웃라인도 생성 (기본 true)")
            },
            Handler = (a, ctx) => toolkit.DumpAsync(new DumpArguments
            {
                GamePath = a.Required("game_path"),
                OutputDirectory = a.String("output_dir"),
                Targets = a.String("targets"),
                Engine = a.String("engine"),
                IncludeInteropMembers = a.Bool("include_interop_members", false),
                Outline = a.Bool("outline", true)
            }, ctx.Progress, ctx.CancellationToken)
        },
        new()
        {
            Name = "describe_reference",
            Title = "레퍼런스 개요",
            Description = "레퍼런스 폴더의 게임·백엔드·모듈 정보와 타입이 많은 네임스페이스 목록을 보여 줍니다. 어디서부터 찾을지 감을 잡을 때 쓰세요.",
            ReadOnly = true,
            Properties = new[] { new McpProperty("reference_dir", "string", RefDirDescription) },
            Handler = (a, _) => Task.FromResult(toolkit.DescribeReference(a.String("reference_dir")))
        },
        new()
        {
            Name = "search_reference",
            Title = "레퍼런스 검색",
            Description = "덤프된 레퍼런스에서 타입·메서드·프로퍼티·필드를 이름으로 찾아 한 줄 시그니처와 파일:줄을 돌려줍니다. " +
                          "'Type.Member' 형태, 공백으로 구분한 여러 단어(모두 포함), '*'/'?' 와일드카드를 지원합니다. 파일을 직접 grep하는 것보다 토큰이 훨씬 적게 듭니다.",
            ReadOnly = true,
            Properties = new[]
            {
                new McpProperty("query", "string", "찾을 이름 (예: PlayerController, TakeDamage, AccountCanvas.OnBtnLoginClick, *Score*)", Required: true),
                new McpProperty("kind", "string", "결과 종류 필터", Enum: new[] { "all", "type", "member", "method", "property", "field" }),
                new McpProperty("namespace", "string", "이 네임스페이스로 시작하는 타입만"),
                new McpProperty("limit", "integer", "최대 결과 수 (기본 30, 최대 200)"),
                new McpProperty("reference_dir", "string", RefDirDescription)
            },
            Handler = (a, _) => Task.FromResult(toolkit.Search(a.Required("query"), a.String("reference_dir"), a.String("kind"), a.String("namespace"), a.Int("limit", 30)))
        },
        new()
        {
            Name = "read_reference_type",
            Title = "타입/멤버 선언 읽기",
            Description = "타입 하나의 선언(필드·프로퍼티·메서드 시그니처)을 돌려줍니다. 기본은 본문을 뺀 아웃라인이고, member를 주면 그 멤버만 " +
                          "(Mono는 메서드 본문까지) 돌려줍니다. 큰 클래스의 로직을 볼 때는 member로 범위를 좁히세요.",
            ReadOnly = true,
            Properties = new[]
            {
                new McpProperty("type", "string", "타입 이름 (단순 이름, Outer.Inner, 또는 네임스페이스 포함 전체 이름)", Required: true),
                new McpProperty("member", "string", "메서드/프로퍼티/필드 이름 (생성자는 .ctor)"),
                new McpProperty("include_bodies", "boolean", "타입 전체를 메서드 본문까지 읽기 (Mono 전체 소스가 있을 때만, 기본 false)"),
                new McpProperty("max_chars", "integer", "최대 글자 수 (기본 40000)"),
                new McpProperty("reference_dir", "string", RefDirDescription)
            },
            Handler = (a, _) => Task.FromResult(toolkit.ReadType(a.Required("type"), a.String("reference_dir"), a.String("member"), a.Bool("include_bodies", false), a.Int("max_chars", 40000)))
        },
        new()
        {
            Name = "generate_harmony_patch",
            Title = "Harmony 패치 생성",
            Description = "레퍼런스의 실제 시그니처(파라미터 이름·타입·ref/out·static·오버로드)로 MelonLoader 모드용 HarmonyX Prefix/Postfix 패치 클래스를 만듭니다. " +
                          "메서드·프로퍼티(getter/setter)·생성자(.ctor)를 지원하고 Il2Cpp/Mono별 주의점을 함께 알려 줍니다.",
            ReadOnly = true,
            Properties = new[]
            {
                new McpProperty("type", "string", "대상 타입 이름", Required: true),
                new McpProperty("method", "string", "대상 메서드/프로퍼티 이름 (생성자는 .ctor)", Required: true),
                new McpProperty("patch", "string", "만들 패치 종류", Enum: new[] { "both", "prefix", "postfix" }),
                new McpProperty("reference_dir", "string", RefDirDescription)
            },
            Handler = (a, _) => Task.FromResult(toolkit.HarmonyPatch(a.Required("type"), a.Required("method"), a.String("reference_dir"), a.String("patch")))
        },
        new()
        {
            Name = "generate_melon_mod_project",
            Title = "MelonLoader 모드 프로젝트 생성",
            Description = "게임 폴더를 보고 실제 MelonLoader/게임 어셈블리 경로를 참조하는 모드 프로젝트(.csproj + Core.cs, MelonInfo/MelonGame 포함)를 만듭니다. " +
                          "output_dir를 주면 파일로 쓰고, 생략하면 내용을 돌려줍니다. Il2Cpp는 net6.0, Mono는 MelonLoader 폴더에 맞춰 net472/net35.",
            Properties = new[]
            {
                new McpProperty("game_path", "string", "게임 설치 폴더 (하위 폴더를 넣어도 루트를 찾음)", Required: true),
                new McpProperty("mod_name", "string", "모드 이름 (어셈블리/네임스페이스 이름으로도 사용)", Required: true),
                new McpProperty("author", "string", "MelonInfo 작성자"),
                new McpProperty("output_dir", "string", "파일을 쓸 폴더 (생략하면 내용만 반환)"),
                new McpProperty("overwrite", "boolean", "이미 있는 파일 덮어쓰기 (기본 false)")
            },
            Handler = (a, _) => Task.FromResult(toolkit.ModTemplate(a.Required("game_path"), a.Required("mod_name"), a.String("author"), a.String("output_dir"), a.Bool("overwrite", false)))
        }
    };
}
