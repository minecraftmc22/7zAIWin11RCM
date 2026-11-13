using Microsoft.Win32;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 旧版（经典）右键菜单集成：直接以注册表 verb 的方式写入 HKCU\Software\Classes，
/// 无需管理员权限，也无需 7-Zip 自带的 shell 扩展。
///
/// 级联子菜单统一走 <c>ExtendedSubCommandsKey</c>：把子项放在 Classes 根下的独立键里，
/// 父项只引用它。实测（Windows 11 26200）用空 <c>SubCommands</c> 或显式动词列表
/// 都会渲染出一个空菜单，只有 ExtendedSubCommandsKey 能正确列出子项。
/// </summary>
public static class LegacyMenuService
{
    private const string MenuKeyName = "7zAIWin11RCM";
    private const string MenuTitle = "7-Zip";
    private const string AppPathValueName = "AppPath";

    /// <summary>存放子菜单动词的独立键（Classes 根下），按作用域区分子项集合。</summary>
    private const string FileCommandsKey = "7zAIWin11RCM.File";
    private const string FolderCommandsKey = "7zAIWin11RCM.Folder";

    /// <summary>旧版本用过的背景命令集键，仅用于清理。</summary>
    private const string ObsoleteBackgroundCommandsKey = "7zAIWin11RCM.Background";

    /// <summary>文件夹空白处的菜单标题（该作用域不支持级联子菜单，直接挂命令）。</summary>
    private const string BackgroundTitle = "7-Zip 添加到压缩包";

    private sealed record VerbDefinition(string Key, string Label, string Verb, string Target);

    private static readonly VerbDefinition[] FileVerbs =
    {
        new("01-extract-here", "解压到当前文件夹", ShellVerbHandler.VerbExtractHere, "%1"),
        new("02-extract-to", "解压到 文件夹名\\", ShellVerbHandler.VerbExtractTo, "%1"),
        new("03-add", "添加到压缩包", ShellVerbHandler.VerbAdd, "%1"),
        new("04-open", "用 7-Zip 打开", ShellVerbHandler.VerbOpen, "%1"),
    };

    private static readonly VerbDefinition[] FolderVerbs =
    {
        new("01-add", "添加到压缩包", ShellVerbHandler.VerbAdd, "%1"),
        new("02-open", "用 7-Zip 打开", ShellVerbHandler.VerbOpen, "%1"),
    };

    private static readonly VerbDefinition[] BackgroundVerbs =
    {
        new("01-add", BackgroundTitle, ShellVerbHandler.VerbAdd, "%V"),
    };

    /// <summary>注册表作用域 → 父项位置 + 子项集合所在的独立键（null 表示直接挂命令）。</summary>
    private static readonly (string Scope, VerbDefinition[] Verbs, string? CommandsKey)[] Scopes =
    {
        ("*", FileVerbs, FileCommandsKey),
        ("Directory", FolderVerbs, FolderCommandsKey),
        ("Drive", FolderVerbs, FolderCommandsKey),
        ("Folder", FolderVerbs, FolderCommandsKey),
        // 实测（Windows 11 26200）文件夹空白处的菜单不解析级联子项，只能用单条命令
        (@"Directory\Background", BackgroundVerbs, null),
    };

    private static readonly string[] CommandKeys = { FileCommandsKey, FolderCommandsKey, ObsoleteBackgroundCommandsKey };

