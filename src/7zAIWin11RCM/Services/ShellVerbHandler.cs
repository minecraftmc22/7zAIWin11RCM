using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 右键菜单动词的真实执行者。
/// 注册表里写入的命令是 <c>"7zAIWin11RCM.exe" --verb extract-here "%1"</c>，
/// 这样路径计算全部在托管代码里完成，不必依赖注册表字符串拼接技巧。
/// </summary>
public static class ShellVerbHandler
{
    public const string VerbExtractHere = "extract-here";
    public const string VerbExtractTo = "extract-to";
    public const string VerbAdd = "add";
    public const string VerbOpen = "open";

    /// <summary>若命令行是菜单动词调用，则执行并返回 true（此时不启动 GUI）。</summary>
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 3 || !string.Equals(args[0], "--verb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var verb = args[1].Trim().ToLowerInvariant();
        var target = args[2].Trim();
        if (target.Length == 0)
        {
            return false;
        }

        try
        {
            exitCode = Execute(verb, target);
        }
        catch (Exception ex)
        {
            Log.Write($"执行菜单动词失败：{verb} {target}", ex);
            ShowError($"执行 7-Zip 操作时出错：{ex.Message}");
            exitCode = 1;
        }

        return true;
    }

    private static int Execute(string verb, string target)
    {
        var install = ResolveInstallation();
        if (install is null)
        {
            ShowError("没有找到 7-Zip。请先安装 7-Zip，或在「7-Zip 右键菜单助手」中指定安装位置。");
            return 2;
        }

        var isDirectory = Directory.Exists(target);
        var isFile = File.Exists(target);
        if (!isDirectory && !isFile)
        {
            ShowError($"找不到目标：{target}");
            return 2;
        }

        switch (verb)
        {
            case VerbExtractHere when isFile:
                return Start(install.SevenZipGuiPath, Path.GetDirectoryName(target)!,
                    "x", target, "-o" + Path.GetDirectoryName(target)!);

            case VerbExtractTo when isFile:
            {
                var parent = Path.GetDirectoryName(target)!;
                var destination = Path.Combine(parent, Path.GetFileNameWithoutExtension(target));
                return Start(install.SevenZipGuiPath, parent, "x", target, "-o" + destination);
            }

            case VerbAdd:
            {
                var archive = BuildArchivePath(target);
                var workingDirectory = isDirectory
                    ? Path.GetDirectoryName(target.TrimEnd('\\')) ?? target
                    : Path.GetDirectoryName(target)!;
                return Start(install.SevenZipGuiPath, workingDirectory, "a", archive, target);
            }

            case VerbOpen:
                return Start(install.SevenZipFileManagerPath, Path.GetDirectoryName(target) ?? install.Directory, target);

            default:
                Log.Write($"未知的菜单动词：{verb}");
                return 3;
        }
    }

    /// <summary>压缩包默认放在目标同级目录，命名为「名称.7z」。</summary>
    private static string BuildArchivePath(string target)
    {
        var parent = Directory.Exists(target)
            ? Path.GetDirectoryName(target.TrimEnd('\\')) ?? target
            : Path.GetDirectoryName(target) ?? target;

        var name = Directory.Exists(target)
            ? new DirectoryInfo(target.TrimEnd('\\')).Name
            : Path.GetFileNameWithoutExtension(target);

        if (string.IsNullOrEmpty(name))
        {
            name = "archive";
        }

        var archive = Path.Combine(parent, name + ".7z");
        if (File.Exists(target) && string.Equals(archive, target, StringComparison.OrdinalIgnoreCase))
        {
            archive = Path.Combine(parent, name + ".archive.7z");
        }

        return archive;
    }

    private static SevenZipInstallation? ResolveInstallation()
    {
        var configured = SettingsStore.Load().SevenZipPath;
        return SevenZipLocator.Validate(configured) ?? SevenZipLocator.AutoDetect();
    }

    private static int Start(string exePath, string workingDirectory, params string[] arguments)
    {
        if (!File.Exists(exePath))
        {
            ShowError($"找不到 7-Zip 组件：{exePath}");
            return 2;
        }

        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Path.GetDirectoryName(exePath)!,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        Log.Write($"已启动：{exePath} {string.Join(' ', arguments)}");
        return 0;
    }

    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static void ShowError(string message)
    {
        MessageBoxW(0, message, AppInfo.DisplayName, MbIconError);
    }
}
