using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using Tmds.DBus.Protocol;

namespace BlastDesigner.App.Services;

/// <summary>Sends a report to the printer through the print dialog of the system: Avalonia has none of its own.</summary>
public static class Printing
{
    /// <summary>Shows the dialog of the system and prints what it says; cancelling it does nothing.</summary>
    public static async Task Print(Window owner, ReportDocument report)
    {
        if (OperatingSystem.IsWindows()) { await WindowsPrinting.Print(owner, report); return; }
        // the other systems print a PDF themselves, in vectors
        var file = Path.Combine(Path.GetTempPath(), $"blastdesigner-print-{Guid.NewGuid():N}.pdf");
        await Task.Run(() => { using var stream = File.Create(file); report.WritePdf(stream); });
        if (OperatingSystem.IsMacOS()) MacPrinting.Print(file);
        else if (OperatingSystem.IsLinux()) await LinuxPrinting.Print(file, report.Title);
    }
}

/// <summary>The Windows print dialog and a print job of page pictures: the printer drivers do not take a PDF.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinting
{
    private const int NoSelection = 0x4, PageNumbers = 0x2, DevModeCopies = 0x40000;
    private const float DefaultDpi = 300, MaxDpi = 600;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrintDialogData
    {
        public int Size;
        public IntPtr Owner, DevMode, DevNames, Dc;
        public int Flags;
        public short FromPage, ToPage, MinPage, MaxPage, Copies;
        public IntPtr Instance, CustomData, PrintHook, SetupHook, PrintTemplate, SetupTemplate, PrintTemplateHandle, SetupTemplateHandle;
    }

    [DllImport("comdlg32.dll", EntryPoint = "PrintDlgW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PrintDlg(ref PrintDialogData data);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    public static async Task Print(Window owner, ReportDocument report)
    {
        var pages = (short)report.Pages.Count;
        var data = new PrintDialogData
        {
            Size = Marshal.SizeOf<PrintDialogData>(),
            Owner = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
            Flags = NoSelection | DevModeCopies,
            FromPage = 1, ToPage = pages, MinPage = 1, MaxPage = pages, Copies = 1
        };
        if (!PrintDlg(ref data)) return;
        var settings = new PrinterSettings();
        try
        {
            settings.SetHdevnames(data.DevNames);
            settings.SetHdevmode(data.DevMode);
            settings.DefaultPageSettings.SetHdevmode(data.DevMode);
        }
        finally
        {
            GlobalFree(data.DevMode);
            GlobalFree(data.DevNames);
        }
        if ((data.Flags & PageNumbers) != 0)
        {
            settings.PrintRange = PrintRange.SomePages;
            (settings.FromPage, settings.ToPage) = (data.FromPage, data.ToPage);
        }
        await Task.Run(() => Send(report, settings));
    }

    /// <summary>Prints on the printer of the settings, each page in the orientation it was laid out in.</summary>
    public static void Send(ReportDocument report, PrinterSettings settings)
    {
        var some = settings.PrintRange == PrintRange.SomePages;
        var page = some ? Math.Clamp(settings.FromPage - 1, 0, report.Pages.Count - 1) : 0;
        var last = some ? Math.Clamp(settings.ToPage - 1, page, report.Pages.Count - 1) : report.Pages.Count - 1;
        using var job = new PrintDocument { PrinterSettings = settings, DocumentName = report.Title, PrintController = new StandardPrintController() };
        job.QueryPageSettings += (_, e) => e.PageSettings.Landscape = report.Pages[page].Width > report.Pages[page].Height;
        job.PrintPage += (_, e) =>
        {
            DrawPage(report, page, e);
            e.HasMorePages = ++page <= last;
        };
        job.Print();
    }

    private static void DrawPage(ReportDocument report, int index, PrintPageEventArgs e)
    {
        // a page at a time: at 600 dpi a sheet of A4 takes about 140 MB
        var resolution = e.PageSettings.PrinterResolution.X;
        using var picture = report.Rasterize(index, resolution > 0 ? Math.Min(resolution, MaxDpi) : DefaultDpi);
        using var image = new Bitmap(picture.Width, picture.Height, picture.RowBytes, PixelFormat.Format32bppPArgb, picture.GetPixels());
        // hundredths of an inch from the corner of the printable area; another paper takes the sheet scaled to fit
        var page = report.Pages[index];
        float width = page.Width / 72 * 100, height = page.Height / 72 * 100;
        var scale = Math.Min(1, Math.Min(e.PageBounds.Width / width, e.PageBounds.Height / height));
        e.Graphics!.DrawImage(image, new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, width * scale, height * scale));
    }
}

/// <summary>The print panel of macOS, on the PDF: PDFKit makes the operation, AppKit runs it on the main thread.</summary>
[SupportedOSPlatform("macos")]
internal static class MacPrinting
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const int ScaleToFit = 1;

    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendText(IntPtr target, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendFlag(IntPtr target, IntPtr selector, byte flag);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendOperation(IntPtr target, IntPtr selector, IntPtr info, long scaling, byte rotate);

    public static void Print(string file)
    {
        dlopen("/System/Library/Frameworks/PDFKit.framework/PDFKit", 1);
        var path = SendText(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), file);
        var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), path);
        var document = Send(Send(objc_getClass("PDFDocument"), sel_registerName("alloc")), sel_registerName("initWithURL:"), url);
        if (document == IntPtr.Zero) throw new InvalidOperationException("PDFKit could not read the report.");
        var info = Send(objc_getClass("NSPrintInfo"), sel_registerName("sharedPrintInfo"));
        var operation = SendOperation(document, sel_registerName("printOperationForPrintInfo:scalingMode:autoRotate:"), info, ScaleToFit, 1);
        SendFlag(operation, sel_registerName("setShowsPrintPanel:"), 1);
        SendFlag(operation, sel_registerName("setShowsProgressPanel:"), 1);
        Send(operation, sel_registerName("runOperation"));
    }
}

