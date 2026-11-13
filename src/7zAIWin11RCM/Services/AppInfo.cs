using System.Reflection;

namespace SevenZipAIWin11RCM.Services;

/// <summary>应用元信息（关于页、窗口标题、注册表写入共用）。</summary>
public static class AppInfo
{
    public const string DisplayName = "7-Zip 右键菜单助手";
    public const string Author = "Minecraftmc22";
    public const string GitHubUrl = "https://github.com/Minecraftmc22";
    public const string BlogUrl = "https://minecraftmc22.top";
    public const string RepositoryUrl = "https://github.com/Minecraftmc22/7zAIWin11RCM";
    public const string RepositoryCloneUrl = "https://github.com/Minecraftmc22/7zAIWin11RCM.git";
    public const string License = "MIT License";

    /// <summary>形如 1.0.0。</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(AppInfo).Assembly;
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    /// <summary>当前进程可执行文件路径。</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "7zAIWin11RCM.exe");

    public static string AppDirectory => AppContext.BaseDirectory;
}
