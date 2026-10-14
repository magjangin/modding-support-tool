using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModdingSupportTool.Core.Detection;

namespace ModdingSupportTool.Core.Reference;

/// <summary>
/// 덤프 출력 폴더 루트의 reference.json. 어떤 게임·백엔드·엔진으로 만든 레퍼런스인지 기록해서
/// 에이전트(MCP/CLI)가 폴더 구조를 추측하지 않고 바로 쓸 수 있게 한다.
/// </summary>
public sealed class ReferenceManifest
{
    public const string FileName = "reference.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Schema { get; set; } = 1;
    public string Tool { get; set; } = "ModdingSupportTool";
    public string ToolVersion { get; set; } = AppPaths.Version;
    public string Purpose { get; set; } = "MelonLoader 모드(DLL) 작성용 C# 레퍼런스";
    public string? GameName { get; set; }
    public string? GameRoot { get; set; }
    public string? Company { get; set; }
    public string? Product { get; set; }
    public UnityBackend Backend { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ReferenceModule> Modules { get; set; } = new();

    public static ReferenceManifest? Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ReferenceManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>모듈 하나의 덤프 결과를 매니페스트에 반영한다. (같은 이름의 모듈은 교체)</summary>
    public static void Record(string outputDirectory, GameInfo? game, UnityBackend backend, ReferenceModule module)
    {
        var manifest = Load(outputDirectory) ?? new ReferenceManifest();
        manifest.ToolVersion = AppPaths.Version;
        manifest.UpdatedAt = DateTime.Now;
        if (game != null)
        {
            manifest.GameName = game.Name;
            manifest.GameRoot = game.Root;
            manifest.Company = game.Company;
            manifest.Product = game.Product;
        }
        if (backend != UnityBackend.Unknown) manifest.Backend = backend;

        manifest.Modules.RemoveAll(m => m.Name.Equals(module.Name, StringComparison.OrdinalIgnoreCase));
        manifest.Modules.Add(module);
        manifest.Modules = manifest.Modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, FileName), JsonSerializer.Serialize(manifest, JsonOptions));
    }
}

public sealed class ReferenceModule
{
    public string Name { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public string? Source { get; set; }

    /// <summary>출력 폴더 기준 상대 경로 (전체 소스 또는 스켈레톤)</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>AI 아웃라인 폴더 (출력 폴더 기준 상대 경로, Mono/ilspycmd만)</summary>
    public string? OutlinePath { get; set; }

    public bool HasMethodBodies { get; set; }
    public int Files { get; set; }
    public int? OmittedInteropMembers { get; set; }
    public DateTime DumpedAt { get; set; }
}
