using System.Diagnostics;
using GitKeyRouter.App.Controls;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.App.Updates;

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
    private CheckBox? _checkForUpdatesOnStartupCheckbox;
    private Button? _checkForUpdatesButton;
    private string _activePageKey = PageKeys.Overview;
    private bool _updatingLanguage;
    private bool _updatingUpdatePreference;
    private bool _checkingForUpdates;
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
            await InitializeUpdatePreferenceAsync();
            ShowPreviousUpdateResult();
            var toolsReady = await RequiredToolInstallationUi.CheckAndOfferAsync(
                this,
                services,
                SetStatus,
                showHealthyMessage: false);
            if (toolsReady)
            {
                await ShowPageAsync(PageKeys.Overview);
            }

            if (_checkForUpdatesOnStartupCheckbox?.Checked == true)
            {
                _ = CheckForUpdatesAsync(showCurrentMessage: false);
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
            Height = 176,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18, 6, 18, 12),
            BackColor = UiHelpers.SidebarBackground
        };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
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
        var checkForUpdatesOnStartup = new CheckBox
        {
            Name = "CheckForUpdatesOnStartupCheckbox",
            Text = AppLocalization.T("启动时检查更新", "Check for updates on startup"),
            Dock = DockStyle.Fill,
            ForeColor = UiHelpers.SidebarMuted,
            Font = new Font("Segoe UI", 8F),
            Checked = true,
            AutoSize = false
        };
        _checkForUpdatesOnStartupCheckbox = checkForUpdatesOnStartup;
        checkForUpdatesOnStartup.CheckedChanged += async (_, _) =>
        {
            if (!_updatingUpdatePreference)
            {
                await PersistUpdatePreferenceAsync(checkForUpdatesOnStartup.Checked);
            }
        };
        var checkForUpdatesButton = new Button
        {
            Name = "CheckForUpdatesButton",
            Text = AppLocalization.T("检查更新", "Check for updates"),
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiHelpers.NavigationActive,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8.5F),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        checkForUpdatesButton.FlatAppearance.BorderSize = 0;
        checkForUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(showCurrentMessage: true);
        _checkForUpdatesButton = checkForUpdatesButton;
        footer.Controls.Add(footerHint, 0, 0);
        footer.Controls.Add(languageLabel, 0, 1);
        footer.Controls.Add(languageSelector, 0, 2);
        footer.Controls.Add(checkForUpdatesOnStartup, 0, 3);
        footer.Controls.Add(checkForUpdatesButton, 0, 4);

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

        if (_checkForUpdatesOnStartupCheckbox is not null)
        {
            _checkForUpdatesOnStartupCheckbox.Text = AppLocalization.T("启动时检查更新", "Check for updates on startup");
        }

        if (_checkForUpdatesButton is not null)
        {
            _checkForUpdatesButton.Text = AppLocalization.T("检查更新", "Check for updates");
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

    private async Task InitializeUpdatePreferenceAsync()
    {
        if (_checkForUpdatesOnStartupCheckbox is null)
        {
            return;
        }

        try
        {
            var snapshot = await _services.ConfigStore.LoadSnapshotAsync();
            _updatingUpdatePreference = true;
            _checkForUpdatesOnStartupCheckbox.Checked = snapshot.Config.CheckForUpdatesOnStartup;
        }
        catch (Exception exception)
        {
            _services.Logger.Error("Failed to load update-check preference.", exception);
            SetStatus(AppLocalization.T("无法读取更新检查偏好", "Could not load the update-check preference"));
        }
        finally
        {
            _updatingUpdatePreference = false;
        }
    }

    private async Task PersistUpdatePreferenceAsync(bool enabled)
    {
        try
        {
            var snapshot = await _services.ConfigStore.LoadSnapshotAsync();
            var config = snapshot.Config;
            config.CheckForUpdatesOnStartup = enabled;
            await _services.ConfigStore.SaveIfUnchangedAsync(config, snapshot.Version);
            SetStatus(enabled
                ? AppLocalization.T("已启用启动时检查更新", "Startup update checks enabled")
                : AppLocalization.T("已关闭启动时检查更新", "Startup update checks disabled"));
        }
        catch (Exception exception)
        {
            _services.Logger.Error("Failed to persist update-check preference.", exception);
            SetStatus(AppLocalization.T("更新检查偏好保存失败", "Could not save the update-check preference"));
        }
    }

    private async Task CheckForUpdatesAsync(bool showCurrentMessage)
    {
        if (_checkingForUpdates)
        {
            return;
        }

        _checkingForUpdates = true;
        if (_checkForUpdatesButton is not null)
        {
            _checkForUpdatesButton.Enabled = false;
        }

        try
        {
            SetStatus(AppLocalization.T("正在检查更新…", "Checking for updates…"));
            var release = await _services.UpdateChecker.GetLatestAsync();
            var currentVersion = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            if (!GitHubUpdateChecker.IsNewer(currentVersion, release))
            {
                SetStatus(AppLocalization.T("当前已是最新版本", "GitKeyRouter is up to date"));
                if (showCurrentMessage)
                {
                    MessageBox.Show(
                        this,
                        AppLocalization.T(
                            $"当前版本 {DisplayVersion} 已是最新稳定版本。",
                            $"GitKeyRouter {DisplayVersion} is the latest stable version."),
                        AppLocalization.T("检查更新", "Check for updates"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return;
            }

            await HandleAvailableUpdateAsync(release);
        }
        catch (Exception exception)
        {
            _services.Logger.Error("Update check failed.", exception);
            SetStatus(AppLocalization.T("检查更新失败", "Update check failed"));
            if (showCurrentMessage)
            {
                MessageBox.Show(
                    this,
                    AppLocalization.T(
                        $"无法检查更新。\r\n\r\n{exception.Message}",
                        $"Unable to check for updates.\r\n\r\n{exception.Message}"),
                    AppLocalization.T("检查更新", "Check for updates"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _checkingForUpdates = false;
            if (_checkForUpdatesButton is not null && !IsDisposed && !Disposing)
            {
                _checkForUpdatesButton.Enabled = true;
            }
        }
    }

    private async Task HandleAvailableUpdateAsync(UpdateReleaseInfo release)
    {
        var packageKind = UpdatePackageDetector.Detect();
        var installedPackage = packageKind is UpdatePackageKind.InstallerFrameworkDependent or UpdatePackageKind.InstallerSelfContained;
        var notes = BoundUpdateNotes(release.Notes);
        var packageLabel = UpdatePackageDetector.DisplayName(packageKind);

        if (installedPackage
            && release.HasVerifiedInstallerDownload(packageKind)
            && _services.UpdateInstallerLauncher.CanInstall(packageKind))
        {
            var confirm = MessageBox.Show(
                this,
                AppLocalization.T(
                    $"发现新版本 {release.TagName}。\r\n当前版本：{DisplayVersion}\r\n安装类型：{packageLabel}\r\n\r\n{notes}\r\n\r\n下载并安装经过 SHA-256 校验的 MSI 更新吗？",
                    $"GitKeyRouter {release.TagName} is available.\r\nCurrent version: {DisplayVersion}\r\nPackage: {packageLabel}\r\n\r\n{notes}\r\n\r\nDownload and install the SHA-256 verified MSI update now?"),
                AppLocalization.T("发现更新", "Update available"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (confirm != DialogResult.Yes)
            {
                SetStatus(AppLocalization.T($"发现更新 {release.TagName}", $"Update {release.TagName} is available"));
                return;
            }

            SetStatus(AppLocalization.T("正在下载并校验安装包…", "Downloading and verifying the installer…"));
            var package = await _services.UpdateDownloadService.DownloadVerifiedInstallerAsync(release, packageKind);
            if (!_services.UpdateInstallerLauncher.Launch(package))
            {
                throw new InvalidOperationException("The verified update installer could not be launched safely.");
            }

            SetStatus(AppLocalization.T("安装程序已就绪，正在退出以完成更新…", "Installer ready; closing GitKeyRouter to finish the update…"));
            Close();
            return;
        }

        var target = installedPackage
            ? release.ReleasePage
            : release.PreferredDownload(packageKind) ?? release.ReleasePage;
        var prompt = MessageBox.Show(
            this,
            AppLocalization.T(
                $"发现新版本 {release.TagName}。\r\n当前版本：{DisplayVersion}\r\n安装类型：{packageLabel}\r\n\r\n{notes}\r\n\r\n当前安装类型不执行应用内覆盖更新。是否打开经过验证的 GitHub 下载/发布页面？",
                $"GitKeyRouter {release.TagName} is available.\r\nCurrent version: {DisplayVersion}\r\nPackage: {packageLabel}\r\n\r\n{notes}\r\n\r\nThis package type is not overwritten in-app. Open the verified GitHub download/release page?"),
            AppLocalization.T("发现更新", "Update available"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (prompt == DialogResult.Yes)
        {
            OpenExternalUri(target);
        }

        SetStatus(AppLocalization.T($"发现更新 {release.TagName}", $"Update {release.TagName} is available"));
    }

    private void ShowPreviousUpdateResult()
    {
        var result = _services.UpdateInstallerLauncher.TryConsumeResult();
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            SetStatus(result.RestartRequired
                ? AppLocalization.T("更新已安装；Windows Installer 建议重新启动系统", "Update installed; Windows Installer requested a system restart")
                : AppLocalization.T("GitKeyRouter 已成功更新", "GitKeyRouter was updated successfully"));
            return;
        }

        _services.Logger.Error($"Previous GitKeyRouter update failed: {result.Message}");
        MessageBox.Show(
            this,
            AppLocalization.T(
                $"上一次更新未完成。\r\n\r\n{result.Message}",
                $"The previous update did not complete.\r\n\r\n{result.Message}"),
            AppLocalization.T("更新失败", "Update failed"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static string BoundUpdateNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return AppLocalization.T("发布说明未提供。", "No release notes were provided.");
        }

        var normalized = notes.Trim();
        const int maximum = 2400;
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private static void OpenExternalUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var repositoryPrefix = $"/{UpdateProjectLinks.GitHubOwner}/{UpdateProjectLinks.GitHubRepository}/releases/";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(repositoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The update link is not a canonical GitKeyRouter GitHub release URL.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
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
