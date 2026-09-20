using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace ChronoRecorder
{
    /// <summary>One display, as Windows' graphics stack (DXGI) sees it.</summary>
    /// <param name="AdapterIndex">Which GPU it is connected to. Desktop Duplication only reaches GPU 0.</param>
    /// <param name="OutputIndex">Its number on that GPU; this is what ddagrab's output_idx takes.</param>
    /// <param name="Handle">The HMONITOR, the same value user32 returns for a window on this display.</param>
    public sealed record MonitorInfo(int AdapterIndex, int OutputIndex, IntPtr Handle, Rectangle Bounds, string DeviceName, bool Rotated);

    /// <summary>
    /// Finds which monitor a window is on and how to number it for FFmpeg. DXGI is asked directly because
    /// ddagrab's output numbers follow DXGI's order, which is not guaranteed to match WinForms' Screen.AllScreens.
    /// </summary>
    public static class MonitorLocator
    {
        private const uint MonitorDefaultToNearest = 2;

        // DXGI_MODE_ROTATION: 0 = unspecified, 1 = identity, 2/3/4 = rotated 90/180/270
        private static bool IsRotated(int rotation) => rotation > 1;

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DxgiOutputDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        // Only the methods we call are named. The rest are placeholders that keep the vtable order right,
        // which is why every interface starts with IDXGIObject's four methods.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
            [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
            [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetParent(IntPtr a, out IntPtr b);
            [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr a, uint b);
            [PreserveSig] int GetWindowAssociation(out IntPtr a);
            [PreserveSig] int CreateSwapChain(IntPtr a, IntPtr b, out IntPtr c);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr a, out IntPtr b);
            [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
            [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
            [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetParent(IntPtr a, out IntPtr b);
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
            [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
            [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetParent(IntPtr a, out IntPtr b);
            [PreserveSig] int GetDesc(out DxgiOutputDesc desc);
        }

        /// <summary>Every monitor attached to the desktop. Empty if DXGI can't be reached.</summary>
        public static IReadOnlyList<MonitorInfo> Enumerate()
        {
            var result = new List<MonitorInfo>();
            IDXGIFactory1? factory = null;

            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out IntPtr factoryPtr) != 0) return result;
                factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
                Marshal.Release(factoryPtr);

                for (uint a = 0; factory.EnumAdapters1(a, out IntPtr adapterPtr) == 0; a++)
                {
                    var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
                    Marshal.Release(adapterPtr);

                    for (uint o = 0; adapter.EnumOutputs(o, out IntPtr outputPtr) == 0; o++)
                    {
                        var output = (IDXGIOutput)Marshal.GetObjectForIUnknown(outputPtr);
                        Marshal.Release(outputPtr);

                        if (output.GetDesc(out var desc) == 0 && desc.AttachedToDesktop != 0)
                        {
                            result.Add(new MonitorInfo(
                                (int)a, (int)o, desc.Monitor,
                                Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom),
                                desc.DeviceName, IsRotated(desc.Rotation)));
                        }

                        Marshal.ReleaseComObject(output);
                    }

                    Marshal.ReleaseComObject(adapter);
                }
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or InvalidCastException or EntryPointNotFoundException)
            {
                Console.WriteLine($"⚠ Could not list monitors: {ex.Message}");
            }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }

            return result;
        }

        /// <summary>The monitor a window is mostly on, or null for no window or an unknown display.</summary>
        public static MonitorInfo? ForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            return Enumerate().FirstOrDefault(m => m.Handle == monitor);
        }

        /// <summary>The primary monitor: the one whose area contains the desktop origin.</summary>
        public static MonitorInfo? Primary() => Enumerate().FirstOrDefault(m => m.Bounds.Contains(Point.Empty));
    }

    /// <summary>Decides how to capture a monitor.</summary>
    public static class CaptureSourceChooser
    {
        public static CaptureSource Choose(MonitorInfo? monitor, Rectangle primaryBounds)
        {
            if (monitor == null)
                return new CaptureSource(CaptureMethod.Gdi, 0, primaryBounds);

            // Desktop Duplication only sees the default GPU's monitors, and hands back rotated ones un-rotated.
            bool duplicable = monitor.AdapterIndex == 0 && !monitor.Rotated;

            return new CaptureSource(
                duplicable ? CaptureMethod.DesktopDuplication : CaptureMethod.Gdi,
                monitor.OutputIndex,
                monitor.Bounds);
        }
    }
}
