using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Preview;
using MidiAutoPlayer.Recognition;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.UI;

public sealed class ConvertDialog : Form
{
    private readonly IReadOnlyList<RawMidiNote> _rawNotes;
    private readonly IReadOnlyList<TrackMetadata>? _trackMetas;
    private readonly string _sourceName;
    private readonly string _sourceHash;
    private readonly string? _existingId;

    private readonly TextBox _txtName;
    private readonly NumericUpDown _numTranspose;
    private readonly CheckBox _chkIgnoreDrum;
    private readonly ComboBox _cmbOutOfRange;

    // Target Instrument Selection Controls
    private readonly RadioButton _rbTargetGuitar;
    private readonly RadioButton _rbTarget36Key;
    private readonly Label _lblTargetInstrumentDesc;

    // Auxiliary Melody Extraction Controls
    private readonly RadioButton _rbFullSong;
    private readonly RadioButton _rbMelodyExtraction;
    private readonly Panel _pnlMelodyOptions;
    private readonly Button _btnModeTrack;
    private readonly Button _btnModeHighest;
    private readonly Button _btnModeContinuous;
    private readonly Button _btnModeMono;
    private readonly Label _lblMelodySummary;
    private readonly CheckBox _chkManualTracks;
    private readonly CheckedListBox _clbTracks;

    // Instant Audition Controls
    private readonly Button _btnPlayPreview;
    private readonly Button _btnStopPreview;
    private readonly TrackBar _tbPreviewScrubber;
    private readonly Label _lblPreviewTime;
    private bool _isScrubbing = false;

    // Conversion result labels & buttons
    private readonly Label _lblPreviewCount;
    private readonly Label _lblPreviewDuration;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;

    private readonly MidiPreviewPlayer _previewPlayer;
    private Dictionary<RecognitionMode, RecognitionResult>? _candidates;
    private RecognitionMode _currentMode = RecognitionMode.FullSong;
    private int _previewVersion = 0;
    private ConvertedSong? _cachedPreviewSong;
    private ConvertOptions? _cachedPreviewOptions;

    public ConvertedSong? ResultSong { get; private set; }

