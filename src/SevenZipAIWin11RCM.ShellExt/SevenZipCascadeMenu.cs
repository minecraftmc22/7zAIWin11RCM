using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SevenZipAIWin11RCM.ShellExt;

/// <summary>
/// 旧版（经典）右键菜单处理器。
///
/// 它自己不实现菜单内容，而是激活 7-Zip 自己的 shell 扩展
/// （7-zip.dll，CLSID 23170F69-40C1-278A-1000-000100020000），
/// 让 7-Zip 把条目填进我们创建的子菜单里。
///
/// 这么做的原因：7-Zip 的条目是运行时按选中对象生成的
/// （「解压到 "文件夹名\"」「添加到 "文件夹名.7z"」都会带当前名称），
/// 注册表里并不存在这些动词，静态注册表项无法还原。
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SevenZipCascadeMenu : IShellExtInit, IContextMenu
{
    public const string ClassId = "6F2B7C51-9E4A-4E3D-9C2B-5A7E1F0D3C48";

    /// <summary>7-Zip 经典菜单扩展的 CLSID（取不到注册表时的兜底）。</summary>
    private static readonly Guid SevenZipHandlerFallback = new("23170F69-40C1-278A-1000-000100020000");

    private static Guid? _sevenZipHandler;

    private IntPtr _pidlFolder;
    private IntPtr _dataObject;
    private IntPtr _hKeyProgId;

    private object? _inner;
    private IntPtr _subMenu;

    // ---------------------------------------------------------------- IShellExtInit

    public int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID)
    {
        _pidlFolder = pidlFolder;
        _hKeyProgId = hKeyProgID;

        if (pDataObj != IntPtr.Zero)
        {
            _dataObject = pDataObj;
            Marshal.AddRef(_dataObject);
        }

        return ShellInterop.S_OK;
    }

    // ---------------------------------------------------------------- IContextMenu

    public int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags)
    {
        // 只处理默认项时不展开子菜单
        if ((uFlags & 0x1) != 0 || hMenu == IntPtr.Zero || idCmdLast < idCmdFirst)
        {
            return ShellInterop.S_OK;
        }

        var contextMenu = TryCreateInner();
        if (contextMenu is null)
        {
            return ShellInterop.S_OK;
        }

        var subMenu = CreatePopupMenu();
        if (subMenu == IntPtr.Zero)
        {
            return ShellInterop.S_OK;
        }

        int count;
        try
        {
            var hr = contextMenu.QueryContextMenu(subMenu, 0, idCmdFirst, idCmdLast, uFlags);
            if (hr < 0)
            {
                DestroyMenu(subMenu);
                return hr;
            }

            count = hr & 0xFFFF;
        }
        catch (Exception)
        {
            DestroyMenu(subMenu);
            return ShellInterop.S_OK;
        }

        if (count == 0)
        {
            // 7-Zip 对当前选中对象没有可用条目，就不要留一个空子菜单
            DestroyMenu(subMenu);
            return ShellInterop.S_OK;
        }

        // 7-Zip 自己会把条目放进它自己的「7-Zip」子菜单里。
        // 直接把那个子菜单摘过来用，避免出现「7-Zip > 7-Zip > 条目」这样的双层嵌套。
        var nested = GetSubMenu(subMenu, 0);
        if (nested != IntPtr.Zero)
        {
            RemoveMenu(subMenu, 0, MfByPosition);
            DestroyMenu(subMenu);
            subMenu = nested;
        }

        if (!InsertMenu(hMenu, indexMenu, MfByPosition | MfPopup | MfString, subMenu, MenuTitle))
        {
            DestroyMenu(subMenu);
            return ShellInterop.S_OK;
        }

        _subMenu = subMenu;

        // 返回消耗掉的命令 id 数量，供后续处理器接着编号
        return count;
    }

    public int InvokeCommand(IntPtr pici)
    {
        return _inner is IContextMenu inner
            ? inner.InvokeCommand(pici)
            : ShellInterop.E_FAIL;
    }

    public int GetCommandString(int idCmd, uint uType, IntPtr pReserved, IntPtr pszName, int cchMax)
    {
        return _inner is IContextMenu inner
            ? inner.GetCommandString(idCmd, uType, pReserved, pszName, cchMax)
            : ShellInterop.E_NOTIMPL;
    }

    // ---------------------------------------------------------------- 内部实现

    private const string MenuTitle = "7-Zip";

    private const uint MfByPosition = 0x00000400;
    private const uint MfPopup = 0x00000010;
    private const uint MfString = 0x00000000;

    private IContextMenu? TryCreateInner()
    {
        if (_inner is IContextMenu existing)
        {
            return existing;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(SevenZipHandlerClsid);
            if (type is null)
            {
                return null;
            }

            var instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                return null;
            }

            // 把 shell 交给我们的上下文原样转交给 7-Zip，让它自己判断该出现哪些条目
            if (instance is IShellExtInit init)
            {
                init.Initialize(_pidlFolder, _dataObject, _hKeyProgId);
            }

            if (instance is not IContextMenu contextMenu)
            {
                return null;
            }

            _inner = contextMenu;
            return contextMenu;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>从注册表读取 7-Zip 菜单处理器的 CLSID，读不到则用已知值。</summary>
    private static Guid SevenZipHandlerClsid
    {
        get
        {
            if (_sevenZipHandler is { } cached)
            {
                return cached;
            }

            foreach (var scope in new[] { "*", "Directory", "Folder" })
            {
                var value = ReadHandlerClsid($@"{scope}\shellex\ContextMenuHandlers\7-Zip");
                if (value is { } guid)
                {
                    _sevenZipHandler = guid;
                    return guid;
                }
            }

            _sevenZipHandler = SevenZipHandlerFallback;
            return SevenZipHandlerFallback;
        }
    }

    private static Guid? ReadHandlerClsid(string path)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(path);
            var raw = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return Guid.TryParse(raw.Trim().Trim('{', '}'), out var guid) ? guid : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    ~SevenZipCascadeMenu()
    {
        if (_subMenu != IntPtr.Zero)
        {
            DestroyMenu(_subMenu);
            _subMenu = IntPtr.Zero;
        }

        if (_dataObject != IntPtr.Zero)
        {
            Marshal.Release(_dataObject);
            _dataObject = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string lpNewItem);
}

[ComImport]
[Guid("000214E8-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellExtInit
{
    [PreserveSig]
    int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID);
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

    [PreserveSig]
    int InvokeCommand(IntPtr pici);

    [PreserveSig]
    int GetCommandString(int idCmd, uint uType, IntPtr pReserved, IntPtr pszName, int cchMax);
}
