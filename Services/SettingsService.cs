using MidiAutoPlayer.Help;
using MidiAutoPlayer.Models;
using System.Runtime.InteropServices;

namespace MidiAutoPlayer.Services;

public sealed class SettingsService
{
    private readonly AppConfig _config;
    private readonly Action _saveConfigCallback;
    private readonly WallpaperService _wallpaper;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    public SettingsService(AppConfig config, Action saveConfigCallback)
        : this(config, saveConfigCallback, new WallpaperService())
    {
    }

    public SettingsService(AppConfig config, Action saveConfigCallback, WallpaperService wallpaper)
    {
        _config = config;
        _saveConfigCallback = saveConfigCallback;
        _wallpaper = wallpaper;
    }

    public object GetSettings()
    {
        return new
        {
            recommendedSubmissionUploaderName = _config.RecommendedSubmissionUploaderName,
            playMode = _config.PlayMode, // 0: Piano, 1: Fret, 2: Chord, 3: Hybrid, 4: Drums
            drumKeyLayout = _config.DrumKeyLayout,
            chordMode = _config.ChordMode.ToString(),
            playStyle = (int)_config.PlayStyle,
            autoStrum = (int)_config.AutoStrum,
            useVirtualGamepad = _config.UseVirtualGamepad,
            avoidStrum = _config.AvoidStrum,
            enableArrangedAccompaniment = _config.EnableArrangedAccompaniment,
            transpose = _config.Transpose,
            preRollMs = _config.PreRollMs,
            enableSustain = _config.EnableSustain,
            recommendDynamicStartPage = _config.RecommendDynamicStartPage,
            autoStopOnFocusLoss = _config.AutoStopOnFocusLoss,
            minHoldMs = Math.Clamp(_config.MinHoldMs, 5, 100),
            releaseGapMs = _config.ReleaseGapMs,
            tapDurationMs = _config.TapDurationMs,
            densePulseHoldMs = _config.DensePulseHoldMs,
            wheelMouseSettleMs = _config.WheelMouseSettleMs,
            pageKeyHoldMs = _config.PageKeyHoldMs,
            pageInterTapGapMs = _config.PageInterTapGapMs,
            pageInputSafetyMs = _config.PageInputSafetyMs,
            doNotShowTutorialOnStartup = _config.DoNotShowTutorialOnStartup,
            tutorialVersionSeen = _config.TutorialVersionSeen,
            globalHotkeyVk = _config.GlobalHotkeyVk,
            semiAutoPagePrevVk = VirtualKeyFromScanCode(_config.WheelPreset.PagePrevScanCode),
            semiAutoPageNextVk = VirtualKeyFromScanCode(_config.WheelPreset.PageNextScanCode),
            alwaysOnTop = _config.AlwaysOnTop,
            disableGlassEffect = _config.DisableGlassEffect,
            wallpaperEnabled = _config.WallpaperEnabled && _wallpaper.IsManagedAsset(_config.WallpaperFileName),
            wallpaperMissing = _config.WallpaperEnabled && !_wallpaper.IsManagedAsset(_config.WallpaperFileName),
            wallpaperFileName = _wallpaper.IsManagedAsset(_config.WallpaperFileName) ? _config.WallpaperFileName : "",
            wallpaperMediaType = _wallpaper.IsManagedAsset(_config.WallpaperFileName) ? _wallpaper.GetMediaType(_config.WallpaperFileName) : "",
            wallpaperMediaUrl = _wallpaper.IsManagedAsset(_config.WallpaperFileName) ? _wallpaper.GetMediaUrl(_config.WallpaperFileName) : "",
            wallpaperScale = Math.Clamp(_config.WallpaperScale, 1, 3),
            wallpaperOffsetX = Math.Clamp(_config.WallpaperOffsetX, -1, 1),
            wallpaperOffsetY = Math.Clamp(_config.WallpaperOffsetY, -1, 1)
        };
    }

    public object UpdateRecommendedSubmissionUploaderName(string? uploaderName)
    {
        string normalized = (uploaderName ?? "").Trim();
        if (normalized.Length > 80)
            throw new ArgumentException("投稿者名称最多 80 个字符。", nameof(uploaderName));

        _config.RecommendedSubmissionUploaderName = normalized;
        _saveConfigCallback();
        return new { recommendedSubmissionUploaderName = normalized };
    }