/// <summary>
/// The print dialog of the Linux desktop, through its portal (it shows GNOME's or KDE's own). A desktop without the portal
/// gets the job on the default printer with lp, and without that the PDF opens in its viewer.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxPrinting
{
    private const string Portal = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop", PrintInterface = "org.freedesktop.portal.Print";

    public static async Task Print(string file, string title)
    {
        try { await ThroughPortal(file, title); }
        catch (Exception ex) when (ex is DBusException or ConnectException or DisconnectedException)
        {
            Log.Error("No print portal", ex);
            await WithoutPortal(file);
        }
    }

    private static async Task ThroughPortal(string file, string title)
    {
        var connection = Connection.Session;
        await connection.ConnectAsync();
        var answer = new TaskCompletionSource<(uint Code, uint Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule { Type = MessageType.Signal, Sender = Portal, Interface = "org.freedesktop.portal.Request", Member = "Response" };
        using var listening = await connection.AddMatchAsync(rule, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var code = reader.ReadUInt32();
            var results = reader.ReadDictionaryOfStringToVariantValue();
            return (code, results.TryGetValue("token", out var token) ? token.GetUInt32() : 0u);
        }, (error, value, _, _) =>
        {
            if (error != null) answer.TrySetException(error);
            else answer.TrySetResult(value);
        }, null, null, false, ObserverFlags.None);

        MessageBuffer Prepare()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Portal, path: PortalPath, @interface: PrintInterface, signature: "ssa{sv}a{sv}a{sv}", member: "PreparePrint");
            writer.WriteString("");
            writer.WriteString(title);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }
        await connection.CallMethodAsync(Prepare());
        var (code, token) = await answer.Task;
        if (code != 0) return;

        using var handle = File.OpenHandle(file);
        MessageBuffer Send()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Portal, path: PortalPath, @interface: PrintInterface, signature: "ssha{sv}", member: "Print");
            writer.WriteString("");
            writer.WriteString(title);
            writer.WriteHandle(handle);
            writer.WriteDictionary(new Dictionary<string, VariantValue> { ["token"] = token });
            return writer.CreateMessage();
        }
        await connection.CallMethodAsync(Send());
    }

    private static async Task WithoutPortal(string file)
    {
        try
        {
            using var lp = System.Diagnostics.Process.Start("lp", new[] { file });
            await lp.WaitForExitAsync();
            if (lp.ExitCode == 0) { await Dialogs.Info(Loc.Ui("Sent to the default printer: this desktop has no print dialog.")); return; }
        }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error("No lp", ex); }
        ViewModels.MainViewModel.OpenExternal(file);
    }
}
