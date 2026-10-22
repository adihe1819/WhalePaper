using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    internal static class ShellThumbnailService
    {
        public static BitmapSource GetThumbnail(string path, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return null;
            }

            var iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
            NativeMethods.IShellItemImageFactory factory;
            var hr = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr < 0 || factory == null)
            {
                return null;
            }

            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                var flags = NativeMethods.SIIGBF.SIIGBF_THUMBNAILONLY | NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK;
                hr = factory.GetImage(new NativeMethods.SIZE(width, height), flags, out hBitmap);
                if (hr < 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
                if (factory != null && Marshal.IsComObject(factory))
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
        }
    }
}
