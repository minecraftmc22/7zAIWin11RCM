using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SevenZipAIWin11RCM.Services;
using SevenZipAIWin11RCM.Views;
using Windows.Graphics;

namespace SevenZipAIWin11RCM;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1120;
    private const int DefaultHeight = 800;

    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.DisplayName;
        PaneVersionText.Text = $"版本 {AppInfo.Version}";
        TitleBarIcon.Source = UiAssets.AppIcon;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        ConfigureWindow();

        ApplyBackdrop();

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = AppInfo.DisplayName;

        var icon = Path.Combine(AppInfo.AppDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
        {
            _appWindow.SetIcon(icon);
        }

        _appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        CenterOnScreen();

        if (_appWindow.TitleBar is { } titleBar)
        {
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - DefaultWidth) / 2),
                work.Y + Math.Max(0, (work.Height - DefaultHeight) / 2)));
        }
        catch (Exception ex)
        {
            Log.Write("窗口居中失败", ex);
        }
    }

    private void ApplyBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("启用 Mica 材质失败，回退为纯色背景", ex);
        }

        SystemBackdrop = null;
        RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "about" => typeof(AboutPage),
            _ => typeof(SettingsPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
