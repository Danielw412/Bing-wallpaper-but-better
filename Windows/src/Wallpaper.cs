using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DailyWallpaper
{
    internal enum Position { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }
    internal interface IWallpaperChange : IDisposable { void Commit(); }
    internal interface IWallpaper { IWallpaperChange Apply(string path, Position position); }

    internal sealed class Wallpaper : IWallpaper
    {
        internal static int Probe()
        {
            using (var snapshot = new Change())
            {
                snapshot.Commit(); // Read the native API without changing the desktop.
                return snapshot.MonitorCount;
            }
        }

        internal static void Detach(string cacheDirectory, string picturesDirectory)
        {
            string prefix = Path.GetFullPath(cacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var change = new Change())
            {
                change.RetainImages(prefix, picturesDirectory);
                change.Commit();
            }
        }

        public IWallpaperChange Apply(string path, Position position)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new AppError(6, "wallpaper image does not exist: " + path);
            Change change = null;
            try
            {
                change = new Change();
                change.Set(path, position);
                return change;
            }
            catch (Exception e)
            {
                if (change != null) change.Dispose();
                throw new AppError(6, "Windows could not apply the wallpaper: " + e.Message, e);
            }
        }

        // Keep the original per-monitor images and position until metadata is
        // safely saved. A failed apply/save restores this snapshot.
        private sealed class Change : IWallpaperChange
        {
            private IDesktopWallpaper desktop;
            private readonly Dictionary<string, string> originals = new Dictionary<string, string>();
            private Position originalPosition;
            private bool committed;
            internal int MonitorCount { get { return originals.Count; } }
            internal Change()
            {
                desktop = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), true));
                try
                {
                    Check(desktop.GetPosition(out originalPosition));
                    uint count;
                    Check(desktop.GetMonitorDevicePathCount(out count));
                    for (uint i = 0; i < count; i++)
                    {
                        string monitor, path;
                        Check(desktop.GetMonitorDevicePathAt(i, out monitor));
                        Check(desktop.GetWallpaper(monitor, out path));
                        originals.Add(monitor, path ?? "");
                    }
                    if (originals.Count == 0) throw new InvalidOperationException("no desktop monitors are available");
                }
                catch { Marshal.ReleaseComObject(desktop); desktop = null; throw; }
            }
            internal void Set(string path, Position position)
            {
                Check(desktop.SetPosition(position));
                Check(desktop.SetWallpaper(null, path)); // NULL applies to every monitor.
            }
            internal void RetainImages(string prefix, string picturesDirectory)
            {
                var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var original in originals)
                {
                    if (String.IsNullOrEmpty(original.Value) || !Path.GetFullPath(original.Value).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string retained;
                    if (!copies.TryGetValue(original.Value, out retained))
                    {
                        if (String.IsNullOrEmpty(picturesDirectory)) throw new AppError(7, "cannot locate Pictures; cache retained");
                        Directory.CreateDirectory(picturesDirectory);
                        retained = Path.Combine(picturesDirectory, "daily-wallpaper-" + Guid.NewGuid().ToString("N") + Path.GetExtension(original.Value));
                        File.Copy(original.Value, retained);
                        copies.Add(original.Value, retained);
                        Console.WriteLine("Retained wallpaper: " + retained);
                    }
                    Check(desktop.SetWallpaper(original.Key, retained));
                }
            }
            public void Commit() { committed = true; }
            public void Dispose()
            {
                if (desktop == null) return;
                if (!committed)
                {
                    try
                    {
                        Check(desktop.SetPosition(originalPosition));
                        foreach (var original in originals) Check(desktop.SetWallpaper(original.Key, original.Value));
                    }
                    catch (Exception e) { Console.Error.WriteLine("Could not restore the previous wallpaper: " + e.Message); }
                }
                Marshal.ReleaseComObject(desktop);
                desktop = null;
            }
        }

        private static void Check(int result) { Marshal.ThrowExceptionForHR(result); }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        // Methods must remain in native vtable order, including unused slots.
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out Rect rect);
            [PreserveSig] int SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            [PreserveSig] int SetPosition(Position position);
            [PreserveSig] int GetPosition(out Position position);
        }
    }
}
