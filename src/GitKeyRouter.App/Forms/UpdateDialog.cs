using GitKeyRouter.App.Presentation;
using GitKeyRouter.App.Updates;

namespace GitKeyRouter.App.Forms;

public sealed class UpdateDialog : Form
{
    public Uri? SelectedUri { get; private set; }

    public bool InstallRequested { get; private set; }

    public UpdateDialog(
        Version currentVersion,
        UpdateReleaseInfo release,
        UpdatePackageKind packageKind,
        bool allowVerifiedInstaller)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(release);

        Text = AppLocalization.T("发现 GitKeyRouter 新版本", "GitKeyRouter update available");
        Icon = AppIcon.LoadWindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 520);
        MinimumSize = new Size(620, 440);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = UiHelpers.AppBackground;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 112,
            BackColor = UiHelpers.Surface,
            Padding = new Padding(22, 17, 22, 14)
        };
        var mark = new Label
        {
            Text = "↻",
            Location = new Point(20, 20),
            Size = new Size(52, 52),
            BackColor = UiHelpers.Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 22F),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var title = new Label
        {
            Text = AppLocalization.T(
                $"GitKeyRouter {release.TagName} 可用",
                $"GitKeyRouter {release.TagName} is available"),
            Font = new Font("Segoe UI Semibold", 12F),
            ForeColor = UiHelpers.TextPrimary,
            Location = new Point(88, 18),
            AutoSize = true
        };
        var summary = new Label
        {
            Text = AppLocalization.T(
                $"当前版本 {ShortVersion(currentVersion)}  →  最新版本 {ShortVersion(release.Version)}",
                $"Current {ShortVersion(currentVersion)}  →  Latest {ShortVersion(release.Version)}"),
            ForeColor = UiHelpers.TextSecondary,
            Location = new Point(88, 49),
            AutoSize = true
        };
        var privacy = new Label
        {
            Text = allowVerifiedInstaller
                ? AppLocalization.T(
                    "安装版会先下载并校验 SHA-256；安装仍由你确认，Windows 可能请求管理员权限。",
                    "Installer updates are SHA-256 verified first; you still confirm installation and Windows may request elevation.")
                : AppLocalization.T(
                    "更新检查只读取项目 Pages/GitHub 的公开发布信息；便携版不会在应用内覆盖自身。",
                    "Update checks only read public Pages/GitHub release data; portable builds are never overwritten in-app."),
            ForeColor = UiHelpers.TextSecondary,
            Location = new Point(88, 75),
            AutoSize = true
        };
        header.Controls.AddRange([mark, title, summary, privacy]);

        var notesLabel = new Label
        {
            Text = AppLocalization.T("版本说明", "Release notes"),
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(18, 12, 0, 0),
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = UiHelpers.TextPrimary
        };
        var notes = new TextBox
        {
            Name = "UpdateNotesTextBox",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = BoundNotes(release.Notes),
            BackColor = Color.White,
            ForeColor = UiHelpers.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5F)
        };
        var notesHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 0, 18, 12),
            BackColor = UiHelpers.AppBackground
        };
        notesHost.Controls.Add(notes);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 12, 12, 8),
            BackColor = UiHelpers.Surface
        };
        var primary = new Button
        {
            Name = "UpdatePrimaryActionButton",
            Text = allowVerifiedInstaller
                ? AppLocalization.T("下载并安装", "Download and install")
                : release.PreferredDownload(packageKind) is null
                    ? AppLocalization.T("打开发布页面", "Open release page")
                    : AppLocalization.T("打开匹配下载", "Open matching download"),
            Width = 132,
            Height = 34,
            AccessibleDescription = UpdatePackageDetector.DisplayName(packageKind)
        };
        primary.Click += (_, _) =>
        {
            if (allowVerifiedInstaller)
            {
                InstallRequested = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            SelectedUri = release.PreferredDownload(packageKind) ?? release.ReleasePage;
            DialogResult = DialogResult.OK;
            Close();
        };
        var openRelease = new Button
        {
            Name = "UpdateReleaseButton",
            Text = AppLocalization.T("查看 Release", "View Release"),
            Width = 112,
            Height = 34
        };
        openRelease.Click += (_, _) =>
        {
            SelectedUri = release.ReleasePage;
            DialogResult = DialogResult.OK;
            Close();
        };
        var later = new Button
        {
            Name = "UpdateLaterButton",
            Text = AppLocalization.T("稍后提醒", "Remind me later"),
            DialogResult = DialogResult.Cancel,
            Width = 104,
            Height = 34
        };
        var packageHint = new Label
        {
            Text = AppLocalization.T(
                $"匹配当前安装：{UpdatePackageDetector.DisplayName(packageKind)}",
                $"Matching package: {UpdatePackageDetector.DisplayName(packageKind)}"),
            AutoSize = true,
            ForeColor = UiHelpers.TextSecondary,
            Margin = new Padding(8, 9, 10, 0)
        };
        buttons.Controls.AddRange([primary, openRelease, later, packageHint]);

        Controls.Add(notesHost);
        Controls.Add(notesLabel);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = primary;
        CancelButton = later;
    }

    private static string BoundNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return AppLocalization.T("发布说明未提供。", "No release notes were provided.");
        }

        var normalized = notes.Trim();
        const int maximum = 20_000;
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string ShortVersion(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
