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

/// <summary>菜单树上的一项（文本、命令 id、可选子项）。</summary>
public sealed record MenuNode(string Text, int Id, IReadOnlyList<MenuNode> Children)
{
    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// 把 7-Zip 自己的菜单「借」过来。
///
/// 新版（紧凑）菜单只允许本应用包内的 IExplorerCommand，无法直接调用 7-Zip 的
/// 现代扩展；但我们可以像旧版菜单那样，创建一个隐藏的弹出菜单，
/// 让 7-Zip 的经典处理器（7-zip.dll）把条目填进去，再逐项读出文本与命令 id。
/// 这样条目、顺序、图标、动态命名都与 7-Zip 原生菜单一致。
/// </summary>
internal static class SevenZipMenuBridge
{
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid SevenZipHandlerFallback = new("23170F69-40C1-278A-1000-000100020000");

    private const int MfByPosition = 0x00000400;

    /// <summary>读取指定选中对象对应的菜单项。</summary>
    public static List<MenuNode> ReadMenu(IReadOnlyList<string> paths)
    {
        var result = new List<MenuNode>();
        if (paths.Count == 0)
        {
            return result;
        }

        using var session = Open(paths);
        if (session is null)
        {
            return result;
        }

        var menu = session.Menu;
        // 7-Zip 会把自己的条目放进一个「7-Zip」子菜单里，取那一层
        var nested = GetSubMenu(menu, 0);
        return ReadItems(nested != IntPtr.Zero ? nested : menu);
    }

    /// <summary>执行菜单项：重建同样的菜单，然后按命令 id 转交给 7-Zip。</summary>
    public static bool Invoke(IReadOnlyList<string> paths, int id)
    {
        using var session = Open(paths);
        if (session?.ContextMenu is null)
        {
            return false;
        }

        var contextMenu = session.ContextMenu;

        var info = new CMINVOKECOMMANDINFO
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            fMask = 0,
            hwnd = IntPtr.Zero,
            lpVerb = (IntPtr)id,
            lpParameters = IntPtr.Zero,
            lpDirectory = IntPtr.Zero,
            nShow = 1,
            dwHotKey = 0,
            hIcon = IntPtr.Zero,
        };

        var buffer = Marshal.AllocHGlobal(info.cbSize);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            return contextMenu.InvokeCommand(buffer) >= 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<MenuNode> ReadItems(IntPtr menu)
    {
        var nodes = new List<MenuNode>();
        var count = GetMenuItemCount(menu);
        var buffer = Marshal.AllocHGlobal(1024);

        try
        {
            for (var i = 0; i < count; i++)
            {
                var info = new MENUITEMINFO
                {
                    cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = 0x00000040 | 0x00000004 | 0x00000100 | 0x00000002, // STRING|SUBMENU|FTYPE|ID
                    dwTypeData = buffer,
                    cch = 511,
                };

                if (!GetMenuItemInfo(menu, (uint)i, true, ref info))
                {
                    continue;
                }

                var text = Marshal.PtrToStringUni(buffer) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 分隔线（7-Zip 用空文本项），新版菜单里用不着，跳过
                    continue;
                }

                var sub = GetSubMenu(menu, i);
                var children = sub != IntPtr.Zero ? ReadItems(sub) : new List<MenuNode>();
                nodes.Add(new MenuNode(text, (int)info.wID, children));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return nodes;
    }

    private static MenuSession? Open(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        var folderPath = Directory.Exists(paths[0])
            ? paths[0]
            : Path.GetDirectoryName(paths[0]);

        if (string.IsNullOrEmpty(folderPath))
        {
            return null;
        }

        var session = new MenuSession();
        try
        {
            if (SHParseDisplayName(folderPath, IntPtr.Zero, out var folderPidl, 0, out _) != 0)
            {
                session.Dispose();
                return null;
            }

            session.FolderPidl = folderPidl;

            // 取 IDataObject 必须走父文件夹的 IShellFolder + 子 PIDL：
            // SHCreateDataObject 要的是相对父项的子 PIDL，喂绝对 PIDL 会返回 E_INVALIDARG。
            var absolute = new List<IntPtr>();
            var children = new List<IntPtr>();
            IShellFolder? folder = null;
            IntPtr folderPtr = IntPtr.Zero;

            try
            {
                foreach (var path in paths)
                {
                    if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0)
                    {
                        continue;
                    }

                    absolute.Add(pidl);

                    var iidFolder = IidShellFolder;
                    if (SHBindToParent(pidl, ref iidFolder, out var parentPtr, out var child) != 0)
                    {
                        continue;
                    }

                    if (folder is null)
                    {
                        folder = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                        folderPtr = parentPtr;
                    }
                    else
                    {
                        Marshal.Release(parentPtr);
                    }

                    children.Add(child);
                }

                if (folder is null || children.Count == 0)
                {
                    session.Dispose();
                    return null;
                }

                var array = Marshal.AllocHGlobal(IntPtr.Size * children.Count);
                for (var i = 0; i < children.Count; i++)
                {
                    Marshal.WriteIntPtr(array, i * IntPtr.Size, children[i]);
                }

                try
                {
                    var iidObject = IidDataObject;
                    if (folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Count, array, ref iidObject,
                            IntPtr.Zero, out var dataObject) != 0)
                    {
                        session.Dispose();
                        return null;
                    }

                    session.DataObject = dataObject;
                }
                finally
                {
                    Marshal.FreeHGlobal(array);
                }
            }
            finally
            {
                if (folderPtr != IntPtr.Zero)
                {
                    Marshal.Release(folderPtr);
                }

                foreach (var pidl in absolute)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            var type = Type.GetTypeFromCLSID(SevenZipHandlerClsid);
            if (type is null)
            {
                session.Dispose();
                return null;
            }

            var instance = Activator.CreateInstance(type);
            if (instance is not IShellExtInit init || instance is not IContextMenu contextMenu)
            {
                session.Dispose();
                return null;
            }

            init.Initialize(folderPidl, session.DataObject, IntPtr.Zero);

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                session.Dispose();
                return null;
            }

            session.Menu = menu;
            session.ContextMenu = contextMenu;
            contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, 0);

