using System.Drawing;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>
/// 轮盘与安全点统一控制器: 品格与和弦完全共用底层 6 槽位坐标与安全点
/// </summary>
public sealed class WheelController
{
    private readonly object _lock = new();

    public WheelPreset Preset { get; set; }
    public int CurrentSlot { get; private set; } = 0; // 0 表示未选/初始状态
    public Point? InitialCursorPos { get; set; }
    public int MouseClickHoldMs { get; set; } = 25;

    public WheelController(WheelPreset? preset = null)
    {
        Preset = preset ?? new WheelPreset();
    }

    /// <summary>
    /// 翻页或重置时使当前槽位缓存失效，确保新页面的槽位绝对能被点击
    /// </summary>
    public void InvalidateCurrentSlot()
    {
        lock (_lock)
        {
            CurrentSlot = 0;
        }
    }

    /// <summary>
    /// 选择轮盘指定槽位 (1..6)
    /// 若与当前槽位相同，则绝对消除重复点击
    /// </summary>
    public bool SelectSlot(IntPtr gameHwnd, int slotIndex)
    {
        if (slotIndex < 1 || slotIndex > 6) return false;

        lock (_lock)
        {
            if (CurrentSlot == slotIndex)
            {
                // 槽位未变，不重复点击
                return true;
            }

            var normPt = Preset.Slots[slotIndex - 1];
            Point screenPt = MouseSender.NormalizedClientToScreen(gameHwnd, normPt.X, normPt.Y);

            if (!screenPt.IsEmpty && MouseSender.SetPos(screenPt.X, screenPt.Y))
            {
                Thread.Sleep(4);
                if (!MouseSender.SendMouseClick(MouseButton.Left, Math.Max(25, MouseClickHoldMs)))
                    return false;

                CurrentSlot = slotIndex;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 和弦模式语义切换
    /// </summary>
    public bool SelectChord(IntPtr gameHwnd, ChordId chord)
    {
        int slot = chord switch
        {
            ChordId.G => 1,
            ChordId.Am => 2,
            ChordId.Bm => 3,
            ChordId.C => 4,
            ChordId.D => 5,
            ChordId.Em => 6,
            _ => 0
        };

        if (slot > 0)
        {
            return SelectSlot(gameHwnd, slot);
        }
        return false;
    }

    /// <summary>
    /// 品格模式语义切换 (1..6品)
    /// </summary>
    public bool SelectFret(IntPtr gameHwnd, byte fret)
    {
        if (fret >= 1 && fret <= 6)
        {
            return SelectSlot(gameHwnd, fret);
        }
        return false;
    }

    /// <summary>
    /// 将鼠标平滑/直接移动到轮盘外扫弦安全点
    /// </summary>
    public bool MoveToStrumPoint(IntPtr gameHwnd)
    {
        Point screenPt = MouseSender.NormalizedClientToScreen(gameHwnd, Preset.StrumPoint.X, Preset.StrumPoint.Y);
        if (!screenPt.IsEmpty)
        {
            return MouseSender.SetPos(screenPt.X, screenPt.Y);
        }

        return false;
    }

    /// <summary>
    /// 在扫弦安全点发送扫弦点击 (左键/右键)
    /// </summary>
    public bool SendStrumClick(IntPtr gameHwnd, MouseButton button)
    {
        if (!MoveToStrumPoint(gameHwnd)) return false;
        Thread.Sleep(4);
        return MouseSender.SendMouseClick(button, Math.Max(25, MouseClickHoldMs));
    }

    /// <summary>
    /// 重置控制器状态 (停止播放时调用)
    /// </summary>
    public void Reset(Point? restorePos = null)
    {
        lock (_lock)
        {
            CurrentSlot = 0;
            if (restorePos.HasValue && !restorePos.Value.IsEmpty)
            {
                MouseSender.SetPos(restorePos.Value.X, restorePos.Value.Y);
            }
        }
    }
}
