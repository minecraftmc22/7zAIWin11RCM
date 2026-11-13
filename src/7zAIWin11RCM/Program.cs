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

        if (TryHandleLegacyMenu(args, out var legacyExitCode))
        {
            return legacyExitCode;
        }

        if (TryHandleModernMenuDiag(args))
        {
            return 0;
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

    /// <summary>
    /// 命令行方式开关旧版右键菜单，方便脚本化 / 排错：
    ///   SevenZipAIWin11RCM.exe --legacy-menu on  [--seven-zip "C:\Program Files\7-Zip"]
    ///   SevenZipAIWin11RCM.exe --legacy-menu off
    /// </summary>
    private static bool TryHandleLegacyMenu(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !args[0].Equals("--legacy-menu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var action = args[1].Trim().ToLowerInvariant();
        if (action is not ("on" or "off"))
        {
            Console.Error.WriteLine("用法：--legacy-menu on|off [--seven-zip <目录>]");
            exitCode = 2;
            return true;
        }

        try
        {
            if (action == "off")
            {
                LegacyMenuService.Uninstall();
                Console.WriteLine("旧版右键菜单已移除。");
                return true;
            }

            var directory = ReadOption(args, "--seven-zip");
            var install = directory is null
                ? SevenZipLocator.AutoDetect()
                : SevenZipLocator.Validate(directory);

            if (install is null)
            {
                Console.Error.WriteLine(directory is null
                    ? "未找到 7-Zip 安装目录，请用 --seven-zip 指定。"
                    : $"路径无效：{directory}");
                exitCode = 1;
                return true;
            }

            LegacyMenuService.Install(install.Directory);
            Console.WriteLine($"旧版右键菜单已写入：{install.Directory}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"操作失败：{ex.Message}");
            exitCode = 1;
        }

        return true;
    }

    /// <summary>
    /// 诊断新版菜单的安装前提：SevenZipAIWin11RCM.exe --modern-menu diag
    /// 只做只读检查，不弹 UAC、不改系统。
    /// </summary>
    private static bool TryHandleModernMenuDiag(string[] args)
    {
        if (args.Length < 2
            || !args[0].Equals("--modern-menu", StringComparison.OrdinalIgnoreCase)
            || !args[1].Equals("diag", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            foreach (var line in ModernMenuService.Diagnose())
            {
                Console.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"诊断失败：{ex.Message}");
        }

        return true;
    }

    private static string? ReadOption(string[] args, string name)    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
