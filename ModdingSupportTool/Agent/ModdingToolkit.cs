using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;
using ModdingSupportTool.Core.Detection;
using ModdingSupportTool.Core.Reference;
using ModdingSupportTool.Models;
using ModdingSupportTool.Services;

namespace ModdingSupportTool.Agent;

public sealed record ToolResult(string Text, bool IsError = false)
{
    public static ToolResult Fail(string text) => new(text, true);
}

public sealed class DumpArguments
{
    public string GamePath { get; set; } = string.Empty;
    public string? OutputDirectory { get; set; }
    public string? Targets { get; set; }
    public string? Engine { get; set; } // auto | signature | ilspy
    public bool IncludeInteropMembers { get; set; }
    public bool Outline { get; set; } = true;
}

/// <summary>
/// 에이전트용 기능 모음. MCP 서버의 도구와 CLI 하위 명령이 같은 구현을 쓴다.
/// 이 도구의 범위는 "Unity(Mono/Il2Cpp) 게임에 MelonLoader로 주입할 모드 DLL을 만들 때 참고할 C# 레퍼런스"다.
/// </summary>
public sealed class ModdingToolkit
{
    public const string ScopeNotice =
        "이 도구는 Unity(Mono/Il2Cpp) 게임에서 MelonLoader로 주입할 모드 DLL을 만들 때 참고할 C# 레퍼런스를 만듭니다. " +
        "Unreal Engine 게임은 UE4SS, 네이티브 바이너리 분석은 Ghidra/IDA, Il2Cpp 메서드 본문(네이티브 로직)은 Cpp2IL/Il2CppDumper+Ghidra 같은 다른 도구를 쓰세요.";

    private const string ReferenceEnvironmentVariable = "MST_REFERENCE_DIR";

    private readonly DumpService _dumpService = new();
    private string? _lastReferenceDirectory;

    /// <summary>mcp --ref 로 지정한 기본 레퍼런스 폴더</summary>
    public string? DefaultReferenceDirectory { get; set; }

    // ───────────────────────────── detect ─────────────────────────────

    public ToolResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path가 비어 있습니다.");
        if (!File.Exists(path) && !Directory.Exists(path)) return ToolResult.Fail($"경로가 없습니다: {path}");

        var plan = DumpService.Plan(path);
        var sb = new StringBuilder();
        sb.AppendLine($"경로: {path}");
        sb.AppendLine($"Unity 백엔드: {plan.Detection.Backend} ({plan.Detection.Reason})");

        if (plan.Game != null)
        {
            sb.AppendLine($"게임: {plan.Game.Name} · 루트 {plan.Game.Root}" + (plan.Game.Company != null ? $" · 회사 {plan.Game.Company}" : ""));
            string? ml = plan.Game.MelonLoaderDirectory;
            sb.AppendLine(ml != null ? $"MelonLoader: 설치됨 ({ml})" : "MelonLoader: 설치되지 않음 → 모드를 로드하려면 먼저 설치해야 합니다.");
            if (plan.Detection.Backend == UnityBackend.Il2Cpp)
            {
                bool hasAssemblies = ml != null && Directory.Exists(Path.Combine(ml, "Il2CppAssemblies"));
                sb.AppendLine(hasAssemblies
                    ? "Il2CppAssemblies: 있음 (덤프 가능)"
                    : "Il2CppAssemblies: 없음 → MelonLoader 설치 후 게임을 한 번 실행해야 생성됩니다. 그 전엔 덤프할 수 없습니다.");
            }
        }

        if (plan.Detection.Backend == UnityBackend.Unknown)
        {
            string? other = GuessOtherEngine(plan.Game?.Root ?? (Directory.Exists(path) ? path : Path.GetDirectoryName(path)));
            sb.AppendLine(other ?? "Unity 게임 마커를 찾지 못했습니다.");
            sb.AppendLine();
            sb.AppendLine(ScopeNotice);
            return new ToolResult(sb.ToString().TrimEnd());
        }