            return session;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("7zAIWin11RCM: 构建 7-Zip 菜单失败 - " + ex);
            session.Dispose();
            return null;
        }
    }

    private static Guid SevenZipHandlerClsid
    {
        get
        {
            foreach (var scope in new[] { "*", "Directory", "Folder" })
            {
                try
                {
                    using var key = Registry.ClassesRoot.OpenSubKey(
                        $@"{scope}\shellex\ContextMenuHandlers\7-Zip");
                    var raw = key?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(raw)
                        && Guid.TryParse(raw.Trim().Trim('{', '}'), out var guid))
                    {
                        return guid;
                    }
                }
                catch (Exception)
                {
                    // 读不到就用已知 CLSID
                }
            }

            return SevenZipHandlerFallback;
        }
    }

    private sealed class MenuSession : IDisposable
    {
        public IntPtr FolderPidl { get; set; }
        public IntPtr DataObject { get; set; }
        public IntPtr Menu { get; set; }
        public IContextMenu? ContextMenu { get; set; }

        public void Dispose()
        {
            if (Menu != IntPtr.Zero)
            {
                DestroyMenu(Menu);
                Menu = IntPtr.Zero;
            }

            if (DataObject != IntPtr.Zero)
            {
                Marshal.Release(DataObject);
                DataObject = IntPtr.Zero;
            }

            if (FolderPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(FolderPidl);
                FolderPidl = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
}

/// <summary>父文件夹接口，只用到 GetUIObjectOf（vtable 顺序必须与原生一致）。</summary>
[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

    [PreserveSig]
    int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

    [PreserveSig]
    int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

    [PreserveSig]
    int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

    [PreserveSig]
    int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

    [PreserveSig]
    int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr lpName);

    [PreserveSig]
    int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags,
        out IntPtr ppidlOut);
}

