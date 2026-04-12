using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using WinRT.Interop;
using Buzzr.Services;
using Buzzr.Views;

namespace Buzzr;

public partial class App : Application
{
    public const string Version = "0.0.2";

    private Window? _window;
    private Frame? _rootFrame;
    private static Process? _sidecarProcess;

    public static BeeperApiService Api { get; } = new();
    public static SettingsService Settings { get; } = new();
    public static Window? MainWindow { get; private set; }
    public static Frame? RootFrame { get; private set; }
    public static bool IsWindowFocused { get; private set; } = true;

    public App() { this.InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BeeperApiService.SetBaseUrl(Settings.BaseUrl);

        _window = new Window { Title = "Buzzr" };
        MainWindow = _window;

        // set taskbar icon
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "buzzr.ico"));
        }
        catch { }

        try
        {
            _window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        }
        catch
        {
            try { _window.SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
        }

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

        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        }
        catch { }

        _rootFrame = new Frame();
        RootFrame = _rootFrame;

        var rootGrid = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        rootGrid.Children.Add(_rootFrame);
        _window.Content = rootGrid;

        _window.Content.KeyDown += OnGlobalKeyDown;

        _window.Activated += (s, e) =>
        {
            IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        };

        _window.Closed += (s, e) => StopSidecar();

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

    public static string? FindSidecarPath()
    {
        var appDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(appDir, "buzzr-sidecar.exe");
        if (File.Exists(candidate)) return candidate;

        var projectRoot = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
        candidate = Path.Combine(projectRoot, "sidecar", "buzzr-sidecar.exe");
        if (File.Exists(candidate)) return candidate;

        var dir = appDir;
        for (int i = 0; i < 6; i++)
        {
            candidate = Path.Combine(dir, "sidecar", "buzzr-sidecar.exe");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return null;
    }

    public static bool StartSidecar(int port = 29110)
    {
        if (_sidecarProcess != null && !_sidecarProcess.HasExited)
            return true;

        try
        {
            using var probe = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
            var resp = probe.GetStringAsync($"http://localhost:{port}/v1/info").GetAwaiter().GetResult();
            if (resp.Contains("Buzzr"))
            {
                AppLog.Write($"[Sidecar] Reusing existing sidecar on port {port}");
                BeeperApiService.SetBaseUrl($"http://localhost:{port}");
                return true;
            }
        }
        catch { }

        KillOrphanedSidecars();

        var exePath = FindSidecarPath();
        if (exePath == null)
        {
            AppLog.Write("[Sidecar] buzzr-sidecar.exe not found");
            return false;
        }

        for (int p = port; p < port + 5; p++)
        {
            try
            {
                using (var test = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p))
                {
                    test.Start();
                    test.Stop();
                }

                AppLog.Write($"[Sidecar] Starting: {exePath} --port {p}");
                _sidecarProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"--port {p}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
                _sidecarProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) AppLog.Write($"[Sidecar] {e.Data}");
                };
                _sidecarProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) AppLog.Write($"[Sidecar] {e.Data}");
                };
                _sidecarProcess.Start();
                _sidecarProcess.BeginOutputReadLine();
                _sidecarProcess.BeginErrorReadLine();
                AppLog.Write($"[Sidecar] Started (PID {_sidecarProcess.Id}) on port {p}");

                Settings.SidecarPort = p;
                BeeperApiService.SetBaseUrl($"http://localhost:{p}");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[Sidecar] Port {p} unavailable: {ex.Message}");
            }
        }

        AppLog.Write("[Sidecar] Failed to start on any port (29110-29114)");
        return false;
    }

    public static void StopSidecar()
    {
        if (_sidecarProcess == null) return;
        try
        {
            if (!_sidecarProcess.HasExited)
            {
                AppLog.Write($"[Sidecar] Stopping (PID {_sidecarProcess.Id})...");
                _sidecarProcess.Kill(entireProcessTree: true);
                _sidecarProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Sidecar] Error stopping: {ex.Message}");
        }
        finally
        {
            _sidecarProcess.Dispose();
            _sidecarProcess = null;
        }
    }

    private static void KillOrphanedSidecars()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("buzzr-sidecar"))
            {
                try
                {
                    AppLog.Write($"[Sidecar] Killing orphaned process PID {proc.Id}");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    public static bool IsSidecarRunning =>
        _sidecarProcess != null && !_sidecarProcess.HasExited;

    private void OnGlobalKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && !shift && e.Key == Windows.System.VirtualKey.K)
        {
            e.Handled = true;
            ShellPage.FocusSearch?.Invoke();
        }
        else if (ctrl && shift && e.Key == Windows.System.VirtualKey.N)
        {
            e.Handled = true;
            ShellPage.OpenNewChat?.Invoke();
        }
        else if (ctrl && !shift && e.Key == (Windows.System.VirtualKey)192)
        {
            e.Handled = true;
            ShellPage.OpenTerminal?.Invoke();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ShellPage.CloseOverlays?.Invoke();
        }
    }
}
