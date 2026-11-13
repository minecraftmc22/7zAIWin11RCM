using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SevenZipAIWin11RCM.ShellExt;

/// <summary>在扩展侧独立定位 7-Zip（扩展由 Explorer 加载，无法依赖主程序的内存状态）。</summary>
internal static class SevenZipLocator
{
    public static string? FindDirectory()
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var directory = candidate.Trim().Trim('"').TrimEnd('\\');
                if (File.Exists(Path.Combine(directory, "7zG.exe"))
                    && File.Exists(Path.Combine(directory, "7zFM.exe")))
                {
                    return directory;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                // 忽略单个候选目录的异常
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return ReadValue(@"SOFTWARE\7-Zip", "Path64");
        yield return ReadValue(@"SOFTWARE\7-Zip", "Path");
        yield return ReadValue(@"SOFTWARE\WOW6432Node\7-Zip", "Path");

        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432")
                           ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "7-Zip");
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip");
    }

    private static string? ReadValue(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>三个菜单项的公共实现。</summary>
[ComVisible(false)]
public abstract class ExplorerCommandBase : IExplorerCommand
{
    /// <summary>菜单显示文本。</summary>
    protected abstract string Title { get; }

    /// <summary>执行命令；返回 false 表示无法执行（例如没装 7-Zip）。</summary>
    protected abstract bool Execute(string path);

    public int GetTitle(IShellItemArray psiItemArray, out IntPtr ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni(Title);
        return ShellInterop.S_OK;
    }

    public int GetIcon(IShellItemArray psiItemArray, out IntPtr ppszIcon)
    {
        var directory = SevenZipLocator.FindDirectory();
        if (directory is null)
        {
            ppszIcon = IntPtr.Zero;
            return ShellInterop.E_FAIL;
        }

        ppszIcon = Marshal.StringToCoTaskMemUni(Path.Combine(directory, "7zFM.exe"));
        return ShellInterop.S_OK;
    }

    public int GetToolTip(IShellItemArray psiItemArray, out IntPtr ppszInfotip)
    {
        ppszInfotip = IntPtr.Zero;
        return ShellInterop.E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;
        return ShellInterop.S_OK;
    }

    public int GetState(IShellItemArray psiItemArray, int fOkToBeSlow, out int pCmdState)
    {
        pCmdState = ShellInterop.EcsEnabled;
        return ShellInterop.S_OK;
    }

    public int Invoke(IShellItemArray psiItemArray, IntPtr pbc)
    {
        try
        {
            var path = GetFirstPath(psiItemArray);
            if (string.IsNullOrEmpty(path))
            {
                return ShellInterop.E_INVALIDARG;
            }

            return Execute(path) ? ShellInterop.S_OK : ShellInterop.E_FAIL;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("7zAIWin11RCM: Invoke failed - " + ex);
            return ShellInterop.E_FAIL;
        }
    }

    public int GetFlags(out int pFlags)
    {
        pFlags = ShellInterop.EcfDefault;
        return ShellInterop.S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return ShellInterop.E_NOTIMPL;
    }

    private static string? GetFirstPath(IShellItemArray? itemArray)
    {
        if (itemArray is null)
        {
            return null;
        }

        if (itemArray.GetCount(out var count) != ShellInterop.S_OK || count == 0)
        {
            return null;
        }

        if (itemArray.GetItemAt(0, out var item) != ShellInterop.S_OK || item is null)
        {
            return null;
        }

        var path = ReadDisplayName(item);
        ReleaseComObject(item);
        return path;
    }

    private static string? ReadDisplayName(IShellItem item)
    {
        if (item.GetDisplayName(ShellInterop.SigdnFileSysPath, out var pointer) != ShellInterop.S_OK)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static void ReleaseComObject(object comObject)
    {
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // 释放失败不影响菜单行为
        }
    }

    protected static bool StartSevenZipGui(string workingDirectory, params string[] arguments)
    {
        var directory = SevenZipLocator.FindDirectory();
        if (directory is null)
        {
            return false;
        }

        var info = new ProcessStartInfo(Path.Combine(directory, "7zG.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info)?.Dispose();
        return true;
    }
}

[ComVisible(true)]
[Guid(CommandIds.ExtractHere)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractHereCommand : ExplorerCommandBase
{
    protected override string Title => "解压到当前文件夹";

    protected override bool Execute(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(path);
        return parent is not null && StartSevenZipGui(parent, "x", path, "-o" + parent);
    }
}

[ComVisible(true)]
[Guid(CommandIds.ExtractTo)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractToCommand : ExplorerCommandBase
{
    protected override string Title => "解压到新文件夹";

    protected override bool Execute(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return false;
        }

        var destination = Path.Combine(parent, Path.GetFileNameWithoutExtension(path));
        return StartSevenZipGui(parent, "x", path, "-o" + destination);
    }
}

[ComVisible(true)]
[Guid(CommandIds.AddToArchive)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class AddToArchiveCommand : ExplorerCommandBase
{
    protected override string Title => "添加到压缩包";

    protected override bool Execute(string path)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return false;
        }

        var trimmed = path.TrimEnd('\\');
        var parent = isDirectory ? Path.GetDirectoryName(trimmed) : Path.GetDirectoryName(path);
        if (parent is null)
        {
            return false;
        }

        var name = isDirectory ? new DirectoryInfo(trimmed).Name : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
        {
            name = "archive";
        }

        var archive = Path.Combine(parent, name + ".7z");
        if (File.Exists(path) && string.Equals(archive, path, StringComparison.OrdinalIgnoreCase))
        {
            archive = Path.Combine(parent, name + ".archive.7z");
        }

        return StartSevenZipGui(parent, "a", archive, path);
    }
}

/// <summary>与 packaging\AppxManifest.xml 中的 CLSID 一一对应。</summary>
internal static class CommandIds
{
    public const string ExtractHere = "6F3A1B20-7C4D-4E71-9A55-0D1B2C3E4F01";
    public const string ExtractTo = "6F3A1B21-7C4D-4E71-9A55-0D1B2C3E4F02";
    public const string AddToArchive = "6F3A1B22-7C4D-4E71-9A55-0D1B2C3E4F03";
}
