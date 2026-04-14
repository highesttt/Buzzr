using System.Runtime.InteropServices;

namespace Buzzr.Services;

public static class TaskbarBadge
{
    private static bool _failed;

    public static void SetBadge(nint hwnd, int count)
    {
        if (_failed || hwnd == nint.Zero) return;
        try
        {
            if (count <= 0)
            {
                ClearOverlay(hwnd);
                return;
            }

            var bg = Theme.T.AccentLight2;
            var fg = Theme.T.GetContrastFg(bg);
            var hIcon = CreateBadgeIcon(count, bg.R, bg.G, bg.B, fg.R, fg.G, fg.B);
            if (hIcon != nint.Zero)
            {
                SetOverlay(hwnd, hIcon);
                DestroyIcon(hIcon);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[BADGE] Failed: {ex.GetType().Name}: {ex.Message}");
            _failed = true;
        }
    }

    [DllImport("taskbar_badge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int SetOverlay(nint hwnd, nint hIcon);

    [DllImport("taskbar_badge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int ClearOverlay(nint hwnd);

    [DllImport("taskbar_badge.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern nint CreateBadgeIcon(int count, byte bgR, byte bgG, byte bgB, byte fgR, byte fgG, byte fgB);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
