using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SevenZipAIWin11RCM.Services;
using Windows.Storage.Pickers;

namespace SevenZipAIWin11RCM.Dialogs;

/// <summary>
/// 「安装 7-Zip」向导：选择版本 → 下载（带进度）→ 选择安装目录 → 静默安装。
/// 安装成功后 <see cref="InstalledPath"/> 为实际安装目录。
/// </summary>
public sealed partial class InstallSevenZipDialog : ContentDialog
{
    private enum Step
    {
        Version,
        Download,
        Directory,
        Installing,
        Done,
    }

    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<SevenZipRelease> _releases = Array.Empty<SevenZipRelease>();
    private SevenZipRelease? _selectedRelease;
    private SevenZipAsset? _selectedAsset;
    private string? _installerPath;
    private string _targetDirectory = string.Empty;
    private Step _step = Step.Version;

    /// <summary>安装成功后的安装目录；未成功则为 null。</summary>
    public string? InstalledPath { get; private set; }

    /// <summary>更新模式：预先指定的版本，跳过版本选择直接下载。</summary>
    public SevenZipRelease? PresetRelease { get; set; }

    /// <summary>更新模式：固定安装到该目录（覆盖升级，不允许浏览）。</summary>
    public string? UpdateTargetDirectory { get; set; }

    private bool IsUpdate => !string.IsNullOrWhiteSpace(UpdateTargetDirectory);

    public InstallSevenZipDialog()
    {
        InitializeComponent();

        Opened += ContentDialog_Opened;
        PrimaryButtonClick += ContentDialog_PrimaryButtonClick;
        Closing += ContentDialog_Closing;
        Closed += (_, _) => _cts.Cancel();
    }

    // ---------------------------------------------------------------- 生命周期

    private async void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (PresetRelease is not null)
        {
            // 更新模式：版本已确定，直接开始下载
            _selectedRelease = PresetRelease;
            _selectedAsset = PresetRelease.PickForCurrentArchitecture();
            if (_selectedAsset is null)
            {
                GoToStep(Step.Version);
                FetchBar.Severity = InfoBarSeverity.Error;
                FetchBar.Title = "没有可用的安装包";
                FetchBar.Message = $"7-Zip {PresetRelease.Version} 没有适用于当前系统的安装包。";
                FetchBar.IsOpen = true;
                return;
            }

            await StartDownloadAsync();
            return;
        }

        GoToStep(Step.Version);

        VersionRing.IsActive = true;
        VersionRing.Visibility = Visibility.Visible;

