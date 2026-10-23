using System.Drawing;
using System.Runtime.InteropServices;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.UI;

public sealed class WheelCalibrationDialog : Form
{
    private readonly WheelPreset _preset;
    private IntPtr _targetGameHwnd = IntPtr.Zero;
    private string _gameTitle = "(未绑定)";
    private Size _gameClientSize = Size.Empty;

    private readonly Label _lblTargetWindow;
    private readonly Button _btnBindWindow;
    private readonly ListView _lvSlots;
    private readonly Button _btnStartCalibrate;
    private readonly Button _btnTestMove;
    private readonly Button _btnResetDefault;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;
    private readonly Label _lblGuide;

    private int _calibratingIndex = -1; // -1: 未在校准, 0..5: Slot 1..6, 6: Strum Point
    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc;

    private static readonly string[] StepNames = new string[]
    {
        "Slot 1 (上: G 和弦 / I 品格)",
        "Slot 2 (右上: Am 和弦 / II 品格)",
        "Slot 3 (右下: Bm 和弦 / III 品格)",
        "Slot 4 (下: C 和弦 / IV 品格)",
        "Slot 5 (左下: D 和弦 / V 品格)",
        "Slot 6 (左上: Em 和弦 / VI 品格)",
        "扫弦安全点 (轮盘外任意空白区域)"
    };

    public WheelPreset ResultPreset { get; }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public WheelCalibrationDialog(WheelPreset currentPreset, IntPtr initialGameHwnd = default)
    {
        _preset = currentPreset;
        // 复制一份编辑，避免直接污染
        ResultPreset = new WheelPreset
        {
            ProcessName = _preset.ProcessName,
            CalibratedAspectRatio = _preset.CalibratedAspectRatio,
            CalibrationClientWidth = _preset.CalibrationClientWidth,
            CalibrationClientHeight = _preset.CalibrationClientHeight,
            CalibrationSource = _preset.CalibrationSource,
            DownStrumButton = _preset.DownStrumButton,
            UpStrumButton = _preset.UpStrumButton,
            Slots = _preset.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray(),
            StrumPoint = new NormalizedPoint(_preset.StrumPoint.X, _preset.StrumPoint.Y),
            StringScanCodes = (ushort[])_preset.StringScanCodes.Clone()
        };

        // 自愈检测：如果所有槽位 X 坐标均 <= 0.005（或 >= 0.995），说明之前误将窗口外坐标归零，自动重置为默认轮盘坐标
        if (ResultPreset.Slots.All(s => s.X <= 0.005) || ResultPreset.Slots.All(s => s.X >= 0.995))
        {
            var def = new WheelPreset();
            ResultPreset.Slots = def.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray();
            ResultPreset.StrumPoint = new NormalizedPoint(def.StrumPoint.X, def.StrumPoint.Y);
            ResultPreset.CalibrationClientWidth = null;
            ResultPreset.CalibrationClientHeight = null;
            ResultPreset.CalibrationSource = null;
        }

        Text = "吉他轮盘 6 槽位与扫弦安全点校准向导";
        Icon = AppIcon.Value;
        Size = new Size(760, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Body(9F);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        // Header
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 65,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 10, 20, 10)
        };
        var lblTitle = new Label
        {
            Text = "🎯 共用轮盘校准：一次校准，和弦模式与品格模式完全复用",
            Font = Theme.Title(10.5F),
            ForeColor = Theme.Text,
            Location = new Point(18, 12),
            AutoSize = true
        };
        var lblSub = new Label
        {
            Text = "支持 1080p / 2K / 4K 全分辨率自适应缩放，点击自动转换为游戏相对坐标。",
            Font = Theme.Small(8.5F),
            ForeColor = Theme.Muted,
            Location = new Point(18, 36),
            AutoSize = true
        };
        pnlHeader.Controls.AddRange(new Control[] { lblTitle, lblSub });