    public object ApplyWallpaper(string fileName, string mediaType, double scale, double offsetX, double offsetY)
    {
        if (!_wallpaper.IsManagedAsset(fileName))
            throw new ArgumentException("壁纸文件不存在或无效，请重新选择。", nameof(fileName));
        if (!string.Equals(_wallpaper.GetMediaType(fileName), mediaType, StringComparison.Ordinal))
            throw new ArgumentException("壁纸媒体类型无效。", nameof(mediaType));

        _config.WallpaperEnabled = true;
        _config.WallpaperFileName = fileName;
        _config.WallpaperMediaType = mediaType;
        _config.WallpaperScale = Math.Clamp(scale, 1, 3);
        _config.WallpaperOffsetX = Math.Clamp(offsetX, -1, 1);
        _config.WallpaperOffsetY = Math.Clamp(offsetY, -1, 1);
        _saveConfigCallback();
        return GetSettings();
    }

    public object ClearWallpaper()
    {
        _config.WallpaperEnabled = false;
        _config.WallpaperFileName = "";
        _config.WallpaperMediaType = "";
        _config.WallpaperScale = 1;
        _config.WallpaperOffsetX = 0;
        _config.WallpaperOffsetY = 0;
        _saveConfigCallback();
        return GetSettings();
    }

    public object UpdateSetting(string key, object value)
    {
        switch (key)
        {
            case "playMode":
                _config.PlayMode = Math.Clamp(Convert.ToInt32(value), 0, 4);
                break;
            case "guitarMode":
                if (value is string sm)
                {
                    if (sm.Equals("Piano", StringComparison.OrdinalIgnoreCase) || sm == "0") _config.PlayMode = 0;
                    else if (sm.Equals("Fret", StringComparison.OrdinalIgnoreCase) || sm == "1") _config.PlayMode = 1;
                    else if (sm.Equals("Chord", StringComparison.OrdinalIgnoreCase) || sm == "2") _config.PlayMode = 2;
                    else if (sm.Equals("Hybrid", StringComparison.OrdinalIgnoreCase) || sm == "3") _config.PlayMode = 3;
                }
                else
                {
                    _config.PlayMode = Math.Clamp(Convert.ToInt32(value), 0, 3);
                }
                break;
            case "chordMode":
                if (Enum.TryParse<ChordRecognitionMode>(value.ToString(), out var cm))
                    _config.ChordMode = cm;
                break;
            case "playStyle":
                _config.PlayStyle = (GuitarPlayStyle)Convert.ToInt32(value);
                break;
            case "useVirtualGamepad":
                _config.UseVirtualGamepad = Convert.ToBoolean(value);
                break;
            case "drumKeyLayout":
                string layout = Convert.ToString(value) ?? "DFJK";
                if (layout is not "DFJK" and not "ASQuote")
                    throw new ArgumentException("鼓点键位预设无效。", nameof(value));
                _config.DrumKeyLayout = layout;
                break;
            case "autoStrum":
                _config.AutoStrum = (AutoStrumMode)Convert.ToInt32(value);
                break;
            case "avoidStrum":
                _config.AvoidStrum = Convert.ToBoolean(value);
                break;
            case "enableArrangedAccompaniment":
                _config.EnableArrangedAccompaniment = Convert.ToBoolean(value);
                break;
            case "transpose":
                _config.Transpose = Convert.ToInt32(value);
                break;
            case "preRollMs":
                _config.PreRollMs = Math.Max(0, Convert.ToInt32(value));
                break;
            case "enableSustain":
                _config.EnableSustain = Convert.ToBoolean(value);
                break;
            case "recommendDynamicStartPage":
                _config.RecommendDynamicStartPage = Convert.ToBoolean(value);
                break;
            case "autoStopOnFocusLoss":
                _config.AutoStopOnFocusLoss = Convert.ToBoolean(value);
                break;
            case "alwaysOnTop":
                _config.AlwaysOnTop = Convert.ToBoolean(value);
                break;
            case "disableGlassEffect":
                _config.DisableGlassEffect = Convert.ToBoolean(value);
                break;
            case "globalHotkeyVk":
            {
                int vk = Convert.ToInt32(value);
                ValidateBindableKey(vk, "开始/暂停热键");
                if (vk == VirtualKeyFromScanCode(_config.WheelPreset.PagePrevScanCode) ||
                    vk == VirtualKeyFromScanCode(_config.WheelPreset.PageNextScanCode))
                    throw new ArgumentException("开始/暂停热键不能与半自动翻页键相同。");
                _config.GlobalHotkeyVk = vk;
                break;
            }
            case "semiAutoPagePrevVk":
            {
                var scan = ScanCodeFromVirtualKey(Convert.ToInt32(value), "上一页键");
                EnsurePageKeysDiffer(scan, _config.WheelPreset.PageNextScanCode);
                _config.WheelPreset.PagePrevScanCode = scan;
                break;
            }
            case "semiAutoPageNextVk":
            {
                var scan = ScanCodeFromVirtualKey(Convert.ToInt32(value), "下一页键");
                EnsurePageKeysDiffer(_config.WheelPreset.PagePrevScanCode, scan);
                _config.WheelPreset.PageNextScanCode = scan;
                break;
            }
            case "doNotShowTutorialOnStartup":
                _config.DoNotShowTutorialOnStartup = Convert.ToBoolean(value);
                break;
            case "wheelMouseSettleMs":
                _config.WheelMouseSettleMs = Math.Clamp(Convert.ToInt32(value), 10, 500);
                break;
            case "pageKeyHoldMs":
                _config.PageKeyHoldMs = Math.Clamp(Convert.ToInt32(value), 5, 200);
                break;
            case "pageInterTapGapMs":
                _config.PageInterTapGapMs = Math.Clamp(Convert.ToInt32(value), 5, 200);
                break;
            case "minHoldMs":
                _config.MinHoldMs = Math.Clamp(Convert.ToInt32(value), 5, 100);
                break;
            case "releaseGapMs":
                _config.ReleaseGapMs = Math.Clamp(Convert.ToInt32(value), 1, 50);
                break;
            case "densePulseHoldMs":
                _config.DensePulseHoldMs = Math.Clamp(Convert.ToInt32(value), 5, 200);
                break;
            case "tapDurationMs":
                _config.TapDurationMs = Math.Clamp(Convert.ToInt32(value), 5, 200);
                break;
        }

