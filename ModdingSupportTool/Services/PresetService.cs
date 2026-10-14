using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModdingSupportTool.Services;

public sealed record PathPreset(string Key, string DisplayName, string Path);

/// <summary>자주 분석하는 리듬게임 경로 프리셋. GUI 버튼과 CLI 기본 경로(--cli)가 이 목록 하나를 쓴다.</summary>
public static class PresetService
{
    // 순서 = 기본 경로 우선순위 (ResolveInitialPath)
    public static readonly IReadOnlyList<PathPreset> Presets = new List<PathPreset>
    {
        new("MuseDashHwa", "Muse Dash (Hwa)", @"H:\muse dash hwa\MelonLoader\Il2CppAssemblies"),
        new("SixtarCustomMono", "Sixtar Gate (Custom - Mono)", @"H:\Sixtar Gate STARTRAIL custom mode"),
        new("GunvoltRecords", "Gunvolt Records (Mono)", @"H:\steam\steamapps\common\GUNVOLT RECORDS Cychronicle"),
        new("SixtarSteam", "Sixtar Gate (Steam)", @"H:\steam\steamapps\common\Sixtar Gate STARTRAIL"),
        new("SixtarStargazer", "Sixtar Stargazer", @"H:\steam\steamapps\common\Sixtar Gate STARGAZER"),
        new("KalpaMono", "KALPA (Mono)", @"H:\steam\steamapps\common\KALPA CosmicSymphony")
    };

    /// <summary>이 PC에 실제로 있는 첫 번째 프리셋 경로. 하나도 없으면 빈 문자열.</summary>
    public static string ResolveInitialPath() =>
        Presets.FirstOrDefault(p => Directory.Exists(p.Path))?.Path ?? string.Empty;

    public static string? GetPresetPath(string presetKey) =>
        Presets.FirstOrDefault(p => p.Key.Equals(presetKey, StringComparison.OrdinalIgnoreCase))?.Path;
}
