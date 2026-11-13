using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 非打包应用里 ms-appx:/// 不一定可靠，这里统一用磁盘绝对路径加载图片资源。
/// </summary>
public static class UiAssets
{
    private static ImageSource? _appIcon;
    private static ImageSource? _appIconLarge;

    public static ImageSource? AppIcon => _appIcon ??= Load("app-64.png");

    public static ImageSource? AppIconLarge => _appIconLarge ??= Load("app-256.png");

    private static ImageSource? Load(string fileName)
    {
        try
        {
            var path = Path.Combine(AppInfo.AppDirectory, "Assets", fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            Log.Write($"加载图片资源失败：{fileName}", ex);
            return null;
        }
    }
}
