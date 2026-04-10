using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using WinRT.Interop;
using BeeperWinUI.Services;
using BeeperWinUI.Views;

namespace BeeperWinUI;

public partial class App : Application
{
    private Window? _window;
    private Frame? _rootFrame;

    public static BeeperApiService Api { get; } = new();
    public static SettingsService Settings { get; } = new();
    public static Window? MainWindow { get; private set; }
    public static Frame? RootFrame { get; private set; }
    public static bool IsWindowFocused { get; private set; } = true;

    public App() { this.InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Beeper" };
        MainWindow = _window;

        // Mica backdrop with DesktopAcrylic fallback
        try
        {
            _window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        }
        catch
        {
            try { _window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
        }

        // Extended title bar
        _window.ExtendsContentIntoTitleBar = true;
        try
        {
            var tb = _window.AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 255, 255, 255);
            tb.ButtonForegroundColor = Colors.White;
        }
        catch { }

        // Window sizing
        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        }
        catch { }

        // Root frame for page navigation
        _rootFrame = new Frame();
        RootFrame = _rootFrame;

        // Wrap frame in a Grid with title bar drag region to prevent resize glitches
        var rootGrid = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        rootGrid.Children.Add(_rootFrame);
        _window.Content = rootGrid;

        // App-level keyboard shortcuts
        _window.Content.KeyDown += OnGlobalKeyDown;

        _window.Activated += (s, e) =>
        {
            IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        };

        try { AppNotificationManager.Default.Register(); } catch { }

        ShowSetup();
        _window.Activate();
    }

    public void ShowSetup()
    {
        _rootFrame?.Navigate(typeof(SetupPage));
    }

    public void ShowShell()
    {
        _rootFrame?.Navigate(typeof(ShellPage));
    }

    public void Disconnect()
    {
        Settings.ClearSession();
        Api.SetToken("");
        ShowSetup();
    }

    private void OnGlobalKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && !shift && e.Key == Windows.System.VirtualKey.K)
        {
            // Ctrl+K: focus search / quick chat switcher
            e.Handled = true;
            ShellPage.FocusSearch?.Invoke();
        }
        else if (ctrl && shift && e.Key == Windows.System.VirtualKey.N)
        {
            // Ctrl+Shift+N: new chat dialog
            e.Handled = true;
            ShellPage.OpenNewChat?.Invoke();
        }
        else if (ctrl && !shift && e.Key == (Windows.System.VirtualKey)192) // Ctrl+` (backtick)
        {
            e.Handled = true;
            ShellPage.OpenTerminal?.Invoke();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Escape: close overlays
            e.Handled = true;
            ShellPage.CloseOverlays?.Invoke();
        }
    }
}