        // Window bind panel
        var grpBind = new GroupBox
        {
            Text = " 1. 绑定游戏窗口 ",
            Location = new Point(18, 75),
            Size = new Size(708, 68),
            Font = Theme.Body(9F, FontStyle.Bold),
            ForeColor = Theme.Accent
        };
        _lblTargetWindow = new Label
        {
            Text = "当前窗口: 未绑定 (请点击右侧按钮绑定游戏)",
            Location = new Point(16, 28),
            Size = new Size(530, 26),
            Font = Theme.Body(9F),
            ForeColor = Theme.Text
        };
        _btnBindWindow = new Button
        {
            Text = "🎯 绑定游戏",
            Location = new Point(560, 20),
            Size = new Size(130, 34),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnBindWindow.FlatAppearance.BorderSize = 0;
        _btnBindWindow.Click += OnBindWindowClicked;
        grpBind.Controls.AddRange(new Control[] { _lblTargetWindow, _btnBindWindow });

        // Slot list
        var grpSlots = new GroupBox
        {
            Text = " 2. 六大扇区中心与扫弦点坐标 (相对比例) ",
            Location = new Point(18, 152),
            Size = new Size(708, 280),
            Font = Theme.Body(9F, FontStyle.Bold),
            ForeColor = Theme.Accent
        };

        _lvSlots = new ListView
        {
            Location = new Point(15, 24),
            Size = new Size(678, 205),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = Theme.Body(9F),
            BackColor = Theme.SurfaceCard,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lvSlots.Columns.Add("目标项", 285);
        _lvSlots.Columns.Add("相对 X (nx)", 125);
        _lvSlots.Columns.Add("相对 Y (ny)", 125);
        _lvSlots.Columns.Add("校准状态", 110);

        _lblGuide = new Label
        {
            Text = "💡 提示：点击下方「开始依次校准」，按提示在游戏轮盘相应位置单击鼠标即可。",
            Location = new Point(15, 238),
            Size = new Size(678, 30),
            ForeColor = Theme.Muted,
            Font = Theme.Small(8.5F)
        };
        grpSlots.Controls.AddRange(new Control[] { _lvSlots, _lblGuide });

        // Button bar
        _btnStartCalibrate = new Button
        {
            Text = "⚡ 开始依次校准 (1~6 槽位+扫弦)",
            Location = new Point(18, 442),
            Size = new Size(320, 38),
            BackColor = Theme.Success,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnStartCalibrate.FlatAppearance.BorderSize = 0;
        _btnStartCalibrate.Click += OnStartCalibrateClicked;

        _btnTestMove = new Button
        {
            Text = "▶ 测试移动验证",
            Location = new Point(350, 442),
            Size = new Size(160, 38),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _btnTestMove.FlatAppearance.BorderColor = Theme.Border;
        _btnTestMove.Click += OnTestMoveClicked;

        _btnResetDefault = new Button
        {
            Text = "↺ 重置默认坐标",
            Location = new Point(522, 442),
            Size = new Size(166, 38),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _btnResetDefault.FlatAppearance.BorderColor = Theme.Border;
        _btnResetDefault.Click += (_, _) =>
        {
            var def = new WheelPreset();
            ResultPreset.Slots = def.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray();
            ResultPreset.StrumPoint = new NormalizedPoint(def.StrumPoint.X, def.StrumPoint.Y);
            ResultPreset.CalibrationClientWidth = null;
            ResultPreset.CalibrationClientHeight = null;
            ResultPreset.CalibrationSource = null;
            RefreshList();
        };

        // Bottom dialog buttons
        _btnSave = new Button
        {
            Text = "保存并应用",
            Location = new Point(470, 535),
            Size = new Size(125, 38),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnSave.FlatAppearance.BorderSize = 0;
        _btnSave.Click += (_, _) =>
        {
            // 校验：若所有槽位 X 坐标均靠近 0 或靠近 1，提示异常
            if (ResultPreset.Slots.All(s => s.X <= 0.01) || ResultPreset.Slots.All(s => s.X >= 0.99))
            {
                var dr = MessageBox.Show(this,
                    "⚠️ 检测到轮盘所有槽位的 X 相对坐标全为 0.0000（或靠近边缘）！\n\n这通常是因为在游戏窗口外部点击，或者窗口绑定错误导致的。\n如果保存此配置，弹奏时鼠标将完全卡在屏幕最左侧。\n\n是否为您自动恢复为标准的默认轮盘坐标？\n【是】恢复默认轮盘坐标并保存退出\n【否】依然保存当前全 0 坐标\n【取消】返回向导重新校准",
                    "轮盘坐标异常提醒",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);

                if (dr == DialogResult.Yes)
                {
                    var def = new WheelPreset();
                    ResultPreset.Slots = def.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray();
                    ResultPreset.StrumPoint = new NormalizedPoint(def.StrumPoint.X, def.StrumPoint.Y);
                    ResultPreset.CalibrationClientWidth = null;
                    ResultPreset.CalibrationClientHeight = null;
                    ResultPreset.CalibrationSource = null;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                else if (dr == DialogResult.Cancel)
                {
                    return;
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        _btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(608, 535),
            Size = new Size(118, 38),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
            Cursor = Cursors.Hand
        };
        _btnCancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[]
        {
            pnlHeader, grpBind, grpSlots,
            _btnStartCalibrate, _btnTestMove, _btnResetDefault,
            _btnSave, _btnCancel
        });

        RefreshList();

        if (initialGameHwnd != IntPtr.Zero && !IsSelfOrOwnedWindow(initialGameHwnd))
        {
            BindWindow(initialGameHwnd);
        }
        else
        {
            TryAutoDetectForegroundWindow();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableImmersiveDarkMode(Handle);
    }

    private bool IsSelfOrOwnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Handle) return true;
        if (Owner != null && (hwnd == Owner.Handle || hwnd == Owner.Handle)) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;
        return false;
    }

    private void TryAutoDetectForegroundWindow()
    {
        var fg = KeyboardSender.GetForegroundWindow();
        if (!IsSelfOrOwnedWindow(fg))
        {
            BindWindow(fg);
        }
    }

    private void OnBindWindowClicked(object? sender, EventArgs e)
    {
        _btnBindWindow.Enabled = false;
        _btnBindWindow.Text = "请在 3 秒内切到游戏...";
        _lblTargetWindow.Text = "正在等待前台游戏窗口激活...";

        var tmr = new System.Windows.Forms.Timer { Interval = 3000 };
        tmr.Tick += (_, _) =>
        {
            tmr.Stop();
            tmr.Dispose();
            var fg = KeyboardSender.GetForegroundWindow();
            if (!IsSelfOrOwnedWindow(fg))
            {
                BindWindow(fg);
            }
            else
            {
                _lblTargetWindow.Text = "未检测到外部游戏窗口 (不能绑定 NTE-GUITAR 本身)，请重试。";
                _lblTargetWindow.ForeColor = Color.FromArgb(220, 53, 69);
            }
            _btnBindWindow.Enabled = true;
            _btnBindWindow.Text = "🎯 重新绑定";
        };
        tmr.Start();
    }

    private void BindWindow(IntPtr hwnd)
    {
        if (IsSelfOrOwnedWindow(hwnd)) return;
        _targetGameHwnd = hwnd;
        _gameTitle = KeyboardSender.GetWindowTitle(hwnd);

        if (MouseSender.GetWindowScreenInfo(hwnd, out var origin, out var clientSize, out _))
        {
            _gameClientSize = clientSize;
            _lblTargetWindow.Text = $"已绑定: {_gameTitle} ({_gameClientSize.Width}x{_gameClientSize.Height}) [屏幕原点: ({origin.X}, {origin.Y})]";
            _lblTargetWindow.ForeColor = Color.FromArgb(40, 167, 69);
            if (_gameClientSize.Height > 0)
            {
                ResultPreset.CalibratedAspectRatio = _gameClientSize.Width / (double)_gameClientSize.Height;
                ResultPreset.CalibrationClientWidth = _gameClientSize.Width;
                ResultPreset.CalibrationClientHeight = _gameClientSize.Height;
                ResultPreset.CalibrationSource = "手动校准";
            }
        }
        else
        {
            _lblTargetWindow.Text = $"已绑定: {_gameTitle} (无法读取窗口客户区尺寸)";
            _lblTargetWindow.ForeColor = Color.FromArgb(220, 53, 69);
        }
    }

    private void RefreshList()
    {
        _lvSlots.Items.Clear();
        for (int i = 0; i < 6; i++)
        {
            var p = ResultPreset.Slots[i];
            var item = new ListViewItem(StepNames[i]);
            item.SubItems.Add(p.X.ToString("F4"));
            item.SubItems.Add(p.Y.ToString("F4"));
            item.SubItems.Add("已就绪");
            _lvSlots.Items.Add(item);
        }

        var sp = ResultPreset.StrumPoint;
        var spItem = new ListViewItem(StepNames[6]);
        spItem.SubItems.Add(sp.X.ToString("F4"));
        spItem.SubItems.Add(sp.Y.ToString("F4"));
        spItem.SubItems.Add("已就绪");
        _lvSlots.Items.Add(spItem);
    }

    private void OnStartCalibrateClicked(object? sender, EventArgs e)
    {
        if (_targetGameHwnd == IntPtr.Zero || IsSelfOrOwnedWindow(_targetGameHwnd))
        {
            MessageBox.Show(this, "请先点击「绑定游戏」，切到游戏窗口完成绑定，以便精确计算游戏窗口客户区相对坐标。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _calibratingIndex = 0;
        _btnStartCalibrate.Enabled = false;
        _lblGuide.Text = $"👉 请在游戏窗口内点击: 【{StepNames[0]}】 中心...";
        _lblGuide.ForeColor = Color.FromArgb(220, 53, 69);

        InstallMouseHook();
    }

    private void OnMouseClickCaptured(Point screenPt)
    {
        if (_calibratingIndex < 0 || _calibratingIndex > 6) return;

        var norm = MouseSender.ScreenToNormalizedClient(_targetGameHwnd, screenPt, clamp: false);
        if (!norm.HasValue)
        {
            Invoke(() =>
            {
                _lblGuide.Text = $"⚠️ 点击在游戏窗口外部 (屏幕坐标: {screenPt.X}, {screenPt.Y})，已忽略！请在游戏窗口内点击【{StepNames[_calibratingIndex]}】";
                _lblGuide.ForeColor = Color.FromArgb(220, 53, 69);
                try { System.Media.SystemSounds.Beep.Play(); } catch { }
            });
            return;
        }

        var (nx, ny) = norm.Value;

        if (_calibratingIndex < 6)
        {
            ResultPreset.Slots[_calibratingIndex].X = Math.Round(nx, 4);
            ResultPreset.Slots[_calibratingIndex].Y = Math.Round(ny, 4);
            ResultPreset.CalibrationSource = "手动校准";
            if (_gameClientSize.Width > 0 && _gameClientSize.Height > 0)
            {
                ResultPreset.CalibrationClientWidth = _gameClientSize.Width;
                ResultPreset.CalibrationClientHeight = _gameClientSize.Height;
                ResultPreset.CalibratedAspectRatio = _gameClientSize.Width / (double)_gameClientSize.Height;
            }
        }
        else
        {
            ResultPreset.StrumPoint.X = Math.Round(nx, 4);
            ResultPreset.StrumPoint.Y = Math.Round(ny, 4);
        }

        Invoke(() =>
        {
            RefreshList();
            _calibratingIndex++;
            if (_calibratingIndex <= 6)
            {
                _lblGuide.Text = $"👉 请在游戏窗口内点击: 【{StepNames[_calibratingIndex]}】 中心...";
                _lblGuide.ForeColor = Color.FromArgb(220, 53, 69);
            }
            else
            {
                // 完成全部校准
                UninstallMouseHook();
                _calibratingIndex = -1;
                _btnStartCalibrate.Enabled = true;
                _lblGuide.Text = "✅ 7 个目标点已全部校准完毕！可点击「测试移动验证」确认对准精度。";
                _lblGuide.ForeColor = Color.FromArgb(40, 167, 69);
            }
        });
    }

    private void OnTestMoveClicked(object? sender, EventArgs e)
    {
        if (_targetGameHwnd == IntPtr.Zero)
        {
            MessageBox.Show(this, "请先绑定游戏窗口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnTestMove.Enabled = false;
        var originalPos = MouseSender.GetCurrentCursorPos();

        Task.Run(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                var pt = MouseSender.NormalizedClientToScreen(_targetGameHwnd, ResultPreset.Slots[i].X, ResultPreset.Slots[i].Y);
                if (!pt.IsEmpty)
                {
                    MouseSender.SetPos(pt.X, pt.Y);
                    Thread.Sleep(350);
                }
            }

            // Move to strum point
            var spt = MouseSender.NormalizedClientToScreen(_targetGameHwnd, ResultPreset.StrumPoint.X, ResultPreset.StrumPoint.Y);
            if (!spt.IsEmpty)
            {
                MouseSender.SetPos(spt.X, spt.Y);
                Thread.Sleep(500);
            }

            MouseSender.SetPos(originalPos.X, originalPos.Y);

            Invoke(() =>
            {
                _btnTestMove.Enabled = true;
                MessageBox.Show(this, "移动测试完成！鼠标已依次划过 Slot 1~6 及扫弦安全点。", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        });
    }

    #region Win32 LowLevel Mouse Hook
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private void InstallMouseHook()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookProc = MouseHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(curModule?.ModuleName), 0);
    }

    private void UninstallMouseHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookProc = null;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var screenPt = new Point(hookStruct.pt.x, hookStruct.pt.y);
            OnMouseClickCaptured(screenPt);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UninstallMouseHook();
        base.OnFormClosing(e);
    }
    #endregion
}
