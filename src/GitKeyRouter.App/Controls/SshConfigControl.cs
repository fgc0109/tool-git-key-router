using System.Text;
using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.App.Controls;

public sealed class SshConfigControl : UserControl, IAsyncRefreshable
{
    private readonly ApplicationServices _services;
    private readonly Action<string> _status;
    private readonly DataGridView _managedGrid = UiHelpers.CreateGrid();
    private readonly TextBox _parsedText = CreateTextBox();
    private readonly TextBox _rawText = CreateTextBox();
    private readonly Panel _viewHost = new()
    {
        Name = "SshConfigViewHost",
        Dock = DockStyle.Fill,
        BackColor = UiHelpers.Surface,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly List<(Button Button, Control View)> _views = [];
    private string _raw = string.Empty;
    private IReadOnlyList<SshManagedBlock> _blocks = [];

    public SshConfigControl(ApplicationServices services, Action<string> status)
    {
        _services = services;
        _status = status;

        var header = UiHelpers.CreatePageHeader(
            "SSH Config",
            AppLocalization.T("查看、同步和恢复 GitKeyRouter 管理的 Host 配置", "View, synchronize, and restore Host entries managed by GitKeyRouter"),
            AppLocalization.T(
                "此页面管理 ~/.ssh/config 中由 GitKeyRouter 标记的 Host 区块。\r\n\r\n• 优先使用“同步全部身份”，不要手工复制区块。\r\n• 编辑原始文本前会显示内容，写入操作会创建备份。\r\n• 不要修改受管理区块的起止标记。\r\n• “解析结果”可用于检查重复 Host 或异常结构。",
                "This page manages the Host blocks marked as GitKeyRouter-managed in ~/.ssh/config.\r\n\r\n• Prefer Synchronize all identities instead of copying blocks manually.\r\n• Raw-text changes are reviewed and backed up before writing.\r\n• Do not modify the start and end markers of managed blocks.\r\n• Use Parsed result to find duplicate Hosts or malformed structure."));
        var toolbar = UiHelpers.CreateToolbar();
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("同步全部身份", "Synchronize all identities"), async (_, _) => await SynchronizeAllAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("严格同步受管区块", "Strictly synchronize managed blocks"), async (_, _) => await SynchronizeAllStrictAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("删除选中区块", "Delete selected block"), async (_, _) => await DeleteSelectedAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("编辑原始文本", "Edit raw text"), async (_, _) => await EditRawAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("恢复最近备份", "Restore latest backup"), async (_, _) => await RestoreLatestAsync()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("打开文件", "Open file"), (_, _) => OpenConfig()));
        toolbar.Controls.Add(UiHelpers.Button(AppLocalization.T("刷新", "Refresh"), async (_, _) => await RefreshAsync()));

        _managedGrid.Name = "SshManagedHostsView";
        _parsedText.Name = "SshParsedResultView";
        _rawText.Name = "SshRawTextView";

