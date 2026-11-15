using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace ThixInstall;

/// <summary>
/// Base of every ThixInstall window: theme colours and font (inherited by all text), the app icon, and on Windows a title
/// bar that follows the light or dark theme.
/// </summary>
public class ThemedWindow : Window
{
    private static WindowIcon? _appIcon;

    public ThemedWindow()
    {
        this[!BackgroundProperty] = this.GetResourceObservable("WindowBrush").ToBinding();
        this[!ForegroundProperty] = this.GetResourceObservable("TextBrush").ToBinding();
        if (OperatingSystem.IsWindows())
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        }

        FontSize = 14;
        Icon = AppIcon;
        UseLayoutRounding = true;
        Opened += (_, _) => ApplyTitleBarTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
    }

    protected override Type StyleKeyOverride => typeof(Window);

    public static WindowIcon AppIcon => _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ThixInstall/Assets/thixinstall.ico")));

    /// <summary>The usable area of the screen the window is on (or the primary screen); never larger than the screen.</summary>
    public Rect WorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return new Rect(0, 0, 1280, 800);
        }

        var area = screen.WorkingArea;
        return new Rect(area.X / screen.Scaling, area.Y / screen.Scaling, area.Width / screen.Scaling, area.Height / screen.Scaling);
    }

    /// <summary>Brings the window to the front, also when it was minimized or hidden.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Windows 10 2004+/11: a dark caption in the dark theme, a light one in the light theme.</summary>
    private void ApplyTitleBarTheme()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
