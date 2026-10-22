using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

// Runs the shipped host/decoder in an STA process. It never changes the system
// wallpaper, startup registration, or the user's real library/settings.
internal static class DesktopPlaybackSmoke
{
    private static MethodInfo _pump;

    [STAThread]
    private static int Main(string[] args)
    {
        object host = null;
        try
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var paths = assembly.GetType("WhalePaper.Services.AppPaths", true);
            paths.GetMethod("Initialize").Invoke(null,
                new object[] { new[] { "--data-dir", Path.GetFullPath(args[3]) } });
            _pump = assembly.GetType("WhalePaper.Services.VisibilityProbe", true).GetMethod("Pump");
            var hostType = assembly.GetType("WhalePaper.Services.WallpaperHostWindow", true);
            host = Activator.CreateInstance(hostType, true);
            Call(host, "SetPoster", Path.GetFullPath(args[2]));
            Require((bool)Call(host, "AttachToDesktop", new object[] { null }), "attach");
            Console.WriteLine("ATTACH " + Get(host, "AttachDescription") +
                " pixelsVerified=" + Get(host, "AttachVerified"));

            var itemType = assembly.GetType("WhalePaper.Models.WallpaperItem", true);
            var item = Activator.CreateInstance(itemType);
            itemType.GetProperty("StoredPath").SetValue(item, Path.GetFullPath(args[1]));
            var kindType = assembly.GetType("WhalePaper.Models.WallpaperKind", true);
            var isGif = string.Equals(Path.GetExtension(args[1]), ".gif", StringComparison.OrdinalIgnoreCase);
            itemType.GetProperty("Kind").SetValue(item, Enum.Parse(kindType, isGif ? "Gif" : "Video"));
            var fitType = assembly.GetType("WhalePaper.Models.FitMode", true);
            Require((bool)Call(host, "Load", item, Enum.Parse(fitType, "Fill"), true), "load");

            var startup = Stopwatch.StartNew();
            while (!(bool)Get(host, "FirstFramePainted") && startup.ElapsedMilliseconds < 6000) { Pump(100); }
            Require((bool)Get(host, "FirstFramePainted"), "first frame actually painted");
            var before = Frames(host);
            var interval = Stopwatch.StartNew();
            Pump(2000);
            var painted = Frames(host) - before;
            Console.WriteLine("PAINTED frames=" + painted + " inMs=" + interval.ElapsedMilliseconds);
            Require(painted >= 10, "continuous painting");
            if (!isGif) { Require(painted <= 75, "30fps paint cap"); }
            Require((bool)Call(host, "EnsureLayer"), "parent and icon-list Z order during playback");

            Call(host, "Pause");
            Pump(150);
            var pausedCount = Frames(host);
            Pump(650);
            Require(Frames(host) == pausedCount, "pause stops painting");
            Call(host, "Resume");
            Pump(800);
            Require(Frames(host) > pausedCount, "resume paints again");
            Console.WriteLine("PAUSE/RESUME PASS totalFrames=" + Frames(host));
            Call(host, "CheckPlaybackPixels");
            if (args.Length > 5) { CaptureDesktop(args[5]); }

            if (args.Length > 4 && int.TryParse(args[4], out int extraSeconds))
            {
                for (var second = 0; second < extraSeconds; second++)
                {
                    Pump(1000);
                    Require((bool)Call(host, "EnsureLayer"), "sustained placement");
                }
                Console.WriteLine("SUSTAINED PASS totalFrames=" + Frames(host));
            }
            Console.WriteLine("PASS (screen/icon visibility is only verified when desktop pixels were available)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + (ex.InnerException ?? ex));
            return 1;
        }
        finally
        {
            var disposable = host as IDisposable;
            if (disposable != null) { disposable.Dispose(); }
        }
    }

    private static object Call(object target, string method, params object[] arguments)
    {
        return target.GetType().GetMethod(method).Invoke(target, arguments);
    }
    private static object Get(object target, string property)
    {
        return target.GetType().GetProperty(property).GetValue(target);
    }
    private static long Frames(object host) { return (long)Get(host, "PaintedFrameCount"); }
    private static void CaptureDesktop(string path)
    {
        var bounds = Screen.PrimaryScreen.Bounds;
        using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            bitmap.Save(Path.GetFullPath(path), ImageFormat.Png);
        }
        Console.WriteLine("SCREEN CAPTURE " + Path.GetFullPath(path));
    }
    private static void Pump(int milliseconds) { _pump.Invoke(null, new object[] { milliseconds }); }
    private static void Require(bool condition, string description)
    {
        if (!condition) { throw new InvalidOperationException(description); }
    }
}
