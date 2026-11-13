using Microsoft.Win32;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 旧版（经典）右键菜单集成：直接以注册表 verb 的方式写入 HKCU\Software\Classes，
/// 无需管理员权限，也无需 7-Zip 自带的 shell 扩展。
/// </summary>
public static class LegacyMenuService
{
    private const string MenuKeyName = "7zAIWin11RCM";
    private const string MenuTitle = "7-Zip";
    private const string AppPathValueName = "AppPath";

    private sealed record VerbDefinition(string Key, string Label, string Verb, string Target);

    private static readonly VerbDefinition[] FileVerbs =
    {
        new("01-extract-here", "解压到当前文件夹", ShellVerbHandler.VerbExtractHere, "%1"),
        new("02-extract-to", "解压到新文件夹", ShellVerbHandler.VerbExtractTo, "%1"),
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
        new("01-add", "添加到压缩包", ShellVerbHandler.VerbAdd, "%V"),
    };

    /// <summary>需要写入的注册表作用域 → 对应菜单项。</summary>
    private static readonly (string Scope, VerbDefinition[] Verbs)[] Scopes =
    {
        ("*", FileVerbs),
        ("Directory", FolderVerbs),
        ("Drive", FolderVerbs),
        ("Folder", FolderVerbs),
        (@"Directory\Background", BackgroundVerbs),
    };

    /// <summary>当前用户下是否已注册旧版菜单。</summary>
    public static bool IsInstalled()
    {
        foreach (var (scope, _) in Scopes)
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

        foreach (var (scope, verbs) in Scopes)
        {
            var parentPath = $@"Software\Classes\{scope}\shell\{MenuKeyName}";
            using var parent = Registry.CurrentUser.CreateSubKey(parentPath, writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}");

            parent.SetValue(null, MenuTitle, RegistryValueKind.String);
            parent.SetValue("MUIVerb", MenuTitle, RegistryValueKind.String);
            parent.SetValue("Icon", icon, RegistryValueKind.String);
            // 空字符串 = 使用子键 shell 作为级联菜单
            parent.SetValue("SubCommands", string.Empty, RegistryValueKind.String);
            parent.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
            parent.SetValue(AppPathValueName, appExe, RegistryValueKind.String);

            using var subShell = parent.CreateSubKey("shell", writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}\\shell");

            foreach (var verb in verbs)
            {
                using var verbKey = subShell.CreateSubKey(verb.Key, writable: true)
                    ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}\\shell\\{verb.Key}");

                verbKey.SetValue("MUIVerb", verb.Label, RegistryValueKind.String);
                verbKey.SetValue("Icon", icon, RegistryValueKind.String);

                using var commandKey = verbKey.CreateSubKey("command", writable: true)
                    ?? throw new IOException($"无法创建注册表项 HKCU\\{parentPath}\\shell\\{verb.Key}\\command");

                commandKey.SetValue(null, BuildCommandLine(appExe, verb.Verb, verb.Target), RegistryValueKind.String);
            }
        }

        Log.Write($"旧版右键菜单已写入：{sevenZipDirectory}");
    }

    /// <summary>移除旧版右键菜单。</summary>
    public static void Uninstall()
    {
        foreach (var (scope, _) in Scopes)
        {
            DeleteKeyTree($@"Software\Classes\{scope}\shell", MenuKeyName);
        }

        Log.Write("旧版右键菜单已移除");
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
