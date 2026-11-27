using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace BlastDesigner.App.Services;

/// <summary>
/// A drawing put on the Windows clipboard in the forms other programs paste: an enhanced metafile (vector: Word keeps
/// its lines, AutoCAD can explode it), SVG (newer Office), PNG and a bitmap (DIB) for the rest. All in one go, so a
/// paste takes the best form it knows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipboardImage
{
    /// <summary>The forms put on the clipboard by the last <see cref="Copy"/>, by name.</summary>
    public static IReadOnlyList<string> LastFormats { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Puts the drawing on the clipboard: its SVG (a unit of it so many millimetres on the page) as a metafile and as SVG,
    /// and its PNG as PNG and as a bitmap, the window (its handle) the owner of the clipboard: without one the system takes
    /// none of it. False when the clipboard could not be opened.
    /// </summary>
    public static bool Copy(IntPtr owner, byte[] svg, byte[] png, double mmPerUnit)
    {
        var formats = new List<string>();
        var emf = SvgToEmf.Convert(svg, mmPerUnit);
        // a metafile that holds nothing is not put
        if (SvgToEmf.Size(emf) == 0) { SvgToEmf.Delete(emf); emf = IntPtr.Zero; }
        // the clipboard may be held a moment by another program: tried a few times
        var open = false;
        for (int i = 0; i < 10 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(20);
        if (!open) { SvgToEmf.Delete(emf); return false; }
        try
        {
            EmptyClipboard();
            // the system owns what is put; what was not put is ours to free
            if (emf != IntPtr.Zero)
            {
                if (SetClipboardData(CF_ENHMETAFILE, emf) != IntPtr.Zero) formats.Add("CF_ENHMETAFILE");
                else SvgToEmf.Delete(emf);
            }
            if (Put(RegisterClipboardFormatW("image/svg+xml"), svg)) formats.Add("image/svg+xml");
            if (Put(RegisterClipboardFormatW("PNG"), png)) formats.Add("PNG");
            if (Dib(png) is { } dib && Put(CF_DIB, dib)) formats.Add("CF_DIB");
        }
        finally { CloseClipboard(); }
        LastFormats = formats;
        return formats.Count > 0;
    }

    /// <summary>Bytes put on the clipboard in a format, in memory of the system.</summary>
    private static bool Put(uint format, byte[] bytes)
    {
        if (format == 0) return false;
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) return false;
        var at = GlobalLock(memory);
        if (at == IntPtr.Zero) { GlobalFree(memory); return false; }
        Marshal.Copy(bytes, 0, at, bytes.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) != IntPtr.Zero) return true;
        GlobalFree(memory);
        return false;
    }

    /// <summary>The PNG as a device-independent bitmap: a header and the rows of 32-bit pixels, bottom up.</summary>
    private static byte[]? Dib(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        if (decoded == null) return null;
        using var bgra = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bgra)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(decoded, 0, 0); }
        int w = bgra.Width, h = bgra.Height, row = w * 4;
        var bytes = new byte[40 + row * h];
        void Int(int at, int v) => BitConverter.GetBytes(v).CopyTo(bytes, at);
        Int(0, 40); Int(4, w); Int(8, h);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 12);
        BitConverter.GetBytes((short)32).CopyTo(bytes, 14);
        Int(16, 0); Int(20, row * h);
        // about 96 dpi, in pixels a metre
        Int(24, 3780); Int(28, 3780);
        var pixels = bgra.GetPixelSpan();
        for (int y = 0; y < h; y++) pixels.Slice(y * bgra.RowBytes, row).CopyTo(bytes.AsSpan(40 + (h - 1 - y) * row, row));
        return bytes;
    }

    private const uint CF_DIB = 8, CF_ENHMETAFILE = 14, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
