using System.Runtime.InteropServices;

namespace SevenZipAIWin11RCM.ShellExt;

internal static class ShellInterop
{
    public const uint SigdnFileSysPath = 0x80058000;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);

    // EXPCMDSTATE
    public const int EcsEnabled = 0x00000000;
    public const int EcsDisabled = 0x00000001;
    public const int EcsHidden = 0x00000002;

    // EXPCMDFLAGS
    public const int EcfDefault = 0x00000000;
    public const int EcfHasSubCommands = 0x00000001;
}

/// <summary>
/// Windows 11 新版右键菜单的命令接口。
/// 方法顺序必须与 shell 的原生 vtable 完全一致，不能调整。
/// </summary>
[ComVisible(true)]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray psiItemArray, out IntPtr ppszName);

    [PreserveSig]
    int GetIcon(IShellItemArray psiItemArray, out IntPtr ppszIcon);

    [PreserveSig]
    int GetToolTip(IShellItemArray psiItemArray, out IntPtr ppszInfotip);

    [PreserveSig]
    int GetCanonicalName(out Guid pguidCommandName);

    [PreserveSig]
    int GetState(IShellItemArray psiItemArray, int fOkToBeSlow, out int pCmdState);

    [PreserveSig]
    int Invoke(IShellItemArray psiItemArray, IntPtr pbc);

    [PreserveSig]
    int GetFlags(out int pFlags);

    [PreserveSig]
    int EnumSubCommands(out IntPtr ppEnum);
}

/// <summary>枚举子命令（Windows 11 新版菜单的级联子菜单靠它）。</summary>
[ComImport]
[Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    [PreserveSig]
    int Next(uint celt, out IExplorerCommand pUICommand, out uint pCeltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IntPtr ppenum);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(int attribFlags, int sfgaoMask, out int psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem ppsi);

    [PreserveSig]
    int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}
