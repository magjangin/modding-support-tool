using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModdingSupportTool.Core;

namespace ModdingSupportTool.Agent.Mcp;

/// <summary>
/// stdio 전송(줄 단위 JSON-RPC 2.0)으로 동작하는 최소 MCP 서버.
/// Claude Code, Antigravity, Gemini CLI 등 MCP 클라이언트가 이 exe를 "ModdingSupportTool.exe mcp"로 띄워 도구를 호출한다.
/// </summary>
public sealed class McpServer
{
    private static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Instructions =
        "Unity(Mono/Il2Cpp) 게임용 MelonLoader 모드 DLL을 만들 때 쓰는 C# 레퍼런스 도구입니다.\n" +
        "순서: detect_unity_backend → dump_game_reference → search_reference / read_reference_type → generate_harmony_patch → generate_melon_mod_project.\n" +
        "덤프 폴더 전체를 직접 읽지 말고 search_reference로 필요한 선언만 찾으세요(토큰 절약).\n" +
        "범위 밖: Unreal Engine은 UE4SS, 네이티브 바이너리·Il2Cpp 메서드 본문은 Ghidra/IDA/Cpp2IL 같은 다른 도구를 쓰세요.";

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TextWriter _log;
    private readonly ModdingToolkit _toolkit;
    private readonly IReadOnlyList<McpTool> _tools;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();

    public McpServer(Stream input, Stream output, TextWriter log, ModdingToolkit toolkit)
    {
        _input = input;
        _output = output;
        _log = log;
        _toolkit = toolkit;
        _tools = McpTools.Create(toolkit);
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        _log.WriteLine($"[mcp] {AppPaths.McpServerName} {AppPaths.Version} 시작 (stdio)");
        using var reader = new StreamReader(_input, new UTF8Encoding(false));
        var pending = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break; // 클라이언트가 stdin을 닫으면 종료
            if (string.IsNullOrWhiteSpace(line)) continue;

            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(HandleLineAsync(line, ct)); // 긴 덤프 중에도 ping/cancel을 받을 수 있게 동시에 처리한다
        }

        await Task.WhenAll(pending);
        _log.WriteLine("[mcp] 종료");
        return 0;
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            await SendAsync(ErrorResponse(null, -32700, $"Parse error: {ex.Message}"));
            return;
        }

        if (node is JsonArray batch)
        {
            foreach (var item in batch)
            {
                if (item is JsonObject obj) await HandleMessageAsync(obj, ct);
            }
        }
        else if (node is JsonObject message)
        {
            await HandleMessageAsync(message, ct);
        }
        else
        {
            await SendAsync(ErrorResponse(null, -32600, "Invalid Request"));
        }
    }

    private async Task HandleMessageAsync(JsonObject message, CancellationToken ct)
    {
        JsonNode? id = message["id"]?.DeepClone();
        bool isNotification = !message.ContainsKey("id");
        if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            // method가 없으면 클라이언트 응답(이 서버는 요청을 보내지 않음), 문자열이 아니면 잘못된 요청
            if (message.ContainsKey("method") && !isNotification) await SendAsync(ErrorResponse(id, -32600, "Invalid Request"));
            return;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();

        try
        {
            JsonNode? result;
            switch (method)
            {
                case "initialize":
                    result = Initialize(parameters);
                    break;
                case "ping":
                    result = new JsonObject();
                    break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)t.ToJson()).ToArray()) };
                    break;
                case "tools/call":
                    result = await CallToolAsync(parameters, id, ct);
                    break;
                case "resources/list":
                    result = new JsonObject { ["resources"] = new JsonArray() };
                    break;
                case "resources/templates/list":
                    result = new JsonObject { ["resourceTemplates"] = new JsonArray() };
                    break;
                case "prompts/list":
                    result = new JsonObject { ["prompts"] = new JsonArray() };
                    break;
                case "notifications/cancelled":
                    string? requestId = parameters["requestId"]?.ToJsonString();
                    if (requestId != null && _inflight.TryGetValue(requestId, out var cts)) cts.Cancel();
                    return;
                default:
                    if (method.StartsWith("notifications/", StringComparison.Ordinal) || isNotification) return;
                    await SendAsync(ErrorResponse(id, -32601, $"Method not found: {method}"));
                    return;
            }

            if (!isNotification) await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (McpException ex)
        {
            if (!isNotification) await SendAsync(ErrorResponse(id, ex.Code, ex.Message));
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[mcp] {method} 처리 중 오류: {ex}");
            if (!isNotification) await SendAsync(ErrorResponse(id, -32603, ex.Message));
        }
    }

    private static JsonObject Initialize(JsonObject parameters)
    {
        string? requested = parameters["protocolVersion"]?.GetValue<string>();
        string version = requested != null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = AppPaths.McpServerName,
                ["title"] = "Modding Support Tool (MelonLoader 레퍼런스)",
                ["version"] = AppPaths.Version
            },
            ["instructions"] = Instructions
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, JsonNode? id, CancellationToken ct)
    {
        string? name = parameters["name"]?.GetValue<string>();
        var tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new McpException(-32602, $"Unknown tool: {name}");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        JsonNode? progressToken = parameters["_meta"]?["progressToken"]?.DeepClone();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? key = id?.ToJsonString();
        if (key != null) _inflight[key] = cts;

        var watch = Stopwatch.StartNew();
        try
        {
            var context = new McpCallContext(cts.Token, progressToken != null ? CreateProgressReporter(progressToken) : null);
            ToolResult result;
            try
            {
                result = await tool.Handler(new McpArguments(arguments), context);
            }
            catch (OperationCanceledException)
            {
                result = ToolResult.Fail("작업이 취소되었습니다.");
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail($"{tool.Name} 실행 중 오류: {ex.Message}");
            }

            _log.WriteLine($"[mcp] {tool.Name} {(result.IsError ? "실패" : "완료")} ({watch.ElapsedMilliseconds}ms)");
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
                ["isError"] = result.IsError
            };
        }
        finally
        {
            if (key != null) _inflight.TryRemove(key, out _);
        }
    }

    // 긴 덤프 동안 클라이언트에 진행 상황을 보낸다. (초당 몇 번으로 제한)
    private Action<string> CreateProgressReporter(JsonNode progressToken)
    {
        int count = 0;
        long lastSent = 0;
        return line =>
        {
            int n = Interlocked.Increment(ref count);
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastSent) < 300) return;
            Interlocked.Exchange(ref lastSent, now);

            _ = SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JsonObject
                {
                    ["progressToken"] = progressToken.DeepClone(),
                    ["progress"] = n,
                    ["message"] = line.Length > 200 ? line[..200] : line
                }
            });
        };
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions) + "\n");
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteAsync(bytes);
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>"ModdingSupportTool.exe mcp [--ref 폴더]" 진입점. stdout은 프로토콜 전용이라 다른 출력은 stderr로 돌린다.</summary>
    public static Task<int> RunStdioAsync(string? referenceDirectory)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stderr);
        Console.SetError(stderr);

        var toolkit = new ModdingToolkit { DefaultReferenceDirectory = referenceDirectory };
        return new McpServer(stdin, stdout, stderr, toolkit).RunAsync();
    }
}

public sealed class McpException : Exception
{
    public McpException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}
