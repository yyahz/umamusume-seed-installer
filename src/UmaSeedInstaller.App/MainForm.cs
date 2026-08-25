using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using UmaSeedInstaller.Core;

namespace UmaSeedInstaller.App;

internal sealed class MainForm : Form
{
    private static readonly Color BrandGreen = Color.FromArgb(16, 126, 75);
    private readonly InstallLayout _layout;
    private readonly ExtensionInstaller _installer;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly IReadOnlyList<BrowserInfo> _browsers;
    private readonly Label _installedValue = new();
    private readonly Label _latestValue = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _installButton = new();
    private readonly Button _refreshButton = new();
    private readonly ComboBox _browserBox = new();
    private ExtensionRelease? _latestRelease;
    private CancellationTokenSource? _operationCancellation;
    private readonly bool _migratedLegacyDirectory;

    public MainForm()
    {
        (_layout, _migratedLegacyDirectory) = SelectInstallLayout();
        _installer = new ExtensionInstaller(_layout);
        _releaseClient = new GitHubReleaseClient(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        });
        _browsers = BrowserDetector.DetectInstalled();

        Text = "种马搜索器安装助手";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 570);
        ClientSize = new Size(700, 640);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 249, 247);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BuildInterface();
        Shown += async (_, _) =>
        {
            await RefreshReleaseAsync();
            CompleteLegacyMigrationGuidance();
        };
        FormClosing += (_, _) => _operationCancellation?.Cancel();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 24),
            RowCount = 5,
            ColumnCount = 1,
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 121));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateVersionCard(), 0, 1);
        root.Controls.Add(CreateInstallCard(), 0, 2);
        root.Controls.Add(CreateBrowserCard(), 0, 3);
        root.Controls.Add(CreateFooter(), 0, 4);
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var logo = new PictureBox
        {
            Location = new Point(0, 0),
            Size = new Size(120, 120),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadLogo()
        };
        panel.Controls.Add(logo);
        var title = new Label
        {
            Text = "种马搜索器",
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            ForeColor = BrandGreen,
            AutoSize = true,
            Location = new Point(136, 14)
        };
        panel.Controls.Add(title);
        panel.Controls.Add(new Label
        {
            Text = "by Songe",
            Font = new Font(Font.FontFamily, 8F, FontStyle.Regular),
            ForeColor = BrandGreen,
            AutoSize = true,
            Location = new Point(title.Left + title.PreferredWidth + 8, 28)
        });
        panel.Controls.Add(new Label
        {
            Text = $"安装与更新助手 v{GetInstallerVersion()} · 不读取浏览器资料",
            ForeColor = Color.FromArgb(77, 91, 83),
            AutoSize = true,
            Location = new Point(138, 61)
        });
        return panel;
    }

    private Control CreateVersionCard()
    {
        var card = CreateCard();
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 12),
            ColumnCount = 3,
            RowCount = 2
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        card.Controls.Add(grid);
        AddVersionRow(grid, 0, "本机版本", _installedValue, FormatVersion(_installer.GetInstalledVersion()));
        AddVersionRow(grid, 1, "GitHub 最新版", _latestValue, "正在检查…");
        _refreshButton.Text = "强制刷新";
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Margin = new Padding(8, 8, 0, 8);
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.BackColor = Color.White;
        _refreshButton.ForeColor = BrandGreen;
        _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(174, 207, 187);
        _refreshButton.Click += async (_, _) => await RefreshReleaseAsync();
        grid.Controls.Add(_refreshButton, 2, 0);
        grid.SetRowSpan(_refreshButton, 2);
        return card;
    }

    private Control CreateInstallCard()
    {
        var card = CreateCard();
        _installButton.Text = "安装 / 更新到最新版";
        _installButton.BackColor = BrandGreen;
        _installButton.ForeColor = Color.White;
        _installButton.FlatStyle = FlatStyle.Flat;
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _installButton.Location = new Point(18, 16);
        _installButton.Size = new Size(230, 46);
        _installButton.Enabled = false;
        _installButton.Click += async (_, _) => await InstallLatestAsync();
        card.Controls.Add(_installButton);

        var copyButton = CreateSecondaryButton("复制扩展目录", new Point(260, 16), (_, _) => CopyInstallPath());
        card.Controls.Add(copyButton);
        var folderButton = CreateSecondaryButton("打开目录", new Point(420, 16), (_, _) => OpenInstallFolder());
        folderButton.Size = new Size(115, 46);
        card.Controls.Add(folderButton);

        _progress.Location = new Point(18, 77);
        _progress.Size = new Size(517, 8);
        _progress.Style = ProgressBarStyle.Continuous;
        card.Controls.Add(_progress);
        _statusLabel.Text = "等待检查最新版本";
        _statusLabel.ForeColor = Color.FromArgb(77, 91, 83);
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Location = new Point(18, 95);
        _statusLabel.Size = new Size(560, 26);
        card.Controls.Add(_statusLabel);
        return card;
    }

    private Control CreateBrowserCard()
    {
        var card = CreateCard();
        _browserBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _browserBox.Location = new Point(18, 17);
        _browserBox.Size = new Size(220, 30);
        foreach (var browser in _browsers)
        {
            _browserBox.Items.Add(browser.DisplayName);
        }

        if (_browserBox.Items.Count > 0)
        {
            _browserBox.SelectedIndex = 0;
        }

        card.Controls.Add(_browserBox);
        var manageButton = CreateSecondaryButton("打开扩展管理页", new Point(250, 13), (_, _) => OpenManagementPage());
        manageButton.Size = new Size(170, 40);
        manageButton.Enabled = _browsers.Count > 0;
        card.Controls.Add(manageButton);
        var toolboxButton = CreateSecondaryButton("打开吗哩吗哩", new Point(432, 13), (_, _) => OpenToolbox());
        toolboxButton.Size = new Size(145, 40);
        toolboxButton.Enabled = _browsers.Count > 0;
        card.Controls.Add(toolboxButton);
        var firstInstallTip = new Label
        {
            Text = _browsers.Count > 0
                ? "首次安装：打开管理页 → 开启开发者模式 → 加载已解压的扩展程序 → 选择上方复制的目录。"
                : "未检测到 Chrome、Edge 或 360 浏览器；安装扩展后仍可手动打开浏览器的扩展管理页。",
            ForeColor = Color.FromArgb(77, 91, 83),
            AutoEllipsis = false,
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 68,
            Padding = new Padding(18, 7, 18, 7),
            TextAlign = ContentAlignment.MiddleLeft
        };
        card.Controls.Add(firstInstallTip);
        firstInstallTip.SendToBack();
        return card;
    }

    private Control CreateFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(new Label
        {
            Text = "扩展包仅从 yyahz/umamusume-seed-optimizer 的 GitHub Release 下载并校验 SHA256。\n助手不需要管理员权限，不读取 Cookie、历史记录或浏览器个人资料。",
            ForeColor = Color.FromArgb(92, 104, 97),
            AutoSize = true,
            Location = new Point(2, 15)
        });
        return panel;
    }

    private async Task RefreshReleaseAsync()
    {
        try
        {
            _latestRelease = null;
            _installedValue.Text = FormatVersion(_installer.GetInstalledVersion());
            _latestValue.Text = "正在检查…";
            SetBusy(true, "正在从 GitHub 检查最新版本…");
            _latestRelease = await _releaseClient.GetLatestAsync();
            _latestValue.Text = $"v{_latestRelease.Version}";
            _statusLabel.Text = $"{DateTime.Now:HH:mm:ss} 已强制刷新；安装时会再次校验下载文件。";
            _installButton.Enabled = true;
        }
        catch (Exception exception)
        {
            _latestValue.Text = "检查失败";
            ShowFailure("无法检查 GitHub 最新版本", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallLatestAsync()
    {
        if (_latestRelease is null)
        {
            await RefreshReleaseAsync();
            if (_latestRelease is null)
            {
                return;
            }
        }

        var running = _browsers.Where(BrowserDetector.IsRunning).Select(browser => browser.DisplayName).ToArray();
        if (running.Length > 0)
        {
            var response = MessageBox.Show(
                $"检测到以下浏览器正在运行：{string.Join("、", running)}。\n\n助手不会强制关闭浏览器。更新后请在扩展管理页点击一次“重新加载”，是否继续？",
                "浏览器正在运行",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (response != DialogResult.Yes)
            {
                return;
            }
        }

        _operationCancellation = new CancellationTokenSource();
        var packagePath = Path.Combine(_layout.WorkDirectory, $"download-{Guid.NewGuid():N}.zip");
        try
        {
            SetBusy(true, $"正在下载 v{_latestRelease.Version}…");
            var progress = new Progress<int>(value => _progress.Value = value);
            await _releaseClient.DownloadVerifiedAsync(
                _latestRelease.Asset,
                packagePath,
                progress,
                _operationCancellation.Token);
            _statusLabel.Text = "下载校验通过，正在安全替换扩展文件…";
            var result = await Task.Run(
                () => _installer.Install(packagePath, _latestRelease.Version),
                _operationCancellation.Token);
            _installedValue.Text = $"v{result.InstalledVersion}";
            var browser = SelectedBrowser();
            var is360SafeBrowser = string.Equals(browser?.Id, "360se", StringComparison.OrdinalIgnoreCase);
            _statusLabel.Text = result.WasUpgrade
                ? is360SafeBrowser
                    ? "文件更新完成；请在已打开的360扩展管理页重新加载，或停用后再启用。"
                    : "更新完成。请在浏览器扩展管理页点击“重新加载”。"
                : "安装完成。请按下方步骤首次加载扩展。";
            Clipboard.SetText(result.ExtensionDirectory);
            if (browser is not null)
            {
                BrowserDetector.Open(browser, browser.ManagementUrl);
            }

            var browserGuidance = is360SafeBrowser
                ? $"\n\n360 安全浏览器的扩展管理页已经打开：\n"
                  + "1. 找到“种马搜索器”，点击“重新加载”；如果没有该按钮，先停用再启用。\n"
                  + "2. 如果360原来加载的是旧文件夹，请删除旧项，再用“加载已解压的扩展程序”选择下面的固定目录。此迁移只需一次。"
                : "\n\n浏览器扩展管理页已经打开，请点击扩展卡片上的“重新加载”。";
            MessageBox.Show(
                $"已{(result.WasUpgrade ? "更新" : "安装")}到 v{result.InstalledVersion}。"
                + browserGuidance
                + $"\n\n固定扩展目录已复制到剪贴板：\n{result.ExtensionDirectory}",
                "操作完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "操作已取消。";
        }
        catch (Exception exception)
        {
            ShowFailure("安装或更新失败，原版本已尽力保留", exception);
        }
        finally
        {
            TryDeleteFile(packagePath);
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void CopyInstallPath()
    {
        Clipboard.SetText(_layout.ExtensionDirectory);
        _statusLabel.Text = "扩展目录已复制到剪贴板。";
    }

    private static (InstallLayout Layout, bool Migrated) SelectInstallLayout()
    {
        var current = InstallLayout.CreateDefault();
        var legacy = InstallLayout.CreateLegacy();
        if (!Directory.Exists(legacy.BaseDirectory) || Directory.Exists(current.BaseDirectory))
        {
            return (current, false);
        }

        var answer = MessageBox.Show(
            "检测到旧安装目录中包含作者名：\n"
            + $"{legacy.BaseDirectory}\n\n"
            + "是否迁移到不含作者名的新目录？扩展文件和备份都会保留。迁移后需要在浏览器中重新加载一次新目录。",
            "迁移安装目录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return (legacy, false);
        }

        current.MigrateFrom(legacy);
        TryRemoveEmptyDirectory(Path.GetDirectoryName(legacy.BaseDirectory));
        return (current, true);
    }

    private void CompleteLegacyMigrationGuidance()
    {
        if (!_migratedLegacyDirectory)
        {
            return;
        }

        Clipboard.SetText(_layout.ExtensionDirectory);
        var browser = SelectedBrowser();
        if (browser is not null)
        {
            BrowserDetector.Open(browser, browser.ManagementUrl);
        }

        _statusLabel.Text = "旧目录迁移完成；请在浏览器中重新加载一次新的固定扩展目录。";
        MessageBox.Show(
            "旧目录已经迁移完成，扩展和备份均已保留。\n\n"
            + "新扩展目录已复制到剪贴板：\n"
            + $"{_layout.ExtensionDirectory}\n\n"
            + "请在扩展管理页删除指向旧路径的扩展项，再通过“加载已解压的扩展程序”选择新目录。此操作只需一次。",
            "目录迁移完成",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OpenInstallFolder()
    {
        Directory.CreateDirectory(_layout.ExtensionDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_layout.ExtensionDirectory}\"",
            UseShellExecute = false
        });
    }

    private void OpenManagementPage()
    {
        var browser = SelectedBrowser();
        if (browser is not null)
        {
            BrowserDetector.Open(browser, browser.ManagementUrl);
        }
    }

    private void OpenToolbox()
    {
        var browser = SelectedBrowser();
        if (browser is not null)
        {
            BrowserDetector.Open(browser, BrowserDetector.ToolboxUrl);
        }
    }

    private BrowserInfo? SelectedBrowser() =>
        _browserBox.SelectedIndex >= 0 && _browserBox.SelectedIndex < _browsers.Count
            ? _browsers[_browserBox.SelectedIndex]
            : null;

    private void SetBusy(bool busy, string? status = null)
    {
        UseWaitCursor = busy;
        _installButton.Enabled = !busy && _latestRelease is not null;
        _refreshButton.Enabled = !busy;
        if (status is not null)
        {
            _statusLabel.Text = status;
        }

        if (!busy)
        {
            _progress.Value = 0;
        }
    }

    private void ShowFailure(string title, Exception exception)
    {
        _statusLabel.Text = $"{title}：{exception.Message}";
        MessageBox.Show(
            $"{title}。\n\n{exception.Message}",
            "种马搜索器安装助手",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Panel CreateCard() => new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 4, 0, 8),
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button CreateSecondaryButton(string text, Point location, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = new Size(148, 46),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = BrandGreen
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(174, 207, 187);
        button.Click += handler;
        return button;
    }

    private static void AddVersionRow(TableLayoutPanel grid, int row, string label, Label value, string text)
    {
        grid.Controls.Add(new Label
        {
            Text = label,
            ForeColor = Color.FromArgb(77, 91, 83),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        value.Text = text;
        value.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        value.ForeColor = Color.FromArgb(28, 49, 37);
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        grid.Controls.Add(value, 1, row);
    }

    private static string FormatVersion(Version? version) => version is null ? "尚未安装" : $"v{version}";

    private static string GetInstallerVersion()
    {
        var version = typeof(MainForm).Assembly.GetName().Version;
        return version is null ? "未知" : version.ToString(3);
    }

    private static Image? LoadLogo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .SingleOrDefault(resource => resource.EndsWith("icon-128.png", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        return stream is null ? null : new Bitmap(stream);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryRemoveEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
