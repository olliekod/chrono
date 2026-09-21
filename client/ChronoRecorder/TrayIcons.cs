using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChronoRecorder
{
    /// <summary>
    /// Chrono's icons, made from the logo (Assets\logo-256.png, embedded in the program). The logo has a red dot in the
    /// middle, which reads as "recording"; for the other states that dot is recoloured (grey while waiting for a game,
    /// amber when paused), so the tray icon shows what Chrono is doing at a glance.
    /// </summary>
    public static class TrayIcons
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private static readonly Dictionary<TrayState, Icon> Cache = new();
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64 };

        /// <summary>The tray icon for a state.</summary>
        public static Icon For(TrayState state)
        {
            if (!Cache.TryGetValue(state, out var icon))
                Cache[state] = icon = Build(state);
            return icon;
        }

        /// <summary>The logo as it is, for the window and taskbar.</summary>
        public static Icon Logo => For(TrayState.Recording);

        public static Color DotColor(TrayState state) => state switch
        {
            TrayState.Paused => Color.FromArgb(250, 168, 26),     // amber
            TrayState.Idle => Color.FromArgb(148, 155, 164),      // grey: waiting for a game
            _ => Color.FromArgb(240, 65, 64)                      // the logo's own red: recording
        };

        // ------------------------------------------------------------------ recolouring (pure, tested)

        /// <summary>How red a pixel is, 0 to 1: the logo's dot is red, its ring is blue and its disc is dark, so this isolates the dot.</summary>
        public static double Redness(int r, int g, int b)
        {
            double value = r - (g + b) / 2.0;
            return Math.Clamp(value / 150.0, 0, 1);
        }

        /// <summary>Replace the red of a pixel with <paramref name="target"/>, keeping its shading (the dot's highlight and soft edge).</summary>
        public static (int R, int G, int B) Recolor(int r, int g, int b, Color target)
        {
            double weight = Redness(r, g, b);
            if (weight <= 0) return (r, g, b);

            double shade = Math.Max(r, Math.Max(g, b)) / 240.0;   // the dot's red is about 240 at full strength
            int nr = (int)Math.Clamp(target.R * shade, 0, 255);
            int ng = (int)Math.Clamp(target.G * shade, 0, 255);
            int nb = (int)Math.Clamp(target.B * shade, 0, 255);
            return ((int)Math.Round(r + (nr - r) * weight), (int)Math.Round(g + (ng - g) * weight), (int)Math.Round(b + (nb - b) * weight));
        }

        private static void Recolor(Bitmap bitmap, Color target)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (int i = 0; i + 3 < bytes.Length; i += 4)
                {
                    if (bytes[i + 3] == 0) continue;   // BGRA: fully transparent
                    var (r, g, b) = Recolor(bytes[i + 2], bytes[i + 1], bytes[i], target);
                    bytes[i + 2] = (byte)r; bytes[i + 1] = (byte)g; bytes[i] = (byte)b;
                }
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally { bitmap.UnlockBits(data); }
        }

        // ------------------------------------------------------------------------- building icons

        private static Icon Build(TrayState state)
        {
            try
            {
                using var master = LoadMaster();
                if (master != null)
                {
                    if (state != TrayState.Recording) Recolor(master, DotColor(state));
                    return FromMaster(master);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't build the icon from the logo: {ex.Message}");
            }
            return Fallback(state);
        }

        private static Bitmap? LoadMaster()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo-256.png");
            if (stream == null) return null;
            using var loaded = new Bitmap(stream);
            return new Bitmap(loaded);   // a copy that doesn't depend on the stream
        }

        /// <summary>An icon with a picture at each size Windows may want (tray 16-32, high-DPI 40-64).</summary>
        private static Icon FromMaster(Bitmap master)
        {
            var pngs = new List<(int Size, byte[] Data)>();
            foreach (int size in Sizes)
            {
                using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(master, new Rectangle(0, 0, size, size));
                }
                using var ms = new MemoryStream();
                scaled.Save(ms, ImageFormat.Png);
                pngs.Add((size, ms.ToArray()));
            }

            using var ico = new MemoryStream();
            using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
                int offset = 6 + 16 * pngs.Count;
                foreach (var (size, data) in pngs)
                {
                    w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write((uint)data.Length); w.Write((uint)offset);
                    offset += data.Length;
                }
                foreach (var (_, data) in pngs) w.Write(data);
            }
            ico.Position = 0;
            return new Icon(ico, SystemInformation.SmallIconSize);
        }

        /// <summary>Only if the logo is missing from the program: a plain disc with a state-coloured dot.</summary>
        private static Icon Fallback(TrayState state)
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var disc = new SolidBrush(Color.FromArgb(32, 34, 37))) g.FillEllipse(disc, 1, 1, 30, 30);
                using (var ring = new Pen(Color.FromArgb(88, 101, 242), 3)) g.DrawEllipse(ring, 3, 3, 26, 26);
                using (var centre = new SolidBrush(DotColor(state))) g.FillEllipse(centre, 10, 10, 12, 12);
            }

            IntPtr handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
    }
}
