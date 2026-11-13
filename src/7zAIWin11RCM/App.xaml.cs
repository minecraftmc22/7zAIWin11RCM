using Microsoft.UI.Xaml;
using SevenZipAIWin11RCM.Services;

namespace SevenZipAIWin11RCM;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Write("未处理异常：" + e.Message, e.Exception);
        };
    }

    /// <summary>当前窗口句柄，文件选择器等 Win32 交互需要。</summary>
    public static nint WindowHandle =>
        MainWindow is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
