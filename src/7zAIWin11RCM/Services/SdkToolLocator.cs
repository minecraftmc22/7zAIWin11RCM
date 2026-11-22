using System.IO;
using Microsoft.Win32;

namespace SevenZipAIWin11RCM.Services;

/// <summary>一个打包工具的定位结果。</summary>
public sealed record SdkToolLocation(string FileName, string? Path, string Source)
{
    public bool Found => Path is not null;

    /// <summary>用于界面显示的简短描述。</summary>
    public string Describe() => Path is null
        ? $"未找到 {FileName}"
        : $"{FileName} → {Path}（{Source}）";
}

/// <summary>
/// 定位 Windows SDK 里的 MakeAppx.exe / signtool.exe。
///
/// 查找顺序：用户手动指定的路径 → Windows SDK 安装根（注册表）→
/// Program Files 下的 Windows Kits → App Certification Kit → PATH。
/// 每个根目录下的版本子目录按版本号倒序，x64 优先，保证优先用最新 SDK。
/// </summary>
public static class SdkToolLocator
{
    public const string MakeAppx = "MakeAppx.exe";
    public const string SignTool = "signtool.exe";

    /// <summary>按「手动指定优先、其次自动查找」解析工具路径。</summary>
    public static string? Resolve(string fileName, string? userHint)
    {
        var hinted = ResolveHint(userHint, fileName);
        if (hinted is not null)
        {
            return hinted;
        }

        foreach (var (path, _) in EnumerateCandidates(fileName))
        {
            return path;
        }

        return null;
    }

    /// <summary>解析结果连同来源一起返回，供界面显示"是自动找到的还是手动指定的"。</summary>
    public static SdkToolLocation Locate(string fileName, string? userHint)
    {
        var hinted = ResolveHint(userHint, fileName);
        if (hinted is not null)
        {
            return new SdkToolLocation(fileName, hinted, "手动指定");
        }

        var hintIgnored = !string.IsNullOrWhiteSpace(userHint);
        foreach (var (path, source) in EnumerateCandidates(fileName))
        {
            return new SdkToolLocation(fileName, path, hintIgnored ? $"{source}（手动路径无效，已自动改用）" : source);
        }

        return new SdkToolLocation(fileName, null, "未找到");
    }

