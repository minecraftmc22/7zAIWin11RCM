using System.Diagnostics;
using System.IO;
using System.Security.Principal;

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

    /// <summary>当前进程是否已提权。</summary>
    public static bool IsProcessElevated => GetIsElevated();

    /// <summary>当前账户是否属于管理员组（可以弹出 UAC 提权）。</summary>
    public static bool IsUserAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                return identity.Groups?.Contains(adminSid) == true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>系统里是否有 MakeAppx.exe 与 signtool.exe（Windows SDK 自带）。</summary>
    public static bool HasPackagingTools =>
        FindSdkTool("makeappx.exe") is not null && FindSdkTool("signtool.exe") is not null;

    /// <summary>在 Windows SDK 目录里找打包工具；找不到返回 null。</summary>
    public static string? FindSdkTool(string fileName)
    {
        var kits = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Kits", "10", "bin"),
        };

        foreach (var bin in kits.Where(Directory.Exists))
        {
            // 版本目录按名称倒序，优先用最新的 SDK
            var versions = Directory.GetDirectories(bin)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            foreach (var version in versions)
            {
                foreach (var arch in new[] { "x64", "x86" })
                {
                    var candidate = Path.Combine(version, arch, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static bool GetIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

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

    /// <summary>
    /// 输出安装前提的诊断信息，供 --modern-menu diag 使用。
    /// 全部为只读检查，不弹 UAC、不改系统。
    /// </summary>
    public static IReadOnlyList<string> Diagnose()
    {
        var lines = new List<string>
        {
            $"系统为 Windows 11 22000+：{IsSupported}",
            $"当前账户在管理员组：{IsUserAdministrator}",
            $"当前进程已提权：{IsProcessElevated}",
            $"打包工具齐全：{HasPackagingTools}",
            $"  MakeAppx：{FindSdkTool("makeappx.exe") ?? "<未找到>"}",
            $"  signtool：{FindSdkTool("signtool.exe") ?? "<未找到>"}",
            $"应用目录：{AppInfo.AppDirectory}",
            $"安装脚本：{InstallScript}（存在：{File.Exists(InstallScript)}）",
            $"卸载脚本：{UninstallScript}（存在：{File.Exists(UninstallScript)}）",
            $"扩展 DLL：{FindShellExtensionDirectory() ?? "<未找到>"}",
        };

        var manifest = Path.Combine(AppInfo.AppDirectory, "packaging", "AppxManifest.xml");
        lines.Add($"包清单：{manifest}（存在：{File.Exists(manifest)}）");
        lines.Add($"已安装版本：{GetInstalledVersion() ?? "<未安装>"}");

        if (File.Exists(InstallScript))
        {
            var size = new FileInfo(InstallScript).Length;
            lines.Add($"安装脚本大小：{size} 字节");
            lines.Add($"脚本语法检查：{CheckScriptSyntax(InstallScript)}");

            // 非管理员时，把脚本以不提权方式跑一次：脚本第一件事就是检查管理员权限，
            // 会在做任何改动之前退出。用来验证"PowerShell 能正常加载并执行它"这条链路。
            if (!IsProcessElevated)
            {
                var shell = FindShellExtensionDirectory() ?? AppInfo.AppDirectory;
                var parameters = new Dictionary<string, string>
                {
                    ["ExternalPath"] = AppInfo.AppDirectory.TrimEnd('\\'),
                    ["ShellExtensionPath"] = shell.TrimEnd('\\'),
                };

                var (code, log) = RunScript(InstallScript, parameters, elevated: false, timeoutMilliseconds: 60000);
                lines.Add($"非提权试跑：退出码 {code}（预期 5 = 需要管理员权限）");
                foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                {
                    lines.Add("  " + line.Trim());
                }
            }
        }

        return lines;
    }

    /// <summary>用 PowerShell 解析器检查脚本语法，返回结果描述（不需要管理员权限）。</summary>
    private static string CheckScriptSyntax(string scriptPath)
    {
        var command =
            $"$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('{Escape(scriptPath)}',[ref]$null,[ref]$e); " +
            "if ($e) { $e | ForEach-Object { $_.Message } } else { 'OK' }";

        try
        {
            var (exitCode, output) = RunPowerShell(command, elevated: false, timeoutMilliseconds: 30000);
            var text = output.Trim();
            return string.IsNullOrWhiteSpace(text) ? $"无输出（退出码 {exitCode}）" : text;
        }
        catch (Exception ex)
        {
            return "检查失败：" + ex.Message;
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

        // 打包工具是硬性前提：缺了就别白弹一次 UAC
        if (!HasPackagingTools)
        {
            return new ModernMenuResult(false,
                "系统缺少打包工具，无法安装新版菜单。",
                "新版右键菜单需要把扩展打成 MSIX 包并签名，这要用到 Windows SDK 里的 " +
                "MakeAppx.exe 与 signtool.exe，当前系统没有找到它们。\n\n" +
                "解决办法（任选其一）：\n" +
                "1. 安装 Windows SDK：https://developer.microsoft.com/windows/downloads/windows-sdk/\n" +
                "2. 只使用旧版右键菜单 —— 不需要任何额外组件，也不需要管理员权限。");
        }

        var parameters = new Dictionary<string, string>
        {
            ["ExternalPath"] = AppInfo.AppDirectory.TrimEnd('\\'),
            ["ShellExtensionPath"] = shellDirectory.TrimEnd('\\'),
        };

        var (exitCode, log) = RunScript(InstallScript, parameters, elevated: true, timeoutMilliseconds: 240000);

        // 以实际结果为准：退出码在提权场景下不一定可靠，装没装上查包最准
        var version = GetInstalledVersion();
        if (version is not null)
        {
            return new ModernMenuResult(true,
                $"新版右键菜单扩展已安装（{version}）。如果菜单没立刻出现，重启资源管理器或注销后重试。",
                log);
        }

        if (exitCode == 0)
        {
            // 提权到别的管理员账户时，包会注册到那个账户，当前用户查不到
            return new ModernMenuResult(false,
                "扩展包已写入，但当前账户查不到，菜单可能不会出现。",
                "这通常是因为授权时用的是另一个管理员账户：MSIX 扩展按账户注册，" +
                "装到了管理员账户上，当前账户反而没有。\n\n" +
                "建议改用旧版右键菜单（无需管理员权限），或登录管理员账户后再运行本程序。" +
                DescribeLogTail(log));
        }

        return new ModernMenuResult(false, DescribeFailure(exitCode, log), log);
    }

    private static ModernMenuResult Uninstall()
    {
        if (!File.Exists(UninstallScript))
        {
            return new ModernMenuResult(false, "卸载脚本缺失。", $"缺失文件：{UninstallScript}");
        }

        var (exitCode, log) = RunScript(
            UninstallScript,
            new Dictionary<string, string>(),
            elevated: true,
            timeoutMilliseconds: 180000);

        if (GetInstalledVersion() is null)
        {
            return new ModernMenuResult(true, "新版右键菜单扩展已移除。", log);
        }

        return new ModernMenuResult(false, DescribeFailure(exitCode, log), log);
    }

    private static string DescribeLogTail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(6)
            .ToList();

        return lines.Count == 0 ? string.Empty : "\n\n脚本输出：\n" + string.Join('\n', lines);
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
            5 => "没有管理员权限，新版菜单未安装。请改用旧版菜单，或用管理员账户运行本程序。",
            6 => "注册扩展包失败，请查看详细信息。",
            1 => string.IsNullOrWhiteSpace(tail)
                ? "安装脚本没能启动（退出码 1），日志里没有输出。请用管理员身份运行本程序后重试，或改用旧版菜单。"
                : $"安装脚本执行失败：{tail}",
            _ => string.IsNullOrWhiteSpace(tail)
                ? $"安装失败（退出码 {exitCode}）。"
                : $"安装失败（退出码 {exitCode}）：{tail}",
        };
    }

    /// <summary>
    /// 以管理员权限执行 scripts 下的脚本。
    ///
    /// 用 -EncodedCommand 内联执行脚本正文，而不是 -File：
    ///   * 不用拼命令行引号，路径里有空格/中文都不会被拆开；
    ///   * 不受"脚本文件执行策略"影响；
    ///   * 外面套一层 try/catch，脚本语法错误、参数绑定失败之类的启动期问题
    ///     也会写进日志，不会只留一个光秃秃的退出码。
    /// </summary>
    private static (int ExitCode, string Log) RunScript(
        string scriptPath,
        IReadOnlyDictionary<string, string> parameters,
        bool elevated,
        int timeoutMilliseconds)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "7zAIWin11RCM",
            $"setup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var bindings = string.Concat(parameters.Select(p => $" -{p.Key} '{Escape(p.Value)}'"));

        var script = string.Join("\n",
            $"$logPath = '{Escape(logPath)}'",
            "try {",
            $"    $source = Get-Content -LiteralPath '{Escape(scriptPath)}' -Raw -Encoding UTF8",
            "    $block = [scriptblock]::Create($source)",
            $"    & $block{bindings} -LogPath $logPath",
            "    exit $LASTEXITCODE",
            "}",
            "catch {",
            "    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ('启动脚本失败：' + $_.Exception.Message)",
            "    if ($_.ScriptStackTrace) { Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $_.ScriptStackTrace }",
            "    exit 1",
            "}");

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var info = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (elevated)
        {
            info.Verb = "runas";
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 提示框里点了「否」
            return (1223, "已取消管理员授权。");
        }
        catch (Exception ex)
        {
            Log.Write("启动安装脚本失败", ex);
            return (-1, "无法启动脚本宿主进程：" + ex.Message);
        }

        if (process is null)
        {
            return (-1, "无法启动脚本宿主进程。");
        }

        using (process)
        {
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

            var exitCode = process.ExitCode;
            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            return (exitCode, log);
        }
    }

    /// <summary>PowerShell 单引号字符串里的转义：把 ' 变成 ''。</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，不影响主流程
        }
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