    /// <summary>当前用户下是否已注册旧版菜单。</summary>
    public static bool IsInstalled()
    {
        foreach (var (scope, _, _) in Scopes)
        {
            if (KeyExists($@"Software\Classes\{scope}\shell\{MenuKeyName}"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>已注册的菜单项指向的可执行文件（用于发现"程序被移动过"的情况）。</summary>
    public static string? GetRegisteredAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{MenuKeyName}");
            return key?.GetValue(AppPathValueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static void Apply(string sevenZipDirectory, bool enabled)
    {
        if (enabled)
        {
            Install(sevenZipDirectory);
        }
        else
        {
            Uninstall();
        }
    }

    /// <summary>写入旧版右键菜单。可重复调用（覆盖式写入）。</summary>
    public static void Install(string sevenZipDirectory)
    {
        var appExe = AppInfo.ExecutablePath;
        var installDirectory = Path.GetFullPath(sevenZipDirectory.Trim().Trim('"'));
        var icon = Path.Combine(installDirectory, "7zFM.exe");

        // 1) 先写子菜单动词（每个集合一次），供父项通过 ExtendedSubCommandsKey 引用
        WriteCommandSet(FileCommandsKey, FileVerbs, appExe, icon);
        WriteCommandSet(FolderCommandsKey, FolderVerbs, appExe, icon);

        // 2) 再在各作用域下挂父项
        foreach (var (scope, verbs, commandsKey) in Scopes)
        {
            var parentPath = $@"Software\Classes\{scope}\shell\{MenuKeyName}";
            using var parent = Registry.CurrentUser.CreateSubKey(parentPath, writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}");

            var title = commandsKey is null ? BackgroundTitle : MenuTitle;

            parent.SetValue(null, title, RegistryValueKind.String);
            parent.SetValue("MUIVerb", title, RegistryValueKind.String);
            parent.SetValue("Icon", icon, RegistryValueKind.String);
            parent.SetValue(AppPathValueName, appExe, RegistryValueKind.String);

            // 清掉旧版本用过的写法，否则会叠加出重复/空白菜单
            parent.DeleteValue("SubCommands", throwOnMissingValue: false);
            parent.DeleteSubKeyTree("shell", throwOnMissingSubKey: false);

            if (commandsKey is not null)
            {
                parent.SetValue("ExtendedSubCommandsKey", commandsKey, RegistryValueKind.String);
                parent.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
                parent.DeleteSubKeyTree("command", throwOnMissingSubKey: false);
                continue;
            }

            // 不支持级联的作用域：直接把唯一动作挂成命令
            parent.DeleteValue("ExtendedSubCommandsKey", throwOnMissingValue: false);
            parent.DeleteValue("MultiSelectModel", throwOnMissingValue: false);

            var verb = verbs[0];
            parent.SetValue("VerbName", verb.Key, RegistryValueKind.String);
            using var commandKey = parent.CreateSubKey("command", writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}\\command");

            commandKey.SetValue(null, BuildCommandLine(appExe, verb.Verb, verb.Target), RegistryValueKind.String);
        }

        Log.Write($"旧版右键菜单已写入：{installDirectory}");
    }

    /// <summary>移除旧版右键菜单（含旧版本留下的其它写法）。</summary>
    public static void Uninstall()
    {
        foreach (var (scope, _, _) in Scopes)
        {
            DeleteKeyTree($@"Software\Classes\{scope}\shell", MenuKeyName);
        }

        foreach (var commandsKey in CommandKeys)
        {
            DeleteKeyTree(@"Software\Classes", commandsKey);
        }

        Log.Write("旧版右键菜单已移除");
    }

    private static void WriteCommandSet(string commandsKey, VerbDefinition[] verbs, string appExe, string icon)
    {
        var path = $@"Software\Classes\{commandsKey}\shell";
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{commandsKey}", throwOnMissingSubKey: false);

        using var shellKey = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new IOException($"无法创建注册表项 HKCU\\{path}");

        foreach (var verb in verbs)
        {
            using var verbKey = shellKey.CreateSubKey(verb.Key, writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{path}\\{verb.Key}");

            verbKey.SetValue("MUIVerb", verb.Label, RegistryValueKind.String);
            verbKey.SetValue("Icon", icon, RegistryValueKind.String);

            using var commandKey = verbKey.CreateSubKey("command", writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{path}\\{verb.Key}\\command");

            commandKey.SetValue(null, BuildCommandLine(appExe, verb.Verb, verb.Target), RegistryValueKind.String);
        }
    }

    private static string BuildCommandLine(string appExe, string verb, string target) =>
        $"\"{appExe}\" --verb {verb} \"{target}\"";

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void DeleteKeyTree(string parentPath, string keyName)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
            if (parent?.OpenSubKey(keyName) is null)
            {
                return;
            }

            parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"删除注册表项失败：HKCU\\{parentPath}\\{keyName}", ex);
            throw;
        }
    }
}
