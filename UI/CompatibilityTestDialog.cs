using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.UI;

public sealed class CompatibilityTestDialog : Form
{
    private readonly Button _btnStartTest;
    private readonly Label _lblCountdown;
    private readonly TextBox _txtLog;
    private readonly System.Windows.Forms.Timer _timer;
    private int _countdown = 3;

    public CompatibilityTestDialog()
    {
        Text = "阶段 0: 游戏按键兼容性测试 (SendInput + ScanCode)";
        Icon = AppIcon.Value;
        Size = new Size(580, 480);
        MinimumSize = new Size(500, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Body(9F);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 135,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 15, 20, 15)
        };

        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0)
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45F));

        var lblDesc = new Label
        {
            Text = "点击“开始 3 秒倒计时测试”后，请立即切换到目标游戏窗口。\n" +
                   "本测试将依次模拟发送: A 键、S 键、D 键，以及 A+S+D 同时和弦。\n" +
                   "若游戏中人物能正常响应/弹奏，说明 SendInput + ScanCode 兼容良好！",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text
        };

        var flowButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };

        _btnStartTest = new Button
        {
            Text = "▶ 开始 3 秒倒计时测试",
            Size = new Size(190, 36),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 15, 0)
        };
        _btnStartTest.FlatAppearance.BorderSize = 0;
        _btnStartTest.Click += (_, _) => StartCountdown();

        _lblCountdown = new Label
        {
            Text = "等待开始...",
            AutoSize = true,
            Font = Theme.Body(10F, FontStyle.Bold),
            ForeColor = Theme.Warning,
            Margin = new Padding(0, 8, 0, 0)
        };

        flowButtons.Controls.AddRange(new Control[] { _btnStartTest, _lblCountdown });

        headerLayout.Controls.Add(lblDesc, 0, 0);
        headerLayout.Controls.Add(flowButtons, 0, 1);
        pnlHeader.Controls.Add(headerLayout);

        // Center Log Box
        var pnlCenter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 15, 20, 15)
        };

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Monospace(9F),
            BackColor = Theme.SurfaceCard,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };

        pnlCenter.Controls.Add(_txtLog);

        Controls.Add(pnlCenter);
        Controls.Add(pnlHeader);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;

        Log("已加载按键兼容性测试模块。");
        Log("提示: 如游戏以管理员权限运行，本工具也需以管理员身份运行。");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableImmersiveDarkMode(Handle);
    }

    private void StartCountdown()
    {
        _btnStartTest.Enabled = false;
        _countdown = 3;
        _lblCountdown.Text = $"请切换到游戏窗口: 3 秒...";
        _lblCountdown.ForeColor = Color.Crimson;
        Log("\n--- 开始测试: 请在 3 秒内切入目标游戏窗口 ---");
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _countdown--;
        if (_countdown > 0)
        {
            _lblCountdown.Text = $"请切换到游戏窗口: {_countdown} 秒...";
        }
        else
        {
            _timer.Stop();
            _lblCountdown.Text = "正在向当前活动窗口发送测试按键...";
            _lblCountdown.ForeColor = Color.FromArgb(40, 167, 69);

            ThreadPool.QueueUserWorkItem(_ => RunInputSimulation());
        }
    }

    private void RunInputSimulation()
    {
        try
        {
            IntPtr targetHwnd = KeyboardSender.GetForegroundWindow();
            string windowTitle = KeyboardSender.GetWindowTitle(targetHwnd);

            Invoke(() => Log($"[1/4] 捕获当前活动前台窗口: \"{windowTitle}\" (0x{targetHwnd.ToInt64():X})"));

            // 1. Send A (0x1E)
            Invoke(() => Log("[2/4] 发送单键 A (ScanCode: 0x1E)..."));
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x1E, isDown: true) });
            Thread.Sleep(30);
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x1E, isDown: false) });
            Thread.Sleep(300);

            // 2. Send S (0x1F)
            Invoke(() => Log("[3/4] 发送单键 S (ScanCode: 0x1F)..."));
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x1F, isDown: true) });
            Thread.Sleep(30);
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x1F, isDown: false) });
            Thread.Sleep(300);

            // 3. Send D (0x20)
            Invoke(() => Log("[4/4] 发送单键 D (ScanCode: 0x20)..."));
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x20, isDown: true) });
            Thread.Sleep(30);
            KeyboardSender.SendBatch(new[] { new KeyAction(0, 0x20, isDown: false) });
            Thread.Sleep(400);

            // 4. Send Chord: A + S + D together in a single SendInput call
            Invoke(() => Log("[和弦测试] 发送批量和弦: A + S + D 同时按下..."));
            var chordDown = new[]
            {
                new KeyAction(0, 0x1E, isDown: true),
                new KeyAction(0, 0x1F, isDown: true),
                new KeyAction(0, 0x20, isDown: true)
            };
            KeyboardSender.SendBatch(chordDown);
            Thread.Sleep(40);

            var chordUp = new[]
            {
                new KeyAction(0, 0x1E, isDown: false),
                new KeyAction(0, 0x1F, isDown: false),
                new KeyAction(0, 0x20, isDown: false)
            };
            KeyboardSender.SendBatch(chordUp);

            Invoke(() =>
            {
                Log("✔ 测试按键发送完成！全部按键已安全释放。");
                _lblCountdown.Text = "测试完成！";
                _lblCountdown.ForeColor = Color.FromArgb(40, 167, 69);
                _btnStartTest.Enabled = true;
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                Log($"❌ 测试异常: {ex.Message}");
                _lblCountdown.Text = "测试失败";
                _lblCountdown.ForeColor = Color.Red;
                _btnStartTest.Enabled = true;
            });
        }
        finally
        {
            KeyboardSender.ReleaseAllKeys();
        }
    }

    private void Log(string message)
    {
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        KeyboardSender.ReleaseAllKeys();
        base.OnFormClosing(e);
    }
}
