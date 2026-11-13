using System.Diagnostics;
using Microsoft.Win32;

namespace SevenZipAIWin11RCM.Services;

/// <summary>一次 7-Zip 检测结果。</summary>
/// <param name="Directory">安装目录。</param>
/// <param name="Version">7-Zip 版本号（取不到时为空字符串）。</param>
/// <param name="Source">发现来源，例如"注册表"、"Program Files"。</param>
public sealed record SevenZipInstallation(string Directory, string Version, string Source)
{
    public string SevenZipGuiPath => Path.Combine(Directory, "7zG.exe");

    public string SevenZipFileManagerPath => Path.Combine(Directory, "7zFM.exe");

    public string Summary =>
        string.IsNullOrEmpty(Version) ? Directory : $"{Directory}（7-Zip {Version}）";
}

/// <summary>自动查找 / 校验 7-Zip 安装目录。</summary>
public static class SevenZipLocator
{
    /// <summary>判定一个目录"是不是 7-Zip 安装目录"所必须存在的文件。</summary>
    private static readonly string[] RequiredFiles = { "7zG.exe", "7zFM.exe" };

    /// <summary>按"注册表 → 常见安装目录 → PATH"的顺序自动查找。</summary>
    public static SevenZipInstallation? AutoDetect()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            var found = Validate(candidate.Directory);
            if (found is not null)
            {
                return found with { Source = candidate.Source };
            }
        }

        return null;
    }

    /// <summary>校验用户给定的目录（也允许直接传入 7zG.exe / 7zFM.exe 的路径）。</summary>
    public static SevenZipInstallation? Validate(string? directoryOrExePath)
    {
        var directory = NormalizeDirectory(directoryOrExePath);
        if (directory is null)
        {
            return null;
        }

        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                return null;
            }
        }

        return new SevenZipInstallation(directory, ReadVersion(directory), string.Empty);
    }

    /// <summary>把用户输入（可能带引号、可能是 exe 路径）规整成目录。</summary>
    public static string? NormalizeDirectory(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var path = input.Trim().Trim('"');
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(path))
            {
                path = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
            }

            path = Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static string ReadVersion(string directory)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "7zFM.exe"));
            var version = info.ProductVersion ?? info.FileVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                // 7-Zip 的文件版本形如 25.01，偶尔带多余空格
                return version.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("读取 7-Zip 版本失败", ex);
        }

        return string.Empty;
    }

    private static IEnumerable<(string Directory, string Source)> EnumerateCandidates()
    {
        foreach (var item in FromRegistry())
        {
            yield return item;
        }

        var programFiles = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        foreach (var root in programFiles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return (Path.Combine(root!, "7-Zip"), "Program Files");
        }

        // 便携版：和应用放在一起
        yield return (Path.Combine(AppInfo.AppDirectory, "7-Zip"), "应用目录");

        foreach (var item in FromPathVariable())
        {
            yield return item;
        }
    }

    private static IEnumerable<(string Directory, string Source)> FromRegistry()
    {
        var hives = new[] { (RegistryHive.LocalMachine, "注册表"), (RegistryHive.CurrentUser, "注册表(用户)") };
        var views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Registry32 };

        foreach (var (hive, source) in hives)
        {
            foreach (var view in views)
            {
                foreach (var valueName in new[] { "Path64", "Path", "Path32" })
                {
                    var value = ReadRegistryValue(hive, view, @"SOFTWARE\7-Zip", valueName);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        yield return (value!, source);
                    }
                }

                var installLocation = ReadRegistryValue(
                    hive, view,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
                    "InstallLocation");
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    yield return (installLocation!, source);
                }
            }
        }
    }

    private static string? ReadRegistryValue(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Directory, string Source)> FromPathVariable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            yield return (trimmed, "PATH 环境变量");
        }
    }
}
