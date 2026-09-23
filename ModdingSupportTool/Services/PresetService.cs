using System;
using System.Collections.Generic;
using System.IO;

namespace ModdingSupportTool.Services;

public sealed record PathPreset(string Key, string DisplayName, string Path);

public static class PresetService
{
    private static readonly string[] DefaultPathCandidates =
    {
        @"H:\muse dash hwa\MelonLoader\Il2CppAssemblies",
        @"H:\steam\steamapps\common\Muse Dash\MelonLoader\Il2CppAssemblies",
        @"H:\steam\steamapps\common\MiSide\MelonLoader\Il2CppAssemblies",
        @"H:\muse dash hwa",
        @"H:\steam\steamapps\common\Muse Dash"
    };

    public static readonly IReadOnlyList<PathPreset> Presets = new List<PathPreset>
    {
        new("MuseDashHwa", "Muse Dash (Hwa - Il2Cpp)", @"H:\muse dash hwa\MelonLoader\Il2CppAssemblies"),
        new("ShortHikeMono", "A Short Hike (Mono)", @"H:\steam\steamapps\common\A Short Hike"),
        new("SixtarGate", "Sixtar Gate (Il2Cpp)", @"H:\steam\steamapps\common\Sixtar Gate STARTRAIL"),
        new("SteamMuseDash", "Steam Muse Dash", @"H:\steam\steamapps\common\Muse Dash"),
        new("SteamMiSide", "Steam MiSide", @"H:\steam\steamapps\common\MiSide\MelonLoader\Il2CppAssemblies")
    };

    public static string ResolveInitialPath()
    {
        foreach (var candidate in DefaultPathCandidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return DefaultPathCandidates[0];
    }

    public static string? GetPresetPath(string presetKey)
    {
        foreach (var preset in Presets)
        {
            if (preset.Key.Equals(presetKey, StringComparison.OrdinalIgnoreCase))
            {
                return preset.Path;
            }
        }

        return null;
    }
}
