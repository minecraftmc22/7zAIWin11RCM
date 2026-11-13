using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SevenZipAIWin11RCM.Services;

namespace SevenZipAIWin11RCM;

/// <summary>
/// 自定义入口点（配合 DISABLE_XAML_GENERATED_MAIN）。
/// 右键菜单动词走"无窗口"快速返回路径，正常启动才初始化 XAML。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (ShellVerbHandler.TryHandle(args, out var exitCode))
        {
            return exitCode;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _ = new App();
        });

        return 0;
    }
}
