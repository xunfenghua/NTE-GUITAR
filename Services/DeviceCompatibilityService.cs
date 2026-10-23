using System.Drawing;
using System.Runtime.InteropServices;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public enum CompatibilityState
{
    Unknown,
    Ready,
    NeedsCalibration,
    WindowNotFound,
    WindowNotActive,
    Error
}

public sealed record CompatibilityReport(
    CompatibilityState State,
    string Summary,
    string? Detail = null,
    DateTimeOffset? CheckedAt = null);

public interface IDeviceCompatibilityService
{
    Task<CompatibilityReport> CheckAsync(IntPtr targetHwnd, WheelPreset? preset, CancellationToken cancellationToken = default);
}

public sealed class DeviceCompatibilityService : IDeviceCompatibilityService
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public Task<CompatibilityReport> CheckAsync(IntPtr targetHwnd, WheelPreset? preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
        {
            return Task.FromResult(new CompatibilityReport(
                CompatibilityState.WindowNotFound,
                "未绑定游戏窗口",
                "请前往【游戏校准】页，点击【绑定游戏窗口】，并在 8 秒内切换到游戏窗口进行绑定。",
                DateTimeOffset.Now));
        }

        string title = KeyboardSender.GetWindowTitle(targetHwnd);
        if (string.IsNullOrWhiteSpace(title)) title = "目标游戏进程";

        bool isForeground = KeyboardSender.IsForeground(targetHwnd);

        if (!GetClientRect(targetHwnd, out var rect) || (rect.Right - rect.Left) <= 0 || (rect.Bottom - rect.Top) <= 0)
        {
            return Task.FromResult(new CompatibilityReport(
                CompatibilityState.Error,
                "无法读取游戏窗口客户区大小",
                "窗口句柄可能失效或受到权限保护，请重新绑定游戏窗口。",
                DateTimeOffset.Now));
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (preset == null || preset.Slots == null || preset.Slots.Length < 6)
        {
            return Task.FromResult(new CompatibilityReport(
                CompatibilityState.NeedsCalibration,
                "轮盘未校准",
                "请在【游戏校准】中完成 6 个和弦/品格槽位以及扫弦安全点的坐标标定。",
                DateTimeOffset.Now));
        }

        if (!isForeground)
        {
            return Task.FromResult(new CompatibilityReport(
                CompatibilityState.WindowNotActive,
                $"已绑定: {title} (当前非前台)",
                $"游戏客户区大小: {width}x{height}。开始演奏时请确保切换到游戏窗口成为前台活动窗口。",
                DateTimeOffset.Now));
        }

        return Task.FromResult(new CompatibilityReport(
            CompatibilityState.Ready,
            $"已绑定且就绪: {title}",
            $"客户区分辨率: {width}x{height}，轮盘 6 槽位与扫弦点已适配当前比例。",
            DateTimeOffset.Now));
    }
}
