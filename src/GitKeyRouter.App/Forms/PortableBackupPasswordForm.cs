using GitKeyRouter.App.Presentation;

namespace GitKeyRouter.App.Forms;

public sealed class PortableBackupPasswordForm : Form
{
    private const int MinimumExportPasswordLength = 12;
    private readonly TextBox _password = new()
    {
        Name = "PortableBackupPasswordTextBox",
        UseSystemPasswordChar = true,
        Dock = DockStyle.Fill
    };
    private readonly TextBox? _confirmation;

    public PortableBackupPasswordForm(bool confirmPassword)
    {
        Text = confirmPassword
            ? AppLocalization.T("设置便携备份口令", "Set portable backup password")
            : AppLocalization.T("输入便携备份口令", "Enter portable backup password");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, confirmPassword ? 220 : 170);
        Padding = new Padding(18);

        _confirmation = confirmPassword
            ? new TextBox
            {
                Name = "PortableBackupPasswordConfirmTextBox",
                UseSystemPasswordChar = true,
                Dock = DockStyle.Fill
            }
            : null;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = confirmPassword ? 4 : 3,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        if (confirmPassword)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var row = 0;
        layout.Controls.Add(new Label
        {
            Text = AppLocalization.T("口令", "Password"),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        layout.Controls.Add(_password, 1, row++);

        if (_confirmation is not null)
        {
            layout.Controls.Add(new Label
            {
                Text = AppLocalization.T("确认口令", "Confirm password"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            layout.Controls.Add(_confirmation, 1, row++);
        }

        layout.Controls.Add(new Label
        {
            Text = confirmPassword
                ? AppLocalization.T(
                    "该口令用于加密设置和私钥。至少 12 个字符；丢失后无法恢复。",
                    "This password encrypts settings and private keys. Use at least 12 characters; it cannot be recovered.")
                : AppLocalization.T(
                    "口令仅用于本次解密，不会保存。",
                    "The password is used only for this decryption and is not stored."),
            Dock = DockStyle.Fill,
            AutoSize = false
        }, 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        row++;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        var ok = UiHelpers.Button(AppLocalization.T("确定", "OK"), (_, _) => Accept(confirmPassword));
        ok.Name = "PortableBackupPasswordOkButton";
        var cancel = UiHelpers.Button(AppLocalization.T("取消", "Cancel"), (_, _) => DialogResult = DialogResult.Cancel);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    public string Password => _password.Text;

    private void Accept(bool confirmPassword)
    {
        if (string.IsNullOrEmpty(_password.Text))
        {
            MessageBox.Show(
                this,
                AppLocalization.T("请输入口令。", "Enter the password."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (confirmPassword && _password.Text.Length < MinimumExportPasswordLength)
        {
            MessageBox.Show(
                this,
                AppLocalization.T("便携备份口令至少需要 12 个字符。", "The portable backup password must contain at least 12 characters."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_confirmation is not null && !string.Equals(_password.Text, _confirmation.Text, StringComparison.Ordinal))
        {
            MessageBox.Show(
                this,
                AppLocalization.T("两次输入的口令不一致。", "The passwords do not match."),
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        DialogResult = DialogResult.OK;
    }
}