        try
        {
            var result = await SevenZipReleaseCatalog.FetchAsync(_cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _releases = result.Releases.Count > 0 ? result.Releases : SevenZipReleaseCatalog.BuiltInReleases;
            VersionBox.ItemsSource = _releases;
            if (_releases.Count > 0)
            {
                VersionBox.SelectedIndex = 0;   // 默认最新
            }

            if (!string.IsNullOrEmpty(result.Warning))
            {
                FetchBar.Severity = InfoBarSeverity.Warning;
                FetchBar.Message = result.Warning;
                FetchBar.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
            // 对话框已关闭
        }
        catch (Exception ex)
        {
            Log.Write("读取 7-Zip 版本列表失败", ex);
            _releases = SevenZipReleaseCatalog.BuiltInReleases;
            VersionBox.ItemsSource = _releases;
            if (_releases.Count > 0)
            {
                VersionBox.SelectedIndex = 0;
            }

            FetchBar.Severity = InfoBarSeverity.Warning;
            FetchBar.Message = "无法获取在线版本列表，已回退到内置版本：" + ex.Message;
            FetchBar.IsOpen = true;
        }
        finally
        {
            VersionRing.IsActive = false;
            VersionRing.Visibility = Visibility.Collapsed;

            // 只有仍停留在第 1 步时才接管按钮状态，避免覆盖后续步骤
            if (_step == Step.Version)
            {
                IsPrimaryButtonEnabled = _releases.Count > 0 && _selectedAsset is not null;
            }
        }
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // 安装过程中不允许关闭，避免出现"界面关了、UAC 还在"的困惑
        if (_step == Step.Installing)
        {
            args.Cancel = true;
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        switch (_step)
        {
            case Step.Version:
                await StartDownloadAsync();
                break;

            case Step.Download:
                await StartDownloadAsync();
                break;

            case Step.Directory:
                await StartInstallAsync();
                break;

            case Step.Done:
                Hide();
                break;
        }
    }

    // ---------------------------------------------------------------- 步骤 1：版本

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRelease = VersionBox.SelectedItem as SevenZipRelease;
        _selectedAsset = _selectedRelease?.PickForCurrentArchitecture();

        VersionHintText.Text = _selectedAsset is null
            ? "该版本没有适用于当前系统的安装包，请换一个版本。"
            : $"将下载 {_selectedAsset.Name}（{_selectedAsset.SizeText}），来自 7-Zip 官方 GitHub Release。";

        if (_step == Step.Version && !_cts.IsCancellationRequested)
        {
            IsPrimaryButtonEnabled = _selectedAsset is not null;
        }
    }

    // ---------------------------------------------------------------- 步骤 2：下载

    private async Task StartDownloadAsync()
    {
        if (_selectedAsset is null)
        {
            FetchBar.Severity = InfoBarSeverity.Error;
            FetchBar.Message = "请先选择一个可用的版本。";
            FetchBar.IsOpen = true;
            return;
        }

        GoToStep(Step.Download);
        DownloadBar.Value = 0;
        DownloadDetail.Text = "正在连接…";
        DownloadTitle.Text = IsUpdate
            ? $"正在下载 7-Zip {_selectedRelease?.Version} 安装包…"
            : "正在下载安装包…";
        DownloadNameText.Text = _selectedAsset.Name;
        DownloadErrorBar.IsOpen = false;

        var progress = new Progress<DownloadProgress>(value =>
        {
            DownloadBar.Value = value.Percent;
            DownloadDetail.Text = value.DetailText;
        });

        try
        {
            _installerPath = await SevenZipInstaller.DownloadAsync(_selectedAsset, progress, _cts.Token);
            DownloadBar.Value = 100;
            DownloadDetail.Text = "下载完成";
            PrepareDirectoryStep();
            GoToStep(Step.Directory);
        }
        catch (OperationCanceledException)
        {
            // 对话框已关闭
        }
        catch (Exception ex)
        {
            Log.Write("下载 7-Zip 安装包失败", ex);
            DownloadErrorBar.Severity = InfoBarSeverity.Error;
            DownloadErrorBar.Title = "下载失败";
            DownloadErrorBar.Message = ex.Message;
            DownloadErrorBar.IsOpen = true;

            PrimaryButtonText = "重试";
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "取消";
        }
    }

    // ---------------------------------------------------------------- 步骤 3：安装位置

    private void PrepareDirectoryStep()
    {
        InstallBar.IsOpen = false;

        if (UpdateTargetDirectory is { Length: > 0 } fixedTarget)
        {
            // 更新模式：覆盖升级到现有目录，不允许改动
            _targetDirectory = fixedTarget;
            TargetBox.Text = fixedTarget;
            BrowseButton.Visibility = Visibility.Collapsed;
            DirectoryHintText.Text = "会在现有安装目录上覆盖升级，7-Zip 原有设置与右键集成保持在原位。";
            InstallHintText.Text = "更新需要管理员权限，稍后会弹出 UAC 提示。";
            return;
        }

        _targetDirectory = SevenZipInstaller.ResolveTargetDirectory(SevenZipInstaller.DefaultInstallRoot);
        TargetBox.Text = _targetDirectory;
        BrowseButton.Visibility = Visibility.Visible;
        DirectoryHintText.Text = "会在所选目录下自动创建 7-Zip 子文件夹；若已直接选中 7-Zip 目录则原样使用。";
        InstallHintText.Text = "安装到该目录需要管理员权限，稍后会弹出 UAC 提示。";
    }

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
            if (folder is null)
            {
                return;
            }

            _targetDirectory = SevenZipInstaller.ResolveTargetDirectory(folder.Path);
            TargetBox.Text = _targetDirectory;
        }
        catch (Exception ex)
        {
            Log.Write("选择 7-Zip 安装目录失败", ex);
            InstallBar.Severity = InfoBarSeverity.Error;
            InstallBar.Title = "无法打开文件夹选择器";
            InstallBar.Message = ex.Message;
            InstallBar.IsOpen = true;
        }
    }

    // ---------------------------------------------------------------- 步骤 4：安装

    private async Task StartInstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_installerPath) || !File.Exists(_installerPath))
        {
            GoToStep(Step.Download);
            DownloadErrorBar.Severity = InfoBarSeverity.Error;
            DownloadErrorBar.Title = "安装包已丢失";
            DownloadErrorBar.Message = "请重新下载安装包。";
            DownloadErrorBar.IsOpen = true;
            PrimaryButtonText = "重试";
            IsPrimaryButtonEnabled = true;
            return;
        }

        var target = string.IsNullOrWhiteSpace(_targetDirectory)
            ? SevenZipInstaller.ResolveTargetDirectory(SevenZipInstaller.DefaultInstallRoot)
            : _targetDirectory;

        GoToStep(Step.Installing);

        try
        {
            var result = await SevenZipInstaller.InstallAsync(_installerPath, target, _cts.Token);
            if (result.Success)
            {
                InstalledPath = result.Directory ?? target;
                GoToStep(Step.Done);
                FinishBar.Severity = InfoBarSeverity.Success;
                FinishBar.Title = IsUpdate ? "更新成功" : "安装成功";
                FinishBar.Message = result.Message;
                FinishBar.IsOpen = true;
            }
            else
            {
                GoToStep(Step.Directory);
                InstallBar.Severity = InfoBarSeverity.Error;
                InstallBar.Title = "安装未完成";
                InstallBar.Message = result.Message;
                InstallBar.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
            // 对话框已关闭
        }
        catch (Exception ex)
        {
            Log.Write("安装 7-Zip 失败", ex);
            GoToStep(Step.Directory);
            InstallBar.Severity = InfoBarSeverity.Error;
            InstallBar.Title = "安装失败";
            InstallBar.Message = ex.Message;
            InstallBar.IsOpen = true;
        }
    }

    // ---------------------------------------------------------------- 步骤切换

    private static Style? AccentStyle =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var value) ? value as Style : null;

    private void GoToStep(Step step)
    {
        _step = step;

        var action = IsUpdate ? "更新" : "安装";
        var total = IsUpdate ? "2" : "3";

        VersionStep.Visibility = step == Step.Version ? Visibility.Visible : Visibility.Collapsed;
        DownloadStep.Visibility = step == Step.Download ? Visibility.Visible : Visibility.Collapsed;
        DirectoryStep.Visibility = step == Step.Directory ? Visibility.Visible : Visibility.Collapsed;
        FinishStep.Visibility = step is Step.Installing or Step.Done ? Visibility.Visible : Visibility.Collapsed;

        // 主按钮统一使用强调色（改 PrimaryButtonText 后样式会被重置，这里显式指定）
        PrimaryButtonStyle = step is Step.Version or Step.Directory or Step.Done ? AccentStyle : null;
        InstallingPanel.Visibility = step == Step.Installing ? Visibility.Visible : Visibility.Collapsed;

        switch (step)
        {
            case Step.Version:
                Title = $"{action} 7-Zip（1/{total} 选择版本）";
                PrimaryButtonText = "下一步";
                CloseButtonText = "取消";
                IsPrimaryButtonEnabled = _releases.Count > 0 && _selectedAsset is not null;
                break;

            case Step.Download:
                Title = $"{action} 7-Zip（{(IsUpdate ? "1" : "2")}/{total} 下载安装包）";
                PrimaryButtonText = "下一步";
                CloseButtonText = "取消";
                IsPrimaryButtonEnabled = false;
                break;

            case Step.Directory:
                Title = $"{action} 7-Zip（{(IsUpdate ? "2" : "3")}/{total} 确认安装位置）";
                PrimaryButtonText = IsUpdate ? "开始更新" : "开始安装";
                CloseButtonText = "取消";
                IsPrimaryButtonEnabled = true;
                break;

            case Step.Installing:
                Title = $"正在{action} 7-Zip";
                PrimaryButtonText = $"{action}中…";
                InstallingText.Text = IsUpdate
                    ? "正在更新，请在弹出的 UAC 提示中选择「是」…"
                    : "正在安装，请在弹出的 UAC 提示中选择「是」…";
                CloseButtonText = string.Empty;
                IsPrimaryButtonEnabled = false;
                break;

            case Step.Done:
                Title = $"{action}完成";
                PrimaryButtonText = "完成";
                CloseButtonText = string.Empty;
                IsPrimaryButtonEnabled = true;
                break;
        }
    }
}
