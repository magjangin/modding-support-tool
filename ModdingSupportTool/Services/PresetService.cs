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
        @"H:\Sixtar Gate STARTRAIL custom mode",
        @"H:\steam\steamapps\common\GUNVOLT RECORDS Cychronicle",
        @"H:\muse dash hwa",
        @"H:\steam\steamapps\common\Sixtar Gate STARTRAIL",
        @"H:\steam\steamapps\common\Sixtar Gate STARGAZER",
        @"H:\steam\steamapps\common\KALPA CosmicSymphony"
    };

    public static readonly IReadOnlyList<PathPreset> Presets = new List<PathPreset>
    {
        new("MuseDashHwa", "Muse Dash (Hwa)", @"H:\muse dash hwa\MelonLoader\Il2CppAssemblies"),
        new("SixtarCustomMono", "Sixtar Gate (Custom - Mono)", @"H:\Sixtar Gate STARTRAIL custom mode"),
        new("GunvoltRecords", "Gunvolt Records (Mono)", @"H:\steam\steamapps\common\GUNVOLT RECORDS Cychronicle"),
        new("SixtarSteam", "Sixtar Gate (Steam)", @"H:\steam\steamapps\common\Sixtar Gate STARTRAIL"),
        new("SixtarStargazer", "Sixtar Stargazer", @"H:\steam\steamapps\common\Sixtar Gate STARGAZER"),
        new("KalpaMono", "KALPA (Mono)", @"H:\steam\steamapps\common\KALPA CosmicSymphony")
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