        _saveConfigCallback();
        return GetSettings();
    }

    private void EnsurePageKeysDiffer(ushort prevScanCode, ushort nextScanCode)
    {
        var prev = VirtualKeyFromScanCode(prevScanCode);
        var next = VirtualKeyFromScanCode(nextScanCode);
        if (prev == next) throw new ArgumentException("上一页键和下一页键不能相同。");
        if (_config.GlobalHotkeyVk == prev || _config.GlobalHotkeyVk == next)
            throw new ArgumentException("半自动翻页键不能与开始/暂停热键相同。");
    }

    private static ushort ScanCodeFromVirtualKey(int vk, string label)
    {
        ValidateBindableKey(vk, label);
        var scanCode = MapVirtualKey((uint)vk, 0);
        if (scanCode == 0 || scanCode > ushort.MaxValue)
            throw new ArgumentException($"无法识别{label}。");
        return (ushort)scanCode;
    }

    private static int VirtualKeyFromScanCode(ushort scanCode)
    {
        var vk = MapVirtualKey(scanCode, 1);
        return vk <= int.MaxValue ? (int)vk : 0;
    }

    private static void ValidateBindableKey(int vk, string label)
    {
        bool letter = vk is >= 0x41 and <= 0x5A;
        bool function = vk is >= 0x70 and <= 0x87;
        if ((!letter && !function) || vk == 0x78 || vk is >= 0x31 and <= 0x36)
            throw new ArgumentException($"{label}仅支持字母键或 F1～F24，且不能使用 F9 或游戏弦键 1～6。");
    }

    public object ResetTimingDefaults()
    {
        _config.PreRollMs = 1500;
        _config.PageInterTapGapMs = 25;
        _config.PageKeyHoldMs = 25;
        _config.MinHoldMs = 20;
        _config.WheelMouseSettleMs = 75;
        _config.MouseClickHoldMs = 25;
        _config.ReleaseGapMs = 2;
        _config.DensePulseHoldMs = 25;
        _config.TapDurationMs = 30;

        _saveConfigCallback();
        return GetSettings();
    }

    public object GetAlgorithmSections()
    {
        return AlgorithmHelpContent.Sections.Select(s => new
        {
            title = s.Title,
            badge = s.Badge,
            explanation = s.PlainExplanation,
            advice = s.TuningAdvice
        }).ToList();
    }

    public object GetTutorialSteps()
    {
        return TutorialContent.Steps.Select(s => new
        {
            stepNumber = s.StepNumber,
            icon = s.Icon,
            title = s.Title,
            summary = s.Summary,
            advice = s.DetailedAdvice,
            safetyTip = s.SafetyTip
        }).ToList();
    }

    public string ExportDiagnostics(ConvertedSong? currentSong)
    {
        string report = DiagnosticsExportService.GenerateReport(_config, _config.TargetWindowHandle, currentSong, new Library.SongLibrary());
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"NTE_GUITAR_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, report);
        return path;
    }
}