    /// <summary>
    /// 校验用户填的路径。允许三种写法：exe 本身、exe 所在目录、
    /// 更上层的目录（比如只填了 Windows Kits\10，此时向下找几层）。
    /// </summary>
    public static string? ResolveHint(string? userHint, string fileName)
    {
        if (string.IsNullOrWhiteSpace(userHint))
        {
            return null;
        }

        var raw = userHint.Trim().Trim('"').Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            if (File.Exists(raw))
            {
                // 用户直接选了文件：文件名必须对得上，否则算无效
                return string.Equals(Path.GetFileName(raw), fileName, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(raw)
                    : null;
            }

            if (!Directory.Exists(raw))
            {
                return null;
            }

            var direct = Path.Combine(raw, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            // 选到了 bin、Windows Kits\10 这类上层目录：向下找几层（不整盘递归，避免卡住）
            return FindWithinDepth(raw, fileName, maxDepth: 4)
                .OrderBy(ArchRank)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>按优先级枚举所有候选路径。</summary>
    public static IEnumerable<(string Path, string Source)> EnumerateCandidates(string fileName)
    {
        foreach (var (binRoot, source) in EnumerateBinRoots())
        {
            foreach (var candidate in EnumerateInBinRoot(binRoot, fileName))
            {
                yield return (candidate, source);
            }
        }

        // App Certification Kit 里通常直接放 exe，不再分版本目录
        foreach (var (directory, source) in EnumerateFlatRoots())
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                yield return (candidate, source);
            }
        }

        foreach (var (directory, source) in EnumeratePathRoots())
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                yield return (candidate, source);
            }
        }
    }

    private static IEnumerable<string> EnumerateInBinRoot(string binRoot, string fileName)
    {
        if (!Directory.Exists(binRoot))
        {
            yield break;
        }

        // bin 下直接放文件的老版本 SDK
        var flat = Path.Combine(binRoot, fileName);
        if (File.Exists(flat))
        {
            yield return flat;
        }

        // iterator 里不能把 yield 放进 try/catch，所以目录枚举单独封装
        foreach (var version in SafeGetDirectories(binRoot)
            .OrderByDescending(ParseVersion)
            .ThenByDescending(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var arch in new[] { "x64", "x86", "arm64" })
            {
                var candidate = Path.Combine(version, arch, fileName);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static Version ParseVersion(string directory) =>
        Version.TryParse(Path.GetFileName(directory), out var version) ? version : new Version(0, 0);

    private static List<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static string[] SafeGetFiles(string directory, string fileName)
    {
        try
        {
            return Directory.GetFiles(directory, fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 在指定目录下按层（BFS）查找文件，深度受限。
    /// 用户把「Windows Kits\10」这种大目录填进来时，整树递归会卡很久。
    /// </summary>
    private static List<string> FindWithinDepth(string root, string fileName, int maxDepth)
    {
        var results = new List<string>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            results.AddRange(SafeGetFiles(directory, fileName));

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var sub in SafeGetDirectories(directory))
            {
                queue.Enqueue((sub, depth + 1));
            }
        }

        return results;
    }

    /// <summary>架构优先级：x64 → arm64 → x86 → 其它（如 App Certification Kit 这种不分架构的目录）。</summary>
    private static int ArchRank(string path)
    {
        if (path.Contains(@"\x64\", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (path.Contains(@"\arm64\", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (path.Contains(@"\x86\", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    /// <summary>SDK 的 bin 根目录（含版本子目录）。</summary>
    private static IEnumerable<(string Directory, string Source)> EnumerateBinRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, source) in EnumerateSdkRoots())
        {
            var bin = Path.Combine(root, "bin");
            if (seen.Add(bin) && Directory.Exists(bin))
            {
                yield return (bin, source);
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var (baseDir, label) in new[] { (programFilesX86, "Program Files (x86)"), (programFiles, "Program Files") })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            var bin = Path.Combine(baseDir, "Windows Kits", "10", "bin");
            if (seen.Add(bin) && Directory.Exists(bin))
            {
                yield return (bin, label);
            }

            // Windows 10 之前的 SDK 目录结构
            var legacy = Path.Combine(baseDir, "Windows Kits", "8.1", "bin");
            if (seen.Add(legacy) && Directory.Exists(legacy))
            {
                yield return (legacy, label);
            }
        }
    }

    /// <summary>SDK 安装根，来自注册表与环境变量（SDK 可以装在非系统盘）。</summary>
    private static IEnumerable<(string Root, string Source)> EnumerateSdkRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows Kits\Installed Roots",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots",
                 })
        {
            foreach (var valueName in new[] { "KitsRoot10", "KitsRoot81" })
            {
                var root = ReadRegistryString(Registry.LocalMachine, keyPath, valueName);
                if (!string.IsNullOrWhiteSpace(root) && seen.Add(root))
                {
                    yield return (root, "注册表");
                }
            }
        }

        foreach (var variable in new[] { "WindowsSdkDir", "WindowsSDK80Path" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root) && seen.Add(root))
            {
                yield return (root, $"环境变量 {variable}");
            }
        }
    }

    /// <summary>不再分层、exe 直接放在目录里的位置。</summary>
    private static IEnumerable<(string Directory, string Source)> EnumerateFlatRoots()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var (baseDir, label) in new[] { (programFilesX86, "Program Files (x86)"), (programFiles, "Program Files") })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            yield return (Path.Combine(baseDir, "Windows Kits", "10", "App Certification Kit"), label + " · App Certification Kit");
        }
    }

    private static IEnumerable<(string Directory, string Source)> EnumeratePathRoots()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length > 0)
            {
                yield return (directory, "PATH");
            }
        }
    }

    private static string? ReadRegistryString(RegistryKey hive, string keyPath, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
