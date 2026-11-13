using System.Diagnostics;
using System.IO;

namespace SevenZipAIWin11RCM.Services;

/// <summary>一次新版菜单安装/卸载的结果。</summary>
public sealed record ModernMenuResult(bool Success, string Message, string? Detail = null);

/// <summary>
/// Windows 11 新版（紧凑）右键菜单集成。
///
/// 新版菜单只接受带"包标识"的 IExplorerCommand 扩展，因此这里走
/// 「稀疏 MSIX 包（sparse package + ExternalLocation）+ COM 宿主进程内扩展」的官方路线：
///   scripts\install-modern-menu.ps1 负责打包、签名、注册；
///   真正的菜单逻辑在 7zAIWin11RCM.ShellExt 项目里。
/// 该步骤需要管理员权限（安装自签名证书 + Add-AppxPackage）。
/// </summary>
public static class ModernMenuService
{
    public const string PackageName = "7zAIWin11RCM.ShellExt";
    public const string ShellExtensionFileName = "SevenZipAIWin11RCM.ShellExt.comhost.dll";

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"System32\WindowsPowerShell\v1.0\powershell.exe");

    private static string ScriptsDirectory => Path.Combine(AppInfo.AppDirectory, "scripts");

    private static string InstallScript => Path.Combine(ScriptsDirectory, "install-modern-menu.ps1");

    private static string UninstallScript => Path.Combine(ScriptsDirectory, "uninstall-modern-menu.ps1");

    /// <summary>扩展 DLL 所在目录；找不到返回 null。</summary>
    public static string? FindShellExtensionDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppInfo.AppDirectory, "shell"),
            AppInfo.AppDirectory,
        };

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, ShellExtensionFileName)))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>已安装的新版菜单扩展版本号；未安装返回 null。</summary>
    public static string? GetInstalledVersion()
    {
        try
        {
            var command =
                $"(Get-AppxPackage -Name '{PackageName}' | Select-Object -First 1).Version";
            var output = RunPowerShell(command, elevated: false, timeoutMilliseconds: 20000);
            var version = output.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception ex)
        {
            Log.Write("查询新版菜单扩展状态失败", ex);
            return null;
        }
    }

    public static Task<ModernMenuResult> InstallAsync() => Task.Run(Install);

    public static Task<ModernMenuResult> UninstallAsync() => Task.Run(Uninstall);

    private static ModernMenuResult Install()
    {
        if (!IsSupported)
        {
            return new ModernMenuResult(false, "当前系统低于 Windows 11（内部版本 22000），不支持新版右键菜单。");
        }

        var shellDirectory = FindShellExtensionDirectory();
        if (shellDirectory is null)
        {
            return new ModernMenuResult(false,
                "未找到右键菜单扩展组件，无法安装新版菜单。",
                $"缺失文件：{ShellExtensionFileName}\n请先构建整个解决方案（build.ps1 或 dotnet build），" +
                "或从 Release 包完整解压后重试。");
        }

        if (!File.Exists(InstallScript))
        {
            return new ModernMenuResult(false, "安装脚本缺失，无法安装新版菜单。", $"缺失文件：{InstallScript}");
        }

        var arguments =
            $"-ExternalPath \"{AppInfo.AppDirectory.TrimEnd('\\')}\" -ShellExtensionPath \"{shellDirectory.TrimEnd('\\')}\"";

        var (exitCode, log) = RunScript(InstallScript, arguments, elevated: true, timeoutMilliseconds: 240000);
        if (exitCode == 0)
        {
            var version = GetInstalledVersion();
            return new ModernMenuResult(true,
                version is null ? "新版右键菜单扩展已安装。" : $"新版右键菜单扩展已安装（{version}）。",
                log);
        }

        return new ModernMenuResult(false, DescribeFailure(exitCode, log), log);
    }

    private static ModernMenuResult Uninstall()
    {
        if (!File.Exists(UninstallScript))
        {
            return new ModernMenuResult(false, "卸载脚本缺失。", $"缺失文件：{UninstallScript}");
        }

        var (exitCode, log) = RunScript(UninstallScript, string.Empty, elevated: true, timeoutMilliseconds: 180000);
        if (exitCode == 0)
        {
            return new ModernMenuResult(true, "新版右键菜单扩展已移除。", log);
        }

        return new ModernMenuResult(false, DescribeFailure(exitCode, log), log);
    }

    private static string DescribeFailure(int exitCode, string log)
    {
        var lastLines = log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(4);

        var tail = string.Join(" / ", lastLines);
        return exitCode switch
        {
            1223 => "已取消管理员授权，新版菜单未安装。",
            2 => "系统缺少打包工具（MakeAppx / SignTool），请安装 Windows SDK 后重试。",
            6 => "注册扩展包失败，请查看详细信息。",
            _ => string.IsNullOrWhiteSpace(tail)
                ? $"安装失败（退出码 {exitCode}）。"
                : $"安装失败（退出码 {exitCode}）：{tail}",
        };
    }

    private static (int ExitCode, string Log) RunScript(string scriptPath, string arguments, bool elevated, int timeoutMilliseconds)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "7zAIWin11RCM",
            $"setup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var commandLine =
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments} -LogPath \"{logPath}\""
                .Replace("  ", " ");

        var info = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = commandLine,
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (elevated)
        {
            info.Verb = "runas";
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, "无法启动 PowerShell。");
        }

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Write("终止安装脚本失败", ex);
            }

            return (-1, "安装脚本执行超时。");
        }

        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        return (process.ExitCode, log);
    }

    private static (int ExitCode, string StandardOutput) RunPowerShell(string command, bool elevated, int timeoutMilliseconds)
    {
        var info = new ProcessStartInfo(PowerShellPath, $"-NoProfile -NonInteractive -Command \"{command}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略
            }
        }

        return (process.ExitCode, output);
    }
}
