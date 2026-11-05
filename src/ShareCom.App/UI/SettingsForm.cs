using ShareCom.Core.Input;

namespace ShareCom.App.UI;

/// <summary>설정 창에 넣고 꺼내는 값.</summary>
internal sealed record SettingsValues(
    bool AutoStart,
    bool ClipboardEnabled,
    bool EncryptionEnabled,
    bool EdgeLocked,
    HotkeySettings Hotkeys,
    bool AutoUpdate);

/// <summary>이 PC 정보(읽기 전용 표시).</summary>
internal sealed record SettingsInfo(string PcName, string Thumbprint, string Version, bool CanChangeAutoStart, string UpdateStatus = "");

/// <summary>설정 창. [확인]을 누르면 <see cref="Result"/> 에 고른 값이 담긴다.</summary>
internal sealed class SettingsForm : Form
{
    private readonly CheckBox _autoStart, _clipboard, _encryption, _edgeLocked, _autoUpdate;
    private readonly Label _updateStatus;
    private readonly ComboBox _lockKey, _jumpMods;
    private readonly Label _jumpHint;

    public SettingsValues? Result { get; private set; }

    /// <param name="checkUpdate">[지금 확인]을 누르면 부른다. 돌려준 글을 상태 칸에 보여 준다.</param>
    public SettingsForm(SettingsValues values, SettingsInfo info, Action openLogFolder, Func<Task<string>>? checkUpdate = null)
    {
        Text = "ShareCom 설정";
        Font = UiFonts.Normal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        // ── 일반
        var general = Group("일반");
        _autoStart = Check("Windows 에 로그인할 때 자동으로 시작", values.AutoStart);
        _autoStart.Enabled = info.CanChangeAutoStart;
        general.Controls.Add(_autoStart);
        general.Controls.Add(Hint(info.CanChangeAutoStart
            ? "작업 스케줄러에 '가장 높은 권한'으로 등록해 UAC 창 없이 켭니다."
            : "관리자 권한으로 실행해야 바꿀 수 있습니다."));
        _clipboard = Check("클립보드 공유 (복사한 글·그림·파일 목록을 다른 PC로)", values.ClipboardEnabled);
        general.Controls.Add(_clipboard);
        _encryption = Check("암호화 (TLS)", values.EncryptionEnabled);
        general.Controls.Add(_encryption);
        general.Controls.Add(Hint("끄면 같은 와이파이의 다른 기기가 키 입력을 엿보거나 가짜 입력을 넣어 이 PC를 조작할 수 있습니다.\n끄지 않기를 권합니다. 바꾸면 다시 연결합니다."));
        _edgeLocked = Check("가장자리 넘기기 잠금", values.EdgeLocked);
        general.Controls.Add(_edgeLocked);
        root.Controls.Add(general.Parent!);

        // ── 단축키
        var keys = Group("단축키");
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lockKey = Combo(HotkeySettings.LockKeyChoices.Select(c => c.Name));
        _lockKey.SelectedIndex = Math.Max(0, HotkeySettings.LockKeyChoices.ToList().FindIndex(c => c.Key == values.Hotkeys.LockKey));
        AddRow(grid, "가장자리 넘기기 잠금 켜기/끄기", _lockKey);

        _jumpMods = Combo(HotkeySettings.JumpModifierChoices.Select(HotkeySettings.ModifiersName));
        _jumpMods.SelectedIndex = Math.Max(0, HotkeySettings.JumpModifierChoices.ToList().IndexOf(values.Hotkeys.JumpModifiers));
        AddRow(grid, "PC 바로 이동 (+ 숫자 1~4)", _jumpMods);

        AddRow(grid, "긴급 복귀 (무조건 내 PC로)", new Label { Text = "Ctrl+Alt+Esc (바꿀 수 없음)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        keys.Controls.Add(grid);
        _jumpHint = Hint("");
        keys.Controls.Add(_jumpHint);
        _jumpMods.SelectedIndexChanged += (_, _) => UpdateJumpHint();
        UpdateJumpHint();
        root.Controls.Add(keys.Parent!);

        // ── 업데이트
        var update = Group("업데이트");
        _autoUpdate = Check("새 버전이 나오면 자동으로 업데이트", values.AutoUpdate);
        update.Controls.Add(_autoUpdate);
        update.Controls.Add(Hint("GitHub 정식 릴리즈를 6시간마다 확인하고, 서명을 확인한 뒤\n이 PC를 5분 넘게 쓰지 않을 때 바꿉니다. 데이터 통신 연결에서는 받지 않습니다."));
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var checkNow = new Button { Text = "지금 확인", AutoSize = true, MinimumSize = new Size(90, 0), Enabled = checkUpdate is not null };
        _updateStatus = new Label { Text = info.UpdateStatus, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(8, 8, 3, 3) };
        checkNow.Click += async (_, _) =>
        {
            if (checkUpdate is null) return;
            checkNow.Enabled = false;
            _updateStatus.Text = "확인하는 중…";
            try { _updateStatus.Text = await checkUpdate(); }
            catch (Exception ex) { _updateStatus.Text = "확인하지 못했습니다: " + ex.Message; }
            finally { if (!IsDisposed) checkNow.Enabled = true; }
        };
        row.Controls.Add(checkNow);
        row.Controls.Add(_updateStatus);
        update.Controls.Add(row);
        root.Controls.Add(update.Parent!);

        // ── 이 PC
        var about = Group("이 PC");
        about.Controls.Add(new Label { Text = $"이름: {info.PcName}", AutoSize = true });
        about.Controls.Add(new Label { Text = $"인증서 지문: {info.Thumbprint}", AutoSize = true });
        about.Controls.Add(new Label { Text = $"버전: {info.Version}", AutoSize = true });
        var logs = new LinkLabel { Text = "기록 폴더 열기", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        logs.LinkClicked += (_, _) => openLogFolder();
        about.Controls.Add(logs);
        root.Controls.Add(about.Parent!);

        // ── 버튼
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "취소", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "확인", AutoSize = true, MinimumSize = new Size(90, 0) };
        ok.Click += (_, _) => AcceptAndClose();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>지금 고른 값으로 [확인]을 누른 것처럼 닫는다.</summary>
    public void AcceptAndClose()
    {
        Result = new SettingsValues(
            _autoStart.Checked,
            _clipboard.Checked,
            _encryption.Checked,
            _edgeLocked.Checked,
            new HotkeySettings(
                HotkeySettings.LockKeyChoices[_lockKey.SelectedIndex].Key,
                HotkeySettings.JumpModifierChoices[_jumpMods.SelectedIndex]),
            _autoUpdate.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateJumpHint()
    {
        var mods = HotkeySettings.JumpModifierChoices[_jumpMods.SelectedIndex];
        _jumpHint.Text = mods == HotkeyModifiers.None
            ? "PC 바로 이동을 쓰지 않습니다."
            : $"{HotkeySettings.ModifiersName(mods)}+1 은 내 PC, +2 부터는 [화면 배치]에서 왼쪽 PC부터 차례로입니다.";
    }

    /// <summary>제목 있는 묶음. 안쪽 세로 목록을 돌려준다(묶음 자체는 Parent).</summary>
    private static FlowLayoutPanel Group(string title)
    {
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10) };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
        box.Controls.Add(flow);
        return flow;
    }

    private static CheckBox Check(string text, bool value) =>
        new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 6, 3, 0) };

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(22, 0, 3, 4) };

    private static ComboBox Combo(IEnumerable<string> items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items.Cast<object>().ToArray());
        return c;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control value)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 12, 3) });
        grid.Controls.Add(value);
    }
}
