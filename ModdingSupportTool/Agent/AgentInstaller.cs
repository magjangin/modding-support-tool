using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;

namespace ModdingSupportTool.Agent;

public enum AgentClient { Claude, Antigravity, Gemini }

public sealed class AgentInstallOptions
{
    /// <summary>비우면 이 PC에 설치된 에이전트를 자동으로 고른다.</summary>
    public List<AgentClient> Clients { get; set; } = new();

    /// <summary>true면 사용자 전역(~/.claude/skills 등), false면 프로젝트 폴더(.claude/skills 등)</summary>
    public bool UserScope { get; set; } = true;

    public string? ProjectDirectory { get; set; }
    public bool RegisterMcp { get; set; } = true;

    /// <summary>스킬 파일 출처. null=exe 내장본, http(s) URL 또는 로컬 폴더(저장소의 agent 폴더)면 거기서 받는다.</summary>
    public string? Source { get; set; }

    /// <summary>테스트용 홈 폴더. 지정하면 외부 CLI(claude/gemini)를 실행하지 않고 그 안의 파일만 바꾼다.</summary>
    public string? HomeDirectory { get; set; }

    public bool DryRun { get; set; }
    public bool Force { get; set; }
}

/// <summary>
/// 스킬(SKILL.md + references/*.md)을 각 AI 에이전트의 스킬 폴더에 설치하고 MCP 서버를 등록한다.
/// Claude Code: ~/.claude/skills, .claude/skills, MCP는 `claude mcp add` 또는 .mcp.json
/// Antigravity: ~/.gemini/config/skills, .agents/skills, MCP는 ~/.gemini/config/mcp_config.json
/// Gemini CLI 등 Agent Skills 표준: ~/.agents/skills, .agents/skills, MCP는 `gemini mcp add` 또는 .gemini/settings.json
/// </summary>
public static class AgentInstaller
{
    public const string GitHubSource = "https://raw.githubusercontent.com/magjangin/modding-support-tool/main/agent";
    public const string MarkerFileName = ".modding-support-tool.json";

    private const string ResourcePrefix = "agent/";
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // ───────────────────────────── 경로 규칙 ─────────────────────────────

    public static string DisplayName(AgentClient client) => client switch
    {
        AgentClient.Claude => "Claude Code",
        AgentClient.Antigravity => "Antigravity (agy)",
        _ => "Gemini CLI / Agent Skills (~/.agents)"
    };

    public static string SkillsRoot(AgentClient client, bool userScope, string home, string project) => (client, userScope) switch
    {
        (AgentClient.Claude, true) => Path.Combine(home, ".claude", "skills"),
        (AgentClient.Claude, false) => Path.Combine(project, ".claude", "skills"),
        (AgentClient.Antigravity, true) => Path.Combine(home, ".gemini", "config", "skills"),
        (AgentClient.Gemini, true) => Path.Combine(home, ".agents", "skills"),
        _ => Path.Combine(project, ".agents", "skills") // Antigravity·Gemini CLI 워크스페이스 공용
    };

    public static List<AgentClient> DetectInstalledClients(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clients = new List<AgentClient>();
        if (Directory.Exists(Path.Combine(home, ".claude")) || FindExecutable("claude") != null) clients.Add(AgentClient.Claude);
        if (Directory.Exists(Path.Combine(home, ".gemini", "config")) || Directory.Exists(Path.Combine(home, ".gemini", "antigravity")) || FindExecutable("agy") != null)
            clients.Add(AgentClient.Antigravity);
        if (FindExecutable("gemini") != null || Directory.Exists(Path.Combine(home, ".agents"))) clients.Add(AgentClient.Gemini);
        return clients;
    }

