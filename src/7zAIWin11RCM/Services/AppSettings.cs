namespace SevenZipAIWin11RCM.Services;

/// <summary>持久化的用户设置。</summary>
public sealed class AppSettings
{
    /// <summary>7-Zip 安装目录（包含 7zG.exe / 7zFM.exe）。</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>是否添加至 Windows 11 新版（紧凑）右键菜单。</summary>
    public bool EnableModernMenu { get; set; }

    /// <summary>是否添加至 Windows 11 旧版（"显示更多选项"）右键菜单。默认开启。</summary>
    public bool EnableLegacyMenu { get; set; } = true;

    /// <summary>
    /// 用户手动指定的 MakeAppx.exe 路径（可填 exe 或目录）。
    /// 留空表示自动查找 Windows SDK。
    /// </summary>
    public string? MakeAppxPath { get; set; }

    /// <summary>用户手动指定的 signtool.exe 路径。留空表示自动查找。</summary>
    public string? SignToolPath { get; set; }

    /// <summary>最后一次保存时间。</summary>
    public DateTimeOffset? LastSavedAt { get; set; }

    public AppSettings Clone() => new()
    {
        SevenZipPath = SevenZipPath,
        EnableModernMenu = EnableModernMenu,
        EnableLegacyMenu = EnableLegacyMenu,
        MakeAppxPath = MakeAppxPath,
        SignToolPath = SignToolPath,
        LastSavedAt = LastSavedAt,
    };

    /// <summary>默认设置：旧版菜单开、新版菜单关、路径自动查找。</summary>
    public static AppSettings CreateDefault() => new();

    public bool ContentEquals(AppSettings other) =>
        other is not null
        && string.Equals(NormalizePath(SevenZipPath), NormalizePath(other.SevenZipPath), StringComparison.OrdinalIgnoreCase)
        && EnableModernMenu == other.EnableModernMenu
        && EnableLegacyMenu == other.EnableLegacyMenu
        && string.Equals(NormalizePath(MakeAppxPath), NormalizePath(other.MakeAppxPath), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizePath(SignToolPath), NormalizePath(other.SignToolPath), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().TrimEnd('\\', '/');
}
