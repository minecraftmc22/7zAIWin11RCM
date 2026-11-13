using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SevenZipAIWin11RCM.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SevenZipAIWin11RCM.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
    }

    private void AboutPage_Loaded(object sender, RoutedEventArgs e)
    {
        HeroIcon.Source = UiAssets.AppIconLarge;
        VersionText.Text = $"版本 {AppInfo.Version} · WinUI 3 / .NET {Environment.Version.ToString(2)}";
        RepositoryBox.Text = AppInfo.RepositoryCloneUrl;
        var isWindows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        OsText.Text = $"{(isWindows11 ? "Windows 11" : "Windows 10")} {Environment.OSVersion.Version}（{RuntimeInformation.OSArchitecture}）";
        DataDirectoryText.Text = SettingsStore.DataDirectory;
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(AppInfo.RepositoryCloneUrl);
            Clipboard.SetContent(package);

            CopyButton.Content = "已复制";
            await Task.Delay(1500);
            CopyButton.Content = "复制";
        }
        catch (Exception ex)
        {
            Log.Write("复制仓库地址失败", ex);
        }
    }

    private async void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(AppInfo.RepositoryUrl));
        }
        catch (Exception ex)
        {
            Log.Write("打开仓库地址失败", ex);
        }
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
}
