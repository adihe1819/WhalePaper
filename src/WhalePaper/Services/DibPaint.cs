using System;
using System.Runtime.InteropServices;
using WhalePaper.Interop;
using DrawingColor = System.Drawing.Color;

namespace WhalePaper.Services
{
    /// <summary>
    /// Small GDI helpers shared by the wallpaper host and the native layer probe.
    /// </summary>
    internal static class DibPaint
    {
        public static void StretchSolid(
            IntPtr hdc, int x, int y, int width, int height, DrawingColor color)
        {
            if (hdc == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }

            var pixels = new[] { color.B, color.G, color.R, (byte)255 };
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bmi = new NativeMethods.BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = 1;
                bmi.bmiHeader.biHeight = -1;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;
                bmi.bmiHeader.biSizeImage = 4;

                NativeMethods.SetStretchBltMode(hdc, NativeMethods.COLORONCOLOR);
                NativeMethods.StretchDIBits(
                    hdc,
                    x,
                    y,
                    width,
                    height,
                    0,
                    0,
                    1,
                    1,
                    pin.AddrOfPinnedObject(),
                    ref bmi,
                    NativeMethods.DIB_RGB_COLORS,
                    NativeMethods.SRCCOPY);
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
