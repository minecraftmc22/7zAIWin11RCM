using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        _suspendTracking = false;

        UpdatePathStatus();
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
        if (string.Equals(registered, AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrWhiteSpace(input))
        {
            SetBadge(BadgeState.Neutral, "未指定", "\uE946");
            DetectBar.IsOpen = false;
            return;
        }

        var install = SevenZipLocator.Validate(input);
        if (install is null)
        {
            SetBadge(BadgeState.Critical, "路径无效", "\uE711");
            DetectBar.Severity = InfoBarSeverity.Warning;
            DetectBar.Title = "没有找到可用的 7-Zip";
            DetectBar.Message = $"在「{input}」下未找到 7zG.exe 或 7zFM.exe，请重新选择安装目录。";
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
                SetBusy(true, "正在安装新版菜单扩展（可能需要管理员授权）…");
                var result = await ModernMenuService.InstallAsync();
                summary.Add(result.Message);
                if (!result.Success)
                {
                    failed = true;
                    // 安装失败时勾选状态回退，避免界面与实际不一致
                    current.EnableModernMenu = false;
                    failureDetail = result.Detail ?? result.Message;
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
            _suspendTracking = false;

            UpdatePathStatus();
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
        _suspendTracking = false;

        UpdatePathStatus();
        UpdateDirtyState();

        ResultBar.Severity = InfoBarSeverity.Informational;
        ResultBar.Title = "已恢复默认值";
        ResultBar.Message = "旧版菜单开启、新版菜单关闭、路径使用自动查找结果。点击「保存」后生效。";
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
