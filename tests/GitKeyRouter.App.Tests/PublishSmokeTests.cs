using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace GitKeyRouter.App.Tests;

public sealed class PublishSmokeTests
{
    [Fact]
    public void PublishProfiles_UseDistinctOutputDirectoriesAndRuntimeModes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var profileDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "GitKeyRouter.App",
            "Properties",
            "PublishProfiles");
        var selfContained = LoadProfile(
            Path.Combine(profileDirectory, "win-x64-single-file.pubxml"));
        var frameworkDependent = LoadProfile(
            Path.Combine(profileDirectory, "win-x64-framework-dependent.pubxml"));

        Assert.Equal("self-contained", selfContained["GitKeyRouterPublishFlavor"]);
        Assert.Equal("true", selfContained["SelfContained"]);
        Assert.Equal("framework-dependent", frameworkDependent["GitKeyRouterPublishFlavor"]);
        Assert.Equal("false", frameworkDependent["SelfContained"]);
        Assert.Equal("true", selfContained["PublishSingleFile"]);
        Assert.Equal("true", frameworkDependent["PublishSingleFile"]);
        Assert.NotEqual(selfContained["PublishDir"], frameworkDependent["PublishDir"]);
    }

    [Fact]
    public async Task ManualPublishBatchFiles_RouteToExpectedVariants()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Publish-WinX64.bat"] = "-Variant All",
            ["Publish-WinX64-SelfContained.bat"] = "-Variant SelfContained",
            ["Publish-WinX64-FrameworkDependent.bat"] = "-Variant FrameworkDependent"
        };

        foreach (var (fileName, variantArgument) in expected)
        {
            var path = Path.Combine(repositoryRoot, fileName);
            Assert.True(File.Exists(path), $"Manual publisher was not found: {path}");
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("scripts\\Publish-WinX64.ps1", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(variantArgument, content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-OpenOutput", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("artifacts\\publish", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GITKEYROUTER_NO_PAUSE", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pause", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exit /b %RC%", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ManualPublishScript_ReportsAndCanOpenTheGeneratedOutputDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "Publish-WinX64.ps1");
        var content = await File.ReadAllTextAsync(scriptPath);

        Assert.Contains("[switch]$OpenOutput", content, StringComparison.Ordinal);
        Assert.Contains("Repository root:", content, StringComparison.Ordinal);
        Assert.Contains("Self-contained binary:", content, StringComparison.Ordinal);
        Assert.Contains("Framework-dependent binary:", content, StringComparison.Ordinal);
        Assert.Contains("Versioned archives, installers, and checksums:", content, StringComparison.Ordinal);
        Assert.Contains("Opening output folder:", content, StringComparison.Ordinal);
        Assert.Contains("explorer.exe", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerPackaging_MatchesTheMultiFileMsiReleaseContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var installerProject = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "installer", "GitKeyRouter.Installer.wixproj"));
        var installerSource = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "installer", "Package.wxs"));
        var buildScript = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "scripts", "Build-WinX64Installers.ps1"));
        var validationScript = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "scripts", "Test-WinX64Installer.ps1"));
        var lifecycleScript = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "scripts", "Test-InstallerLifecycle.ps1"));
        var releaseWorkflow = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, ".github", "workflows", "release.yml"));
        var lifecycleWorkflow = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, ".github", "workflows", "installer-lifecycle.yml"));

        Assert.Contains("WixToolset.Sdk/5.0.2", installerProject, StringComparison.Ordinal);
        Assert.Contains("WixToolset.UI.wixext", installerProject, StringComparison.Ordinal);
        Assert.Contains("ProgramFiles64Folder", installerSource, StringComparison.Ordinal);
        Assert.Contains("MajorUpgrade", installerSource, StringComparison.Ordinal);
        Assert.Contains("8E52C902-B4A2-4635-A1AF-549B3A0CDC21", installerSource, StringComparison.Ordinal);
        Assert.Contains("GitKeyRouterInstallDirDlg", installerSource, StringComparison.Ordinal);
        Assert.Contains("DesktopShortcutFeature", installerSource, StringComparison.Ordinal);
        Assert.Contains("StartMenuShortcutComponent", installerSource, StringComparison.Ordinal);
        Assert.Contains("Target=\"[APPLICATIONFOLDER]GitKeyRouter.exe\"", installerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Advertise=\"yes\"", installerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=\"GitKeyRouterIcon.exe\"", installerSource, StringComparison.Ordinal);
        Assert.Contains("INSTALLERFLAVOR", installerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LicenseAgreementDlg", installerSource, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=false", buildScript, StringComparison.Ordinal);
        Assert.Contains("NuGetLockFilePath=packages.publish-win-x64.lock.json", buildScript, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", buildScript, StringComparison.Ordinal);
        Assert.Contains("GitKeyRouter.Updater\\GitKeyRouter.Updater.csproj", buildScript, StringComparison.Ordinal);
        Assert.Contains("GitKeyRouter.Updater.exe", buildScript, StringComparison.Ordinal);
        Assert.Contains("$selfContainedIntermediate, $frameworkIntermediate", buildScript, StringComparison.Ordinal);
        Assert.Contains("GitKeyRouter.Updater.exe", validationScript, StringComparison.Ordinal);
        Assert.Contains("Test-WinX64Installer.ps1", buildScript, StringComparison.Ordinal);
        Assert.Contains("WindowsInstaller.Installer", validationScript, StringComparison.Ordinal);
        Assert.Contains("msiexec.exe", lifecycleScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WScript.Shell", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("RequireStableShortcuts", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("Windows Installer icon cache", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("Build-WinX64Installers.ps1", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("matrix:", lifecycleWorkflow, StringComparison.Ordinal);
        Assert.Contains("framework-dependent", lifecycleWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseScripts_HaveValidPowerShellSyntax()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptNames = new[]
        {
            "Publish-WinX64.ps1",
            "Prepare-ReleaseAssets.ps1",
            "Build-WinX64Installers.ps1",
            "Test-WinX64Installer.ps1",
            "Test-InstallerLifecycle.ps1"
,
            "Write-UpdateManifest.ps1"
        };

        foreach (var scriptName in scriptNames)
        {
            var scriptPath = Path.Combine(repositoryRoot, "scripts", scriptName);
            var quotedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
            var parseCommand = $$"""
                $tokens = $null
                $errors = $null
                [System.Management.Automation.Language.Parser]::ParseFile('{{quotedScriptPath}}', [ref]$tokens, [ref]$errors) | Out-Null
                if ($errors.Count -gt 0) {
                    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
                    exit 1
                }
                """;

            var result = await RunProcessAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", parseCommand],
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                captureOutput: true);

            Assert.True(
                result.ExitCode == 0,
                $"{scriptName} has invalid syntax.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
        }
    }

    [Fact]
    public async Task PagesWorkflow_GeneratesSchema3UpdateManifestFromReleaseFixture()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, ".github", "workflows", "pages.yml"));
        Assert.Contains("workflow_run:", workflow, StringComparison.Ordinal);
        Assert.Contains("Write-UpdateManifest.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("site/update.json", workflow, StringComparison.Ordinal);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "GitKeyRouter.UpdateManifestTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            const string version = "1.2.3";
            const string tag = "v1.2.3";
            var expectedFiles = new[]
            {
                $"GitKeyRouter-v{version}-win-x64-framework-dependent.zip",
                $"GitKeyRouter-v{version}-win-x64-portable.zip",
                $"GitKeyRouter-v{version}-win-x64-framework-dependent-setup.msi",
                $"GitKeyRouter-v{version}-win-x64-setup.msi",
                "SHA256SUMS.txt"
            };
            var assets = expectedFiles.Select(name => new
            {
                name,
                browser_download_url = $"https://github.com/project-base-mirror/tool-git-key-router/releases/download/{tag}/{name}"
            }).ToArray();
            var fixturePath = Path.Combine(temporaryRoot, "release.json");
            var outputPath = Path.Combine(temporaryRoot, "update.json");
            await File.WriteAllTextAsync(
                fixturePath,
                JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    html_url = $"https://github.com/project-base-mirror/tool-git-key-router/releases/tag/{tag}",
                    draft = false,
                    prerelease = false,
                    body = "release notes",
                    assets
                }));

            var result = await RunProcessAsync(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", Path.Combine(repositoryRoot, "scripts", "Write-UpdateManifest.ps1"),
                    "-OutputPath", outputPath,
                    "-ReleaseJsonPath", fixturePath
                ],
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                captureOutput: true);
            Assert.True(result.ExitCode == 0, $"Update manifest generation failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var root = document.RootElement;
            Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(tag, root.GetProperty("tagName").GetString());
            Assert.Equal(version, root.GetProperty("version").GetString());
            Assert.Equal("release notes", root.GetProperty("notes").GetString());
            Assert.EndsWith(expectedFiles[1], root.GetProperty("downloads").GetProperty("portableSelfContained").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("SHA256SUMS.txt", root.GetProperty("checksumsUrl").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RepositoryBuild_IsPinnedAndUsesLockedRestore()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var globalJsonStream = File.OpenRead(Path.Combine(repositoryRoot, "global.json"));
        using var globalJson = await JsonDocument.ParseAsync(globalJsonStream);
        var sdk = globalJson.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.302", sdk.GetProperty("version").GetString());
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());

        var lockFiles = new[]
        {
            "src/GitKeyRouter.App/packages.lock.json",
            "src/GitKeyRouter.Core/packages.lock.json",
            "src/GitKeyRouter.Infrastructure/packages.lock.json",
            "tests/GitKeyRouter.App.Tests/packages.win-x64.lock.json",
            "tests/GitKeyRouter.Tests/packages.lock.json",
            "src/GitKeyRouter.App/packages.win-x64.lock.json",
            "src/GitKeyRouter.Core/packages.win-x64.lock.json",
            "src/GitKeyRouter.Infrastructure/packages.win-x64.lock.json",
            "src/GitKeyRouter.App/packages.publish-win-x64.lock.json",
            "src/GitKeyRouter.Core/packages.publish-win-x64.lock.json",
            "src/GitKeyRouter.Infrastructure/packages.publish-win-x64.lock.json",
            "installer/packages.lock.json"
        };
        Assert.All(lockFiles, relativePath => Assert.True(
            File.Exists(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))),
            $"NuGet lock file was not found: {relativePath}"));

        var directoryBuildProps = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var appTestProject = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "tests", "GitKeyRouter.App.Tests", "GitKeyRouter.App.Tests.csproj"));
        var ciWorkflow = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));
        var releaseWorkflow = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var publishScript = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "scripts", "Publish-WinX64.ps1"));
        Assert.Contains("packages.$(RuntimeIdentifier).lock.json", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("<NuGetLockFilePath>packages.win-x64.lock.json</NuGetLockFilePath>", appTestProject, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 10.0.302", ciWorkflow, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", ciWorkflow, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 10.0.302", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("NuGetLockFilePath=packages.publish-win-x64.lock.json", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("git diff-tree --no-commit-id --name-only -r $env:GITHUB_SHA", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT_BEFORE", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("git diff --name-only $before $env:GITHUB_SHA", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v7", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one changed release note file", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", publishScript, StringComparison.Ordinal);
        Assert.Contains("NuGetLockFilePath=packages.publish-win-x64.lock.json", publishScript, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", publishScript, StringComparison.Ordinal);
        Assert.Contains("@('format', $solution, '--no-restore')", publishScript, StringComparison.Ordinal);
        Assert.Contains("@('test', $solution, '-c', 'Release', '--no-build', '--no-restore')", publishScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasePublish_ProducesBothExecutablesAndVersionedAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solutionPath = Path.Combine(repositoryRoot, "GitKeyRouter.sln");
        var projectPath = Path.Combine(repositoryRoot, "src", "GitKeyRouter.App", "GitKeyRouter.App.csproj");
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "GitKeyRouter.PublishTests",
            Guid.NewGuid().ToString("N"));
        var publishRoot = Path.Combine(temporaryRoot, "publish");
        var installerDirectory = Path.Combine(temporaryRoot, "installer");
        var releaseDirectory = Path.Combine(temporaryRoot, "release");
        var prepareReleaseScript = Path.Combine(repositoryRoot, "scripts", "Prepare-ReleaseAssets.ps1");
        var versionDocument = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var version = versionDocument.Descendants("Version").Single().Value;

        try
        {
            var formatResult = await RunProcessAsync(
                "dotnet",
                ["format", solutionPath, "--no-restore"],
                repositoryRoot,
                TimeSpan.FromMinutes(3),
                captureOutput: true);
            Assert.True(
                formatResult.ExitCode == 0,
                $"dotnet format failed.{Environment.NewLine}{formatResult.StandardOutput}{Environment.NewLine}{formatResult.StandardError}");

            var runtimeRestoreResult = await RunProcessAsync(
                "dotnet",
                [
                    "restore", projectPath, "-r", "win-x64", "--locked-mode",
                "-p:NuGetLockFilePath=packages.publish-win-x64.lock.json",
                "-p:PublishSingleFile=true"
                ],
                repositoryRoot,
                TimeSpan.FromMinutes(3),
                captureOutput: true);
            Assert.True(
                runtimeRestoreResult.ExitCode == 0,
                $"win-x64 locked restore failed.{Environment.NewLine}{runtimeRestoreResult.StandardOutput}{Environment.NewLine}{runtimeRestoreResult.StandardError}");

            var variants = new[]
            {
            new { Profile = "win-x64-single-file", Directory = "win-x64", MaximumMiB = 200L },
            new { Profile = "win-x64-framework-dependent", Directory = "win-x64-framework-dependent", MaximumMiB = 25L }
        };

            foreach (var variant in variants)
            {
                var publishDirectory = Path.Combine(publishRoot, variant.Directory);
                if (Directory.Exists(publishDirectory))
                {
                    Directory.Delete(publishDirectory, recursive: true);
                }

                var publishResult = await RunProcessAsync(
                    "dotnet",
                    [
                        "publish", projectPath, "-c", "Release", "-r", "win-x64", "--no-restore",
                    $"-p:PublishProfile={variant.Profile}", "-o", publishDirectory
                    ],
                    repositoryRoot,
                    TimeSpan.FromMinutes(5),
                    captureOutput: true);
                Assert.True(
                    publishResult.ExitCode == 0,
                    $"dotnet publish failed for {variant.Profile}.{Environment.NewLine}{publishResult.StandardOutput}{Environment.NewLine}{publishResult.StandardError}");

                var entries = Directory.GetFileSystemEntries(publishDirectory);
                var executablePath = Path.Combine(publishDirectory, "GitKeyRouter.exe");
                Assert.Single(entries);
                Assert.True(File.Exists(executablePath), $"Published EXE was not found: {executablePath}");
                Assert.DoesNotContain(entries, path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
                Assert.InRange(new FileInfo(executablePath).Length, 1, variant.MaximumMiB * 1024 * 1024);

                await using (var stream = File.OpenRead(executablePath))
                {
                    Assert.Equal(0x4D, stream.ReadByte());
                    Assert.Equal(0x5A, stream.ReadByte());
                }

                using (var publishedIcon = Icon.ExtractAssociatedIcon(executablePath))
                {
                    Assert.NotNull(publishedIcon);
                    using var iconBitmap = publishedIcon.ToBitmap();
                    IconAssertions.AssertGitRouterMark(iconBitmap);
                }

                var versionResult = await RunProcessAsync(
                    executablePath,
                    ["--version"],
                    repositoryRoot,
                    TimeSpan.FromSeconds(30),
                    captureOutput: true);
                Assert.Equal(0, versionResult.ExitCode);
                Assert.Contains(version, versionResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
            }

            Directory.CreateDirectory(installerDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(installerDirectory, $"GitKeyRouter-v{version}-win-x64-setup.msi"),
                [0xD0, 0xCF, 0x11, 0xE0]);
            await File.WriteAllBytesAsync(
                Path.Combine(installerDirectory, $"GitKeyRouter-v{version}-win-x64-framework-dependent-setup.msi"),
                [0xD0, 0xCF, 0x11, 0xE0]);

            var releaseResult = await RunProcessAsync(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", prepareReleaseScript, "-Version", version,
                    "-PublishRoot", publishRoot, "-InstallerRoot", installerDirectory,
                    "-OutputDirectory", releaseDirectory
                ],
                repositoryRoot,
                TimeSpan.FromMinutes(2),
                captureOutput: true);
            Assert.True(
                releaseResult.ExitCode == 0,
                $"Release packaging failed.{Environment.NewLine}{releaseResult.StandardOutput}{Environment.NewLine}{releaseResult.StandardError}");
            Assert.True(File.Exists(Path.Combine(releaseDirectory, $"GitKeyRouter-v{version}-win-x64-portable.zip")));
            Assert.True(File.Exists(Path.Combine(releaseDirectory, $"GitKeyRouter-v{version}-win-x64-framework-dependent.zip")));
            Assert.True(File.Exists(Path.Combine(releaseDirectory, $"GitKeyRouter-v{version}-win-x64-setup.msi")));
            Assert.True(File.Exists(Path.Combine(releaseDirectory, $"GitKeyRouter-v{version}-win-x64-framework-dependent-setup.msi")));
            Assert.True(File.Exists(Path.Combine(releaseDirectory, "SHA256SUMS.txt")));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FrameworkDependentPublish_ProducesLaunchableCompactExecutable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "GitKeyRouter.App",
            "GitKeyRouter.App.csproj");
        var validationScriptPath = Path.Combine(
            repositoryRoot,
            "scripts",
            "Test-WinX64Publish.ps1");
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "GitKeyRouter.FrameworkPublishTests",
            Guid.NewGuid().ToString("N"));
        var publishRoot = Path.Combine(temporaryRoot, "publish");
        var publishDirectory = Path.Combine(publishRoot, "win-x64-framework-dependent");
        const string checksumFileName = "GitKeyRouter-win-x64-framework-dependent.sha256";
        var checksumPath = Path.Combine(publishRoot, checksumFileName);

        try
        {
            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }

            if (File.Exists(checksumPath))
            {
                File.Delete(checksumPath);
            }

            Directory.CreateDirectory(publishDirectory);

            var publishResult = await RunProcessAsync(
                "dotnet",
                [
                    "publish",
                projectPath,
                "-c",
                "Release",
                "-r",
                "win-x64",
                "-p:PublishProfile=win-x64-framework-dependent",
                "-o",
                publishDirectory
                ],
                repositoryRoot,
                TimeSpan.FromMinutes(3),
                captureOutput: true);

            Assert.True(
                publishResult.ExitCode == 0,
                $"dotnet publish failed.{Environment.NewLine}{publishResult.StandardOutput}{Environment.NewLine}{publishResult.StandardError}");

            var entries = Directory.GetFileSystemEntries(publishDirectory);
            Assert.True(
                entries.Length == 1,
                $"Expected only GitKeyRouter.exe, found: {string.Join(", ", entries.Select(Path.GetFileName))}");

            var executablePath = entries[0];
            Assert.Equal("GitKeyRouter.exe", Path.GetFileName(executablePath));
            Assert.True(File.Exists(executablePath));

            var executableInfo = new FileInfo(executablePath);
            Assert.InRange(executableInfo.Length, 1, 25L * 1024 * 1024);

            await using (var stream = File.OpenRead(executablePath))
            {
                Assert.Equal(0x4D, stream.ReadByte());
                Assert.Equal(0x5A, stream.ReadByte());
            }

            using (var publishedIcon = Icon.ExtractAssociatedIcon(executablePath))
            {
                Assert.NotNull(publishedIcon);
                using var iconBitmap = publishedIcon.ToBitmap();
                IconAssertions.AssertGitRouterMark(iconBitmap);
            }

            var validationResult = await RunProcessAsync(
                "powershell.exe",
                [
                    "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                validationScriptPath,
                "-PublishDir",
                publishDirectory,
                "-ChecksumFileName",
                checksumFileName
                ],
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                captureOutput: true);

            Assert.True(
                validationResult.ExitCode == 0,
                $"Publish validation script failed.{Environment.NewLine}{validationResult.StandardOutput}{Environment.NewLine}{validationResult.StandardError}");
            Assert.True(
                File.Exists(checksumPath),
                "Publish validation must create a SHA-256 checksum file.");

            await using var hashStream = File.OpenRead(executablePath);
            var expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream));
            var checksumContent = await File.ReadAllTextAsync(checksumPath);
            Assert.Equal($"{expectedHash}  GitKeyRouter.exe\r\n", checksumContent);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareReleaseAssets_CreatesVersionedArchivesAndChecksums()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "Prepare-ReleaseAssets.ps1");
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "GitKeyRouter.ReleaseTests",
            Guid.NewGuid().ToString("N"));
        var publishRoot = Path.Combine(temporaryRoot, "publish");
        var installerRoot = Path.Combine(temporaryRoot, "installer");
        var outputDirectory = Path.Combine(temporaryRoot, "release");

        try
        {
            var portableDirectory = Path.Combine(publishRoot, "win-x64");
            var frameworkDirectory = Path.Combine(publishRoot, "win-x64-framework-dependent");
            Directory.CreateDirectory(portableDirectory);
            Directory.CreateDirectory(frameworkDirectory);
            Directory.CreateDirectory(installerRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(portableDirectory, "GitKeyRouter.exe"),
                [0x4D, 0x5A, 0x01, 0x02]);
            await File.WriteAllBytesAsync(
                Path.Combine(frameworkDirectory, "GitKeyRouter.exe"),
                [0x4D, 0x5A, 0x03, 0x04]);
            var selfContainedMsi = Path.Combine(
                installerRoot,
                "GitKeyRouter-v0.3.0-win-x64-setup.msi");
            var frameworkMsi = Path.Combine(
                installerRoot,
                "GitKeyRouter-v0.3.0-win-x64-framework-dependent-setup.msi");
            await File.WriteAllBytesAsync(selfContainedMsi, [0xD0, 0xCF, 0x11, 0xE0]);
            await File.WriteAllBytesAsync(frameworkMsi, [0xD0, 0xCF, 0x11, 0xE0]);

            var result = await RunProcessAsync(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                    "-Version",
                    "0.3.0",
                    "-PublishRoot",
                    publishRoot,
                    "-InstallerRoot",
                    installerRoot,
                    "-OutputDirectory",
                    outputDirectory
                ],
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                captureOutput: true);

            Assert.True(
                result.ExitCode == 0,
                $"Release asset preparation failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

            var portableZip = Path.Combine(
                outputDirectory,
                "GitKeyRouter-v0.3.0-win-x64-portable.zip");
            var frameworkZip = Path.Combine(
                outputDirectory,
                "GitKeyRouter-v0.3.0-win-x64-framework-dependent.zip");
            var packagedSelfContainedMsi = Path.Combine(
                outputDirectory,
                "GitKeyRouter-v0.3.0-win-x64-setup.msi");
            var packagedFrameworkMsi = Path.Combine(
                outputDirectory,
                "GitKeyRouter-v0.3.0-win-x64-framework-dependent-setup.msi");
            var checksumPath = Path.Combine(outputDirectory, "SHA256SUMS.txt");

            Assert.True(File.Exists(portableZip));
            Assert.True(File.Exists(frameworkZip));
            Assert.True(File.Exists(packagedSelfContainedMsi));
            Assert.True(File.Exists(packagedFrameworkMsi));
            Assert.True(File.Exists(checksumPath));

            foreach (var archivePath in new[] { portableZip, frameworkZip })
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
                Assert.Contains("GitKeyRouter.exe", entryNames);
                Assert.Contains("LICENSE.txt", entryNames);
                Assert.Contains("README.txt", entryNames);
                Assert.DoesNotContain("GitKeyRouter.Updater.exe", entryNames);
            }

            var checksumLines = await File.ReadAllLinesAsync(checksumPath);
            Assert.Equal(4, checksumLines.Length);
            foreach (var archivePath in new[]
                     {
                         portableZip,
                         frameworkZip,
                         packagedSelfContainedMsi,
                         packagedFrameworkMsi
                     })
            {
                await using var stream = File.OpenRead(archivePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                Assert.Contains($"{hash}  {Path.GetFileName(archivePath)}", checksumLines);
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, string> LoadProfile(string path)
    {
        var document = XDocument.Load(path);
        var propertyGroup = document.Root?.Element("PropertyGroup")
            ?? throw new InvalidDataException($"Publish profile has no PropertyGroup: {path}");

        return propertyGroup
            .Elements()
            .ToDictionary(
                element => element.Name.LocalName,
                element => element.Value,
                StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GitKeyRouter.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the GitKeyRouter repository root.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool captureOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var standardOutputTask = captureOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var standardErrorTask = captureOutput
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);

        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"Process exceeded timeout {timeout}: {fileName}");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
