using System.Runtime.ExceptionServices;
using GitKeyRouter.App;
using GitKeyRouter.App.Controls;
using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.App.Tests;

public sealed class WinFormsSmokeTests
{
    [Fact]
    public void DialogConstructors_HandleEmptyMissingAndMalformedModels()
        => StaTest.Run(() =>
        {
            var service = new GitServiceInstance
            {
                Id = "office",
                DisplayName = "Office",
                ProviderKind = GitProviderKind.GitLab,
                HostName = "git.example",
                SshUser = "git",
                WebBaseUrl = "https://git.example"
            };
            var identity = new GitIdentity
            {
                Id = "work",
                ServiceInstanceId = service.Id,
                DisplayName = "Work",
                AccountName = "camus",
                HostAlias = "git-work",
                PrivateKeyPath = @"C:\keys\work",
                PublicKeyPath = @"C:\keys\work.pub"
            };
            var profile = new GitProfile
            {
                Id = "profile-work",
                DisplayName = "Work",
                UserName = "Camus",
                UserEmail = "work@example.com",
                DefaultServiceInstanceId = "missing-service",
                DefaultIdentityId = "missing-identity"
            };
            var missingRoute = new RepositoryRoute
            {
                ServiceInstanceId = "missing-service",
                NamespacePath = "company/platform",
                IdentityId = "missing-identity"
            };

            Exercise(new GitServiceEditForm());
            Exercise(new GitServiceEditForm(new GitServiceInstance
            {
                DisplayName = "Invalid low port",
                ProviderKind = (GitProviderKind)999,
                HostName = "low.example",
                SshPort = -10,
                SshUser = "git",
                WebBaseUrl = "https://low.example"
            }));
            Exercise(new GitServiceEditForm(new GitServiceInstance
            {
                DisplayName = "Invalid high port",
                ProviderKind = GitProviderKind.Generic,
                HostName = "high.example",
                SshPort = 70000,
                SshUser = "git",
                WebBaseUrl = "https://high.example"
            }));
            Exercise(new GitProfileEditForm([], []));
            Exercise(new GitProfileEditForm([service], [identity], profile));
            Exercise(new GitProfileRuleEditForm(profile));
            Exercise(new GitProfileRuleEditForm(profile, new GitProfileRule
            {
                ProfileId = profile.Id,
                Kind = (GitProfileRuleKind)999,
                Pattern = "https://git.example/**"
            }));
            Exercise(new IdentityEditForm(Path.GetTempPath(), []));
            Exercise(new IdentityEditForm(Path.GetTempPath(), [service], new GitIdentity
            {
                ServiceInstanceId = "missing-service",
                DisplayName = "Missing",
                AccountName = "missing",
                HostAlias = "missing-alias"
            }));
            Exercise(new OwnerRouteEditForm([], [], missingRoute));
            Exercise(new OwnerRouteEditForm([service], [identity], new RepositoryRoute
            {
                ServiceInstanceId = service.Id,
                NamespacePath = "company/platform",
                IdentityId = identity.Id
            }));
            Exercise(new KeyFormatConversionForm("OpenSSH", @"C:\keys\work.pub"));
            Exercise(new KeyRenameForm("id_work", "id_work_renamed"));
            Exercise(new DiffPreviewForm("Diff", "--- before\n+++ after"));
            Exercise(new CommandResultForm("Result", "output"));
            Exercise(new TextViewForm("View", "content"));
            Exercise(new TextViewForm("Edit", "content", editable: true));
            Exercise(new PortableBackupPasswordForm(confirmPassword: false));
            Exercise(new PortableBackupPasswordForm(confirmPassword: true));
        });

    [Fact]
    public void PageConstructors_AndProfileSplitter_HandleExtremeSizes()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            Exercise(new OverviewControl(services, _ => { }, _ => Task.CompletedTask));
            Exercise(new GitServicesControl(services, _ => { }));
            Exercise(new IdentitiesControl(services, _ => { }));
            Exercise(new OwnerRoutesControl(services, _ => { }));
            Exercise(new SshConfigControl(services, _ => { }));
            Exercise(new GitRewritesControl(services, _ => { }));
            Exercise(new DiagnosticsControl(services, _ => { }));
            Exercise(new BackupControl(services, _ => { }));

