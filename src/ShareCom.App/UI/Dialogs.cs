using System.Net;
using ShareCom.Core.Net;

namespace ShareCom.App.UI;

internal static class UiFonts
{
    public static readonly Font Normal = new("Malgun Gothic", 10f);
    public static readonly Font Code = new("Consolas", 36f, FontStyle.Bold);
}

/// <summary>짝 맺기 확인 창. 두 PC 화면의 6자리 코드가 같은지 사용자가 보고 고른다.</summary>
internal sealed class PairingForm : Form
{
    public bool Accepted { get; private set; }

    public PairingForm(PairingPrompt prompt)
    {
        Text = "ShareCom - 짝 맺기";
        Font = UiFonts.Normal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        var intro = prompt.IsInitiator
            ? $"'{prompt.PeerName}' PC에 짝 맺기를 요청했습니다."
            : $"'{prompt.PeerName}' PC가 짝 맺기를 요청합니다.";
        layout.Controls.Add(new Label { Text = intro, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        layout.Controls.Add(new Label
        {
            Text = "두 PC 화면에 뜬 코드가 같으면 [짝 맺기]를 누르세요.\n다르면 다른 기기가 끼어든 것이니 [취소]를 누르세요.",
            AutoSize = true,
        });
        layout.Controls.Add(new Label
        {
            Text = prompt.Code[..3] + " " + prompt.Code[3..],
            Font = UiFonts.Code,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 16, 0, 16),
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "짝 맺기", AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => { Accepted = true; Close(); };
        cancel.Click += (_, _) => { Accepted = false; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>자동 검색이 안 되는 망에서 IP 주소로 짝 맺기를 요청하는 창.</summary>
internal sealed class ConnectByAddressForm : Form
{
    private readonly TextBox _address = new() { Width = 260 };
    public IPEndPoint? Endpoint { get; private set; }

    public ConnectByAddressForm(int defaultPort)
    {
        Text = "ShareCom - IP 주소로 짝 맺기";
        Font = UiFonts.Normal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = $"상대 PC의 IP 주소를 적으세요. (예: 192.168.0.12)\n상대 PC에서도 ShareCom 이 켜져 있어야 합니다. 포트를 바꿨으면 주소:포트로 적습니다(기본 {defaultPort}).",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(_address);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "짝 맺기 요청", AutoSize = true };
        ok.Click += (_, _) =>
        {
            var text = _address.Text.Trim();
            if (IPEndPoint.TryParse(text, out var ep) && ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (ep.Port == 0) ep.Port = defaultPort;
                Endpoint = ep;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show(this, "IPv4 주소 형식이 아닙니다. 예: 192.168.0.12", "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
