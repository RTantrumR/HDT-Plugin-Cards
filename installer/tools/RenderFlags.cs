// Build-time helper for the Setup wizard: renders the plugin's own language flags (Ui\Flags.cs, the
// website's Flag.tsx port) and its globe to small BMPs, so the wizard's language page shows the same
// marks as the plugin and the site. Compiled and run by installer\build.ps1:
//   RenderFlags.exe <HsbgCardLookup.dll> <out dir>
// Writes flag_<code>_<width>.bmp (+ globe_<width>.bmp) for each width in Widths, opaque on white
// (the wizard page's colour), so the wizard can pick the size nearest its DPI without stretching.
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class RenderFlags
{
    private static readonly string[] Codes = { "en", "de", "es", "es-mx", "fr", "it", "ja", "ko", "pl", "pt", "th", "zh-cn", "zh-tw" };
    private static readonly int[] Widths = { 22, 28, 33, 44 };   // flag width in px at 100/125/150/200 %

    [STAThread]
    private static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        var flags = asm.GetType("HsbgCardLookup.Ui.Flags", true);
        var flag = flags.GetMethod("Flag", BindingFlags.Public | BindingFlags.Static);
        var globe = flags.GetMethod("Globe", BindingFlags.Public | BindingFlags.Static);

        foreach (int w in Widths)
        {
            // Flags.Flag draws a w x 0.75w mark inside a 1 px border.
            foreach (var code in Codes)
                Save((FrameworkElement)flag.Invoke(null, new object[] { code, (double)w }), Path.Combine(outDir, "flag_" + code + "_" + w + ".bmp"));
            // The globe at the flag's height, centred in a box as wide as a flag.
            int h = (int)Math.Round(w * 0.75) + 2;
            var g = (FrameworkElement)globe.Invoke(null, new object[] { (double)h, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) });
            var box = new Grid { Width = w + 2, Height = h };
            g.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(g);
            Save(box, Path.Combine(outDir, "globe_" + w + ".bmp"));
        }
        Console.WriteLine("flags rendered: " + Codes.Length + " codes x " + Widths.Length + " sizes -> " + outDir);
        return 0;
    }

    private static void Save(FrameworkElement e, string path)
    {
        var host = new Border { Background = Brushes.White, Child = e };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        int pw = (int)Math.Ceiling(host.ActualWidth), ph = (int)Math.Ceiling(host.ActualHeight);
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0)));
        using (var fs = File.Create(path)) enc.Save(fs);
    }
}
