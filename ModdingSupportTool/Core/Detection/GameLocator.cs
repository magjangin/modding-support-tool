using System;
using System.IO;
using System.Linq;

namespace ModdingSupportTool.Core.Detection;

/// <summary>게임 설치 루트와 이름. 템플릿(MelonGame 어트리뷰트, 참조 경로)과 게임별 레퍼런스 폴더 이름에 쓴다.</summary>
public sealed record GameInfo(
    string Root,
    string Name,
    string? DataDirectory,
    string? MelonLoaderDirectory,
    string? Company,
    string? Product);

public static class GameLocator
{
    private const int MaxParentLevels = 6;

    /// <summary>게임 폴더·*_Data·Managed·Il2CppAssemblies·DLL 어디를 넣어도 위로 올라가며 게임 루트를 찾는다.</summary>
    public static GameInfo? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string? dir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.GetFullPath(path);
            for (int level = 0; dir != null && level <= MaxParentLevels; level++, dir = Path.GetDirectoryName(dir))
            {
                if (!Directory.Exists(dir)) continue;
                string? dataDir = FindDataDirectory(dir);
                bool hasMelonLoader = Directory.Exists(Path.Combine(dir, "MelonLoader"));
                if (dataDir != null || hasMelonLoader || File.Exists(Path.Combine(dir, "UnityPlayer.dll")))
                {
                    return Build(dir, dataDir, hasMelonLoader);
                }
            }
        }
        catch (Exception)
        {
            // 경로 형식 오류·권한 문제는 "게임 루트 모름"으로 취급한다.
        }

        return null;
    }

    private static string? FindDataDirectory(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*_Data")
                .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Managed")) ||
                                     Directory.Exists(Path.Combine(d, "il2cpp_data")) ||
                                     File.Exists(Path.Combine(d, "globalgamemanagers")));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GameInfo Build(string root, string? dataDir, bool hasMelonLoader)
    {
        string? company = null, product = null;

        // Unity는 *_Data/app.info에 "회사명\n제품명"을 기록한다. MelonGame 어트리뷰트 값과 같다.
        string? appInfo = dataDir != null ? Path.Combine(dataDir, "app.info") : null;
        if (appInfo != null && File.Exists(appInfo))
        {
            var lines = File.ReadAllLines(appInfo);
            company = lines.Length > 0 && lines[0].Trim().Length > 0 ? lines[0].Trim() : null;
            product = lines.Length > 1 && lines[1].Trim().Length > 0 ? lines[1].Trim() : null;
        }

        return new GameInfo(
            root,
            product ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            dataDir,
            hasMelonLoader ? Path.Combine(root, "MelonLoader") : null,
            company,
            product);
    }

    /// <summary>폴더 이름으로 쓸 수 있게 게임 이름을 정리한다.</summary>
    public static string ToFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "Game";
    }
}
