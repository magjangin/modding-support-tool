using System;
using System.IO;
using System.Reflection;

namespace ModdingSupportTool.Core;

public static class AppPaths
{
    public const string McpServerName = "modding-support-tool";

    public static string Version { get; } =
        typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppPaths).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>현재 실행 중인 exe 경로. MCP 서버 등록 명령에 그대로 들어간다.</summary>
    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ModdingSupportTool.exe");

    /// <summary>저장소 루트(ModdingSupportTool.slnx가 있는 폴더). 저장소 밖 배포본이면 null.</summary>
    public static string? RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// 기본 출력 폴더. 저장소 안에서 실행하면(bin, Publish 어디든) 저장소 루트의 Decompiled,
    /// 저장소 밖 배포본이면 exe 옆의 Decompiled.
    /// </summary>
    public static string DefaultOutputDirectory { get; } =
        Path.Combine(RepositoryRoot ?? AppContext.BaseDirectory, "Decompiled");

    /// <summary>bin\Debug 같은 빌드 폴더의 exe면 true. MCP에 등록하기엔 경로가 자주 바뀌므로 경고에 쓴다.</summary>
    public static bool IsRunningFromBuildOutput =>
        ExecutablePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ModdingSupportTool.slnx")))
            {
                return dir.FullName;
            }
        }
        return null;
    }
}