            using var profiles = new GitProfilesControl(services, _ => { });
            _ = profiles.Handle;
            foreach (var size in new[]
                     {
                         new Size(80, 60),
                         new Size(1200, 720),
                         new Size(100, 80),
                         new Size(900, 500)
                     })
            {
                profiles.Size = size;
                profiles.PerformLayout();
                Application.DoEvents();
                var split = Assert.Single(profiles.Controls.OfType<SplitContainer>());
                var available = Math.Max(0, split.ClientSize.Height - split.SplitterWidth);
                if (available > 0)
                {
                    Assert.InRange(split.SplitterDistance, 0, available);
                }
            }

            Exercise(new MainForm(services), new Size(1024, 680), new Size(1280, 820));
        });

    [Fact]
    public void BackupPageExposesPortableEncryptedImportAndExport()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            using var page = new BackupControl(services, _ => { });
            _ = page.Handle;

            Assert.Single(
                Descendants<Button>(page),
                button => button.Name == "ExportPortableBackupButton");
            Assert.Single(
                Descendants<Button>(page),
                button => button.Name == "ImportPortableBackupButton");

            using var password = new PortableBackupPasswordForm(confirmPassword: true);
            _ = password.Handle;
            Assert.Single(
                Descendants<TextBox>(password),
                textBox => textBox.Name == "PortableBackupPasswordTextBox" && textBox.UseSystemPasswordChar);
            Assert.Single(
                Descendants<TextBox>(password),
                textBox => textBox.Name == "PortableBackupPasswordConfirmTextBox" && textBox.UseSystemPasswordChar);
        });

    [Fact]
    public void SharedPageLayout_KeepsLongContentAccessibleAtMinimumViewport()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            AppLocalization.SetLanguage(AppLanguage.English);
            var pages = new UserControl[]
            {
                new OverviewControl(services, _ => { }, _ => Task.CompletedTask),
                new GitServicesControl(services, _ => { }),
                new IdentitiesControl(services, _ => { }),
                new GitProfilesControl(services, _ => { }),
                new OwnerRoutesControl(services, _ => { }),
                new SshConfigControl(services, _ => { }),
                new GitRewritesControl(services, _ => { }),
                new DiagnosticsControl(services, _ => { }),
                new BackupControl(services, _ => { })
            };

            try
            {
                foreach (var page in pages)
                {
                    _ = page.Handle;
                    page.Size = new Size(720, 520);
                    page.PerformLayout();
                    Application.DoEvents();

                    var header = Assert.Single(page.Controls.Cast<Control>(), control => control.Name == "PageHeader");
                    Assert.True(header.Height >= 72);
                    Assert.InRange(header.Bottom, 1, page.ClientSize.Height);

                    var toolbar = page.Controls.Cast<Control>()
                        .SingleOrDefault(control => control.Name == "PageToolbar");
                    if (toolbar is not null)
                    {
                        Assert.True(toolbar.Height >= 52);
                        Assert.False(header.Bounds.IntersectsWith(toolbar.Bounds));
                        Assert.InRange(toolbar.Bottom, 1, page.ClientSize.Height);
                    }

                    foreach (var grid in Descendants<DataGridView>(page))
                    {
                        Assert.Equal(DataGridViewAutoSizeColumnsMode.None, grid.AutoSizeColumnsMode);
                        Assert.Equal(ScrollBars.Both, grid.ScrollBars);
                    }
                }

                using var narrowHeader = UiHelpers.CreatePageHeader(
                    "Repository Routes",
                    "Map SSH identities by service, owner, or repository; repository routes override owner routes, which override service routes.",
                    "Help text");
                narrowHeader.Size = new Size(360, 72);
                _ = narrowHeader.Handle;
                narrowHeader.PerformLayout();
                Application.DoEvents();
                var subtitle = Assert.Single(
                    Descendants<Label>(narrowHeader),
                    label => label.Name == "PageHeaderSubtitle");
                var help = Assert.Single(
                    Descendants<Button>(narrowHeader),
                    button => button.Name == "PageHelpButton");
                Assert.True(narrowHeader.Height > 72);
                var subtitleBounds = narrowHeader.RectangleToClient(
                    subtitle.RectangleToScreen(subtitle.ClientRectangle));
                var helpBounds = narrowHeader.RectangleToClient(
                    help.RectangleToScreen(help.ClientRectangle));
                Assert.True(narrowHeader.ClientRectangle.Contains(subtitleBounds));
                Assert.False(subtitleBounds.IntersectsWith(helpBounds));

                using var gridHost = new Form
                {
                    ClientSize = new Size(940, 260),
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    ShowInTaskbar = false
                };
                using var sampleGrid = UiHelpers.CreateGrid();
                sampleGrid.Location = new Point(12, 12);
                sampleGrid.Size = new Size(900, 220);
                gridHost.Controls.Add(sampleGrid);
                gridHost.Show();
                sampleGrid.DataSource = new[] { new { Name = "Example", Details = "Short value" } };
                sampleGrid.PerformLayout();
                Application.DoEvents();
                var roomyColumns = sampleGrid.Columns.Cast<DataGridViewColumn>()
                    .Where(column => column.Visible)
                    .ToList();
                Assert.Single(roomyColumns, column => column.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill);
                Assert.InRange(
                    sampleGrid.DisplayRectangle.Width - roomyColumns.Sum(column => column.Width),
                    -4,
                    24);

                gridHost.ClientSize = new Size(260, 260);
                sampleGrid.DataSource = new[] { new { Name = "Example", Details = new string('x', 1000) } };
                sampleGrid.PerformLayout();
                Application.DoEvents();
                var constrainedColumns = sampleGrid.Columns.Cast<DataGridViewColumn>()
                    .Where(column => column.Visible)
                    .ToList();
                Assert.All(
                    constrainedColumns,
                    column => Assert.Equal(DataGridViewAutoSizeColumnMode.None, column.AutoSizeMode));
                Assert.True(constrainedColumns.Sum(column => column.Width) > sampleGrid.DisplayRectangle.Width);

                using var main = new MainForm(services);
                _ = main.Handle;
                var contentPanel = Assert.Single(
                    Descendants<Panel>(main),
                    panel => panel.Name == "MainContentPanel");
                Assert.True(contentPanel.AutoScroll);
                Assert.True(contentPanel.AutoScrollMinSize.Width >= 720 + contentPanel.Padding.Horizontal);
                Assert.True(contentPanel.AutoScrollMinSize.Height >= 520 + contentPanel.Padding.Vertical);
            }
            finally
            {
                foreach (var page in pages)
                {
                    page.Dispose();
                }

                AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            }
        });

    [Fact]
    public void MainPagesExposeHelpAndLanguageSelector()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            AppLocalization.SetLanguage(AppLanguage.English);
            var pages = new UserControl[]
            {
                new OverviewControl(services, _ => { }, _ => Task.CompletedTask),
                new GitServicesControl(services, _ => { }),
                new IdentitiesControl(services, _ => { }),
                new GitProfilesControl(services, _ => { }),
                new OwnerRoutesControl(services, _ => { }),
                new SshConfigControl(services, _ => { }),
                new GitRewritesControl(services, _ => { }),
                new DiagnosticsControl(services, _ => { }),
                new BackupControl(services, _ => { })
            };

            try
            {
                foreach (var page in pages)
                {
                    _ = page.Handle;
                    Assert.Single(Descendants<Button>(page), button => button.Name == "PageHelpButton");
                }

                using var main = new MainForm(services);
                _ = main.Handle;
                var selector = Assert.Single(
                    Descendants<ComboBox>(main),
                    item => item.Name == "UiLanguageSelector");
                Assert.Equal(2, selector.Items.Count);
                Assert.Contains("     Git Services", Descendants<Button>(main).Select(button => button.Text));
            }
            finally
            {
                foreach (var page in pages)
                {
                    page.Dispose();
                }

                AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            }
        });

    [Fact]
    public void SshConfigUsesStyledViewSelectorInsteadOfNativeTabs()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            using var page = new SshConfigControl(services, _ => { });
            page.Size = new Size(1000, 700);
            _ = page.Handle;
            page.PerformLayout();
            Application.DoEvents();

            Assert.Empty(Descendants<TabControl>(page));
            var selector = Assert.Single(
                Descendants<FlowLayoutPanel>(page),
                panel => panel.Name == "SshConfigViewSelector");
            var host = Assert.Single(
                Descendants<Panel>(page),
                panel => panel.Name == "SshConfigViewHost");
            var buttons = selector.Controls.Cast<Button>().ToList();
            Assert.Equal(3, buttons.Count);

            foreach (var button in buttons)
            {
                var view = Assert.IsAssignableFrom<Control>(button.Tag);
                button.PerformClick();
                Application.DoEvents();

                Assert.Equal(UiHelpers.Accent, button.BackColor);
                Assert.Equal(Color.White, button.ForeColor);
                Assert.True(view.Visible);
                Assert.Equal(0, host.Controls.GetChildIndex(view));
                Assert.Single(buttons, item => item.BackColor == UiHelpers.Accent);
                Assert.Single(host.Controls.Cast<Control>(), item => item.Visible);
            }
        });

    [Fact]
    public void SidebarBrandTitleRemainsFullyVisibleAtMinimumWindowSize()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            try
            {
                foreach (var language in new[] { AppLanguage.SimplifiedChinese, AppLanguage.English })
                {
                    AppLocalization.SetLanguage(language);
                    using var main = new MainForm(services);
                    main.Size = main.MinimumSize;
                    _ = main.Handle;
                    main.PerformLayout();
                    Application.DoEvents();

                    var sidebar = Assert.Single(
                        Descendants<Panel>(main),
                        panel => panel.Name == "MainSidebar");
                    var title = Assert.Single(
                        Descendants<Label>(main),
                        label => label.Name == "SidebarBrandTitle");
                    var preferredWidth = TextRenderer.MeasureText(
                        title.Text,
                        title.Font,
                        Size.Empty,
                        TextFormatFlags.NoPadding).Width;

                    Assert.True(sidebar.Width >= 260);
                    Assert.True(
                        title.ClientSize.Width >= preferredWidth,
                        $"Brand title width {title.ClientSize.Width}px is smaller than preferred {preferredWidth}px.");
                    Assert.Equal("GitKeyRouter", title.Text);
                    Assert.False(title.AutoEllipsis);
                }
            }
            finally
            {
                AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            }
        });

    [Fact]
    public void MainWindow_UsesBrandedApplicationIcon()
        => StaTest.Run(() =>
        {
            var services = AppBootstrapper.CreateServices();
            using var main = new MainForm(services);

            Assert.NotNull(main.Icon);
            using var bitmap = main.Icon.ToBitmap();
            IconAssertions.AssertGitRouterMark(bitmap);
        });

    [Fact]
    public void SingleInstanceGuard_AllowsOnlyOneOwnerForTheSameName()
    {
        var mutexName = $@"Local\GitKeyRouter.Tests.{Guid.NewGuid():N}";
        using var first = SingleInstanceGuard.TryAcquire(mutexName);
        using var second = SingleInstanceGuard.TryAcquire(mutexName);

        Assert.True(first.IsPrimaryInstance);
        Assert.False(second.IsPrimaryInstance);

        first.Dispose();
        using var third = SingleInstanceGuard.TryAcquire(mutexName);
        Assert.True(third.IsPrimaryInstance);
    }

    private static void Exercise(Control control, params Size[] sizes)
    {
        using (control)
        {
            _ = control.Handle;
            foreach (var size in sizes.Length == 0 ? [new Size(800, 600)] : sizes)
            {
                control.Size = size;
                control.PerformLayout();
                Application.DoEvents();
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static class StaTest
    {
        public static void Run(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            })
            {
                IsBackground = true,
                Name = "GitKeyRouter WinForms smoke test"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The WinForms smoke test did not finish within 30 seconds.");
            }

            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }
}
