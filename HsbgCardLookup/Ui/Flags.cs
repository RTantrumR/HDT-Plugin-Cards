using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Flag marks for the language page, drawn in code on a 60x45 canvas — a port of the website's
    /// components/Flag.tsx shape for shape, so the plugin and the site show the same marks. No
    /// emoji (Windows ships no flag font) and no image files. Simplified to what survives at ~30 px:
    /// fields, bands and the one silhouette that tells a flag from its neighbour (Mexico's emblem
    /// against Italy, Brazil's disc). The Stars and Stripes stands for enUS; Brazil, not Portugal,
    /// for ptBR. Also the globe the switcher wears, from the site's header.
    /// </summary>
    internal static class Flags
    {
        public static FrameworkElement Flag(string web, double width)
        {
            var canvas = new Canvas { Width = 60, Height = 45, ClipToBounds = true };
            Draw(canvas, web);
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2), SnapsToDevicePixels = true,
                Child = new Viewbox { Width = width, Height = width * 0.75, Child = canvas }
            };
        }

        /// <summary>The header globe (a circle, its meridian, two parallels), stroked.</summary>
        public static FrameworkElement Globe(double size, Brush stroke)
        {
            var path = new Path
            {
                Data = Geometry.Parse("M12,21 A9,9 0 1 0 12,3 A9,9 0 1 0 12,21 Z " +
                                      "M12,21 C14.485,21 16.5,16.97 16.5,12 C16.5,7.03 14.485,3 12,3 " +
                                      "C9.515,3 7.5,7.03 7.5,12 C7.5,16.97 9.515,21 12,21 M3.6,9 L20.4,9 M3.6,15 L20.4,15"),
                Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 24, Height = 24, Stretch = Stretch.None
            };
            return new Viewbox { Width = size, Height = size, Child = path };
        }

        private static void Draw(Canvas c, string web)
        {
            switch (web)
            {
                case "en": StarsAndStripes(c); break;
                case "de": Bands(c, false, "#000000", "#DD0000", "#FFCE00"); break;
                case "fr": Bands(c, true, "#002654", "#FFFFFF", "#ED2939"); break;
                case "it": Bands(c, true, "#008C45", "#F4F5F0", "#CD212A"); break;
                case "pl": Bands(c, false, "#FFFFFF", "#DC143C"); break;
                case "es":
                    Rect(c, 0, 0, 60, 45, "#AA151B");
                    Rect(c, 0, 11.25, 60, 22.5, "#F1BF00");
                    break;
                case "es-mx":   // Italy's colours in Italy's order — the emblem is what tells them apart
                    Bands(c, true, "#006847", "#FFFFFF", "#CE1126");
                    Ellipse(c, 30, 20.6, 3, 4.2, "#6B4A23");
                    Path(c, "M26.4,26.6 Q30,29.8 33.6,26.6", null, "#1E6B3A", 1.9);
                    break;
                case "pt":      // the band is what makes the disc Brazil's; motto and stars cannot survive this size
                    Rect(c, 0, 0, 60, 45, "#009C3B");
                    Path(c, "M5.1,22.5 L30,5.46 L54.9,22.5 L30,39.54 Z", "#FFDF00");
                    Ellipse(c, 30, 22.5, 11.25, 11.25, "#FFFFFF");
                    Path(c, "M18.93,20.52 A11.25,11.25 0 0 1 41.12,24.22 A21.55,21.55 0 0 0 18.93,20.52 Z", "#002776");
                    Path(c, "M18.79,23.42 A11.25,11.25 0 0 0 40.31,27.01 A18.95,18.95 0 0 0 18.79,23.42 Z", "#002776");
                    break;
                case "ja":
                    Rect(c, 0, 0, 60, 45, "#FFFFFF");
                    Ellipse(c, 30, 22.5, 13.5, 13.5, "#BC002D");
                    break;
                case "ko": Korea(c); break;
                case "th":
                    Rect(c, 0, 0, 60, 45, "#A51931");
                    Rect(c, 0, 7.5, 60, 30, "#F4F5F8");
                    Rect(c, 0, 15, 60, 15, "#2D2A4A");
                    break;
                case "zh-cn":
                    Rect(c, 0, 0, 60, 45, "#DE2910");
                    Path(c, Star(5, 7, 2.67, 15, 13.5), "#FFDE00");
                    Path(c, Star(5, 2.4, 0.92, 27, 5.4), "#FFDE00");
                    Path(c, Star(5, 2.4, 0.92, 32.4, 11.7), "#FFDE00");
                    Path(c, Star(5, 2.4, 0.92, 32.4, 20.7), "#FFDE00");
                    Path(c, Star(5, 2.4, 0.92, 27, 26.1), "#FFDE00");
                    break;
                case "zh-tw":
                    Rect(c, 0, 0, 60, 45, "#FE0000");
                    Rect(c, 0, 0, 30, 22.5, "#000095");
                    Path(c, Star(12, 7.5, 4.3, 15, 11.25), "#FFFFFF");
                    Ellipse(c, 15, 11.25, 4.3, 4.3, "#FFFFFF", "#000095", 1.1);
                    break;
                default:        // a locale without a mark reads as a plain field, never a broken image
                    Rect(c, 0, 0, 60, 45, "#3A4150");
                    break;
            }
        }

        private static void StarsAndStripes(Canvas c)
        {
            double stripe = 45.0 / 13;
            Rect(c, 0, 0, 60, 45, "#FFFFFF");
            for (int i = 0; i < 13; i += 2) Rect(c, 0, i * stripe, 60, stripe, "#B22234");
            Rect(c, 0, 0, 24, stripe * 7, "#3C3B6E");
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    Ellipse(c, 5 + col * 7 + (row % 2) * 3.5, 5 + row * 7.2, 2.4, 2.4, "#FFFFFF");
        }

        private static void Korea(Canvas c)
        {
            Rect(c, 0, 0, 60, 45, "#FFFFFF");
            var taegeuk = new Canvas { RenderTransform = new RotateTransform(-33.7, 30, 22.5) };
            Ellipse(taegeuk, 30, 22.5, 10, 10, "#0047A0");
            Path(taegeuk, "M20,22.5 A5,5 0 0 1 30,22.5 A5,5 0 0 0 40,22.5 A10,10 0 0 0 20,22.5 Z", "#CD2E3A");
            c.Children.Add(taegeuk);
            // Four trigrams, as on the site: three bars each, rotated toward the centre.
            double[][] trigrams = { new[] { 12.0, 9, -56.3 }, new[] { 48.0, 9, 56.3 }, new[] { 12.0, 36, 56.3 }, new[] { 48.0, 36, -56.3 } };
            foreach (var t in trigrams)
            {
                var g = new Canvas();
                var tr = new TransformGroup();
                tr.Children.Add(new RotateTransform(t[2]));
                tr.Children.Add(new TranslateTransform(t[0], t[1]));
                g.RenderTransform = tr;
                Rect(g, -5.5, -4, 11, 1.8, "#000000");
                Rect(g, -5.5, -0.9, 11, 1.8, "#000000");
                Rect(g, -5.5, 2.2, 11, 1.8, "#000000");
                c.Children.Add(g);
            }
        }

        private static void Bands(Canvas c, bool vertical, params string[] colors)
        {
            double size = (vertical ? 60.0 : 45.0) / colors.Length;
            for (int i = 0; i < colors.Length; i++)
            {
                if (vertical) Rect(c, i * size, 0, size, 45, colors[i]);
                else Rect(c, 0, i * size, 60, size, colors[i]);
            }
        }

        // Regular star as path data: `points` tips at `outer`, valleys at `inner`, first tip up.
        private static string Star(int points, double outer, double inner, double cx, double cy)
        {
            var sb = new StringBuilder("M");
            for (int i = 0; i < points * 2; i++)
            {
                double r = i % 2 == 0 ? outer : inner;
                double a = Math.PI * i / points - Math.PI / 2;
                if (i > 0) sb.Append('L');
                sb.Append((cx + r * Math.Cos(a)).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append((cy + r * Math.Sin(a)).ToString("0.00", CultureInfo.InvariantCulture));
            }
            return sb.Append('Z').ToString();
        }

        private static Brush Br(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private static void Rect(Canvas c, double x, double y, double w, double h, string fill)
        {
            var r = new Rectangle { Width = w, Height = h, Fill = Br(fill) };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            c.Children.Add(r);
        }

        private static void Ellipse(Canvas c, double cx, double cy, double rx, double ry, string fill, string stroke = null, double strokeWidth = 0)
        {
            var e = new Ellipse { Width = rx * 2, Height = ry * 2, Fill = Br(fill) };
            if (stroke != null) { e.Stroke = Br(stroke); e.StrokeThickness = strokeWidth; }
            Canvas.SetLeft(e, cx - rx); Canvas.SetTop(e, cy - ry);
            c.Children.Add(e);
        }

        private static void Path(Canvas c, string data, string fill, string stroke = null, double strokeWidth = 0)
        {
            var p = new Path { Data = Geometry.Parse(data) };
            if (fill != null) p.Fill = Br(fill);
            if (stroke != null)
            {
                p.Stroke = Br(stroke); p.StrokeThickness = strokeWidth;
                p.StrokeStartLineCap = PenLineCap.Round; p.StrokeEndLineCap = PenLineCap.Round;
            }
            c.Children.Add(p);
        }
    }
}