        sb.AppendLine($"덤프 입력: {plan.InputPath}" + (plan.InputPathAdjusted ? " (자동 보정됨)" : ""));
        sb.AppendLine($"덤프 엔진: {DumpService.EngineDisplayName(plan.Engine)}" +
                      (plan.Engine == DumpEngineType.IlSpyCmd ? " → 메서드 본문까지 포함 + AI 아웃라인" : " → 시그니처 스켈레톤 (Il2Cpp는 메서드 본문 없음)"));
        sb.AppendLine($"기본 출력 폴더: {DefaultDumpOutput(plan)}");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static string? GuessOtherEngine(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            // 큰 폴더를 통째로 뒤지지 않도록 Unreal 패키지 구조(<Project>/Content/Paks, <Project>/Binaries/Win64)만 1단계 확인한다.
            bool unreal = Directory.Exists(Path.Combine(dir, "Engine")) ||
                          Directory.EnumerateDirectories(dir).Any(d =>
                              Directory.Exists(Path.Combine(d, "Content", "Paks")) || Directory.Exists(Path.Combine(d, "Binaries", "Win64")));
            if (unreal) return "Unreal Engine 게임으로 보입니다 → 이 도구 대신 UE4SS(Lua/C++ 모드, 헤더 덤프)를 쓰세요.";
            if (Directory.EnumerateFiles(dir, "*.pck", SearchOption.TopDirectoryOnly).Any()) return "Godot 게임으로 보입니다 → 이 도구의 대상이 아닙니다.";
        }
        catch (Exception)
        {
            // 접근할 수 없는 폴더는 추측하지 않는다.
        }
        return null;
    }

    // ───────────────────────────── dump ─────────────────────────────

    public async Task<ToolResult> DumpAsync(DumpArguments args, Action<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.GamePath)) return ToolResult.Fail("game_path가 비어 있습니다.");
        if (!File.Exists(args.GamePath) && !Directory.Exists(args.GamePath)) return ToolResult.Fail($"경로가 없습니다: {args.GamePath}");

        DumpEngineType? forced = (args.Engine ?? "auto").ToLowerInvariant() switch
        {
            "signature" or "signaturedumper" => DumpEngineType.SignatureDumper,
            "ilspy" or "ilspycmd" => DumpEngineType.IlSpyCmd,
            _ => null
        };

        var plan = DumpService.Plan(args.GamePath, forced, ct);
        if (plan.Detection.Backend == UnityBackend.Unknown && forced == null)
        {
            return ToolResult.Fail($"Unity 백엔드를 판별하지 못했습니다: {plan.Detection.Reason}\n{ScopeNotice}");
        }

        string output = string.IsNullOrWhiteSpace(args.OutputDirectory) ? DefaultDumpOutput(plan) : Path.GetFullPath(args.OutputDirectory);
        var tail = new Queue<string>();
        void Log(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 25) tail.Dequeue();
            }
            progress?.Invoke(line);
        }

        var request = new DumpRequest
        {
            Engine = plan.Engine,
            InputPath = plan.InputPath,
            OutputDirectory = output,
            TargetAssemblyNames = string.IsNullOrWhiteSpace(args.Targets) ? DumpService.DefaultTargets : args.Targets!,
            GenerateAiOutline = args.Outline,
            IncludeInteropMembers = args.IncludeInteropMembers,
            Backend = plan.Detection.Backend,
            Game = plan.Game,
            CaptureWrittenSources = true
        };

        var result = await _dumpService.RunDumpAsync(request, Log, ct);
        if (!result.IsSuccess)
        {
            return ToolResult.Fail($"덤프 실패 (성공 {result.SucceededCount}, 실패 {result.FailedCount}). 마지막 로그:\n{string.Join("\n", tail)}");
        }

        _lastReferenceDirectory = output;
        ReferenceIndex.Invalidate(output);
        var index = ReferenceIndex.Load(output, ct, result.WrittenSources);

        var sb = new StringBuilder();
        sb.AppendLine($"덤프 완료: {plan.Game?.Name ?? Path.GetFileName(args.GamePath)} ({plan.Detection.Backend}, {DumpService.EngineDisplayName(plan.Engine)})");
        sb.AppendLine($"레퍼런스 폴더: {output}");
        foreach (var module in result.Modules)
        {
            sb.AppendLine($"- {module.Name}: 파일 {module.FileCount:N0}개 → {module.OutputPath}" + (module.OutlinePath != null ? $" (아웃라인: {module.OutlinePath})" : ""));
        }
        sb.AppendLine($"색인: 타입 {index.Types.Count:N0}개 · 멤버 {index.MemberCount:N0}개 ({index.BuildTime.TotalMilliseconds:N0}ms)");
        sb.AppendLine("이제 search_reference / read_reference_type / generate_harmony_patch 로 필요한 선언만 찾아 쓰세요. (이 폴더가 이후 호출의 기본 레퍼런스가 됩니다)");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static string DefaultDumpOutput(DumpPlan plan)
    {
        // 에이전트 모드는 게임마다 하위 폴더를 쓴다. (여러 게임 레퍼런스가 서로 덮어쓰지 않게)
        string name = plan.Game?.Name ?? Path.GetFileName(plan.OriginalInputPath.TrimEnd('\\', '/'));
        return Path.Combine(AppPaths.DefaultOutputDirectory, GameLocator.ToFolderName(name));
    }

    // ───────────────────────────── reference ─────────────────────────────

    private ReferenceIndex? OpenIndex(string? explicitDirectory, out ToolResult? error, CancellationToken ct = default)
    {
        error = null;
        string? dir = ResolveReferenceDirectory(explicitDirectory, out string? problem);
        if (dir == null)
        {
            error = ToolResult.Fail(problem ?? "레퍼런스 폴더를 찾지 못했습니다.");
            return null;
        }

        try
        {
            var index = ReferenceIndex.Load(dir, ct);
            if (index.Types.Count == 0)
            {
                error = ToolResult.Fail($"레퍼런스 폴더에 C# 선언이 없습니다: {index.Root}\n먼저 dump_game_reference로 게임을 덤프하세요.");
                return null;
            }
            return index;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ToolResult.Fail($"레퍼런스를 읽지 못했습니다: {ex.Message}");
            return null;
        }
    }

    public string? ResolveReferenceDirectory(string? explicitDirectory, out string? problem)
    {
        problem = null;
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            if (Directory.Exists(explicitDirectory)) return explicitDirectory;
            problem = $"reference_dir가 없습니다: {explicitDirectory}";
            return null;
        }

        foreach (var candidate in new[] { _lastReferenceDirectory, DefaultReferenceDirectory, Environment.GetEnvironmentVariable(ReferenceEnvironmentVariable) })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;
        }

        string root = AppPaths.DefaultOutputDirectory;
        if (!Directory.Exists(root))
        {
            problem = $"레퍼런스가 아직 없습니다. 먼저 dump_game_reference로 게임을 덤프하세요. (기본 출력 폴더: {root})";
            return null;
        }
        if (File.Exists(Path.Combine(root, ReferenceManifest.FileName))) return root;

        var games = ListReferenceFolders(root);
        if (games.Count == 1) return games[0];
        if (games.Count > 1)
        {
            problem = "레퍼런스가 여러 개입니다. reference_dir로 하나를 지정하세요:\n" + string.Join("\n", games.Select(g => "  " + g));
            return null;
        }

        // reference.json이 없던 예전 덤프(Decompiled/Assembly-CSharp 등)도 그대로 쓸 수 있다.
        return Directory.EnumerateDirectories(root).Any() ? root : null;
    }

    private static List<string> ListReferenceFolders(string root) =>
        Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ReferenceManifest.FileName)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public ToolResult DescribeReference(string? referenceDirectory)
    {
        var index = OpenIndex(referenceDirectory, out var error);
        if (index == null)
        {
            // 하위에 게임별 레퍼런스가 있으면 목록이라도 보여준다.
            string root = AppPaths.DefaultOutputDirectory;
            var games = Directory.Exists(root) ? ListReferenceFolders(root) : new List<string>();
            return games.Count > 0 && referenceDirectory == null
                ? new ToolResult("게임별 레퍼런스 목록 (reference_dir로 지정):\n" + string.Join("\n", games.Select(g => $"  {g}  ({ReferenceManifest.Load(g)?.Backend})")))
                : error!;
        }

        var sb = new StringBuilder();
        var manifest = index.Manifest;
        sb.AppendLine($"레퍼런스: {index.Root}" + (index.OutlineRoot != null ? $"  (아웃라인: {index.OutlineRoot})" : ""));
        if (manifest != null)
        {
            sb.AppendLine($"게임: {manifest.GameName ?? "?"} · 백엔드 {manifest.Backend} · 갱신 {manifest.UpdatedAt:yyyy-MM-dd HH:mm}");
            if (manifest.GameRoot != null) sb.AppendLine($"게임 루트: {manifest.GameRoot}");
            foreach (var m in manifest.Modules)
            {
                sb.AppendLine($"- 모듈 {m.Name}: {m.Engine}, 파일 {m.Files:N0}개, 메서드 본문 {(m.HasMethodBodies ? "있음" : "없음")}" +
                              (m.OmittedInteropMembers is > 0 ? $", Il2CppInterop 내부 멤버 {m.OmittedInteropMembers:N0}개 생략" : ""));
            }
        }
        sb.AppendLine($"색인: 타입 {index.Types.Count:N0}개 · 멤버 {index.MemberCount:N0}개 ({index.BuildTime.TotalMilliseconds:N0}ms)");
        sb.AppendLine();
        sb.AppendLine("네임스페이스 (타입 수 상위 40):");
        foreach (var group in index.Types.GroupBy(t => (t.Module, t.Namespace)).OrderByDescending(g => g.Count()).Take(40))
        {
            sb.AppendLine($"  {group.Count(),5}  {group.Key.Module} / {(group.Key.Namespace.Length > 0 ? group.Key.Namespace : "(전역)")}");
        }
        return new ToolResult(sb.ToString().TrimEnd());
    }

    public ToolResult Search(string query, string? referenceDirectory, string? kind, string? namespaceFilter, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query가 비어 있습니다.");
        var index = OpenIndex(referenceDirectory, out var error);
        if (index == null) return error!;

        var scope = (kind ?? "all").ToLowerInvariant() switch
        {
            "type" or "types" => SearchScope.Types,
            "member" or "members" => SearchScope.Members,
            "method" or "methods" => SearchScope.Methods,
            "property" or "properties" => SearchScope.Properties,
            "field" or "fields" => SearchScope.Fields,
            _ => SearchScope.All
        };

        int max = Math.Clamp(limit <= 0 ? 30 : limit, 1, 200);
        var hits = index.Search(query, scope, string.IsNullOrWhiteSpace(namespaceFilter) ? null : namespaceFilter, max);
        if (hits.Count == 0)
        {
            return new ToolResult($"'{query}'와 일치하는 선언이 없습니다. (레퍼런스 {index.Root}, 타입 {index.Types.Count:N0}개) 철자나 kind를 바꾸거나 '*'/'?' 와일드카드를 써 보세요.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"'{query}' 검색 결과 {hits.Count}건 · 레퍼런스 {index.Root} (경로는 이 폴더 기준)");
        foreach (var hit in hits)
        {
            if (hit.Member == null)
            {
                sb.AppendLine($"[{hit.Type.Kind}] {hit.Type.FullName}  — {hit.Type.Declaration}");
                sb.AppendLine($"    {hit.Type.RelativePath}:{hit.Type.Line} · 멤버 {hit.Type.Members.Count}개");
            }
            else
            {
                sb.AppendLine($"[{hit.Member.Kind.ToString().ToLowerInvariant()}] {hit.Type.FullName}.{hit.Member.Name}  — {hit.Member.Signature}");
                sb.AppendLine($"    {hit.Type.RelativePath}:{hit.Member.Line}");
            }
        }
        return new ToolResult(sb.ToString().TrimEnd());
    }

    public ToolResult ReadType(string typeName, string? referenceDirectory, string? member, bool includeBodies, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return ToolResult.Fail("type이 비어 있습니다.");
        var index = OpenIndex(referenceDirectory, out var error);
        if (index == null) return error!;

        var type = PickSingleType(index, typeName, out var ambiguity);
        if (type == null) return ambiguity!;

        // 멤버 하나만 읽을 땐 본문을 보는 게 목적이므로 전체 소스(있으면)를 쓴다.
        bool bodies = includeBodies || !string.IsNullOrWhiteSpace(member);
        string path = bodies ? index.GetFullSourcePath(type) : index.GetOutlinePath(type);
        if (!File.Exists(path)) path = index.GetOutlinePath(type);

        var located = ReferenceIndex.LocateTypeNode(type, path);
        if (located is not { } found) return ToolResult.Fail($"파일에서 '{type.FullName}' 선언을 찾지 못했습니다: {path}");

        string code;
        string what;
        if (string.IsNullOrWhiteSpace(member))
        {
            var usings = found.Root.Usings.Select(u => u.ToString().Trim());
            code = string.Join(Environment.NewLine, usings) + (found.Root.Usings.Count > 0 ? Environment.NewLine + Environment.NewLine : "") +
                   (type.Namespace.Length > 0 ? $"namespace {type.Namespace};{Environment.NewLine}{Environment.NewLine}" : "") +
                   Dedent(found.Node.ToFullString());
            what = type.FullName;
        }
        else
        {
            var members = MemberNodes(found.Node, member.Trim(), type.Name).ToList();
            if (members.Count == 0)
            {
                var similar = type.Members.Where(m => m.Name.Contains(member.Trim(), StringComparison.OrdinalIgnoreCase)).Select(m => "  " + m.Signature).Distinct().Take(15).ToList();
                return ToolResult.Fail($"'{type.FullName}'에 '{member}' 멤버가 없습니다." + (similar.Count > 0 ? "\n비슷한 멤버:\n" + string.Join("\n", similar) : ""));
            }
            code = string.Join(Environment.NewLine + Environment.NewLine, members.Select(m => Dedent(m.ToFullString())));
            what = $"{type.FullName}.{member.Trim()} ({members.Count}개 선언)";
        }

        int max = maxChars <= 0 ? 40000 : maxChars;
        bool truncated = code.Length > max;
        if (truncated) code = code[..max];

        var sb = new StringBuilder();
        sb.AppendLine($"{what}");
        sb.AppendLine($"파일: {path}");
        if (!index.HasMethodBodies(type))
        {
            sb.AppendLine("메서드 본문: 없음 (Il2Cpp 더미 어셈블리/시그니처 스켈레톤). 실제 로직은 네이티브 GameAssembly.dll에 있습니다.");
        }
        else if (!bodies)
        {
            sb.AppendLine("메서드 본문 생략 (아웃라인). 특정 메서드 본문은 member를 지정하거나 include_bodies=true로 읽으세요.");
        }
        sb.AppendLine("```csharp");
        sb.AppendLine(code.TrimEnd());
        sb.AppendLine("```");
        if (truncated) sb.AppendLine($"(… {max:N0}자에서 잘림. member로 범위를 좁히거나 max_chars를 늘리세요)");
        return new ToolResult(sb.ToString().TrimEnd());
    }

    public ToolResult HarmonyPatch(string typeName, string memberName, string? referenceDirectory, string? kind)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(memberName)) return ToolResult.Fail("type과 method가 필요합니다.");
        var index = OpenIndex(referenceDirectory, out var error);
        if (index == null) return error!;

        var type = PickSingleType(index, typeName, out var ambiguity);
        if (type == null) return ambiguity!;

        var patchKind = (kind ?? "both").ToLowerInvariant() switch
        {
            "prefix" => PatchKind.Prefix,
            "postfix" => PatchKind.Postfix,
            _ => PatchKind.Both
        };

        var backend = index.Manifest?.Backend ?? UnityBackend.Unknown;
        var result = HarmonyPatchGenerator.Generate(index, type, memberName, patchKind, backend);
        if (result.Error != null) return ToolResult.Fail(result.Error);

        var sb = new StringBuilder();
        sb.AppendLine($"Harmony 패치: {type.FullName}.{memberName} ({(backend == UnityBackend.Unknown ? "백엔드 미상" : backend.ToString())})");
        sb.AppendLine("```csharp");
        sb.AppendLine(result.Code!.TrimEnd());
        sb.AppendLine("```");
        foreach (var note in result.Notes) sb.AppendLine("- " + note);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    public ToolResult ModTemplate(string gamePath, string modName, string? author, string? outputDirectory, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return ToolResult.Fail("game_path가 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(modName)) return ToolResult.Fail("mod_name이 비어 있습니다.");

        var game = GameLocator.Find(gamePath);
        if (game == null) return ToolResult.Fail($"게임 루트를 찾지 못했습니다: {gamePath}\n{ScopeNotice}");

        var backend = UnityBackendDetector.Detect(game.Root).Backend;
        var template = MelonModTemplate.Create(game, backend, modName, author);

        var sb = new StringBuilder();
        sb.AppendLine($"MelonLoader 모드 템플릿: {template.ModName} · {game.Name} ({backend})");

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            try
            {
                foreach (var file in MelonModTemplate.WriteTo(template, outputDirectory, overwrite)) sb.AppendLine($"작성: {file}");
            }
            catch (IOException ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }
        else
        {
            sb.AppendLine($"--- {template.ProjectFileName}");
            sb.AppendLine("```xml");
            sb.AppendLine(template.ProjectXml.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine($"--- {template.CoreFileName}");
            sb.AppendLine("```csharp");
            sb.AppendLine(template.CoreSource.TrimEnd());
            sb.AppendLine("```");
        }

        foreach (var note in template.Notes) sb.AppendLine("- " + note);
        return new ToolResult(sb.ToString().TrimEnd());
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static ReferenceType? PickSingleType(ReferenceIndex index, string typeName, out ToolResult? problem)
    {
        problem = null;
        var types = index.FindTypes(typeName);
        if (types.Count == 1) return types[0];

        if (types.Count == 0)
        {
            var similar = index.Search(typeName, SearchScope.Types, null, 10);
            problem = ToolResult.Fail($"'{typeName}' 타입이 없습니다." +
                (similar.Count > 0 ? "\n비슷한 타입:\n" + string.Join("\n", similar.Select(h => "  " + h.Type.FullName)) : ""));
            return null;
        }

        problem = ToolResult.Fail($"'{typeName}'에 해당하는 타입이 {types.Count}개입니다. 전체 이름으로 지정하세요:\n" +
                                  string.Join("\n", types.Take(20).Select(t => $"  {t.FullName}  ({t.Module}/{t.RelativePath})")));
        return null;
    }

    private static IEnumerable<Microsoft.CodeAnalysis.SyntaxNode> MemberNodes(Microsoft.CodeAnalysis.SyntaxNode typeNode, string member, string typeSimpleName)
    {
        bool ctor = member is ".ctor" or "ctor" || member == typeSimpleName;
        foreach (var node in typeNode.ChildNodes())
        {
            switch (node)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax when ctor:
                    yield return node;
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax m when !ctor && m.Identifier.Text == member:
                case Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax p when !ctor && p.Identifier.Text == member:
                case Microsoft.CodeAnalysis.CSharp.Syntax.EventDeclarationSyntax e when !ctor && e.Identifier.Text == member:
                    yield return node;
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.BaseFieldDeclarationSyntax f when !ctor && f.Declaration.Variables.Any(v => v.Identifier.Text == member):
                    yield return node;
                    break;
            }
        }
    }

    // 중첩 깊이만큼 들어간 들여쓰기를 걷어내 토큰을 아낀다.
    private static string Dedent(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.TakeWhile(c => c == ' ' || c == '\t').Count())
            .DefaultIfEmpty(0)
            .Min();
        return string.Join(Environment.NewLine, lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim('\r', '\n');
    }
}
