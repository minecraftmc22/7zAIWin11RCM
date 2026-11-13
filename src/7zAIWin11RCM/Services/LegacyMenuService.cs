using Microsoft.Win32;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 旧版（经典）右键菜单集成。
///
/// 注册的是本程序自带的壳扩展 <c>SevenZipCascadeMenu</c>（随 shell\ 目录发布），
/// 由它在右击时激活 7-Zip 自己的菜单处理器，把 7-Zip 的**完整**条目
/// （解压 / 添加到 / 测试 / CRC SHA / 打开压缩包 …）原样嵌入「7-Zip」子菜单。
///
/// 为什么不再用注册表 verb：7-Zip 的条目是运行时按选中对象生成的
/// （「解压到 "文件夹名\"」这类名字带当前名称），注册表里没有这些动词，
/// 静态写死的菜单项无法还原。
///
/// 全部写在 HKCU 下，不需要管理员权限。
/// </summary>
public static class LegacyMenuService
{
    /// <summary>与 SevenZipAIWin11RCM.ShellExt.SevenZipCascadeMenu.ClassId 保持一致。</summary>
    private const string HandlerClassId = "6F2B7C51-9E4A-4E3D-9C2B-5A7E1F0D3C48";

    private const string HandlerKeyName = "7zAIWin11RCM";

    /// <summary>7-Zip 自带的菜单处理器键名（用于避免重复）。</summary>
    private const string BuiltInHandlerKeyName = "7-Zip";
    private const string ComHostFileName = "SevenZipAIWin11RCM.ShellExt.comhost.dll";
    private const string AppPathValueName = "AppPath";

    /// <summary>挂载菜单处理器的作用域（7-Zip 本身也注册了这几处）。</summary>
    private static readonly string[] HandlerScopes =
    {
        "*",
        "Directory",
        "Drive",
        "Folder",
    };

    /// <summary>旧版本用过的注册表 verb 键与命令集，仅用于清理。</summary>
    private static readonly string[] ObsoleteCommandKeys = { "7zAIWin11RCM.File", "7zAIWin11RCM.Folder", "7zAIWin11RCM.Background" };

    /// <summary>壳扩展 DLL 的完整路径。</summary>
    public static string HandlerDllPath => Path.Combine(AppInfo.AppDirectory, "shell", ComHostFileName);

    /// <summary>本程序是否具备旧版菜单所需的壳扩展文件。</summary>
    public static bool IsHandlerAvailable => File.Exists(HandlerDllPath);

    /// <summary>当前用户下是否已注册旧版菜单。</summary>
    public static bool IsInstalled()
    {
        foreach (var scope in HandlerScopes)
        {
            if (KeyExists($@"Software\Classes\{scope}\shellex\ContextMenuHandlers\{HandlerKeyName}"))
            {
                return true;
            }
        }

        return KeyExists($@"Software\Classes\Directory\Background\shell\{HandlerKeyName}");
    }

