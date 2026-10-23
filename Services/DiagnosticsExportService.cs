using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public static class DiagnosticsExportService
{
    public static string GenerateReport(AppConfig config, IntPtr boundGameHwnd, ConvertedSong? currentSong, SongLibrary library)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==================================================================");
        sb.AppendLine("NTE-GUITAR V3.4 系统运行与兼容性诊断报告");
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("==================================================================");
        sb.AppendLine();

        // 1. 系统与运行环境
        sb.AppendLine("[1. 系统与运行环境]");
        sb.AppendLine($"• 操作系统: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64位" : "32位")})");
        sb.AppendLine($"• .NET 运行时: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"• 进程架构: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"• 主屏幕尺寸: {Screen.PrimaryScreen?.Bounds.Width}x{Screen.PrimaryScreen?.Bounds.Height}");
        sb.AppendLine($"• 显示器总数: {Screen.AllScreens.Length}");
        sb.AppendLine();

        // 2. 游戏窗口绑定状态
        sb.AppendLine("[2. 目标游戏窗口绑定]");
        if (boundGameHwnd != IntPtr.Zero)
        {
            string title = KeyboardSender.GetWindowTitle(boundGameHwnd);
            sb.AppendLine($"• 绑定句柄 (HWND): 0x{boundGameHwnd.ToInt64():X}");
            sb.AppendLine($"• 窗口标题: {title}");
            sb.AppendLine($"• 当前是否前台活动: {(KeyboardSender.IsForeground(boundGameHwnd) ? "是" : "否")}");
        }
        else
        {
            sb.AppendLine("• 状态: 未绑定游戏窗口 (播放将发送至系统当前前台窗口)");
        }
        sb.AppendLine();

        // 3. 轮盘与按键时序参数
        sb.AppendLine("[3. 轮盘校准与硬件时序参数]");
        sb.AppendLine($"• 轮盘目标进程名: {config.WheelPreset.ProcessName}");
        sb.AppendLine($"• 轮盘槽位数: {config.WheelPreset.Slots.Length} 槽");
        sb.AppendLine($"• 扫弦安全点归一化: ({config.WheelPreset.StrumPoint.X:F3}, {config.WheelPreset.StrumPoint.Y:F3})");
        sb.AppendLine($"• 轮盘选槽稳定延迟 (WheelMouseSettle): {config.WheelMouseSettleMs} ms");
        sb.AppendLine($"• 琴弦按键脉冲时长 (TapDuration): {config.TapDurationMs} ms");
        sb.AppendLine($"• 翻页间隔 (PageInterTapGap): {config.PageInterTapGapMs} ms");
        sb.AppendLine($"• 翻页按下持续 (PageKeyHold): {config.PageKeyHoldMs} ms");
        sb.AppendLine($"• 翻页安全余量 (PageInputSafety): {config.PageInputSafetyMs} ms");
        sb.AppendLine($"• 鼠标点击时长 (MouseClickHold): {config.MouseClickHoldMs} ms");
        sb.AppendLine();

        // 4. 当前乐器模式与歌曲
        sb.AppendLine("[4. 当前乐器模式与加载曲目]");
        string modeName = config.PlayMode switch
        {
            0 => "🎹 钢琴与全音域键盘 (36键)",
            1 => "🎸 吉他品格独奏模式 (Viterbi 算法 0~24品)",
            2 => "🎼 吉他和弦伴奏模式 (24和弦轮盘)",
            _ => "未知模式"
        };
        sb.AppendLine($"• 当前演奏模式: {modeName}");
        sb.AppendLine($"• 实时移调: {(config.Transpose >= 0 ? "+" : "")}{config.Transpose} 半音");
        sb.AppendLine($"• 延迟微调补偿: {config.LatencyCompensationMs} ms");
        sb.AppendLine($"• 动态智能延音: {(config.EnableSustain ? "已开启" : "已关闭")}");
        sb.AppendLine($"• 本地独立曲库歌曲数: {library.GetAllSongs().Count} 首");
        if (currentSong != null)
        {
            sb.AppendLine($"• 当前选中歌曲: {currentSong.DisplayName} (ID: {currentSong.Id})");
            sb.AppendLine($"• 音符总数: {currentSong.Notes.Count:N0}");
            sb.AppendLine($"• 时长: {currentSong.DurationUs / 1_000_000.0:F1} 秒");
        }
        else
        {
            sb.AppendLine("• 当前选中歌曲: (未选择)");
        }
        sb.AppendLine();

        sb.AppendLine("==================================================================");
        sb.AppendLine("如遇按键延迟、丢音或窗口无法激活，请带上此报告联系反馈。");
        sb.AppendLine("==================================================================");

        return sb.ToString();
    }
}
