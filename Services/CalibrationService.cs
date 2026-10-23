using System.Drawing;
using System.Runtime.InteropServices;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public sealed class CalibrationService
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly AppConfig _config;
    private readonly WheelController _wheelController;
    private readonly Action _saveConfigCallback;
    private readonly Func<bool> _saveCalibrationConfigCallback;
    private PendingAutoCalibration? _pendingAutoCalibration;

    public CalibrationService(AppConfig config, WheelController wheelController, Action saveConfigCallback, Func<bool>? saveCalibrationConfigCallback = null)
    {
        _config = config;
        _wheelController = wheelController;
        _saveConfigCallback = saveConfigCallback;
        _saveCalibrationConfigCallback = saveCalibrationConfigCallback ?? (() => false);
    }

    public object GetStatus()
    {
        var compatService = new DeviceCompatibilityService();
        var report = compatService.CheckAsync(_config.TargetWindowHandle, _config.WheelPreset).GetAwaiter().GetResult();
        bool isBound = _config.TargetWindowHandle != IntPtr.Zero;
        bool isForeground = isBound && KeyboardSender.IsForeground(_config.TargetWindowHandle);
        var preset = _config.WheelPreset;

        var slots = new[]
        {
            new { index = 1, name = "Slot 1 (上: G 和弦 / I 品格)", x = preset.Slots[0].X, y = preset.Slots[0].Y },
            new { index = 2, name = "Slot 2 (右上: Am 和弦 / II 品格)", x = preset.Slots[1].X, y = preset.Slots[1].Y },
            new { index = 3, name = "Slot 3 (右下: Bm 和弦 / III 品格)", x = preset.Slots[2].X, y = preset.Slots[2].Y },
            new { index = 4, name = "Slot 4 (下: C 和弦 / IV 品格)", x = preset.Slots[3].X, y = preset.Slots[3].Y },
            new { index = 5, name = "Slot 5 (左下: D 和弦 / V 品格)", x = preset.Slots[4].X, y = preset.Slots[4].Y },
            new { index = 6, name = "Slot 6 (左上: Em 和弦 / VI 品格)", x = preset.Slots[5].X, y = preset.Slots[5].Y },
        };

        return new
        {
            window = new
            {
                handle = _config.TargetWindowHandle.ToInt64(),
                title = _config.TargetWindowTitle ?? "未绑定",
                isBound = isBound,
                isForeground = isForeground,
                level = report.State.ToString(),
                summary = report.Summary,
                detail = report.Detail
            },
            slots,
            strumPoint = new { x = preset.StrumPoint.X, y = preset.StrumPoint.Y },
            calibration = new
            {
                clientWidth = preset.CalibrationClientWidth,
                clientHeight = preset.CalibrationClientHeight,
                source = preset.CalibrationSource
            },
            pagePrevKey = "R (0x13)",
            pageNextKey = "T (0x14)"
        };
    }

    public object BindGameWindow(IntPtr hWnd, string title)
    {
        _pendingAutoCalibration = null;
        _config.TargetWindowHandle = hWnd;
        _config.TargetWindowTitle = title;
        _saveConfigCallback();

        return GetStatus();
    }

    public void StageAutoCalibration(IntPtr hWnd, uint processId, string title, Size clientSize, IReadOnlyList<SlotDetectionCandidate> candidates, bool manualOnly = false)
    {
        _pendingAutoCalibration = new PendingAutoCalibration(hWnd, processId, title, clientSize, candidates.ToArray(), manualOnly);
    }

    public object ConfirmAutoCalibration(double[]? slotXs = null, double[]? slotYs = null)
    {
        var pending = _pendingAutoCalibration;
        if (pending is null)
            return new { success = false, reason = "没有待确认的自动校准结果。" };
        if (pending.Hwnd == IntPtr.Zero || pending.ClientSize.Width <= 0 || pending.ClientSize.Height <= 0 || pending.Candidates.Length != 6)
            return new { success = false, reason = "自动校准结果不完整，请重新识别。" };
        GetWindowThreadProcessId(pending.Hwnd, out uint currentProcessId);
        if (currentProcessId == 0 || currentProcessId != pending.ProcessId)
            return new { success = false, reason = "异环窗口已关闭或发生变化，请重新识别。" };
        if (!MouseSender.GetWindowScreenInfo(pending.Hwnd, out _, out var currentSize, out _) || currentSize != pending.ClientSize)
            return new { success = false, reason = "异环窗口尺寸已变化或窗口已关闭，请重新识别后再保存。" };

        if ((slotXs is null) != (slotYs is null) ||
            (slotXs is not null && (slotXs.Length != 6 || slotYs!.Length != 6)))
            return new { success = false, reason = "六个槽位坐标不完整，请重新检查预览。" };

        var detected = pending.Candidates.OrderBy(c => c.Slot).ToArray();
        var ordered = detected;
        if (slotXs is not null && slotYs is not null)
            ordered = ordered.Select((candidate, index) => candidate with { X = slotXs[index], Y = slotYs[index] }).ToArray();

        if (!ordered.Select(c => c.Slot).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 }) ||
            ordered.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.X is < 0 or > 1 || c.Y is < 0 or > 1 || (!pending.ManualOnly && c.MatchScore < 0.045)))
            return new { success = false, reason = "自动校准结果未通过完整性检查。" };

        double originalArea = SignedArea(detected.Select(c => (c.X, c.Y)).ToArray());
        double adjustedArea = SignedArea(ordered.Select(c => (c.X, c.Y)).ToArray());
        if (Math.Sign(originalArea) != Math.Sign(adjustedArea) || Math.Abs(adjustedArea) < 0.0001)
            return new { success = false, reason = "微调后的槽位顺序或整体布局异常，请检查六个编号位置。" };

        double duplicateDistance = Math.Min(pending.ClientSize.Width, pending.ClientSize.Height) * 0.04;
        for (int i = 0; i < ordered.Length; i++)
        for (int j = i + 1; j < ordered.Length; j++)
        {
            double dx = (ordered[i].X - ordered[j].X) * pending.ClientSize.Width;
            double dy = (ordered[i].Y - ordered[j].Y) * pending.ClientSize.Height;
            if (Math.Sqrt(dx * dx + dy * dy) < duplicateDistance)
                return new { success = false, reason = "有两个槽位标记太近，请在预览中分开后再保存。" };
        }

        var preset = _config.WheelPreset;
        var previousSlots = preset.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray();
        double previousAspect = preset.CalibratedAspectRatio;
        int? previousWidth = preset.CalibrationClientWidth;
        int? previousHeight = preset.CalibrationClientHeight;
        string? previousSource = preset.CalibrationSource;
        IntPtr previousHwnd = _config.TargetWindowHandle;
        string? previousTitle = _config.TargetWindowTitle;

        preset.Slots = ordered.Select(c => new NormalizedPoint(c.X, c.Y)).ToArray();
        preset.CalibratedAspectRatio = pending.ClientSize.Width / (double)pending.ClientSize.Height;
        preset.CalibrationClientWidth = pending.ClientSize.Width;
        preset.CalibrationClientHeight = pending.ClientSize.Height;
        bool manuallyAdjusted = ordered.Where((candidate, index) =>
            Math.Abs(candidate.X - detected[index].X) > 0.000001 ||
            Math.Abs(candidate.Y - detected[index].Y) > 0.000001).Any();
        preset.CalibrationSource = pending.ManualOnly
            ? "异环手动定位"
            : manuallyAdjusted ? "异环自动识别（已手动微调）" : "异环自动识别";

        if (!_saveCalibrationConfigCallback())
        {
            preset.Slots = previousSlots;
            preset.CalibratedAspectRatio = previousAspect;
            preset.CalibrationClientWidth = previousWidth;
            preset.CalibrationClientHeight = previousHeight;
            preset.CalibrationSource = previousSource;
            _config.TargetWindowHandle = previousHwnd;
            _config.TargetWindowTitle = previousTitle;
            _wheelController.Preset = preset;
            return new { success = false, reason = "保存自动校准结果失败，已保留原坐标。" };
        }

        _config.TargetWindowHandle = pending.Hwnd;
        _config.TargetWindowTitle = pending.Title;
        _wheelController.Preset = preset;
        _pendingAutoCalibration = null;
        return new { success = true, status = GetStatus() };
    }

    public void CancelAutoCalibration() => _pendingAutoCalibration = null;

    private static double SignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return area / 2.0;
    }

    public bool TestSlot(int slotIndex)
    {
        if (_config.TargetWindowHandle == IntPtr.Zero) return false;

        if (slotIndex == 0) // Strum Safe Point
        {
            var pt = _config.WheelPreset.StrumPoint;
            Point screenPt = MouseSender.NormalizedClientToScreen(_config.TargetWindowHandle, pt.X, pt.Y);
            if (!screenPt.IsEmpty)
            {
                MouseSender.SetPos(screenPt.X, screenPt.Y);
                MouseSender.SendMouseClick(MouseButton.Left, 15);
                return true;
            }
            return false;
        }

        if (slotIndex >= 1 && slotIndex <= 6)
        {
            _wheelController.InvalidateCurrentSlot();
            return _wheelController.SelectSlot(_config.TargetWindowHandle, slotIndex);
        }

        return false;
    }

    public object SavePreset(float[] slotXs, float[] slotYs, float strumX, float strumY)
    {
        _pendingAutoCalibration = null;
        var preset = _config.WheelPreset;
        for (int i = 0; i < 6 && i < slotXs.Length && i < slotYs.Length; i++)
        {
            preset.Slots[i] = new NormalizedPoint(slotXs[i], slotYs[i]);
        }
        preset.StrumPoint = new NormalizedPoint(strumX, strumY);
        preset.CalibrationSource = "手动校准";
        if (_config.TargetWindowHandle != IntPtr.Zero &&
            MouseSender.GetWindowScreenInfo(_config.TargetWindowHandle, out _, out var clientSize, out _))
        {
            preset.CalibrationClientWidth = clientSize.Width;
            preset.CalibrationClientHeight = clientSize.Height;
            preset.CalibratedAspectRatio = clientSize.Width / (double)clientSize.Height;
        }
        else
        {
            preset.CalibrationClientWidth = null;
            preset.CalibrationClientHeight = null;
        }

        _wheelController.Preset = preset;
        _saveConfigCallback();

        return GetStatus();
    }

    public object ResetDefaults()
    {
        _pendingAutoCalibration = null;
        _config.WheelPreset = new WheelPreset();
        _wheelController.Preset = _config.WheelPreset;
        _saveConfigCallback();

        return GetStatus();
    }

    private sealed record PendingAutoCalibration(IntPtr Hwnd, uint ProcessId, string Title, Size ClientSize, SlotDetectionCandidate[] Candidates, bool ManualOnly);
}
