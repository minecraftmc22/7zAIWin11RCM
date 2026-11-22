using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SevenZipAIWin11RCM.Dialogs;
using SevenZipAIWin11RCM.Services;
using Windows.Storage.Pickers;

namespace SevenZipAIWin11RCM.Views;

public sealed partial class SettingsPage : Page
{
    private enum BadgeState
    {
        Neutral,
        Success,
        Critical,
    }

    private AppSettings _saved = AppSettings.CreateDefault();
    private bool _suspendTracking;
    private bool _busy;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadState();
        _ = RefreshIntegrationStatusAsync();
    }

    private void LoadState()
    {
        _saved = SettingsStore.Load();

        if (string.IsNullOrWhiteSpace(_saved.SevenZipPath))
        {
            var detected = SevenZipLocator.AutoDetect();
            if (detected is not null)
            {
                _saved.SevenZipPath = detected.Directory;
                SettingsStore.Save(_saved);
            }
        }

        _suspendTracking = true;
        PathBox.Text = _saved.SevenZipPath ?? string.Empty;
        ModernMenuCheck.IsChecked = _saved.EnableModernMenu;
        LegacyMenuCheck.IsChecked = _saved.EnableLegacyMenu;
        MakeAppxBox.Text = _saved.MakeAppxPath ?? string.Empty;
        SignToolBox.Text = _saved.SignToolPath ?? string.Empty;
        _suspendTracking = false;

        UpdatePathStatus();
        RefreshSdkToolsStatus(expandWhenMissing: true);
        UpdateDirtyState();
        SelfHealLegacyMenu();
        PromptIfPending();
    }

    /// <summary>程序被移动/更新后，注册表里的命令行会失效，这里自动修复。</summary>
    private void SelfHealLegacyMenu()
    {
        if (!_saved.EnableLegacyMenu || !LegacyMenuService.IsInstalled())
        {
            return;
        }

        var registered = LegacyMenuService.GetRegisteredAppPath();
        if (string.Equals(registered, LegacyMenuService.HandlerDllPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var install = SevenZipLocator.Validate(_saved.SevenZipPath);
        if (install is null)
        {
            return;
        }

        try
        {
            LegacyMenuService.Install(install.Directory);
            Log.Write("检测到程序路径变化，已自动重新注册旧版右键菜单");
        }
        catch (Exception ex)
        {
            Log.Write("自动重新注册旧版右键菜单失败", ex);
        }
    }

    /// <summary>默认开启旧版菜单，但尚未写入注册表时给出明确提示。</summary>
    private void PromptIfPending()
    {
        if (!_saved.EnableLegacyMenu || LegacyMenuService.IsInstalled())
        {
            return;
        }

        ResultBar.Severity = InfoBarSeverity.Informational;
        ResultBar.Title = "默认设置还未应用";
        ResultBar.Message = "当前勾选了「添加至 Windows 11 旧版右键菜单」，点击右下角「保存」即可写入注册表。";
        ResultBar.IsOpen = true;
    }

    private AppSettings ReadUi() => new()
    {
        SevenZipPath = PathBox.Text?.Trim(),
        EnableModernMenu = ModernMenuCheck.IsChecked == true,
        EnableLegacyMenu = LegacyMenuCheck.IsChecked == true,
        MakeAppxPath = MakeAppxBox.Text?.Trim(),
        SignToolPath = SignToolBox.Text?.Trim(),
    };

    // ---------------------------------------------------------------- 路径与勾选

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePathStatus();
        UpdateDirtyState();
    }

    private void MenuOption_Changed(object sender, RoutedEventArgs e)
    {
        // IsChecked 在 XAML 解析期间就会触发，此时其它控件还没创建
        if (_suspendTracking || !IsLoaded)
        {
            return;
        }

        UpdateDirtyState();
    }

    private void UpdatePathStatus()
    {
        var input = PathBox.Text;
        var install = SevenZipLocator.Validate(input);

        // 没找到 → 显示「安装」；已找到 → 显示「更新」
        InstallSevenZipButton.Visibility = install is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSevenZipButton.Visibility = install is null ? Visibility.Collapsed : Visibility.Visible;

        if (install is null)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                SetBadge(BadgeState.Neutral, "未指定", "\uE946");
                DetectBar.Severity = InfoBarSeverity.Warning;
                DetectBar.Title = "未检测到 7-Zip";
                DetectBar.Message = "可以手动指定安装目录，或点击「安装 7-Zip」下载并安装。";
            }
            else
            {
                SetBadge(BadgeState.Critical, "路径无效", "\uE711");
                DetectBar.Severity = InfoBarSeverity.Warning;
                DetectBar.Title = "没有找到可用的 7-Zip";
                DetectBar.Message = $"在「{input}」下未找到 7zG.exe 或 7zFM.exe，请重新选择目录，或点击「安装 7-Zip」。";
            }

            DetectBar.IsOpen = true;
            return;
        }

        SetBadge(
            BadgeState.Success,
            string.IsNullOrEmpty(install.Version) ? "已就绪" : $"7-Zip {install.Version}",
            "\uE73E");

        DetectBar.Severity = InfoBarSeverity.Success;
        DetectBar.Title = "已找到 7-Zip";
        var detail = string.IsNullOrEmpty(install.Version)
            ? install.Directory
            : $"{install.Directory} · 版本 {install.Version}";
        if (!string.IsNullOrEmpty(install.Source))
        {
            detail += $" · 来源：{install.Source}";
        }

        DetectBar.Message = detail;
        DetectBar.IsOpen = true;
    }

    private void UpdateDirtyState()
    {
        if (!IsLoaded)
        {
            return;
        }

        var dirty = !ReadUi().ContentEquals(_saved);

        SaveButton.IsEnabled = dirty && !_busy;
        CancelButton.IsEnabled = dirty && !_busy;
        DirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBadge(BadgeState state, string text, string glyph)
    {
        var (backgroundKey, foregroundKey) = state switch
        {
            BadgeState.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            BadgeState.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _ => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };

        PathBadge.Background = ResourceBrush(backgroundKey);
        PathBadgeText.Foreground = ResourceBrush(foregroundKey);
        PathBadgeText.Text = text;
        PathBadgeIcon.Glyph = glyph;
        PathBadgeIcon.Foreground = ResourceBrush(foregroundKey);
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush();

    // ---------------------------------------------------------------- 浏览 / 自动查找

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                PathBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            Log.Write("选择文件夹失败", ex);
            await ShowMessageAsync("无法打开文件夹选择器", ex.Message);
        }
    }

    private async void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        var detected = await Task.Run(SevenZipLocator.AutoDetect);
        if (detected is null)
        {
            SetBadge(BadgeState.Critical, "未找到", "\uE711");
            DetectBar.Severity = InfoBarSeverity.Warning;
            DetectBar.Title = "自动查找失败";
            DetectBar.Message = "注册表与常见安装目录中都没有找到 7-Zip，请手动指定安装位置。";
            DetectBar.IsOpen = true;
            return;
        }

        PathBox.Text = detected.Directory;
        DetectBar.Severity = InfoBarSeverity.Success;
        DetectBar.Title = "自动查找成功";
        DetectBar.Message = $"{detected.Directory} · 来源：{detected.Source}";
        DetectBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- 打包工具（MakeAppx / SignTool）

    private void SdkToolBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendTracking || !IsLoaded)
        {
            return;
        }

        RefreshSdkToolsStatus();
        UpdateDirtyState();
    }

    private async void BrowseMakeAppxButton_Click(object sender, RoutedEventArgs e) =>
        await BrowseToolAsync(MakeAppxBox, SdkToolLocator.MakeAppx);

    private async void BrowseSignToolButton_Click(object sender, RoutedEventArgs e) =>
        await BrowseToolAsync(SignToolBox, SdkToolLocator.SignTool);

    private async Task BrowseToolAsync(TextBox target, string fileName)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            if (!string.Equals(Path.GetFileName(file.Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageAsync("选错了文件",
                    $"这里需要的是 {fileName}，你选择的是 {Path.GetFileName(file.Path)}。\n\n" +
                    "如果记不清它在哪，可以先点「自动查找」。");
                return;
            }

            _suspendTracking = true;
            target.Text = file.Path;
            _suspendTracking = false;

            RefreshSdkToolsStatus();
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            Log.Write("选择打包工具失败", ex);
            await ShowMessageAsync("无法打开文件选择器", ex.Message);
        }
    }

    /// <summary>「自动查找」= 放弃手动指定，回到全自动模式，并报告真实结果。</summary>
    private void DetectSdkToolsButton_Click(object sender, RoutedEventArgs e)
    {
        SdkToolsExpander.IsExpanded = true;

        var autoMakeAppx = SdkToolLocator.Locate(SdkToolLocator.MakeAppx, null);
        var autoSignTool = SdkToolLocator.Locate(SdkToolLocator.SignTool, null);

        if (autoMakeAppx.Found && autoSignTool.Found)
        {
            _suspendTracking = true;
            MakeAppxBox.Text = string.Empty;
            SignToolBox.Text = string.Empty;
            _suspendTracking = false;
        }

        RefreshSdkToolsStatus(expandWhenMissing: true);
        UpdateDirtyState();
    }

    /// <summary>刷新打包工具状态；返回是否两项都已找到。</summary>
    private bool RefreshSdkToolsStatus(bool expandWhenMissing = false)
    {
        var makeAppx = SdkToolLocator.Locate(SdkToolLocator.MakeAppx, MakeAppxBox.Text);
        var signTool = SdkToolLocator.Locate(SdkToolLocator.SignTool, SignToolBox.Text);
        var ready = makeAppx.Found && signTool.Found;

        SdkToolsStatusText.Text = string.Join("\n", makeAppx.Describe(), signTool.Describe());
        SdkToolsStatusText.Foreground = ResourceBrush(ready
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorCautionBrush");

        if (ready)
        {
            SdkToolsBar.IsOpen = false;
            return true;
        }

        var missing = new List<string>();
        if (!makeAppx.Found)
        {
            missing.Add(SdkToolLocator.MakeAppx);
        }

        if (!signTool.Found)
        {
            missing.Add(SdkToolLocator.SignTool);
        }

        SdkToolsBar.Severity = InfoBarSeverity.Warning;
        SdkToolsBar.Title = $"还没有找到 {string.Join(" 和 ", missing)}";
        SdkToolsBar.Message =
            "可以点「浏览…」指定位置（通常在 Windows SDK 的 bin\\<版本>\\x64 目录下），" +
            "或安装 Windows SDK 后重试：https://developer.microsoft.com/windows/downloads/windows-sdk/";
        SdkToolsBar.IsOpen = true;

        if (expandWhenMissing)
        {
            SdkToolsExpander.IsExpanded = true;
        }

        return false;
    }

    private async void InstallSevenZipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new InstallSevenZipDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        if (string.IsNullOrEmpty(dialog.InstalledPath))
        {
            return;
        }

        var install = SevenZipLocator.Validate(dialog.InstalledPath);
        PathBox.Text = install?.Directory ?? dialog.InstalledPath;
        UpdatePathStatus();
        UpdateDirtyState();

        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Title = "7-Zip 安装完成";
        ResultBar.Message =
            $"已安装到 {PathBox.Text}。点击右下角「保存」把该路径与菜单选项一起生效。";
        ResultBar.IsOpen = true;
    }

    /// <summary>检查在线版本并引导更新（覆盖安装到现有目录）。</summary>
    private async void UpdateSevenZipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var install = SevenZipLocator.Validate(PathBox.Text);
        if (install is null)
        {
            UpdatePathStatus();
            return;
        }

        UpdateSevenZipButton.IsEnabled = false;
        UpdateSevenZipButton.Content = "检查中…";

        ReleaseCatalogResult catalog;
        try
        {
            catalog = await SevenZipReleaseCatalog.FetchAsync();
        }
        catch (Exception ex)
        {
            Log.Write("检查 7-Zip 更新失败", ex);
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Title = "检查更新失败";
            ResultBar.Message = ex.Message;
            ResultBar.IsOpen = true;
            return;
        }
        finally
        {
            UpdateSevenZipButton.Content = "更新 7-Zip";
            UpdateSevenZipButton.IsEnabled = true;
        }

        var latest = catalog.Releases.FirstOrDefault();
        if (latest is null)
        {
            ResultBar.Severity = InfoBarSeverity.Warning;
            ResultBar.Title = "无法检查更新";
            ResultBar.Message = catalog.Warning ?? "没有获取到可用的 7-Zip 版本列表。";
            ResultBar.IsOpen = true;
            return;
        }

        var currentVersion = SevenZipVersion.TryParse(install.Version);
        var latestVersion = SevenZipVersion.TryParse(latest.Version);

        if (currentVersion is not null && latestVersion is not null && currentVersion >= latestVersion)
        {
            await ShowMessageAsync("已是最新版本",
                $"当前安装：7-Zip {install.Version}\n在线最新：7-Zip {latest.Version}\n\n安装位置：{install.Directory}");

            ResultBar.Severity = InfoBarSeverity.Success;
            ResultBar.Title = "已是最新版本";
            ResultBar.Message = $"7-Zip {install.Version}，在线最新为 {latest.Version}。";
            ResultBar.IsOpen = true;
            return;
        }

        Log.Write($"发现 7-Zip 新版本：{install.Version} → {latest.Version}");

        var dialog = new InstallSevenZipDialog
        {
            XamlRoot = XamlRoot,
            PresetRelease = latest,
            UpdateTargetDirectory = install.Directory,
        };
        await dialog.ShowAsync();

        if (string.IsNullOrEmpty(dialog.InstalledPath))
        {
            // 用户取消或安装失败
            return;
        }

        var refreshed = SevenZipLocator.Validate(dialog.InstalledPath) ?? install;
        PathBox.Text = refreshed.Directory;
        UpdatePathStatus();
        UpdateDirtyState();
        _ = RefreshIntegrationStatusAsync();

        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Title = "7-Zip 已更新";
        ResultBar.Message = string.IsNullOrEmpty(refreshed.Version)
            ? $"已更新到在线最新版本 {latest.Version}（{refreshed.Directory}）。"
            : $"当前版本 {refreshed.Version}（{refreshed.Directory}）。";
        ResultBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- 保存 / 恢复默认 / 取消

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var current = ReadUi();
        var install = SevenZipLocator.Validate(current.SevenZipPath);
        if (install is null)
        {
            await ShowMessageAsync("7-Zip 路径无效",
                "请选择一个包含 7zG.exe 与 7zFM.exe 的目录，例如 C:\\Program Files\\7-Zip。");
            return;
        }

        current.SevenZipPath = install.Directory;

        SetBusy(true, "正在应用设置…");
        var summary = new List<string>();
        var failed = false;
        var failureDetail = string.Empty;

        try
        {
            // 1) 旧版（经典）菜单：纯注册表写入，立即生效
            if (current.EnableLegacyMenu)
            {
                LegacyMenuService.Install(install.Directory);
                summary.Add("旧版右键菜单已写入（HKCU，无需管理员权限）。");
            }
            else if (LegacyMenuService.IsInstalled())
            {
                LegacyMenuService.Uninstall();
                summary.Add("旧版右键菜单已移除。");
            }

            // 2) 新版（Win11 紧凑）菜单：需要 MSIX 扩展包
            SetBusy(true, "正在检查新版菜单扩展…");
            var installedVersion = await Task.Run(ModernMenuService.GetInstalledVersion);

            if (current.EnableModernMenu && installedVersion is null)
            {
                // 打包工具缺失时先别弹 UAC：直接引导到「打包工具」面板，省一次授权
                var makeAppxPath = SdkToolLocator.Resolve(SdkToolLocator.MakeAppx, current.MakeAppxPath);
                var signToolPath = SdkToolLocator.Resolve(SdkToolLocator.SignTool, current.SignToolPath);

                if (makeAppxPath is null || signToolPath is null)
                {
                    failed = true;
                    current.EnableModernMenu = false;
                    SdkToolsExpander.IsExpanded = true;
                    failureDetail =
                        "新版右键菜单需要 MakeAppx.exe 与 signtool.exe 把扩展打成 MSIX 包并签名，" +
                        "当前没有找到它们（已在下方展开「打包工具」面板）。\n\n" +
                        "可以点「浏览…」手动指定这两个程序的路径，或安装 Windows SDK 后重试；" +
                        "只想用旧版菜单的话，取消勾选「新版右键菜单」即可。";
                }
                else
                {
                    SetBusy(true, "正在安装新版菜单扩展（可能需要管理员授权）…");
                    var result = await ModernMenuService.InstallAsync(current);
                    summary.Add(result.Message);
                    if (!result.Success)
                    {
                        failed = true;
                        // 安装失败时勾选状态回退，避免界面与实际不一致
                        current.EnableModernMenu = false;
                        failureDetail = result.Detail ?? result.Message;
                    }
                }
            }
            else if (!current.EnableModernMenu && installedVersion is not null)
            {
                SetBusy(true, "正在移除新版菜单扩展…");
                var result = await ModernMenuService.UninstallAsync();
                summary.Add(result.Message);
                if (!result.Success)
                {
                    failed = true;
                    current.EnableModernMenu = true;
                    failureDetail = result.Detail ?? result.Message;
                }
            }
            else if (current.EnableModernMenu)
            {
                summary.Add($"新版右键菜单扩展已安装（{installedVersion}）。");
            }

            // 3) 落盘
            if (!SettingsStore.Save(current))
            {
                failed = true;
                summary.Add("设置文件写入失败，请检查磁盘权限。");
            }
            else
            {
                _saved = current.Clone();
            }

            _suspendTracking = true;
            PathBox.Text = current.SevenZipPath;
            ModernMenuCheck.IsChecked = current.EnableModernMenu;
            LegacyMenuCheck.IsChecked = current.EnableLegacyMenu;
            MakeAppxBox.Text = current.MakeAppxPath ?? string.Empty;
            SignToolBox.Text = current.SignToolPath ?? string.Empty;
            _suspendTracking = false;

            UpdatePathStatus();
            RefreshSdkToolsStatus();
            await RefreshIntegrationStatusAsync();

            ResultBar.Severity = failed ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ResultBar.Title = failed ? "部分设置未能应用" : "设置已保存";
            ResultBar.Message = failed && !string.IsNullOrWhiteSpace(failureDetail)
                ? failureDetail
                : string.Join(" ", summary);
            ResultBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Write("应用设置失败", ex);
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Title = "应用设置失败";
            ResultBar.Message = ex.Message;
            ResultBar.IsOpen = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var detected = await Task.Run(SevenZipLocator.AutoDetect);

        _suspendTracking = true;
        PathBox.Text = detected?.Directory ?? string.Empty;
        ModernMenuCheck.IsChecked = false;   // 默认关闭新版菜单
        LegacyMenuCheck.IsChecked = true;    // 默认开启旧版菜单
        MakeAppxBox.Text = string.Empty;     // 打包工具回到自动查找
        SignToolBox.Text = string.Empty;
        _suspendTracking = false;

        UpdatePathStatus();
        RefreshSdkToolsStatus();
        UpdateDirtyState();

        ResultBar.Severity = InfoBarSeverity.Informational;
        ResultBar.Title = "已恢复默认值";
        ResultBar.Message = "旧版菜单开启、新版菜单关闭、路径与打包工具都使用自动查找结果。点击「保存」后生效。";
        ResultBar.IsOpen = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        LoadState();
        _ = RefreshIntegrationStatusAsync();

        ResultBar.Severity = InfoBarSeverity.Informational;
        ResultBar.Title = "已放弃更改";
        ResultBar.Message = "界面已恢复为上次保存的设置。";
        ResultBar.IsOpen = true;
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logFile = Log.LogFile;
            if (File.Exists(logFile))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{logFile}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.DataDirectory}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Write("打开日志失败", ex);
        }
    }

    // ---------------------------------------------------------------- 状态显示

    private async Task RefreshIntegrationStatusAsync()
    {
        UpdateModernMenuHint();

        var legacyInstalled = LegacyMenuService.IsInstalled();
        ApplyStatusText(LegacyStatusText, legacyInstalled, legacyInstalled ? "已注册" : "未注册");

        if (!ModernMenuService.IsSupported)
        {
            ApplyStatusText(ModernStatusText, false, "系统不支持");
            return;
        }

        ApplyStatusText(ModernStatusText, null, "检查中…");

        string? version = null;
        try
        {
            version = await Task.Run(ModernMenuService.GetInstalledVersion);
        }
        catch (Exception ex)
        {
            Log.Write("查询新版菜单状态失败", ex);
        }

        ApplyStatusText(ModernStatusText, version is not null, version is null ? "未安装" : $"已安装 {version}");
    }

    /// <summary>按当前系统/账户情况说明新版菜单的前提条件，避免用户点了才发现缺东西。</summary>
    private void UpdateModernMenuHint()
    {
        if (!ModernMenuService.IsSupported)
        {
            ModernMenuHint.Text = "当前系统不是 Windows 11，新版右键菜单不可用。";
            ModernMenuCheck.IsEnabled = false;
            return;
        }

        ModernMenuCheck.IsEnabled = true;

        var parts = new List<string> { "在 Windows 11 紧凑菜单中直接显示 7-Zip 条目。" };

        var makeAppx = SdkToolLocator.Locate(SdkToolLocator.MakeAppx, MakeAppxBox.Text);
        var signTool = SdkToolLocator.Locate(SdkToolLocator.SignTool, SignToolBox.Text);
        if (!makeAppx.Found || !signTool.Found)
        {
            parts.Add("启用前还需要 MakeAppx 与 signtool（Windows SDK 自带），可在下方「打包工具」里手动指定。");
        }

        parts.Add(ModernMenuService.IsUserAdministrator
            ? "保存时会弹出 UAC 授权。"
            : "当前账户不是管理员：授权时要输入管理员账户密码，而扩展会注册到那个管理员账户，本账户多半看不到。这种情况建议改用旧版菜单。");

        ModernMenuHint.Text = string.Concat(parts);
    }

    private static void ApplyStatusText(TextBlock target, bool? ok, string text)
    {
        target.Text = text;
        target.Foreground = ResourceBrush(ok switch
        {
            true => "SystemFillColorSuccessBrush",
            false => "TextFillColorTertiaryBrush",
            _ => "TextFillColorSecondaryBrush",
        });
    }

    private void SetBusy(bool busy, string message = "正在应用设置…")
    {
        _busy = busy;
        BusyText.Text = message;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        ModernMenuCheck.IsEnabled = !busy;
        LegacyMenuCheck.IsEnabled = !busy;
        MakeAppxBox.IsEnabled = !busy;
        SignToolBox.IsEnabled = !busy;
        DetectSdkButton.IsEnabled = !busy;
        SdkToolsExpander.IsEnabled = !busy;
        InstallSevenZipButton.IsEnabled = !busy;
        UpdateSevenZipButton.IsEnabled = !busy;
        UpdateDirtyState();
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
