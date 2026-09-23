using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModdingSupportTool.Core.Detection;

public sealed record UnityDetectionResult(
    UnityBackend Backend,
    string Reason,
    string? RecommendedAssemblyPath = null,
    string? DataDirectory = null);

public static class UnityBackendDetector
{
    private const int MaxScanDepth = 8;

    private static readonly string[] IgnoredKeywords =
        { "BepInEx", "Voice Editor", "Modded", "BackUpThisFolder", "__MACOSX" };

    private static readonly string[] MonoAssemblies =
        { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "UnityEngine.dll", "UnityEngine.CoreModule.dll" };

    public static UnityDetectionResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new UnityDetectionResult(UnityBackend.Unknown, "경로가 지정되지 않았습니다.");
        }

        try
        {
            // 1. 단일 파일인 경우
            if (File.Exists(path))
            {
                return DetectFromFile(path);
            }

            // 2. 디렉터리인 경우
            if (Directory.Exists(path))
            {
                return DetectFromDirectory(path);
            }

            return new UnityDetectionResult(UnityBackend.Unknown, "존재하지 않는 파일 또는 폴더입니다.");
        }
        catch (Exception ex)
        {
            return new UnityDetectionResult(UnityBackend.Unknown, $"판별 중 오류 발생: {ex.Message}");
        }
    }

    private static UnityDetectionResult DetectFromFile(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            return new UnityDetectionResult(UnityBackend.Unknown, "파일 상위 경로를 확인할 수 없습니다.");
        }

        // MelonLoader/Il2CppAssemblies 안의 DLL 파일인 경우
        if (dir.Contains("Il2CppAssemblies", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityDetectionResult(
                UnityBackend.Il2Cpp,
                "MelonLoader Il2Cpp 어셈블리 폴더 내 파일 (Il2CppAssemblies)",
                dir);
        }

        // 상위 디렉터리에서 Unity 백엔드 분석 수행
        var dirResult = DetectFromDirectory(dir);
        if (dirResult.Backend != UnityBackend.Unknown)
        {
            return dirResult with { RecommendedAssemblyPath = filePath };
        }

        // 2단계 상위 폴더까지 탐색 (예: Game_Data/Managed/Assembly-CSharp.dll -> Game_Data -> Game root)
        string? parent = Directory.GetParent(dir)?.FullName;
        if (parent != null)
        {
            var parentResult = DetectFromDirectory(parent);
            if (parentResult.Backend != UnityBackend.Unknown)
            {
                return parentResult with { RecommendedAssemblyPath = filePath };
            }
        }

        return new UnityDetectionResult(UnityBackend.Unknown, "어셈블리에서 Unity 백엔드를 판별할 수 없습니다.", filePath);
    }

    private static UnityDetectionResult DetectFromDirectory(string dir)
    {
        // A. 특수 폴더 이름 직접 매칭: MelonLoader/Il2CppAssemblies
        if (dir.EndsWith("Il2CppAssemblies", StringComparison.OrdinalIgnoreCase) ||
            dir.Contains("MelonLoader" + Path.DirectorySeparatorChar + "Il2CppAssemblies", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityDetectionResult(
                UnityBackend.Il2Cpp,
                "MelonLoader Il2CppAssemblies 폴더",
                dir);
        }

        // B. Managed 폴더인 경우
        if (Path.GetFileName(dir).Equals("Managed", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent != null)
            {
                // 부모 *_Data에 il2cpp_data가 있는지 확인
                if (File.Exists(Path.Combine(parent, "il2cpp_data", "Metadata", "global-metadata.dat")))
                {
                    return new UnityDetectionResult(UnityBackend.Il2Cpp, "global-metadata.dat 감지됨", dir, parent);
                }

                // 부모의 부모(게임 루트)에 GameAssembly.dll이 있는지 확인
                string? gameRoot = Directory.GetParent(parent)?.FullName;
                if (gameRoot != null && File.Exists(Path.Combine(gameRoot, "GameAssembly.dll")))
                {
                    return new UnityDetectionResult(UnityBackend.Il2Cpp, "GameAssembly.dll 감지됨", dir, parent);
                }
            }

            // Mono 어셈블리 존재 확인
            bool hasMonoAsm = MonoAssemblies.Any(m => File.Exists(Path.Combine(dir, m)));
            if (hasMonoAsm)
            {
                return new UnityDetectionResult(UnityBackend.Mono, "Managed 폴더 내 Mono 어셈블리 감지", dir, parent);
            }
        }

        // C. 단일 *_Data 폴더인 경우
        if (Path.GetFileName(dir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
        {
            var singleDataResult = CheckDataDirectory(dir);
            if (singleDataResult != null) return singleDataResult;
        }

        // D. 게임 설치 루트 디렉터리 탐색 (steam game tool UnityInspector 로직)
        return ScanGameDirectory(dir);
    }

    private static UnityDetectionResult? CheckDataDirectory(string dataDir)
    {
        string? playerDir = Directory.GetParent(dataDir)?.FullName;

        // 1. global-metadata.dat 확인
        if (File.Exists(Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat")))
        {
            string? melonAssemblies = playerDir != null
                ? Path.Combine(playerDir, "MelonLoader", "Il2CppAssemblies")
                : null;

            string recPath = (melonAssemblies != null && Directory.Exists(melonAssemblies))
                ? melonAssemblies
                : dataDir;

            return new UnityDetectionResult(
                UnityBackend.Il2Cpp,
                "global-metadata.dat 메타데이터 존재",
                recPath,
                dataDir);
        }

        // 2. GameAssembly.dll + UnityPlayer.dll 페어 확인
        if (playerDir != null &&
            File.Exists(Path.Combine(playerDir, "GameAssembly.dll")) &&
            File.Exists(Path.Combine(playerDir, "UnityPlayer.dll")))
        {
            string melonAssemblies = Path.Combine(playerDir, "MelonLoader", "Il2CppAssemblies");
            string recPath = Directory.Exists(melonAssemblies) ? melonAssemblies : dataDir;

            return new UnityDetectionResult(
                UnityBackend.Il2Cpp,
                "GameAssembly.dll & UnityPlayer.dll 네이티브 페어 존재",
                recPath,
                dataDir);
        }

        // 3. Managed 폴더 및 Mono 어셈블리 확인
        string managedDir = Path.Combine(dataDir, "Managed");
        if (Directory.Exists(managedDir))
        {
            bool hasMonoAsm = MonoAssemblies.Any(m => File.Exists(Path.Combine(managedDir, m)));
            if (hasMonoAsm)
            {
                return new UnityDetectionResult(
                    UnityBackend.Mono,
                    "Managed 폴더 내 C# 어셈블리 존재 (Mono)",
                    managedDir,
                    dataDir);
            }
        }

        // 4. MonoBleedingEdge / Mono 런타임 확인
        bool hasRuntime = (playerDir != null && Directory.Exists(Path.Combine(playerDir, "MonoBleedingEdge"))) ||
                          Directory.Exists(Path.Combine(dataDir, "MonoBleedingEdge")) ||
                          Directory.Exists(Path.Combine(dataDir, "Mono"));

        if (hasRuntime && Directory.Exists(managedDir))
        {
            return new UnityDetectionResult(
                UnityBackend.Mono,
                "Mono 런타임 및 Managed 폴더 존재",
                managedDir,
                dataDir);
        }

        return null;
    }

    private static UnityDetectionResult ScanGameDirectory(string installDir)
    {
        // MelonLoader/Il2CppAssemblies가 최상위에 있는 경우
        string melonAssembliesPath = Path.Combine(installDir, "MelonLoader", "Il2CppAssemblies");
        bool hasMelonIl2Cpp = Directory.Exists(melonAssembliesPath);

        // 게임 디렉터리 내의 *_Data 폴더 탐색 (최대 깊이 8)
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((installDir, 0));

        var dataDirs = new List<string>();

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
                    IgnoredKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("il2cpp_data", StringComparison.OrdinalIgnoreCase))
                {
                    dataDirs.Add(sub);
                    continue; // *_Data 아래로는 더 내려가지 않음
                }

                if (depth + 1 < MaxScanDepth)
                {
                    stack.Push((sub, depth + 1));
                }
            }
        }

        // 수집된 *_Data 폴더들에 대해 백엔드 판별 실행
        foreach (var dataDir in dataDirs.OrderBy(d => d.Length))
        {
            var result = CheckDataDirectory(dataDir);
            if (result != null)
            {
                // Il2Cpp인 경우 MelonLoader/Il2CppAssemblies가 있으면 해당 경로를 권장
                if (result.Backend == UnityBackend.Il2Cpp && hasMelonIl2Cpp)
                {
                    return result with { RecommendedAssemblyPath = melonAssembliesPath };
                }
                return result;
            }
        }

        // *_Data를 못 찾았으나 최상위에 GameAssembly.dll이 있는 경우
        if (File.Exists(Path.Combine(installDir, "GameAssembly.dll")))
        {
            return new UnityDetectionResult(
                UnityBackend.Il2Cpp,
                "루트에 GameAssembly.dll 발견",
                hasMelonIl2Cpp ? melonAssembliesPath : installDir);
        }

        // MonoBleedingEdge 런타임만 있는 경우
        if (Directory.Exists(Path.Combine(installDir, "MonoBleedingEdge")))
        {
            return new UnityDetectionResult(
                UnityBackend.Mono,
                "MonoBleedingEdge 런타임 발견",
                installDir);
        }

        return new UnityDetectionResult(
            UnityBackend.Unknown,
            "Unity 백엔드 마커를 찾지 못했습니다 (비Unity 또는 비표준 구조).",
            installDir);
    }
}