    /// <summary>已注册的壳扩展 DLL 路径（用于发现"程序被移动过"的情况）。</summary>
    public static string? GetRegisteredAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{HandlerClassId}}}\InprocServer32");
            return key?.GetValue(null) as string;
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

    /// <summary>注册旧版右键菜单。可重复调用（覆盖式写入）。</summary>
    public static void Install(string sevenZipDirectory)
    {
        var dllPath = HandlerDllPath;
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"缺少壳扩展文件：{dllPath}", dllPath);
        }

        // 1) 注册 COM 类（每用户）。comhost.dll 内嵌了 CLSID → 类的映射，
        //    所以这里只需要指向它并声明线程模型。
        var clsidPath = $@"Software\Classes\CLSID\{{{HandlerClassId}}}";
        using (var clsidKey = Registry.CurrentUser.CreateSubKey(clsidPath, writable: true)
            ?? throw new IOException($"无法创建注册表项 HKCU\\{clsidPath}"))
        {
            clsidKey.SetValue(null, "7-Zip 右键菜单（7zAIWin11RCM）", RegistryValueKind.String);
            clsidKey.SetValue("AppPath", AppInfo.ExecutablePath, RegistryValueKind.String);

            using var inproc = clsidKey.CreateSubKey("InprocServer32", writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{clsidPath}\\InprocServer32");

            inproc.SetValue(null, dllPath, RegistryValueKind.String);
            inproc.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
        }

        // 2) 在各作用域挂上处理器
        foreach (var scope in HandlerScopes)
        {
            var path = $@"Software\Classes\{scope}\shellex\ContextMenuHandlers\{HandlerKeyName}";
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
                ?? throw new IOException($"无法创建注册表项 HKCU\\{path}");

            key.SetValue(null, $"{{{HandlerClassId}}}", RegistryValueKind.String);
        }

        // 3) 清掉旧版本用过的 verb 写法，避免菜单里出现重复项
        RemoveObsoleteEntries();

        // 4) 屏蔽 7-Zip 自带的同名处理器
        SuppressBuiltInHandler();

        // 5) 文件夹空白处：7-Zip 自己在这里没有条目，保留单条「添加到压缩包」
        WriteBackgroundVerb(sevenZipDirectory);

        Log.Write($"旧版右键菜单已注册：{dllPath}（7-Zip：{sevenZipDirectory}）");
    }

    /// <summary>
    /// 7-Zip 自己也在 <c>*</c> / <c>Directory</c> / <c>Folder</c> 下注册了同名的
    /// <c>shellex\ContextMenuHandlers\7-Zip</c>，不屏蔽的话右键会出现两个「7-Zip」子菜单。
    /// 这里在当前用户下把它的默认值覆盖成空串（HKCU 优先于 HKLM），
    /// 卸载时删掉这几个键，7-Zip 原来的菜单就回来了。
    /// </summary>
    private static void SuppressBuiltInHandler()
    {
        foreach (var scope in HandlerScopes)
        {
            var path = $@"Software\Classes\{scope}\shellex\ContextMenuHandlers\{BuiltInHandlerKeyName}";
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key?.SetValue(null, string.Empty, RegistryValueKind.String);
        }
    }

    private static void RestoreBuiltInHandler()
    {
        foreach (var scope in HandlerScopes)
        {
            DeleteKeyTree($@"Software\Classes\{scope}\shellex\ContextMenuHandlers", BuiltInHandlerKeyName);
        }
    }

    /// <summary>
    /// 文件夹空白处的单条命令。
    /// 实测把空白处交给 7-Zip 的处理器会返回 0 个条目（它没有背景菜单），
    /// 所以这里仍旧直接挂一条命令。
    /// </summary>
    private static void WriteBackgroundVerb(string sevenZipDirectory)
    {
        const string title = "7-Zip 添加到压缩包";

        var installDirectory = Path.GetFullPath(sevenZipDirectory.Trim().Trim('"'));
        var icon = Path.Combine(installDirectory, "7zFM.exe");
        var path = $@"Software\Classes\Directory\Background\shell\{HandlerKeyName}";

        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new IOException($"无法创建注册表项 HKCU\\{path}");

        key.SetValue(null, title, RegistryValueKind.String);
        key.SetValue("MUIVerb", title, RegistryValueKind.String);
        key.SetValue("Icon", icon, RegistryValueKind.String);

        using var command = key.CreateSubKey("command", writable: true)
            ?? throw new IOException($"无法创建注册表项 HKCU\\{path}\\command");

        command.SetValue(null, $"\"{AppInfo.ExecutablePath}\" --verb {ShellVerbHandler.VerbAdd} \"%V\"",
            RegistryValueKind.String);
    }

    /// <summary>移除旧版右键菜单（含旧版本留下的其它写法）。</summary>
    public static void Uninstall()
    {
        foreach (var scope in HandlerScopes)
        {
            DeleteKeyTree($@"Software\Classes\{scope}\shellex\ContextMenuHandlers", HandlerKeyName);
        }

        DeleteKeyTree(@"Software\Classes\CLSID", $"{{{HandlerClassId}}}");

        RemoveObsoleteEntries();
        RestoreBuiltInHandler();

        Log.Write("旧版右键菜单已移除");
    }

    /// <summary>清理 v1.0.4 及更早版本写下的注册表 verb 与命令集。</summary>
    private static void RemoveObsoleteEntries()
    {
        foreach (var scope in new[] { "*", "Directory", "Drive", "Folder", @"Directory\Background" })
        {
            DeleteKeyTree($@"Software\Classes\{scope}\shell", HandlerKeyName);
        }

        foreach (var commandsKey in ObsoleteCommandKeys)
        {
            DeleteKeyTree(@"Software\Classes", commandsKey);
        }
    }

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
