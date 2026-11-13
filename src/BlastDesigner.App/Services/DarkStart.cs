using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace BlastDesigner.App.Services;

/// <summary>
/// A window opens black, not white: until its first frame is drawn Windows erases it with the brush of its class, white,
/// and the empty window flashed. The erase is taken here and filled black; once the window has drawn itself it takes the
/// colour of the theme.
/// </summary>
public static class DarkStart
{
    private const uint WmEraseBackground = 0x0014;
    private const int BlackBrush = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);

    /// <summary>
    /// Black until the window has drawn itself, then <paramref name="themed"/>. Called in the window's constructor, before
    /// it is shown: the first erase comes with the showing.
    /// </summary>
    public static void Attach(Window window, IBrush themed)
    {
        window.Background = Brushes.Black;
        if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(window, Erase);
        // the theme's colour once the first frame is up
        window.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Background = themed, Avalonia.Threading.DispatcherPriority.Render);
    }

    private static IntPtr Erase(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmEraseBackground) return IntPtr.Zero;
        try
        {
            if (GetClientRect(hwnd, out var rect)) FillRect(wParam, ref rect, GetStockObject(BlackBrush));
            handled = true;
            return new IntPtr(1);
        }
        catch { return IntPtr.Zero; }
    }
}