        var selector = new FlowLayoutPanel
        {
            Name = "SshConfigViewSelector",
            Dock = DockStyle.Top,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 8),
            Margin = Padding.Empty,
            BackColor = UiHelpers.AppBackground
        };
        var managedButton = AddView(
            selector,
            "SshManagedHostsViewButton",
            AppLocalization.T("受管理 Host", "Managed Hosts"),
            _managedGrid);
        AddView(
            selector,
            "SshParsedResultViewButton",
            AppLocalization.T("解析结果", "Parsed result"),
            _parsedText);
        AddView(
            selector,
            "SshRawTextViewButton",
            AppLocalization.T("原始文本", "Raw text"),
            _rawText);
        ShowView(managedButton);

        Controls.Add(_viewHost);
        Controls.Add(selector);
        Controls.Add(toolbar);
        Controls.Add(header);
        UiHelpers.EnableStatusColors(_managedGrid, nameof(ManagedRow.状态));
    }

    public async Task RefreshAsync()
    {
        _raw = await _services.SshConfigService.ReadRawAsync();
        _blocks = _services.SshConfigService.ParseManagedBlocks(_raw);
        _managedGrid.DataSource = _blocks.Select(block => new ManagedRow
        {
            HostAlias = block.HostAlias,
            起始位置 = block.StartIndex,
            长度 = block.Length,
            状态 = _blocks.Count(item => string.Equals(item.HostAlias, block.HostAlias, StringComparison.OrdinalIgnoreCase)) == 1 ? "正常" : "重复"
        }).ToList();
        _rawText.Text = string.IsNullOrEmpty(_raw) ? "<SSH config 文件不存在或为空>" : _raw;

        var parsed = new StringBuilder();
        parsed.AppendLine($"路径：{_services.Paths.SshConfigPath}");
        parsed.AppendLine($"受管理区块：{_blocks.Count}");
        var hostLines = _raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parsed.AppendLine($"全部 Host 声明：{hostLines.Count}");
        foreach (var hostLine in hostLines)
        {
            parsed.AppendLine($"  {hostLine}");
        }
        parsed.AppendLine();
        foreach (var block in _blocks)
        {
            parsed.AppendLine($"HostAlias: {block.HostAlias}");
            parsed.AppendLine(block.RawText);
            parsed.AppendLine(new string('-', 60));
        }

        _parsedText.Text = parsed.ToString();
        _status($"SSH Config 已读取，受管理区块 {_blocks.Count} 个");
    }

    private async Task SynchronizeAllAsync()
    {
        var config = await _services.ConfigStore.LoadAsync();
        var preview = _services.SshConfigService.PreviewSynchronizeAll(_raw, config);
        using var diff = new DiffPreviewForm("同步全部 SSH managed block", preview.DiffText);
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshConfigService.ApplyAsync(preview, "Synchronize all SSH managed blocks");
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task SynchronizeAllStrictAsync()
    {
        var config = await _services.ConfigStore.LoadAsync();
        var orphanAliases = _services.SshConfigService.FindOrphanManagedBlockAliases(_raw, config.Identities);
        var orphanText = orphanAliases.Count == 0
            ? "没有发现孤儿受管区块。"
            : "将删除以下孤儿受管区块：\r\n\r\n" + string.Join("\r\n", orphanAliases.Select(alias => $"• {alias}"));
        var confirmation = MessageBox.Show(
            this,
            "严格同步会更新全部已配置身份，并删除不再对应身份的完整 GitKeyRouter managed block。\r\n"
            + "普通 Host、注释和不完整标记内容不会被删除。\r\n\r\n"
            + orphanText
            + "\r\n\r\n是否继续生成并检查 diff？",
            "严格同步 SSH managed blocks",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        var preview = _services.SshConfigService.PreviewSynchronizeAllStrict(_raw, config);
        using var diff = new DiffPreviewForm(
            "严格同步 SSH managed blocks",
            preview.DiffText,
            "确认严格同步");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshConfigService.ApplyAsync(
            preview,
            "Strictly synchronize SSH managed blocks");
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_managedGrid.CurrentRow?.DataBoundItem is not ManagedRow row)
        {
            MessageBox.Show(this, "请先选择一个受管理区块。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preview = _services.SshConfigService.PreviewDelete(_raw, row.HostAlias);
        using var diff = new DiffPreviewForm($"删除 SSH managed block: {row.HostAlias}", preview.DiffText);
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshConfigService.ApplyAsync(preview, $"Delete SSH managed block: {row.HostAlias}");
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task EditRawAsync()
    {
        var warning = MessageBox.Show(
            this,
            "原始文本编辑会替换完整 SSH config 文件。保存前仍会显示实际 diff 并创建备份。\r\n\r\n普通身份修改建议使用 managed block 操作。是否继续？",
            "编辑 SSH Config 原始文本",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (warning != DialogResult.Yes)
        {
            return;
        }

        using var editor = new TextViewForm("编辑 SSH Config 原始文本", _raw, editable: true);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var updated = editor.ContentTextBox.Text;
        var preview = _services.SshConfigService.CreatePreview(
            "Replace SSH config raw text",
            _raw,
            updated);
        using var diff = new DiffPreviewForm("SSH Config 完整文件 diff", preview.DiffText, "确认保存完整文件");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.SshConfigService.ApplyAsync(preview, "Replace SSH config raw text");
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private async Task RestoreLatestAsync()
    {
        var backup = (await _services.BackupService.ListAsync()).FirstOrDefault();
        if (backup is null)
        {
            MessageBox.Show(this, "没有可用备份。", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var snapshot = await _services.BackupService.ReadAsync(backup.BackupDirectory);
        var target = snapshot.Manifest.SshConfigExisted ? snapshot.SshConfigText ?? string.Empty : string.Empty;
        var diffText = TextDiffService.CreateSimpleDiff(_raw, target, "ssh_config.current", "ssh_config.backup");
        using var diff = new DiffPreviewForm($"恢复 SSH Config - {backup.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}", diffText, "恢复备份");
        if (diff.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = await _services.BackupService.RestoreSshConfigAsync(backup.BackupDirectory);
        if (!result.Success)
        {
            UiHelpers.ShowErrors(this, result);
            return;
        }

        await RefreshAsync();
    }

    private void OpenConfig()
    {
        Directory.CreateDirectory(_services.Paths.SshDirectory);
        if (!_services.FileSystem.FileExists(_services.Paths.SshConfigPath))
        {
            MessageBox.Show(this, $"SSH Config 尚不存在：\r\n{_services.Paths.SshConfigPath}", "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _services.Paths.SshConfigPath,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    private Button AddView(FlowLayoutPanel selector, string name, string text, Control view)
    {
        view.Dock = DockStyle.Fill;
        view.Visible = false;
        _viewHost.Controls.Add(view);

        var button = new Button
        {
            Name = name,
            Text = text,
            Tag = view,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(132, 36),
            Height = 36,
            Padding = new Padding(16, 0, 16, 0),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiHelpers.Surface,
            ForeColor = UiHelpers.TextSecondary,
            Font = new Font("Segoe UI Semibold", 9F),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AccessibleRole = AccessibleRole.PageTab
        };
        button.FlatAppearance.BorderColor = UiHelpers.Border;
        button.FlatAppearance.MouseOverBackColor = UiHelpers.AccentSoft;
        button.FlatAppearance.MouseDownBackColor = UiHelpers.AccentSoft;
        button.Click += (_, _) => ShowView(button);
        selector.Controls.Add(button);
        _views.Add((button, view));
        return button;
    }

    private void ShowView(Button selectedButton)
    {
        foreach (var (button, view) in _views)
        {
            var selected = ReferenceEquals(button, selectedButton);
            button.BackColor = selected ? UiHelpers.Accent : UiHelpers.Surface;
            button.ForeColor = selected ? Color.White : UiHelpers.TextSecondary;
            button.FlatAppearance.BorderColor = selected ? UiHelpers.Accent : UiHelpers.Border;
            view.Visible = selected;
            if (selected)
            {
                view.BringToFront();
                button.Select();
            }
        }
    }

    private static TextBox CreateTextBox()
        => new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            BackColor = UiHelpers.OutputBackground,
            ForeColor = UiHelpers.TextPrimary,
            BorderStyle = BorderStyle.None
        };

    private sealed class ManagedRow
    {
        public string HostAlias { get; init; } = string.Empty;
        public int 起始位置 { get; init; }
        public int 长度 { get; init; }
        public string 状态 { get; init; } = string.Empty;
    }
}