    public ConvertDialog(
        string sourceName,
        string sourceHash,
        IReadOnlyList<RawMidiNote> rawNotes,
        string? existingDisplayName = null,
        ConvertOptions? initialOptions = null,
        string? existingId = null,
        IReadOnlyList<TrackMetadata>? trackMetas = null)
    {
        _sourceName = sourceName;
        _sourceHash = sourceHash;
        _rawNotes = rawNotes;
        _existingId = existingId;
        _trackMetas = trackMetas;
        _previewPlayer = new MidiPreviewPlayer();

        Text = "转换 MIDI 歌曲并保存到曲库";
        Icon = AppIcon.Value;
        Size = new Size(740, 840);
        MinimumSize = new Size(680, 750);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Body(9F);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        // ==================== 1. HEADER PANEL ====================
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 65,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 10, 20, 10)
        };
        var lblSourceTitle = new Label
        {
            Text = $"🎵 源文件: {_sourceName}",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = Theme.Title(10.5F),
            ForeColor = Theme.Text
        };
        long maxRawDurationUs = 0;
        for (int i = 0; i < rawNotes.Count; i++)
        {
            long end = rawNotes[i].StartUs + rawNotes[i].DurationUs;
            if (end > maxRawDurationUs) maxRawDurationUs = end;
        }
        var lblSourceInfo = new Label
        {
            Text = $"原始音符: {_rawNotes.Count:N0} 个  |  原始时长: {TimeFormatter.FormatUs(maxRawDurationUs)}  |  音轨数: {(_trackMetas?.Count ?? 1)}",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = Theme.Muted
        };
        pnlHeader.Controls.AddRange(new Control[] { lblSourceTitle, lblSourceInfo });

        // ==================== 2. BOTTOM BUTTON BAR ====================
        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 12, 20, 12)
        };
        var flowBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };

        _btnCancel = new Button
        {
            Text = "取消",
            Size = new Size(90, 36),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 0, 0, 0)
        };
        _btnCancel.FlatAppearance.BorderColor = Theme.Border;

        _btnSave = new Button
        {
            Text = "保存到曲库",
            Size = new Size(120, 36),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK,
            Margin = new Padding(0)
        };
        _btnSave.FlatAppearance.BorderSize = 0;
        _btnSave.Click += OnSaveClicked;

        flowBottom.Controls.AddRange(new Control[] { _btnCancel, _btnSave });
        pnlBottom.Controls.Add(flowBottom);

        // ==================== 3. CENTER CONTENT ====================
        var pnlCenter = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18, 10, 18, 10)
        };

        bool isGuitarTarget = (initialOptions?.TargetInstrument != 0);

        // --- Box 0: 目标乐器模式 (全新吉他模式专属集成) ---
        var grpInstrument = new GroupBox
        {
            Text = " 🎯 转换目标乐器模式 ",
            Dock = DockStyle.Top,
            Height = 112,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(13, 110, 253),
            Padding = new Padding(12, 6, 12, 6),
            BackColor = Color.White
        };

        var tblInstrument = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        tblInstrument.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tblInstrument.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        tblInstrument.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

        _rbTargetGuitar = new RadioButton
        {
            Text = "🎸 吉他专用模式 ★ (推荐: 0~24 品单音指弹 + 4 页 24 和弦，全音域保真)",
            Checked = isGuitarTarget,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F, isGuitarTarget ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = isGuitarTarget ? Color.FromArgb(22, 101, 52) : Color.FromArgb(73, 80, 87),
            Cursor = Cursors.Hand,
            Margin = new Padding(0)
        };

        _rbTarget36Key = new RadioButton
        {
            Text = "🎹 36 键常规乐器 (传统键盘模式: 52~87 音域折叠)",
            Checked = !isGuitarTarget,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F, !isGuitarTarget ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = !isGuitarTarget ? Color.FromArgb(13, 110, 253) : Color.FromArgb(73, 80, 87),
            Cursor = Cursors.Hand,
            Margin = new Padding(0)
        };

        tblInstrument.Controls.Add(_rbTargetGuitar, 0, 0);
        tblInstrument.Controls.Add(_rbTarget36Key, 0, 1);

        _lblTargetInstrumentDesc = new Label
        {
            Text = isGuitarTarget
                ? "💡 吉他专用：自动启用【智能连续主旋律提取】，消除伴奏和弦干扰，保证音符清晰不漏！"
                : "💡 36 键乐器模式：支持多声部并发，自动将音高折叠至 52~87 键盘按键音域。",
            Dock = DockStyle.Bottom,
            Height = 28,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = isGuitarTarget ? Color.FromArgb(21, 128, 61) : Color.FromArgb(30, 64, 175),
            BackColor = isGuitarTarget ? Color.FromArgb(240, 253, 244) : Color.FromArgb(239, 246, 255),
            Padding = new Padding(8, 4, 8, 4)
        };

        grpInstrument.Controls.AddRange(new Control[] { tblInstrument, _lblTargetInstrumentDesc });

        // --- Box 1: 主功能配置 (歌曲名, 移调, 超范围, 鼓轨) ---
        var grpMain = new GroupBox
        {
            Text = " 基础转换配置 (主功能) ",
            Dock = DockStyle.Top,
            Height = 175,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 37, 41),
            Padding = new Padding(12),
            BackColor = Color.White
        };

        var tblMain = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
        };
        tblMain.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115F));
        tblMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        tblMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // Name
        tblMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // Transpose
        tblMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // Out of range
        tblMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // Drum

        // Row 0: Name
        tblMain.Controls.Add(new Label { Text = "歌曲名称:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        _txtName = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(existingDisplayName)
                ? Path.GetFileNameWithoutExtension(_sourceName)
                : existingDisplayName,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 10, 2)
        };
        tblMain.Controls.Add(_txtName, 1, 0);

        // Row 1: Transpose
        tblMain.Controls.Add(new Label { Text = "移调 (半音):", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        var flowTrans = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        _numTranspose = new NumericUpDown
        {
            Minimum = -24,
            Maximum = 24,
            Value = initialOptions?.Transpose ?? 0,
            Size = new Size(70, 24),
            Margin = new Padding(0, 2, 6, 2)
        };
        var lblTransHint = new Label { Text = "(-24 ~ +24，调整音调高低)", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 5, 0, 0) };
        flowTrans.Controls.AddRange(new Control[] { _numTranspose, lblTransHint });
        tblMain.Controls.Add(flowTrans, 1, 1);

        // Row 2: Out of range
        tblMain.Controls.Add(new Label { Text = "超范围音:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        _cmbOutOfRange = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200,
            Margin = new Padding(0, 2, 0, 2)
        };
        _cmbOutOfRange.Items.AddRange(new object[] { "自动八度折叠 (推荐)", "丢弃超范围音" });
        _cmbOutOfRange.SelectedIndex = (initialOptions?.OutOfRangeMode == "Discard") ? 1 : 0;
        tblMain.Controls.Add(_cmbOutOfRange, 1, 2);

        // Row 3: Drum filter
        tblMain.Controls.Add(new Label { Text = "鼓轨过滤:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        _chkIgnoreDrum = new CheckBox
        {
            Text = "过滤标准打击乐/鼓通道 (Channel 10)",
            Checked = initialOptions?.IgnoreDrumChannel ?? true,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        tblMain.Controls.Add(_chkIgnoreDrum, 1, 3);
        grpMain.Controls.Add(tblMain);

        // --- Box 2: 声部提取与智能主旋律识别 (副功能) ---
        var grpMelody = new GroupBox
        {
            Text = " 声部提取与智能主旋律 (副功能，按需开启) ",
            Dock = DockStyle.Top,
            Height = 220,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 37, 41),
            Padding = new Padding(12),
            BackColor = Color.White,
            Margin = new Padding(0, 10, 0, 0)
        };

        var tblMelodyBox = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
            Margin = new Padding(0)
        };
        tblMelodyBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tblMelodyBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F)); // Radio buttons row
        tblMelodyBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Mode buttons & options

        var tblRadio = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        tblRadio.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        tblRadio.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

        _rbFullSong = new RadioButton
        {
            Text = "完整曲目演奏 (保留全部声部与和弦伴奏，适合键盘或扫弦伴奏)",
            Checked = !isGuitarTarget,
            AutoSize = true,
            ForeColor = !isGuitarTarget ? Color.FromArgb(13, 110, 253) : Color.FromArgb(73, 80, 87),
            Font = new Font("Microsoft YaHei UI", 9F, !isGuitarTarget ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 2, 0, 2)
        };
        _rbMelodyExtraction = new RadioButton
        {
            Text = "智能提炼主旋律 ★ (推荐: 吉他独奏专属，提炼单音旋律线，绝不吞音卡品)",
            Checked = isGuitarTarget,
            AutoSize = true,
            ForeColor = isGuitarTarget ? Color.FromArgb(13, 110, 253) : Color.FromArgb(73, 80, 87),
            Font = new Font("Microsoft YaHei UI", 9F, isGuitarTarget ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 2, 0, 2)
        };
        tblRadio.Controls.Add(_rbFullSong, 0, 0);
        tblRadio.Controls.Add(_rbMelodyExtraction, 0, 1);

        _pnlMelodyOptions = new Panel
        {
            Dock = DockStyle.Fill,
            Enabled = isGuitarTarget,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
        };

        var flowModes = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };

        _btnModeTrack = CreateModeButton("轨道优先", RecognitionMode.TrackPriority);
        _btnModeHighest = CreateModeButton("高音优先", RecognitionMode.HighestNote);
        _btnModeContinuous = CreateModeButton("连续旋律 ★", RecognitionMode.ContinuousMelody);
        _btnModeMono = CreateModeButton("单音优先", RecognitionMode.Monophonic);

        flowModes.Controls.AddRange(new Control[] { _btnModeContinuous, _btnModeHighest, _btnModeTrack, _btnModeMono });

        _lblMelodySummary = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.FromArgb(108, 117, 125),
            Text = "提示: 勾选提炼主旋律后可在上方快速试听不同算法提取出的主旋律效果",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _chkManualTracks = new CheckBox
        {
            Text = "手动指定音轨 (终极兜底)",
            Dock = DockStyle.Top,
            Height = 24,
            AutoSize = true
        };
        _clbTracks = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            Visible = false,
            CheckOnClick = true
        };
        if (_trackMetas != null)
        {
            foreach (var t in _trackMetas)
            {
                string info = string.IsNullOrWhiteSpace(t.Name)
                    ? $"Track {t.TrackId} [{t.InstrumentName}] ({t.NoteCount}音符)"
                    : $"Track {t.TrackId}: {t.Name} [{t.InstrumentName}] ({t.NoteCount}音符)";
                _clbTracks.Items.Add(info, true);
            }
        }

        _pnlMelodyOptions.Controls.Add(_clbTracks);
        _pnlMelodyOptions.Controls.Add(_chkManualTracks);
        _pnlMelodyOptions.Controls.Add(_lblMelodySummary);
        _pnlMelodyOptions.Controls.Add(flowModes);
        flowModes.BringToFront();
        _lblMelodySummary.BringToFront();
        _chkManualTracks.BringToFront();

        tblMelodyBox.Controls.Add(tblRadio, 0, 0);
        tblMelodyBox.Controls.Add(_pnlMelodyOptions, 0, 1);
        grpMelody.Controls.Add(tblMelodyBox);

        // --- Box 3: 即时电脑试听控制栏 (WinMM 原生硬解) ---
        var grpAudition = new GroupBox
        {
            Text = " 即时电脑试听 (原生软波表，免进游戏即可预览效果) ",
            Dock = DockStyle.Top,
            Height = 105,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 37, 41),
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.White,
            Margin = new Padding(0, 10, 0, 0)
        };

        var tableAudition = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
        };
        tableAudition.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F)); // Play
        tableAudition.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85F)); // Stop
        tableAudition.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // Scrubber + time

        tableAudition.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        tableAudition.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

        _btnPlayPreview = new Button
        {
            Text = "▶ 试听",
            Size = new Size(80, 32),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };
        _btnPlayPreview.FlatAppearance.BorderSize = 0;
        _btnPlayPreview.Click += OnPlayPreviewClicked;

        _btnStopPreview = new Button
        {
            Text = "■ 停止",
            Size = new Size(75, 32),
            BackColor = Color.FromArgb(248, 249, 250),
            ForeColor = Color.FromArgb(73, 80, 87),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnStopPreview.FlatAppearance.BorderColor = Color.FromArgb(206, 212, 218);
        _btnStopPreview.Click += (_, _) => StopAudition();

        var flowScrub = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        flowScrub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        flowScrub.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        flowScrub.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _tbPreviewScrubber = new TrackBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 1000,
            TickStyle = TickStyle.None,
            Height = 30
        };
        _tbPreviewScrubber.MouseDown += (s, e) =>
        {
            _isScrubbing = true;
            if (e.Button == MouseButtons.Left && _tbPreviewScrubber.Width > 24)
            {
                int pad = 12;
                int usableWidth = _tbPreviewScrubber.Width - (pad * 2);
                if (usableWidth > 0)
                {
                    float ratio = Math.Clamp((float)(e.X - pad) / usableWidth, 0f, 1f);
                    _tbPreviewScrubber.Value = (int)Math.Round(ratio * _tbPreviewScrubber.Maximum);
                    UpdateScrubberTimeLabel();
                }
            }
        };
        _tbPreviewScrubber.Scroll += (_, _) =>
        {
            if (_isScrubbing)
            {
                UpdateScrubberTimeLabel();
            }
        };
        _tbPreviewScrubber.MouseUp += (_, _) =>
        {
            _isScrubbing = false;
            OnScrubberSeeked();
        };

        _lblPreviewTime = new Label
        {
            Text = "00:00 / 00:00",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Consolas", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(73, 80, 87),
            Margin = new Padding(0),
            AutoSize = false
        };

        flowScrub.Controls.Add(_tbPreviewScrubber, 0, 0);
        flowScrub.Controls.Add(_lblPreviewTime, 1, 0);

        tableAudition.Controls.Add(_btnPlayPreview, 0, 0);
        tableAudition.Controls.Add(_btnStopPreview, 1, 0);
        tableAudition.Controls.Add(flowScrub, 2, 0);

        // Preview stats in bottom of audition box
        var flowStats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        _lblPreviewCount = new Label
        {
            Text = "有效音符: 0 个",
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 167, 69),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 4, 15, 0)
        };
        _lblPreviewDuration = new Label
        {
            Text = "预计时长: 00:00",
            AutoSize = true,
            ForeColor = Color.FromArgb(13, 110, 253),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 0)
        };
        flowStats.Controls.AddRange(new Control[] { _lblPreviewCount, _lblPreviewDuration });
        tableAudition.Controls.Add(flowStats, 0, 1);
        tableAudition.SetColumnSpan(flowStats, 3);

        grpAudition.Controls.Add(tableAudition);

        // Add groups to Center (in top-down order)
        pnlCenter.Controls.Add(grpAudition);
        pnlCenter.Controls.Add(grpMelody);
        pnlCenter.Controls.Add(grpMain);
        pnlCenter.Controls.Add(grpInstrument);

        Controls.Add(pnlCenter);
        Controls.Add(pnlBottom);
        Controls.Add(pnlHeader);

        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        // Wire event handlers
        _rbTargetGuitar.CheckedChanged += (_, _) =>
        {
            if (_rbTargetGuitar.Checked)
            {
                _rbTargetGuitar.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                _rbTargetGuitar.ForeColor = Color.FromArgb(22, 101, 52);
                _rbTarget36Key.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
                _rbTarget36Key.ForeColor = Color.FromArgb(73, 80, 87);
                _lblTargetInstrumentDesc.Text = "💡 吉他专用：自动启用【智能连续主旋律提取】，消除伴奏和弦干扰，保证音符清晰不漏！";
                _lblTargetInstrumentDesc.ForeColor = Color.FromArgb(21, 128, 61);
                _lblTargetInstrumentDesc.BackColor = Color.FromArgb(240, 253, 244);

                if (_rbFullSong.Checked)
                {
                    _rbMelodyExtraction.Checked = true;
                }
                UpdatePreview();
                RestartAuditionIfPlaying();
            }
        };

        _rbTarget36Key.CheckedChanged += (_, _) =>
        {
            if (_rbTarget36Key.Checked)
            {
                _rbTarget36Key.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                _rbTarget36Key.ForeColor = Color.FromArgb(13, 110, 253);
                _rbTargetGuitar.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
                _rbTargetGuitar.ForeColor = Color.FromArgb(73, 80, 87);
                _lblTargetInstrumentDesc.Text = "💡 36 键乐器模式：支持多声部并发，自动将音高折叠至 52~87 键盘按键音域。";
                _lblTargetInstrumentDesc.ForeColor = Color.FromArgb(30, 64, 175);
                _lblTargetInstrumentDesc.BackColor = Color.FromArgb(239, 246, 255);

                UpdatePreview();
                RestartAuditionIfPlaying();
            }
        };

        _numTranspose.ValueChanged += (_, _) =>
        {
            UpdatePreview();
            RestartAuditionIfPlaying();
        };
        _chkIgnoreDrum.CheckedChanged += (_, _) =>
        {
            UpdatePreview();
            RestartAuditionIfPlaying();
        };
        _cmbOutOfRange.SelectedIndexChanged += (_, _) =>
        {
            UpdatePreview();
            RestartAuditionIfPlaying();
        };

        _rbFullSong.CheckedChanged += (_, _) =>
        {
            if (_rbFullSong.Checked)
            {
                _currentMode = RecognitionMode.FullSong;
                _pnlMelodyOptions.Enabled = false;
                _rbFullSong.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                _rbMelodyExtraction.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
                _rbFullSong.ForeColor = Color.FromArgb(13, 110, 253);
                _rbMelodyExtraction.ForeColor = Color.FromArgb(73, 80, 87);
                HighlightActiveModeButton(null);
                UpdatePreview();
                RestartAuditionIfPlaying();
            }
        };

        _rbMelodyExtraction.CheckedChanged += (_, _) =>
        {
            if (_rbMelodyExtraction.Checked)
            {
                _pnlMelodyOptions.Enabled = true;
                _rbFullSong.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
                _rbMelodyExtraction.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                _rbFullSong.ForeColor = Color.FromArgb(73, 80, 87);
                _rbMelodyExtraction.ForeColor = Color.FromArgb(13, 110, 253);

                EnsureCandidatesGenerated();
                // Default to ContinuousMelody
                SelectMode(RecognitionMode.ContinuousMelody);
            }
        };

        _chkManualTracks.CheckedChanged += (_, _) =>
        {
            _clbTracks.Visible = _chkManualTracks.Checked;
            grpMelody.Height = _chkManualTracks.Checked ? 320 : 220;
            if (_chkManualTracks.Checked)
            {
                _currentMode = RecognitionMode.ManualTracks;
                HighlightActiveModeButton(null);
                UpdatePreview();
                RestartAuditionIfPlaying();
            }
            else
            {
                SelectMode(RecognitionMode.ContinuousMelody);
            }
        };

        _clbTracks.ItemCheck += (_, _) =>
        {
            BeginInvoke(() =>
            {
                if (_chkManualTracks.Checked)
                {
                    UpdatePreview();
                    RestartAuditionIfPlaying();
                }
            });
        };

        _previewPlayer.OnProgress += PreviewPlayer_OnProgress;
        _previewPlayer.OnPlaybackCompleted += PreviewPlayer_OnPlaybackCompleted;

        FormClosing += (_, _) =>
        {
            _previewPlayer.Dispose();
        };

        // Initialize mode based on initialOptions
        if (!string.IsNullOrEmpty(initialOptions?.RecognitionModeName) &&
            Enum.TryParse<RecognitionMode>(initialOptions.RecognitionModeName, out var savedMode))
        {
            if (savedMode == RecognitionMode.FullSong)
            {
                _rbFullSong.Checked = true;
                _rbMelodyExtraction.Checked = false;
            }
            else
            {
                _rbMelodyExtraction.Checked = true;
                _rbFullSong.Checked = false;
                SelectMode(savedMode);
            }
        }
        else if (isGuitarTarget)
        {
            _rbMelodyExtraction.Checked = true;
            _rbFullSong.Checked = false;
            SelectMode(RecognitionMode.ContinuousMelody);
        }

        UpdatePreview();
    }

    private Button CreateModeButton(string text, RecognitionMode mode)
    {
        var btn = new Button
        {
            Text = text,
            Height = 28,
            AutoSize = true,
            BackColor = Color.FromArgb(248, 249, 250),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 8, 2),
            Tag = mode
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(206, 212, 218);
        btn.Click += (_, _) => SelectMode(mode);
        return btn;
    }

    private void EnsureCandidatesGenerated()
    {
        if (_candidates == null)
        {
            _candidates = RecognitionService.GenerateAllCandidates(_rawNotes, _trackMetas);

            // Update button texts with melody scores
            if (_candidates.TryGetValue(RecognitionMode.TrackPriority, out var resA))
                _btnModeTrack.Text = $"轨道 ({resA.MelodyScore})";
            if (_candidates.TryGetValue(RecognitionMode.HighestNote, out var resB))
                _btnModeHighest.Text = $"高音 ({resB.MelodyScore})";
            if (_candidates.TryGetValue(RecognitionMode.ContinuousMelody, out var resC))
                _btnModeContinuous.Text = $"连续 ★ ({resC.MelodyScore})";
            if (_candidates.TryGetValue(RecognitionMode.Monophonic, out var resD))
                _btnModeMono.Text = $"单音 ({resD.MelodyScore})";
        }
    }

    private void SelectMode(RecognitionMode mode)
    {
        _currentMode = mode;
        _chkManualTracks.Checked = false;
        EnsureCandidatesGenerated();

        HighlightActiveModeButton(mode);

        if (_candidates != null && _candidates.TryGetValue(mode, out var cand))
        {
            _lblMelodySummary.Text = $"【{cand.Name}】: {cand.Summary} (旋律评分: {cand.MelodyScore})";
            _lblMelodySummary.ForeColor = Color.FromArgb(40, 167, 69);
        }

        UpdatePreview();
        RestartAuditionIfPlaying();
    }

    private void HighlightActiveModeButton(RecognitionMode? mode)
    {
        ResetButtonVisual(_btnModeTrack, mode == RecognitionMode.TrackPriority);
        ResetButtonVisual(_btnModeHighest, mode == RecognitionMode.HighestNote);
        ResetButtonVisual(_btnModeContinuous, mode == RecognitionMode.ContinuousMelody);
        ResetButtonVisual(_btnModeMono, mode == RecognitionMode.Monophonic);
    }

    private static void ResetButtonVisual(Button btn, bool active)
    {
        if (active)
        {
            btn.BackColor = Color.FromArgb(13, 110, 253);
            btn.ForeColor = Color.White;
            btn.FlatAppearance.BorderColor = Color.FromArgb(13, 110, 253);
            btn.Font = new Font(btn.Font, FontStyle.Bold);
        }
        else
        {
            btn.BackColor = Color.FromArgb(248, 249, 250);
            btn.ForeColor = Color.FromArgb(33, 37, 41);
            btn.FlatAppearance.BorderColor = Color.FromArgb(206, 212, 218);
            btn.Font = new Font(btn.Font, FontStyle.Regular);
        }
    }

    private IReadOnlyList<RawMidiNote> GetCurrentWorkingNotes()
    {
        if (_currentMode == RecognitionMode.FullSong)
        {
            return _rawNotes;
        }

        if (_currentMode == RecognitionMode.ManualTracks)
        {
            var selectedTrackIndices = new HashSet<int>();
            for (int i = 0; i < _clbTracks.Items.Count; i++)
            {
                if (_clbTracks.GetItemChecked(i))
                {
                    selectedTrackIndices.Add(i);
                }
            }
            return _rawNotes.Where(n => selectedTrackIndices.Contains(n.TrackId)).ToList();
        }

        EnsureCandidatesGenerated();
        if (_candidates != null && _candidates.TryGetValue(_currentMode, out var cand))
        {
            return cand.Notes;
        }

        return _rawNotes;
    }

    private async void UpdatePreview()
    {
        int version = ++_previewVersion;
        _lblPreviewCount.Text = "计算中...";
        _lblPreviewDuration.Text = "--:--";

        var workingNotes = GetCurrentWorkingNotes();
        var options = BuildOptions();
        var songName = _txtName.Text;

        try
        {
            var tempSong = await Task.Run(() =>
            {
                return MidiConverter.Convert(workingNotes, _sourceName, _sourceHash, songName, options);
            });

            if (version == _previewVersion && !IsDisposed)
            {
                _cachedPreviewSong = tempSong;
                _cachedPreviewOptions = options;
                _lblPreviewCount.Text = $"有效音符: {tempSong.Notes.Count:N0} 个";
                _lblPreviewDuration.Text = $"预计时长: {TimeFormatter.FormatUs(tempSong.DurationUs)}";

                var durUs = tempSong.DurationUs;
                _lblPreviewTime.Text = $"00:00 / {TimeFormatter.FormatUs(durUs)}";
            }
        }
        catch
        {
            if (version == _previewVersion && !IsDisposed)
            {
                _lblPreviewCount.Text = "计算异常";
                _lblPreviewDuration.Text = "--:--";
            }
        }
    }

    private ConvertOptions BuildOptions()
    {
        return new ConvertOptions
        {
            Transpose = (int)_numTranspose.Value,
            IgnoreDrumChannel = _chkIgnoreDrum.Checked,
            OutOfRangeMode = _cmbOutOfRange.SelectedIndex == 1 ? "Discard" : "OctaveFold",
            TargetInstrument = _rbTargetGuitar.Checked ? 1 : 0
        };
    }

    // ==================== AUDITION LOGIC ====================
    private void OnPlayPreviewClicked(object? sender, EventArgs e)
    {
        if (_previewPlayer.IsPlaying)
        {
            _previewPlayer.Pause();
            _btnPlayPreview.Text = "▶ 恢复";
            _btnPlayPreview.BackColor = Color.FromArgb(13, 110, 253);
        }
        else if (_previewPlayer.IsPaused)
        {
            _previewPlayer.Resume();
            _btnPlayPreview.Text = "⏸ 暂停";
            _btnPlayPreview.BackColor = Color.FromArgb(255, 193, 7);
        }
        else
        {
            StartPreviewAudition();
        }
    }

    private void StartPreviewAudition(long? targetOffsetUs = null)
    {
        var workingNotes = GetCurrentWorkingNotes();
        if (workingNotes.Count == 0) return;

        var options = BuildOptions();
        var song = _cachedPreviewSong;
        if (song == null || _cachedPreviewOptions == null ||
            _cachedPreviewOptions.Transpose != options.Transpose ||
            _cachedPreviewOptions.IgnoreDrumChannel != options.IgnoreDrumChannel ||
            _cachedPreviewOptions.OutOfRangeMode != options.OutOfRangeMode ||
            _cachedPreviewOptions.TargetInstrument != options.TargetInstrument)
        {
            song = MidiConverter.Convert(workingNotes, _sourceName, _sourceHash, _txtName.Text, options);
            _cachedPreviewSong = song;
            _cachedPreviewOptions = options;
        }

        if (song.Notes.Count == 0)
        {
            MessageBox.Show(this, "当前设置下没有有效的音符可供试听（可能全部被过滤或超出音域）", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long startUs = 0;
        if (targetOffsetUs.HasValue)
        {
            startUs = targetOffsetUs.Value;
        }
        else if (song.DurationUs > 0 && _tbPreviewScrubber.Value > 0)
        {
            startUs = (long)((double)_tbPreviewScrubber.Value / _tbPreviewScrubber.Maximum * song.DurationUs);
        }

        var auditionNotes = song.Notes.Select(gn => new RawMidiNote
        {
            TrackId = 0,
            StartUs = gn.TimeUs,
            DurationUs = gn.DurationUs,
            NoteNumber = (byte)(MidiConverter.GameMinMidi + gn.NoteIndex),
            Velocity = 100,
            Channel = 0
        }).ToList();

        _previewPlayer.Play(auditionNotes, startUs, song.DurationUs);
        _btnPlayPreview.Text = "⏸ 暂停";
        _btnPlayPreview.BackColor = Color.FromArgb(255, 193, 7);
    }

    private void StopAudition()
    {
        _previewPlayer.Stop();
        _btnPlayPreview.Text = "▶ 试听";
        _btnPlayPreview.BackColor = Color.FromArgb(40, 167, 69);
        _tbPreviewScrubber.Value = 0;
        if (_cachedPreviewSong != null)
        {
            _lblPreviewTime.Text = $"00:00 / {TimeFormatter.FormatUs(_cachedPreviewSong.DurationUs)}";
        }
    }

    private void RestartAuditionIfPlaying()
    {
        if (_previewPlayer.IsPlaying)
        {
            long currentUs = _previewPlayer.CurrentPositionUs;
            StartPreviewAudition(currentUs);
        }
    }

    private void UpdateScrubberTimeLabel()
    {
        if (_cachedPreviewSong != null && _cachedPreviewSong.DurationUs > 0)
        {
            long currentUs = (long)((double)_tbPreviewScrubber.Value / _tbPreviewScrubber.Maximum * _cachedPreviewSong.DurationUs);
            _lblPreviewTime.Text = $"{TimeFormatter.FormatUs(currentUs)} / {TimeFormatter.FormatUs(_cachedPreviewSong.DurationUs)}";
        }
    }

    private void OnScrubberSeeked()
    {
        if (_cachedPreviewSong == null || _cachedPreviewSong.DurationUs <= 0) return;
        long targetUs = (long)((double)_tbPreviewScrubber.Value / _tbPreviewScrubber.Maximum * _cachedPreviewSong.DurationUs);

        if (_previewPlayer.IsPlaying || _previewPlayer.IsPaused)
        {
            StartPreviewAudition(targetUs);
        }
        else
        {
            _lblPreviewTime.Text = $"{TimeFormatter.FormatUs(targetUs)} / {TimeFormatter.FormatUs(_cachedPreviewSong.DurationUs)}";
        }
    }

    private void PreviewPlayer_OnProgress(long currentUs, long totalUs)
    {
        if (_isScrubbing || IsDisposed) return;

        if (totalUs > 0)
        {
            int val = (int)Math.Clamp((currentUs * _tbPreviewScrubber.Maximum) / totalUs, 0, _tbPreviewScrubber.Maximum);
            _tbPreviewScrubber.Value = val;
        }
        _lblPreviewTime.Text = $"{TimeFormatter.FormatUs(currentUs)} / {TimeFormatter.FormatUs(totalUs)}";
    }

    private void PreviewPlayer_OnPlaybackCompleted()
    {
        if (IsDisposed) return;
        _btnPlayPreview.Text = "▶ 试听";
        _btnPlayPreview.BackColor = Color.FromArgb(40, 167, 69);
        _tbPreviewScrubber.Value = 0;
        if (_cachedPreviewSong != null)
        {
            _lblPreviewTime.Text = $"00:00 / {TimeFormatter.FormatUs(_cachedPreviewSong.DurationUs)}";
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        string name = _txtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请输入歌曲名称", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        StopAudition();

        _btnSave.Enabled = false;
        _btnCancel.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var workingNotes = GetCurrentWorkingNotes();
            var options = BuildOptions();

            ConvertedSong song;
            if (_cachedPreviewSong != null && _cachedPreviewOptions != null &&
                _cachedPreviewOptions.Transpose == options.Transpose &&
                _cachedPreviewOptions.IgnoreDrumChannel == options.IgnoreDrumChannel &&
                _cachedPreviewOptions.OutOfRangeMode == options.OutOfRangeMode &&
                _cachedPreviewOptions.TargetInstrument == options.TargetInstrument)
            {
                song = _cachedPreviewSong;
                song.DisplayName = name;
            }
            else
            {
                song = await Task.Run(() => MidiConverter.Convert(workingNotes, _sourceName, _sourceHash, name, options));
            }

            if (!string.IsNullOrEmpty(_existingId))
            {
                song.Id = _existingId;
            }

            song.TargetInstrument = options.TargetInstrument;
            song.RecognitionModeName = _currentMode.ToString();
            song.RawNotes = workingNotes.ToList();
            ResultSong = song;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存歌曲时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnSave.Enabled = true;
            _btnCancel.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableImmersiveDarkMode(Handle);
    }
}
