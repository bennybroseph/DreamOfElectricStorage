// CaptureCli — snapshots an app window to PNG via Windows.Graphics.Capture.
// Unlike CopyFromScreen, frames come from DWM composition: the window may be
// occluded/backgrounded (minimized stops frames). IncludeSecondaryWindows
// (Win11 24H2+) composites popups (flyouts, dropdowns, dialogs) into the frame.
//
// Usage: CaptureCli <out.png> [processName=DreamOfElectricStorage.App] [timeoutMs=5000]
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: CaptureCli <out.png> [processName=DreamOfElectricStorage.App] [timeoutMs=5000]");
    return 2;
}

string outPath = Path.GetFullPath(args[0]);
string processName = args.Length >= 2 ? args[1] : "DreamOfElectricStorage.App";
int timeoutMs = args.Length >= 3 ? int.Parse(args[2]) : 5000;

var proc = Process.GetProcessesByName(processName).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
if (proc is null)
{
    Console.Error.WriteLine($"no window found for process '{processName}'");
    return 1;
}

GraphicsCaptureItem item = CaptureInterop.CreateItemForWindow(proc.MainWindowHandle);
using var device = new CanvasDevice();
using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
    device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
using var session = pool.CreateCaptureSession(item);

session.IsCursorCaptureEnabled = false;
try { session.IsBorderRequired = false; } catch { } // ignored without consent — cosmetic only
if (ApiInformation.IsWriteablePropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IncludeSecondaryWindows"))
    session.IncludeSecondaryWindows = true;

using var gotFrame = new ManualResetEventSlim();
Direct3D11CaptureFrame? frame = null;
pool.FrameArrived += (p, _) =>
{
    if (gotFrame.IsSet) return;
    frame = p.TryGetNextFrame();
    gotFrame.Set();
};
session.StartCapture();

if (!gotFrame.Wait(timeoutMs) || frame is null)
{
    Console.Error.WriteLine("timed out waiting for a frame (window minimized?)");
    return 1;
}

using (frame)
using (var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface))
{
    bitmap.SaveAsync(outPath).AsTask().Wait();
}
Console.WriteLine($"saved {outPath} ({item.Size.Width}x{item.Size.Height})");
return 0;

static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // Picker-free HWND targeting (windows.graphics.capture.interop.h) — the WinRT
    // statics (TryCreateFromWindowId) need package identity + capability; this doesn't.
    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }
}
