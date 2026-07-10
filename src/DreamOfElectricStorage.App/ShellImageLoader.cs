using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Graphics.Canvas;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Async shell icon/thumbnail source: requests resolve on a background STA thread via
/// IShellItemImageFactory (hand-written interop — CsWin32 lives in Core only), pixels
/// land in a ready queue, and <see cref="DrainReady"/> turns them into device bitmaps
/// on the UI thread (budgeted per frame). Thumbnails are circle-masked CPU-side so
/// they can render through the sprite batch (no per-node geometric clips).
/// Failed paths cache as null — never retried.
/// </summary>
public sealed class ShellImageLoader
{
    private const int IconPixels = 96;
    private const int ThumbPixels = 256;

    private readonly ConcurrentDictionary<string, CanvasBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Key, byte[]? Bgra, int W, int H)> _ready = new();
    private readonly BlockingCollection<(string Key, string Path, bool Thumb)> _work = [];

    /// <summary>Raised from the worker thread when pixels are ready — marshal before touching UI.</summary>
    public event Action? Ready;

    public ShellImageLoader()
    {
        var worker = new Thread(WorkLoop) { IsBackground = true, Name = "ShellImageLoader" };
        worker.SetApartmentState(ApartmentState.STA); // shell imaging misbehaves on MTA for some handlers
        worker.Start();
    }

    /// <summary>UI thread. Null result means unresolved OR failed — check <see cref="IsResolved"/>.</summary>
    public CanvasBitmap? TryGet(string key) => _cache.GetValueOrDefault(key);

    public bool IsResolved(string key) => _cache.ContainsKey(key);

    public void Request(string key, string path, bool thumbnail)
    {
        if (_cache.ContainsKey(key) || !_inFlight.TryAdd(key, 0))
            return;
        _work.Add((key, path, thumbnail));
    }

    /// <summary>UI thread (Draw): turn finished pixel buffers into device bitmaps.</summary>
    public void DrainReady(CanvasDevice device, int budget)
    {
        while (budget-- > 0 && _ready.TryDequeue(out var item))
        {
            CanvasBitmap? bitmap = null;
            if (item.Bgra is not null)
            {
                bitmap = CanvasBitmap.CreateFromBytes(device, item.Bgra, item.W, item.H,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
                    CanvasAlphaMode.Premultiplied);
            }
            _cache[item.Key] = bitmap;
        }
    }

    private void WorkLoop()
    {
        foreach (var (key, path, thumb) in _work.GetConsumingEnumerable())
        {
            (byte[] Bgra, int W, int H)? image = null;
            try
            {
                image = LoadShellImage(path, thumb ? ThumbPixels : IconPixels, thumb);
            }
            catch
            {
                // nonexistent path / no handler / shell hiccup → cache null below
            }

            if (image is { } img)
            {
                if (thumb)
                    MaskCircle(img.Bgra, img.W, img.H);
                _ready.Enqueue((key, img.Bgra, img.W, img.H));
            }
            else
            {
                _ready.Enqueue((key, null, 0, 0));
            }
            Ready?.Invoke();
        }
    }

    /// <summary>Zeroes premultiplied pixels outside the inscribed circle (1.5px feather).</summary>
    private static void MaskCircle(byte[] bgra, int width, int height)
    {
        float cx = width / 2f, cy = height / 2f;
        float radius = Math.Min(width, height) / 2f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dist = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                float factor = Math.Clamp((radius - dist) / 1.5f, 0f, 1f);
                if (factor >= 1f)
                    continue;
                int i = (y * width + x) * 4;
                bgra[i] = (byte)(bgra[i] * factor);
                bgra[i + 1] = (byte)(bgra[i + 1] * factor);
                bgra[i + 2] = (byte)(bgra[i + 2] * factor);
                bgra[i + 3] = (byte)(bgra[i + 3] * factor);
            }
        }
    }

    // --- shell + GDI interop ---

    private const uint SIIGBF_BIGGERSIZEOK = 0x1;
    private const uint SIIGBF_ICONONLY = 0x4;
    private const uint SIIGBF_THUMBNAILONLY = 0x8;

    private static (byte[] Bgra, int W, int H)? LoadShellImage(string path, int pixels, bool thumbnail)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);

        uint flags = (thumbnail ? SIIGBF_THUMBNAILONLY : SIIGBF_ICONONLY) | SIIGBF_BIGGERSIZEOK;
        int hr = factory.GetImage(new SIZE { cx = pixels, cy = pixels }, flags, out IntPtr hbitmap);
        if (hr != 0 || hbitmap == IntPtr.Zero)
            return null;

        try
        {
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmBitsPixel != 32)
                return null;

            int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
            var bits = new byte[width * height * 4];
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hbitmap, 0, (uint)height, bits, ref info, 0 /* DIB_RGB_COLORS */) == 0)
                    return null;
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }
            return (bits, width, height);
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