    public static bool TryParseClients(string? text, out List<AgentClient> clients, out string? error)
    {
        clients = new List<AgentClient>();
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            AgentClient? client = raw.Trim().ToLowerInvariant() switch
            {
                "claude" or "claude-code" or "claudecode" => AgentClient.Claude,
                "antigravity" or "agy" => AgentClient.Antigravity,
                "gemini" or "gemini-cli" or "agents" => AgentClient.Gemini,
                _ => null
            };
            if (client == null)
            {
                error = $"알 수 없는 에이전트: {raw} (claude | antigravity | gemini | all)";
                return false;
            }
            if (!clients.Contains(client.Value)) clients.Add(client.Value);
        }
        return true;
    }

    // ───────────────────────────── install ─────────────────────────────

    public static async Task<bool> InstallAsync(AgentInstallOptions options, Action<string> log, CancellationToken ct = default)
    {
        string home = options.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string project = Path.GetFullPath(options.ProjectDirectory ?? Environment.CurrentDirectory);
        var clients = options.Clients.Count > 0 ? options.Clients : DetectInstalledClients(home);
        if (clients.Count == 0)
        {
            log("[오류] 설치된 AI 에이전트를 찾지 못했습니다. --client claude|antigravity|gemini 로 직접 지정하세요.");
            return false;
        }

        log($"[에이전트 설치] 대상: {string.Join(", ", clients.Select(DisplayName))} · 범위: {(options.UserScope ? "사용자 전역" : "프로젝트 " + project)}" + (options.DryRun ? " · (미리보기)" : ""));

        Dictionary<string, byte[]> files;
        try
        {
            files = await LoadSkillFilesAsync(options.Source, log, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            log($"[오류] 스킬 파일을 받지 못했습니다: {ex.Message}");
            return false;
        }

        var skillNames = files.Keys.Select(SkillNameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        log($"  스킬 {skillNames.Count}개 ({string.Join(", ", skillNames)}) · 파일 {files.Count}개 · 출처: {options.Source ?? "exe 내장"}");
        if (AppPaths.IsRunningFromBuildOutput && options.RegisterMcp)
        {
            log($"  [주의] 빌드 폴더의 exe를 MCP로 등록합니다: {AppPaths.ExecutablePath}");
            log("         경로가 바뀌지 않게 publish.bat으로 게시한 Publish\\ModdingSupportTool.exe에서 다시 설치하는 것을 권장합니다.");
        }

        bool ok = true;
        var writtenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();
            string root = SkillsRoot(client, options.UserScope, home, project);
            log($"- {DisplayName(client)}");

            if (writtenRoots.Add(root))
            {
                foreach (var skill in skillNames)
                {
                    ok &= WriteSkill(root, skill, files, client, options, log);
                }
            }
            else
            {
                log($"  스킬: {root} (앞에서 이미 설치한 공용 폴더)");
            }

            if (options.RegisterMcp)
            {
                ok &= await RegisterMcpAsync(client, options, home, project, log, ct);
            }
        }

        log(ok ? "[완료] 에이전트를 새로 시작하면 스킬과 MCP 도구가 보입니다." : "[경고] 일부 단계가 실패했습니다. 위 로그를 확인하세요.");
        return ok;
    }

    private static bool WriteSkill(string skillsRoot, string skill, Dictionary<string, byte[]> files, AgentClient client, AgentInstallOptions options, Action<string> log)
    {
        string target = Path.Combine(skillsRoot, skill);
        string marker = Path.Combine(target, MarkerFileName);

        if (Directory.Exists(target) && !File.Exists(marker) && !options.Force)
        {
            log($"  [건너뜀] {target} 에 이 도구가 설치하지 않은 같은 이름의 스킬이 있습니다. (덮어쓰려면 --force)");
            return false;
        }

        var skillFiles = files.Where(f => SkillNameOf(f.Key).Equals(skill, StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.DryRun)
        {
            foreach (var f in skillFiles) log($"  (미리보기) {Path.Combine(skillsRoot, ToLocalPath(f.Key[("skills/".Length)..]))}");
            return true;
        }

        try
        {
            // 예전 버전에서 사라진 파일이 남지 않도록 이 도구가 설치한 스킬 폴더는 지우고 다시 쓴다.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

            foreach (var (relative, content) in skillFiles)
            {
                string path = Path.Combine(skillsRoot, ToLocalPath(relative["skills/".Length..]));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            string localInfo = Path.Combine(target, "references", "local-install.md");
            Directory.CreateDirectory(Path.GetDirectoryName(localInfo)!);
            File.WriteAllText(localInfo, BuildLocalInstallNote(client, options), new UTF8Encoding(false));

            File.WriteAllText(marker, JsonSerializer.Serialize(new
            {
                tool = "ModdingSupportTool",
                version = AppPaths.Version,
                source = options.Source ?? "embedded",
                executable = AppPaths.ExecutablePath,
                client = client.ToString(),
                installedAt = DateTime.Now
            }, IndentedJson));

            log($"  스킬 설치: {target} ({skillFiles.Count + 1}개 파일)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"  [실패] 스킬을 쓰지 못했습니다: {target} ({ex.Message})");
            return false;
        }
    }

    private static string BuildLocalInstallNote(AgentClient client, AgentInstallOptions options)
    {
        string exe = AppPaths.ExecutablePath;
        return $"""
# 로컬 설치 정보

`ModdingSupportTool.exe agent install`이 설치할 때 자동으로 만든 파일입니다. (다시 설치하면 갱신됨)

- 실행 파일: `{exe}`
- MCP 서버 이름: `{AppPaths.McpServerName}` (명령: `"{exe}" mcp`)
- 기본 레퍼런스(덤프) 폴더: `{AppPaths.DefaultOutputDirectory}` (에이전트 덤프는 `<이 폴더>\<게임 이름>`)
- 설치 대상: {DisplayName(client)} · {(options.UserScope ? "사용자 전역" : "프로젝트")} · 버전 {AppPaths.Version} · {DateTime.Now:yyyy-MM-dd HH:mm}

## MCP 도구가 안 보일 때 쓰는 CLI (같은 기능)

```powershell
& "{exe}" detect "<게임 폴더>"
& "{exe}" dump "<게임 폴더>" --out "<출력 폴더>"
& "{exe}" refs --ref "<출력 폴더>"
& "{exe}" search "<이름>" --ref "<출력 폴더>" [--kind method]
& "{exe}" type "<타입>" [--member "<멤버>"] --ref "<출력 폴더>"
& "{exe}" patch "<타입>" "<메서드>" --ref "<출력 폴더>"
& "{exe}" template "<게임 폴더>" "<모드 이름>" --out "<프로젝트 폴더>"
```

""";
    }

    private static async Task<bool> RegisterMcpAsync(AgentClient client, AgentInstallOptions options, string home, string project, Action<string> log, CancellationToken ct)
    {
        string exe = AppPaths.ExecutablePath;
        bool testHome = options.HomeDirectory != null;

        switch (client, options.UserScope)
        {
            case (AgentClient.Claude, false):
                return MergeMcpEntry(Path.Combine(project, ".mcp.json"), new JsonObject { ["type"] = "stdio", ["command"] = exe, ["args"] = new JsonArray("mcp") }, options.DryRun, log);

            case (AgentClient.Antigravity, true):
                return MergeMcpEntry(Path.Combine(home, ".gemini", "config", "mcp_config.json"), new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp") }, options.DryRun, log);

            case (AgentClient.Antigravity, false):
                log("  MCP: Antigravity는 전역 mcp_config.json만 지원합니다 → `agent install --client antigravity`(사용자 범위)로 등록하세요.");
                return true;

            case (AgentClient.Gemini, false):
                return MergeMcpEntry(Path.Combine(project, ".gemini", "settings.json"), new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp") }, options.DryRun, log);

            case (AgentClient.Claude, true):
                // ~/.claude.json은 Claude Code가 실행 중에도 쓰는 큰 상태 파일이라 직접 고치지 않고 공식 CLI로 등록한다.
                if (testHome)
                {
                    return MergeMcpEntry(Path.Combine(home, ".claude.json"), new JsonObject { ["type"] = "stdio", ["command"] = exe, ["args"] = new JsonArray("mcp") }, options.DryRun, log);
                }
                return await RegisterWithCliAsync("claude", new[] { "mcp", "add", "--scope", "user", AppPaths.McpServerName, "--", exe, "mcp" },
                    new[] { "mcp", "remove", "--scope", "user", AppPaths.McpServerName }, options.DryRun, log, ct);

            default: // Gemini, user
                if (testHome)
                {
                    return MergeMcpEntry(Path.Combine(home, ".gemini", "settings.json"), new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp") }, options.DryRun, log);
                }
                return await RegisterWithCliAsync("gemini", new[] { "mcp", "add", "--scope", "user", AppPaths.McpServerName, exe, "mcp" },
                    new[] { "mcp", "remove", "--scope", "user", AppPaths.McpServerName }, options.DryRun, log, ct);
        }
    }

    private static async Task<bool> RegisterWithCliAsync(string cli, string[] addArgs, string[] removeArgs, bool dryRun, Action<string> log, CancellationToken ct)
    {
        string manual = $"{cli} {string.Join(" ", addArgs.Select(QuoteForDisplay))}";
        string? path = FindExecutable(cli);
        if (path == null)
        {
            log($"  MCP: `{cli}` CLI를 PATH에서 찾지 못했습니다. 직접 실행하세요: {manual}");
            return true;
        }
        if (dryRun)
        {
            log($"  (미리보기) {manual}");
            return true;
        }

        // 같은 이름이 이미 있으면 add가 실패하므로 지우고 다시 등록한다. (경로가 바뀐 경우 갱신)
        await RunProcessAsync(path, removeArgs, ct);
        var (code, output) = await RunProcessAsync(path, addArgs, ct);
        if (code == 0)
        {
            log($"  MCP 등록: {cli} → {AppPaths.McpServerName}");
            return true;
        }

        log($"  [실패] {manual} (종료 코드 {code}) {output.Trim()}");
        return false;
    }

    // ───────────────────────────── uninstall / status ─────────────────────────────

    public static async Task<bool> UninstallAsync(AgentInstallOptions options, Action<string> log, CancellationToken ct = default)
    {
        string home = options.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string project = Path.GetFullPath(options.ProjectDirectory ?? Environment.CurrentDirectory);
        var clients = options.Clients.Count > 0 ? options.Clients : Enum.GetValues<AgentClient>().ToList();
        bool ok = true;

        foreach (var client in clients)
        {
            string root = SkillsRoot(client, options.UserScope, home, project);
            log($"- {DisplayName(client)}");
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, MarkerFileName))).ToList())
                {
                    if (!options.DryRun) Directory.Delete(dir, recursive: true);
                    log($"  스킬 제거: {dir}");
                }
            }

            if (!options.RegisterMcp) continue;
            bool testHome = options.HomeDirectory != null;
            switch (client, options.UserScope)
            {
                case (AgentClient.Claude, false): ok &= RemoveMcpEntry(Path.Combine(project, ".mcp.json"), options.DryRun, log); break;
                case (AgentClient.Antigravity, true): ok &= RemoveMcpEntry(Path.Combine(home, ".gemini", "config", "mcp_config.json"), options.DryRun, log); break;
                case (AgentClient.Gemini, false): ok &= RemoveMcpEntry(Path.Combine(project, ".gemini", "settings.json"), options.DryRun, log); break;
                case (AgentClient.Claude, true) when testHome: ok &= RemoveMcpEntry(Path.Combine(home, ".claude.json"), options.DryRun, log); break;
                case (AgentClient.Gemini, true) when testHome: ok &= RemoveMcpEntry(Path.Combine(home, ".gemini", "settings.json"), options.DryRun, log); break;
                case (AgentClient.Claude, true):
                case (AgentClient.Gemini, true):
                    string cli = client == AgentClient.Claude ? "claude" : "gemini";
                    string? path = FindExecutable(cli);
                    if (path != null && !options.DryRun)
                    {
                        var (code, _) = await RunProcessAsync(path, new[] { "mcp", "remove", "--scope", "user", AppPaths.McpServerName }, ct);
                        log(code == 0 ? $"  MCP 제거: {cli}" : $"  MCP: {cli}에 등록되어 있지 않음");
                    }
                    break;
            }
        }
        return ok;
    }

    public static void PrintStatus(AgentInstallOptions options, Action<string> log)
    {
        string home = options.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string project = Path.GetFullPath(options.ProjectDirectory ?? Environment.CurrentDirectory);
        var detected = DetectInstalledClients(home);

        log($"실행 파일: {AppPaths.ExecutablePath} (v{AppPaths.Version})");
        foreach (var client in Enum.GetValues<AgentClient>())
        {
            log($"- {DisplayName(client)}{(detected.Contains(client) ? "" : " (이 PC에서 감지 안 됨)")}");
            foreach (bool user in new[] { true, false })
            {
                string root = SkillsRoot(client, user, home, project);
                var installed = Directory.Exists(root)
                    ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, MarkerFileName))).Select(Path.GetFileName).ToList()
                    : new List<string?>();
                log($"  {(user ? "사용자" : "프로젝트")} 스킬 {root}: {(installed.Count > 0 ? string.Join(", ", installed) : "없음")}");
            }

            string? mcpFile = client switch
            {
                AgentClient.Claude => Path.Combine(home, ".claude.json"),
                AgentClient.Antigravity => Path.Combine(home, ".gemini", "config", "mcp_config.json"),
                _ => Path.Combine(home, ".gemini", "settings.json")
            };
            log($"  MCP ({mcpFile}): {(HasMcpEntry(mcpFile) ? "등록됨" : "없음")}");
        }
    }

    // ───────────────────────────── 스킬 파일 출처 ─────────────────────────────

    /// <summary>key: "skills/&lt;스킬&gt;/..." 상대 경로(슬래시 구분)</summary>
    public static async Task<Dictionary<string, byte[]>> LoadSkillFilesAsync(string? source, Action<string> log, CancellationToken ct)
    {
        var embedded = EmbeddedSkillFiles();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(source))
        {
            var asm = typeof(AgentInstaller).Assembly;
            foreach (var (relative, resource) in embedded)
            {
                using var stream = asm.GetManifestResourceStream(resource)!;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                files[relative] = ms.ToArray();
            }
            return files;
        }

        string resolved = source.Equals("github", StringComparison.OrdinalIgnoreCase) ? GitHubSource : source;
        if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 원격 저장소의 최신 스킬 파일을 내려받는다. (파일 목록은 이 exe에 내장된 목록을 기준으로 한다)
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ModdingSupportTool/{AppPaths.Version}");
            string baseUrl = resolved.TrimEnd('/');
            foreach (var relative in embedded.Select(e => e.Relative))
            {
                string url = baseUrl + "/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
                files[relative] = await http.GetByteArrayAsync(url, ct);
                log($"  다운로드: {url}");
            }
            return files;
        }

        // 로컬 폴더: 저장소의 agent 폴더(skills/가 들어 있는 폴더)나 skills 폴더 자체를 받는다.
        string dir = Path.GetFullPath(resolved);
        string skillsDir = Directory.Exists(Path.Combine(dir, "skills")) ? Path.Combine(dir, "skills") : dir;
        if (!Directory.Exists(skillsDir)) throw new IOException($"스킬 폴더가 없습니다: {skillsDir}");

        foreach (var file in Directory.EnumerateFiles(skillsDir, "*", SearchOption.AllDirectories))
        {
            string relative = "skills/" + Path.GetRelativePath(skillsDir, file).Replace('\\', '/');
            if (relative.EndsWith("/" + MarkerFileName, StringComparison.OrdinalIgnoreCase) || relative.EndsWith("/references/local-install.md", StringComparison.OrdinalIgnoreCase)) continue;
            files[relative] = await File.ReadAllBytesAsync(file, ct);
        }
        if (files.Count == 0) throw new IOException($"스킬 파일이 없습니다: {skillsDir}");
        return files;
    }

    private static List<(string Relative, string Resource)> EmbeddedSkillFiles() =>
        typeof(AgentInstaller).Assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith(ResourcePrefix + "skills/", StringComparison.OrdinalIgnoreCase))
            .Select(n => (n.Replace('\\', '/')[ResourcePrefix.Length..], n))
            .OrderBy(e => e.Item1, StringComparer.Ordinal)
            .ToList();

    private static string SkillNameOf(string relative) => relative.Split('/')[1];

    private static string ToLocalPath(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    // ───────────────────────────── JSON 설정 병합 ─────────────────────────────

    private static bool MergeMcpEntry(string file, JsonObject entry, bool dryRun, Action<string> log)
    {
        try
        {
            var root = ReadJsonObject(file);
            var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
            servers[AppPaths.McpServerName] = entry;
            root["mcpServers"] = servers;

            if (dryRun)
            {
                log($"  (미리보기) MCP 등록: {file}");
                return true;
            }

            WriteJsonAtomically(file, root);
            log($"  MCP 등록: {file}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            log($"  [실패] MCP 설정을 갱신하지 못했습니다: {file} ({ex.Message})");
            return false;
        }
    }

    private static bool RemoveMcpEntry(string file, bool dryRun, Action<string> log)
    {
        try
        {
            if (!File.Exists(file)) return true;
            var root = ReadJsonObject(file);
            if (root["mcpServers"] is JsonObject servers && servers.Remove(AppPaths.McpServerName))
            {
                if (!dryRun) WriteJsonAtomically(file, root);
                log($"  MCP 제거: {file}");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            log($"  [실패] MCP 설정을 갱신하지 못했습니다: {file} ({ex.Message})");
            return false;
        }
    }

    private static bool HasMcpEntry(string file)
    {
        try
        {
            return File.Exists(file) && ReadJsonObject(file)["mcpServers"] is JsonObject servers && servers.ContainsKey(AppPaths.McpServerName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static JsonObject ReadJsonObject(string file)
    {
        if (!File.Exists(file)) return new JsonObject();
        string text = File.ReadAllText(file);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: LenientJson) as JsonObject
               ?? throw new InvalidOperationException("JSON 최상위가 객체가 아닙니다.");
    }

    // 다른 설정은 그대로 두고, 처음 고칠 때 한 번 백업을 남기며, 임시 파일에 쓴 뒤 교체해서 중간에 깨지지 않게 한다.
    private static void WriteJsonAtomically(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string backup = file + ".mst-backup";
        if (File.Exists(file) && !File.Exists(backup)) File.Copy(file, backup);

        string temp = file + ".mst-tmp";
        File.WriteAllText(temp, root.ToJsonString(IndentedJson) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    // ───────────────────────────── 외부 CLI ─────────────────────────────

    public static string? FindExecutable(string name)
    {
        string[] extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH에 잘못된 문자가 든 항목은 건너뛴다.
                }
            }
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string path, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 이미 종료됨 */ }
            return (-1, "시간 초과");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string QuoteForDisplay(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
