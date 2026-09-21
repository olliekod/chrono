using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ChronoRecorder
{
    /// <summary>The tray icons, drawn in code (no image files to ship): a dark disc with a state-coloured dot.</summary>
    public static class TrayIcons
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private static readonly Dictionary<TrayState, Icon> Cache = new();

        public static Icon For(TrayState state)
        {
            if (!Cache.TryGetValue(state, out var icon))
                Cache[state] = icon = Draw(state);
            return icon;
        }

        private static Icon Draw(TrayState state)
        {
            Color dot = state switch
            {
                TrayState.Recording => Color.FromArgb(237, 66, 69),   // red: on air
                TrayState.Paused => Color.FromArgb(250, 168, 26),     // amber
                _ => Color.FromArgb(148, 155, 164)                    // grey: waiting
            };

            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var disc = new SolidBrush(Color.FromArgb(32, 34, 37))) g.FillEllipse(disc, 1, 1, 30, 30);
                using (var ring = new Pen(Color.FromArgb(88, 101, 242), 3)) g.DrawEllipse(ring, 3, 3, 26, 26);
                using (var centre = new SolidBrush(dot)) g.FillEllipse(centre, 10, 10, 12, 12);
            }

            IntPtr handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }   // the clone owns its own copy, so the handle can be freed
            finally { DestroyIcon(handle); }
        }
    }
}
