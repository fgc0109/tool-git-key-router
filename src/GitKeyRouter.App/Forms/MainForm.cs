using GitKeyRouter.App.Controls;
using GitKeyRouter.App.Presentation;

namespace GitKeyRouter.App.Forms;

public sealed class MainForm : Form
{
    private static readonly Size MinimumPageSize = new(720, 520);
    private readonly ApplicationServices _services;
    private readonly Panel _contentPanel = new()
    {
        Name = "MainContentPanel",
        Dock = DockStyle.Fill,
        AutoScroll = true,
        AutoScrollMinSize = new Size(MinimumPageSize.Width + 48, MinimumPageSize.Height + 38),
        Padding = new Padding(24, 20, 24, 18),
        BackColor = UiHelpers.AppBackground
    };
    private readonly ToolStripStatusLabel _statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Dictionary<string, PageDefinition> _pageDefinitions;
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.OrdinalIgnoreCase);
    private Label? _brandSubtitle;
    private Label? _navigationLabel;
    private Label? _footerHint;
    private Label? _languageLabel;
    private ComboBox? _languageSelector;
    private string _activePageKey = PageKeys.Overview;
    private bool _updatingLanguage;
    private static string DisplayVersion => typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "Unknown";

    public MainForm(ApplicationServices services)
    {
        _services = services;
        Icon = AppIcon.LoadWindowIcon();
        Text = $"GitKeyRouter {DisplayVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1024, 680);
        Font = new Font("Segoe UI", 9F);
        BackColor = UiHelpers.AppBackground;

        _pageDefinitions = new Dictionary<string, PageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [PageKeys.Overview] = new(() => new OverviewControl(services, SetStatus, ShowPageAsync), () => AppLocalization.T("概览", "Overview")),
            [PageKeys.GitServices] = new(() => new GitServicesControl(services, SetStatus), () => AppLocalization.T("Git 服务", "Git Services")),
            [PageKeys.Identities] = new(() => new IdentitiesControl(services, SetStatus), () => AppLocalization.T("Git 身份", "Git Identities")),
            [PageKeys.GitProfiles] = new(() => new GitProfilesControl(services, SetStatus), () => "Git Profiles"),
            [PageKeys.RepositoryRoutes] = new(() => new OwnerRoutesControl(services, SetStatus), () => AppLocalization.T("仓库路由", "Repository Routes")),
            [PageKeys.SshConfig] = new(() => new SshConfigControl(services, SetStatus), () => "SSH Config"),
            [PageKeys.GitRewrites] = new(() => new GitRewritesControl(services, SetStatus), () => AppLocalization.T("Git 重写配置", "Git URL Rewrites")),
            [PageKeys.Diagnostics] = new(() => new DiagnosticsControl(services, SetStatus), () => AppLocalization.T("诊断", "Diagnostics")),
            [PageKeys.Backup] = new(() => new BackupControl(services, SetStatus), () => AppLocalization.T("备份与恢复", "Backup and Restore"))
        };

        var sidebar = CreateSidebar();
        var divider = new Panel
        {
            Dock = DockStyle.Left,
            Width = 1,
            BackColor = UiHelpers.Border
        };
        var statusStrip = new StatusStrip
        {
            BackColor = UiHelpers.Surface,
            ForeColor = UiHelpers.TextSecondary,
            SizingGrip = false,
            Padding = new Padding(8, 3, 8, 3)
        };
        statusStrip.Items.Add(_statusLabel);
        _statusLabel.Text = AppLocalization.T("就绪", "Ready");

        Controls.Add(_contentPanel);
        Controls.Add(divider);
        Controls.Add(sidebar);
        Controls.Add(statusStrip);
        Shown += async (_, _) =>
        {
            await ShowPageAsync(PageKeys.Overview);
            var toolsReady = await RequiredToolInstallationUi.CheckAndOfferAsync(
                this,
                services,
                SetStatus,
                showHealthyMessage: false);
            if (toolsReady)
            {
                await ShowPageAsync(PageKeys.Overview);
            }
        };
    }

    private Panel CreateSidebar()
    {
        var sidebar = new Panel
        {
            Name = "MainSidebar",
            Dock = DockStyle.Left,
            Width = 260,
            BackColor = UiHelpers.SidebarBackground,
            Padding = Padding.Empty
        };

        var brand = new Panel
        {
            Name = "SidebarBrand",
            Dock = DockStyle.Top,
            Height = 94,
            Padding = new Padding(16, 18, 12, 14),
            Cursor = Cursors.Hand
        };
        var mark = new Label
        {
            Text = "G",
            Dock = DockStyle.Left,
            Width = 44,
            BackColor = UiHelpers.Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 17F),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        var brandText = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 1, 0, 0),
            Cursor = Cursors.Hand
        };
        var title = new Label
        {
            Name = "SidebarBrandTitle",
            Text = "GitKeyRouter",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            AutoEllipsis = false,
            UseMnemonic = false
        };
        var subtitle = new Label
        {
            Name = "SidebarBrandSubtitle",
            Text = AppLocalization.T("SSH 身份与路由管理", "SSH identity and routing manager"),
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = UiHelpers.SidebarMuted,
            TextAlign = ContentAlignment.TopLeft,
            Cursor = Cursors.Hand
        };
        _brandSubtitle = subtitle;
        brandText.Controls.Add(subtitle);
        brandText.Controls.Add(title);
        brand.Controls.Add(brandText);
        brand.Controls.Add(mark);
        foreach (var control in new Control[] { brand, mark, brandText, title, subtitle })
        {
            control.Click += async (_, _) => await ShowPageAsync(PageKeys.Overview);
        }

        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = UiHelpers.SidebarBackground,
            Padding = new Padding(12, 8, 12, 8)
        };
        var navigationLabel = new Label
        {
            Text = AppLocalization.T("导航", "NAVIGATION"),
            Width = 204,
            Height = 28,
            ForeColor = UiHelpers.SidebarMuted,
            Font = new Font("Segoe UI Semibold", 8F),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            Margin = new Padding(0, 0, 0, 6)
        };
        _navigationLabel = navigationLabel;
        navigation.Controls.Add(navigationLabel);

        foreach (var pageKey in _pageDefinitions.Keys)
        {
            var button = CreateNavigationButton(pageKey);
            _navigationButtons[pageKey] = button;
            navigation.Controls.Add(button);
        }

        navigation.SizeChanged += (_, _) => ResizeNavigationItems(navigation, navigationLabel);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 112,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18, 6, 18, 12),
            BackColor = UiHelpers.SidebarBackground
        };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var footerHint = new Label
        {
            Text = AppLocalization.T("点击左上角品牌可随时返回概览", "Click the brand to return to Overview"),
            Dock = DockStyle.Fill,
            ForeColor = UiHelpers.SidebarMuted,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _footerHint = footerHint;
        var languageLabel = new Label
        {
            Text = AppLocalization.T("界面语言", "Interface language"),
            Dock = DockStyle.Fill,
            ForeColor = UiHelpers.SidebarMuted,
            Font = new Font("Segoe UI Semibold", 8F),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _languageLabel = languageLabel;
        var languageSelector = new ComboBox
        {
            Name = "UiLanguageSelector",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _languageSelector = languageSelector;
        languageSelector.Items.AddRange(
        [
            new LanguageChoice(AppLanguage.SimplifiedChinese),
            new LanguageChoice(AppLanguage.English)
        ]);
        languageSelector.SelectedItem = languageSelector.Items.Cast<LanguageChoice>()
            .First(item => item.Language == AppLocalization.CurrentLanguage);
        languageSelector.SelectedIndexChanged += async (_, _) =>
        {
            if (!_updatingLanguage && languageSelector.SelectedItem is LanguageChoice choice)
            {
                await ChangeLanguageAsync(choice.Language);
            }
        };
        footer.Controls.Add(footerHint, 0, 0);
        footer.Controls.Add(languageLabel, 0, 1);
        footer.Controls.Add(languageSelector, 0, 2);

        sidebar.Controls.Add(navigation);
        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(brand);
        return sidebar;
    }

    private Button CreateNavigationButton(string pageKey)
    {
        var button = new Button
        {
            Text = NavigationText(pageKey),
            Width = 204,
            Height = 44,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = UiHelpers.SidebarBackground,
            ForeColor = Color.FromArgb(224, 230, 240),
            Font = new Font("Segoe UI Semibold", 9.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 8, 0),
            Margin = new Padding(0, 0, 0, 5),
            Cursor = Cursors.Hand,
            Tag = pageKey,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(35, 48, 72);
        button.FlatAppearance.MouseDownBackColor = UiHelpers.NavigationActive;
        button.Click += async (_, _) => await ShowPageAsync((string)button.Tag);
        return button;
    }

    private void ResizeNavigationItems(FlowLayoutPanel navigation, Label navigationLabel)
    {
        var width = Math.Max(140, navigation.ClientSize.Width - navigation.Padding.Horizontal);
        navigationLabel.Width = width;
        foreach (var button in _navigationButtons.Values)
        {
            button.Width = width;
        }
    }

    private async Task ShowPageAsync(string pageKey)
    {
        if (!_pageDefinitions.TryGetValue(pageKey, out var definition))
        {
            return;
        }

        _activePageKey = pageKey;
        var pageName = definition.Title();
        if (!_pages.TryGetValue(pageKey, out var page))
        {
            try
            {
                page = definition.Factory();
                _pages[pageKey] = page;
            }
            catch (Exception exception)
            {
                _services.Logger.Error($"Failed to construct page '{pageName}'.", exception);
                SetStatus(AppLocalization.T($"页面初始化失败：{pageName}", $"Failed to initialize page: {pageName}"));
                MessageBox.Show(
                    this,
                    AppLocalization.T(
                        $"页面“{pageName}”初始化失败。其他页面仍可继续使用。\r\n\r\n{exception}",
                        $"The page '{pageName}' could not be initialized. Other pages remain available.\r\n\r\n{exception}"),
                    "GitKeyRouter",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }

        UpdateNavigationState(pageKey);
        _contentPanel.AutoScrollPosition = Point.Empty;
        _contentPanel.SuspendLayout();
        _contentPanel.Controls.Clear();
        page.MinimumSize = MinimumPageSize;
        page.Dock = DockStyle.Fill;
        page.BackColor = UiHelpers.AppBackground;
        _contentPanel.Controls.Add(page);
        _contentPanel.ResumeLayout();
        Text = $"GitKeyRouter {DisplayVersion} - {pageName}";
        SetStatus(AppLocalization.T($"正在刷新：{pageName}", $"Refreshing: {pageName}"));
        try
        {
            if (page is IAsyncRefreshable refreshable)
            {
                await refreshable.RefreshAsync();
            }

            SetStatus(AppLocalization.T($"已显示：{pageName}", $"Showing: {pageName}"));
        }
        catch (Exception exception)
        {
            SetStatus(AppLocalization.T($"刷新失败：{pageName}", $"Refresh failed: {pageName}"));
            MessageBox.Show(this, exception.ToString(), "GitKeyRouter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateNavigationState(string activePageKey)
    {
        foreach (var (pageKey, button) in _navigationButtons)
        {
            var active = string.Equals(pageKey, activePageKey, StringComparison.OrdinalIgnoreCase);
            button.BackColor = active ? UiHelpers.NavigationActive : UiHelpers.SidebarBackground;
            button.ForeColor = active ? Color.White : Color.FromArgb(224, 230, 240);
        }
    }

    private string NavigationText(string pageKey)
    {
        var title = _pageDefinitions[pageKey].Title();
        return pageKey == PageKeys.Overview ? $"⌂   {title}" : $"     {title}";
    }

    private void ApplyShellLanguage()
    {
        if (_brandSubtitle is not null)
        {
            _brandSubtitle.Text = AppLocalization.T("SSH 身份与路由管理", "SSH identity and routing manager");
        }

        if (_navigationLabel is not null)
        {
            _navigationLabel.Text = AppLocalization.T("导航", "NAVIGATION");
        }

        if (_footerHint is not null)
        {
            _footerHint.Text = AppLocalization.T("点击左上角品牌可随时返回概览", "Click the brand to return to Overview");
        }

        if (_languageLabel is not null)
        {
            _languageLabel.Text = AppLocalization.T("界面语言", "Interface language");
        }

        foreach (var (pageKey, button) in _navigationButtons)
        {
            button.Text = NavigationText(pageKey);
        }

        if (_languageSelector is not null)
        {
            _updatingLanguage = true;
            try
            {
                _languageSelector.SelectedItem = _languageSelector.Items.Cast<LanguageChoice>()
                    .First(item => item.Language == AppLocalization.CurrentLanguage);
            }
            finally
            {
                _updatingLanguage = false;
            }
        }
    }

    private async Task ChangeLanguageAsync(AppLanguage language)
    {
        if (language == AppLocalization.CurrentLanguage)
        {
            return;
        }

        AppLocalization.SetLanguage(language);
        ApplyShellLanguage();
        foreach (var page in _pages.Values.Distinct())
        {
            page.Dispose();
        }
        _pages.Clear();

        try
        {
            var snapshot = await _services.ConfigStore.LoadSnapshotAsync();
            var config = snapshot.Config;
            config.UiLanguage = AppLocalization.CurrentCode;
            await _services.ConfigStore.SaveIfUnchangedAsync(config, snapshot.Version);
        }
        catch (Exception exception)
        {
            _services.Logger.Error("Failed to persist UI language preference.", exception);
            SetStatus(AppLocalization.T("语言已切换，但保存偏好失败", "Language changed, but the preference could not be saved"));
        }

        await ShowPageAsync(_activePageKey);
    }

    private void SetStatus(string text)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() => SetStatus(text)));
            }
            catch (ObjectDisposedException)
            {
                // The form can close while a background operation is reporting status.
            }
            catch (InvalidOperationException)
            {
                // The form can close while a background operation is reporting status.
            }
            return;
        }

        _statusLabel.Text = text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var page in _pages.Values.Distinct())
            {
                page.Dispose();
            }

            _pages.Clear();
        }

        base.Dispose(disposing);
    }

    private sealed record PageDefinition(Func<UserControl> Factory, Func<string> Title);

    private sealed record LanguageChoice(AppLanguage Language)
    {
        public override string ToString() => AppLocalization.DisplayName(Language);
    }
}
