using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.UI;

public sealed class PresetEditorForm : Form
{
    private readonly DataGridView _grid;
    private readonly ComboBox _cmbPresets;
    private readonly Button _btnSave;
    private readonly Button _btnSaveAs;
    private readonly Button _btnResetDefault;
    private readonly Button _btnChangeKey;
    private readonly Label _lblStatus;

    private KeyPreset _currentPreset;
    private bool _capturingKey;
    private int _capturingRowIndex = -1;

    public event Action<string>? OnPresetSaved;

    public PresetEditorForm(string initialPresetName)
    {
        Text = "按键映射预设编辑器 (36 音游戏键盘)";
        Icon = AppIcon.Value;
        Size = new Size(740, 750);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body(9F);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        // Top Header Panel
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 12, 16, 12)
        };

        var flowTop = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0)
        };

        var lblPreset = new Label
        {
            Text = "预设:",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
            Font = Theme.Body(9F, FontStyle.Bold),
            ForeColor = Theme.Text
        };

        _cmbPresets = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Size = new Size(180, 28),
            Margin = new Padding(0, 4, 12, 0),
            BackColor = Theme.SurfaceCard,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat
        };

        _btnSave = new Button
        {
            Text = "保存",
            Size = new Size(80, 32),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 8, 0)
        };
        _btnSave.FlatAppearance.BorderSize = 0;

        _btnSaveAs = new Button
        {
            Text = "另存为...",
            Size = new Size(95, 32),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 8, 0)
        };
        _btnSaveAs.FlatAppearance.BorderColor = Theme.Border;

        _btnResetDefault = new Button
        {
            Text = "恢复默认36键",
            Size = new Size(140, 32),
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 0, 0)
        };
        _btnResetDefault.FlatAppearance.BorderColor = Theme.Border;

        flowTop.Controls.AddRange(new Control[] { lblPreset, _cmbPresets, _btnSave, _btnSaveAs, _btnResetDefault });
        topPanel.Controls.Add(flowTop);

        // Center Grid Area
        var pnlCenter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12)
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ReadOnly = true,
            RowHeadersVisible = false,
            BackgroundColor = Theme.SurfaceCard,
            BorderStyle = BorderStyle.None,
            GridColor = Theme.Border,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.SurfaceCard,
                ForeColor = Theme.Text,
                SelectionBackColor = Theme.Selection,
                SelectionForeColor = Theme.Text
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(32, 34, 42)
            },
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowTemplate = { Height = 28 }
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Body(9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        _grid.ColumnHeadersHeight = 32;
        _grid.EnableHeadersVisualStyles = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", FillWeight = 20, Name = "Index" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "音符", FillWeight = 35, Name = "Note" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MIDI", FillWeight = 25, Name = "Midi" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前映射按键", FillWeight = 50, Name = "Key" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ScanCode", FillWeight = 35, Name = "ScanCode" });

        pnlCenter.Controls.Add(_grid);

        // Bottom Action Bar
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 12, 16, 12)
        };

        var bottomLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _btnChangeKey = new Button
        {
            Text = "修改选中按键",
            Size = new Size(140, 36),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 15, 0)
        };
        _btnChangeKey.FlatAppearance.BorderSize = 0;

        _lblStatus = new Label
        {
            Text = "提示: 双击列表行或点击修改按钮，然后按下键盘物理键即可自动绑定。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Theme.Muted
        };

        bottomLayout.Controls.Add(_btnChangeKey, 0, 0);
        bottomLayout.Controls.Add(_lblStatus, 1, 0);
        bottomPanel.Controls.Add(bottomLayout);

        Controls.Add(pnlCenter);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);

        _currentPreset = PresetManager.LoadPreset(initialPresetName);

        RefreshPresetList(initialPresetName);
        PopulateGrid();

        _cmbPresets.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbPresets.SelectedItem is string name)
            {
                _currentPreset = PresetManager.LoadPreset(name);
                PopulateGrid();
            }
        };

        _btnChangeKey.Click += (_, _) => StartKeyCapture();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                StartKeyCapture();
            }
        };

        _btnResetDefault.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "确定将当前预设恢复为默认 36 键映射吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var def = PresetManager.CreateDefaultPreset();
                def.Name = _currentPreset.Name;
                _currentPreset = def;
                PopulateGrid();
                _lblStatus.Text = "已恢复为默认映射，请点击保存。";
                _lblStatus.ForeColor = Color.DarkOrange;
            }
        };

        _btnSave.Click += (_, _) => SaveCurrentPreset();
        _btnSaveAs.Click += (_, _) => SaveAsNewPreset();

        KeyPreview = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableImmersiveDarkMode(Handle);
    }

    private void RefreshPresetList(string selected)
    {
        _cmbPresets.Items.Clear();
        var presets = PresetManager.GetAvailablePresets();
        foreach (var p in presets)
        {
            _cmbPresets.Items.Add(p);
        }
        int idx = _cmbPresets.Items.IndexOf(selected);
        _cmbPresets.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void PopulateGrid()
    {
        _grid.Rows.Clear();
        for (int i = 0; i < _currentPreset.Keys.Count; i++)
        {
            var k = _currentPreset.Keys[i];
            _grid.Rows.Add(
                k.NoteIndex,
                k.NoteName,
                k.MidiNumber,
                k.KeyName,
                $"0x{k.ScanCode:X2}{(k.Extended ? " (Ext)" : "")}"
            );
        }
    }

    private void StartKeyCapture()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "请先在列表中选中一个音符行", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _capturingRowIndex = _grid.SelectedRows[0].Index;
        var note = _currentPreset.Keys[_capturingRowIndex];
        _capturingKey = true;
        _lblStatus.Text = $"👉 请按下要绑定给 [{note.NoteName}] 的按键... (按 Esc 取消)";
        _lblStatus.ForeColor = Color.Red;
    }

    protected override bool ProcessKeyPreview(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        if (_capturingKey && (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN))
        {
            int wParam = m.WParam.ToInt32();
            if (wParam == (int)Keys.Escape)
            {
                _capturingKey = false;
                _lblStatus.Text = "已取消按键绑定。";
                _lblStatus.ForeColor = Color.DimGray;
                return true;
            }

            int lParam = m.LParam.ToInt32();
            ushort scanCode = (ushort)((lParam >> 16) & 0xFF);
            bool extended = ((lParam >> 24) & 1) != 0;

            if (scanCode == 0)
            {
                scanCode = (ushort)PresetManager.MapVirtualKey((uint)wParam, 0);
            }

            string keyDisplay = PresetManager.GetKeyDisplay(scanCode, extended);

            if (_capturingRowIndex >= 0 && _capturingRowIndex < _currentPreset.Keys.Count)
            {
                var item = _currentPreset.Keys[_capturingRowIndex];
                item.ScanCode = scanCode;
                item.Extended = extended;
                item.KeyName = keyDisplay;

                _grid.Rows[_capturingRowIndex].Cells[3].Value = keyDisplay;
                _grid.Rows[_capturingRowIndex].Cells[4].Value = $"0x{scanCode:X2}{(extended ? " (Ext)" : "")}";

                _capturingKey = false;
                _lblStatus.Text = $"已将 [{item.NoteName}] 绑定到按键 [{keyDisplay}] (0x{scanCode:X2})";
                _lblStatus.ForeColor = Color.FromArgb(40, 167, 69);
                return true;
            }
        }

        return base.ProcessKeyPreview(ref m);
    }

    private void SaveCurrentPreset()
    {
        PresetManager.SavePreset(_currentPreset);
        _lblStatus.Text = $"预设 [{_currentPreset.Name}] 保存成功！";
        _lblStatus.ForeColor = Color.FromArgb(40, 167, 69);
        OnPresetSaved?.Invoke(_currentPreset.Name);
    }

    private void SaveAsNewPreset()
    {
        string prompt = "请输入新预设名称:";
        using var promptForm = new Form
        {
            Text = "另存为新预设",
            Width = 360,
            Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        var lbl = new Label { Text = prompt, Left = 20, Top = 18, AutoSize = true };
        var txt = new TextBox { Left = 20, Top = 42, Width = 300, Text = $"{_currentPreset.Name}_副本" };
        var btnOk = new Button { Text = "确定", Left = 150, Top = 80, Width = 80, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var btnCancel = new Button { Text = "取消", Left = 240, Top = 80, Width = 80, DialogResult = DialogResult.Cancel };
        promptForm.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        promptForm.AcceptButton = btnOk;
        promptForm.CancelButton = btnCancel;

        if (promptForm.ShowDialog(this) == DialogResult.OK)
        {
            string newName = txt.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _currentPreset.Name = newName;
                PresetManager.SavePreset(_currentPreset);
                RefreshPresetList(newName);
                _lblStatus.Text = $"新预设 [{newName}] 保存成功！";
                _lblStatus.ForeColor = Color.FromArgb(40, 167, 69);
                OnPresetSaved?.Invoke(newName);
            }
        }
    }
}