/// <summary>新版右键菜单的根项：一个「7-Zip」子菜单，下面挂着 7-Zip 的完整条目。</summary>
[ComVisible(true)]
[Guid(RootClassId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SevenZipMenuRoot : IExplorerCommand
{
    public const string RootClassId = "6F3A1B30-7C4D-4E71-9A55-0D1B2C3E4F10";

    private string[] _paths = Array.Empty<string>();
    private List<MenuNode> _nodes = new();

    public int GetTitle(IShellItemArray psiItemArray, out IntPtr ppszName)
    {
        // 顺手把选中项记下来，供 EnumSubCommands 使用（它拿不到 IShellItemArray）
        try
        {
            var paths = ShellPaths.Read(psiItemArray);
            if (paths.Length > 0)
            {
                _paths = paths;
            }
        }
        catch (Exception)
        {
            // 读不到就沿用上次的结果
        }

        ppszName = Marshal.StringToCoTaskMemUni("7-Zip");
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
        try
        {
            _paths = ShellPaths.Read(psiItemArray);
            _nodes = SevenZipMenuBridge.ReadMenu(_paths);

            // 没有可用条目（例如没装 7-Zip）就不要显示一个空菜单
            pCmdState = _nodes.Count == 0 ? ShellInterop.EcsHidden : ShellInterop.EcsEnabled;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("7zAIWin11RCM: GetState 失败 - " + ex);
            pCmdState = ShellInterop.EcsHidden;
        }

        return ShellInterop.S_OK;
    }

    public int Invoke(IShellItemArray psiItemArray, IntPtr pbc) => ShellInterop.E_FAIL;

    public int GetFlags(out int pFlags)
    {
        pFlags = ShellInterop.EcfHasSubCommands;
        return ShellInterop.S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        // 万一 shell 没先调 GetState，这里补一次
        if (_nodes.Count == 0 && _paths.Length > 0)
        {
            _nodes = SevenZipMenuBridge.ReadMenu(_paths);
        }

        ppEnum = MenuEnumerator.Create(_nodes, _paths);
        return ppEnum == IntPtr.Zero ? ShellInterop.E_FAIL : ShellInterop.S_OK;
    }
}

/// <summary>菜单树上的一个命令。由枚举器直接创建，不需要单独的 CLSID 注册。</summary>
[ComVisible(true)]
[Guid("6F3A1B31-7C4D-4E71-9A55-0D1B2C3E4F11")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SevenZipMenuItem : IExplorerCommand
{
    private readonly MenuNode _node;
    private readonly string[] _paths;

    public SevenZipMenuItem(MenuNode node, string[] paths)
    {
        _node = node;
        _paths = paths;
    }

    public int GetTitle(IShellItemArray psiItemArray, out IntPtr ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni(_node.Text);
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
            return SevenZipMenuBridge.Invoke(_paths, _node.Id)
                ? ShellInterop.S_OK
                : ShellInterop.E_FAIL;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("7zAIWin11RCM: Invoke 失败 - " + ex);
            return ShellInterop.E_FAIL;
        }
    }

    public int GetFlags(out int pFlags)
    {
        pFlags = _node.HasChildren ? ShellInterop.EcfHasSubCommands : ShellInterop.EcfDefault;
        return ShellInterop.S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ppEnum = _node.HasChildren
            ? MenuEnumerator.Create(_node.Children, _paths)
            : IntPtr.Zero;

        return ppEnum == IntPtr.Zero ? ShellInterop.E_NOTIMPL : ShellInterop.S_OK;
    }
}

/// <summary>返回一组 IExplorerCommand。</summary>
[ComVisible(true)]
[Guid("6F3A1B32-7C4D-4E71-9A55-0D1B2C3E4F12")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class MenuEnumerator : IEnumExplorerCommand
{
    private readonly List<MenuNode> _nodes;
    private readonly string[] _paths;
    private int _index;

    private MenuEnumerator(List<MenuNode> nodes, string[] paths)
    {
        _nodes = nodes;
        _paths = paths;
    }

    public static IntPtr Create(IReadOnlyList<MenuNode> nodes, string[] paths)
    {
        if (nodes.Count == 0)
        {
            return IntPtr.Zero;
        }

        var enumerator = new MenuEnumerator(nodes.ToList(), paths);
        return Marshal.GetComInterfaceForObject(enumerator, typeof(IEnumExplorerCommand));
    }

    public int Next(uint celt, out IExplorerCommand pUICommand, out uint pCeltFetched)
    {
        if (_index >= _nodes.Count)
        {
            pUICommand = null!;
            pCeltFetched = 0;
            return ShellInterop.S_FALSE;
        }

        pUICommand = new SevenZipMenuItem(_nodes[_index], _paths);
        _index++;
        pCeltFetched = 1;
        return ShellInterop.S_OK;
    }

    public int Skip(uint celt)
    {
        _index = Math.Min(_index + (int)celt, _nodes.Count);
        return _index < _nodes.Count ? ShellInterop.S_OK : ShellInterop.S_FALSE;
    }

    public int Reset()
    {
        _index = 0;
        return ShellInterop.S_OK;
    }

    public int Clone(out IntPtr ppenum)
    {
        var clone = new MenuEnumerator(_nodes, _paths) { _index = _index };
        ppenum = Marshal.GetComInterfaceForObject(clone, typeof(IEnumExplorerCommand));
        return ShellInterop.S_OK;
    }
}

/// <summary>把 IShellItemArray 里的选中项读成文件路径。</summary>
internal static class ShellPaths
{
    public static string[] Read(IShellItemArray? itemArray)
    {
        if (itemArray is null)
        {
            return Array.Empty<string>();
        }

        if (itemArray.GetCount(out var count) != ShellInterop.S_OK || count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (itemArray.GetItemAt(i, out var item) != ShellInterop.S_OK || item is null)
            {
                continue;
            }

            try
            {
                if (item.GetDisplayName(ShellInterop.SigdnFileSysPath, out var pointer) == ShellInterop.S_OK
                    && pointer != IntPtr.Zero)
                {
                    try
                    {
                        var path = Marshal.PtrToStringUni(pointer);
                        if (!string.IsNullOrEmpty(path))
                        {
                            paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        return paths.ToArray();
    }
}
