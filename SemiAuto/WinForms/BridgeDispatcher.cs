using System.Text.Json;
using Nte.GuitarChart.Core;

namespace Nte.GuitarChart.WinForms;

public interface ISemiAutoAppService
{
    Task ChangeModeAsync(PlayMode mode);
    Task SetExecutionModeAsync(PositionExecutionMode mode);
    Task StartAsync(int? initialPage = null);
    Task PauseAsync();
    Task ResetAsync();
    Task SeekAsync(double ratio);
    Task SetSpeedAsync(int speedPercent);
    Task ResynchronizeAsync();
    Task HideOverlayAsync();
    Task SetWindowLockedAsync(bool locked);
    Task SetThemeAsync(string theme);
    Task BeginPositionSwitchKeyCaptureAsync();
    Task BeginLaneKeyCaptureAsync(int lane);
    Task CancelLaneKeyCaptureAsync();
    Task ResetLaneKeyBindingsAsync();
    Task ResetPositionSwitchKeyAsync();
    Task SetOverlayOpacityAsync(int percent);
    Task SetOverlayWideLayoutAsync(bool wide);
    Task SetAutoImmersionAsync(bool enabled);
    Task BeginImmersionHotkeyCaptureAsync();
    Task CancelImmersionHotkeyCaptureAsync();
    Task SetImmersionHotkeyAsync(int virtualKey);
    Task SetManualPracticePositionAsync(bool enabled);
    Task ListGuitarSongsAsync(string query);
    Task SelectGuitarSongAsync(string songId);
    Task ListRecommendedGuitarScoresAsync(string query);
    Task SelectRecommendedGuitarScoreAsync(string scoreId);
    Task BeginWindowDragAsync();
    Task WebReadyAsync();
}

public sealed class BridgeDispatcher
{
    private readonly ISemiAutoAppService _app;

    public BridgeDispatcher(ISemiAutoAppService app) => _app = app;

    public Task DispatchAsync(string type, JsonElement payload)
    {
        return type switch
        {
            "mode.change" => _app.ChangeModeAsync(ParseMode(payload)),
            "execution.mode.change" => _app.SetExecutionModeAsync(ReadString(payload, "mode") switch
            {
                "full-manual" => PositionExecutionMode.FullManual,
                "semi-auto" => PositionExecutionMode.SemiAuto,
                _ => throw new ArgumentException("Unknown execution mode.")
            }),
            "play.start" => _app.StartAsync(ParseInitialPage(payload)),
            "play.pause" => _app.PauseAsync(),
            "play.reset" => _app.ResetAsync(),
            "play.seek" => _app.SeekAsync(ReadDouble(payload, "ratio")),
            "speed.change" => _app.SetSpeedAsync(ParseSpeed(payload)),
            "position.resync" => _app.ResynchronizeAsync(),
            "window.hide" => _app.HideOverlayAsync(),
            "window.lock" => _app.SetWindowLockedAsync(ReadBool(payload, "locked")),
            "theme.change" => _app.SetThemeAsync(ReadString(payload, "theme")),
            "window.opacity" => _app.SetOverlayOpacityAsync(ReadInt(payload, "percent")),
            "window.layout" => _app.SetOverlayWideLayoutAsync(ReadString(payload, "layout") switch
            {
                "portrait" => false,
                "wide" => true,
                _ => throw new ArgumentException("Unknown overlay layout.")
            }),
            "immersion.auto" => _app.SetAutoImmersionAsync(ReadBool(payload, "enabled")),
            "immersion.hotkey.capture" => _app.BeginImmersionHotkeyCaptureAsync(),
            "immersion.hotkey.cancel" => _app.CancelImmersionHotkeyCaptureAsync(),
            "immersion.hotkey.set" => _app.SetImmersionHotkeyAsync(ParseVirtualKey(payload)),
            "position.manual-practice" => _app.SetManualPracticePositionAsync(ReadBool(payload, "enabled")),
            "window.drag" => _app.BeginWindowDragAsync(),
            "position-key.capture" => _app.BeginPositionSwitchKeyCaptureAsync(),
            "position-key.reset" => _app.ResetPositionSwitchKeyAsync(),
            "lane-key.capture" => _app.BeginLaneKeyCaptureAsync(ParseLane(payload)),
            "lane-key.cancel" => _app.CancelLaneKeyCaptureAsync(),
            "lane-key.reset" => _app.ResetLaneKeyBindingsAsync(),
            "library.guitar.list" => _app.ListGuitarSongsAsync(ReadString(payload, "query")),
            "library.guitar.select" => _app.SelectGuitarSongAsync(ReadString(payload, "songId")),
            "library.recommended.list" => _app.ListRecommendedGuitarScoresAsync(ReadString(payload, "query")),
            "library.recommended.select" => _app.SelectRecommendedGuitarScoreAsync(ReadString(payload, "scoreId")),
            "web.ready" => _app.WebReadyAsync(),
            "chart.loaded" => Task.CompletedTask,
            _ => Task.CompletedTask
        };
    }

    private static PlayMode ParseMode(JsonElement payload) =>
        ReadString(payload, "mode") switch
        {
            "practice" => PlayMode.Practice,
            "rhythm-normal" => PlayMode.RhythmNormal,
            "rhythm-hell" => PlayMode.RhythmHell,
            "auto-showcase" => PlayMode.AutoShowcase,
            "simulated" => PlayMode.Simulated,
            _ => throw new ArgumentException("Unknown play mode.")
        };

    private static int ParseSpeed(JsonElement payload)
    {
        var value = payload.GetProperty("speedPercent").GetInt32();
        if (value is < 25 or > 200)
            throw new ArgumentOutOfRangeException(nameof(payload), "Speed must be 25..200 percent.");
        return value;
    }

    private static int ParseLane(JsonElement payload)
    {
        int lane = ReadInt(payload, "lane");
        if (lane is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(payload), "Lane must be 1..7.");
        return lane;
    }

    private static int ParseVirtualKey(JsonElement payload)
    {
        if (!payload.TryGetProperty("virtualKey", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int virtualKey))
            throw new ArgumentException("A virtualKey integer is required.", nameof(payload));
        if (!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsSupportedVirtualKey(virtualKey))
            throw new ArgumentOutOfRangeException(nameof(payload), "Immersion hotkey must be a letter, digit, function key, or punctuation key.");
        return virtualKey;
    }

    private static int? ParseInitialPage(JsonElement payload)
    {
        if (!payload.TryGetProperty("initialPage", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int page) || page is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(payload), "起始页必须为 P1～P5。");
        return page;
    }

    private static string ReadString(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var elem) ? (elem.GetString() ?? "") : "";

    private static bool ReadBool(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var elem) && elem.GetBoolean();

    private static double ReadDouble(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var elem) && elem.TryGetDouble(out var value)
            ? Math.Clamp(value, 0d, 1d)
            : 0d;

    private static int ReadInt(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var elem) && elem.TryGetInt32(out var value) ? value : 92;
}
