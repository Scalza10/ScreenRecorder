using System.Runtime.InteropServices;
using ScreenRecorder.Core.Recording;
using static ScreenRecorder.App.Services.NativeMethods;

namespace ScreenRecorder.App.Services;

/// <param name="Bounds">Virtual-desktop position and size in physical pixels.</param>
/// <param name="DdagrabIndex">DXGI output index on the default GPU (ffmpeg's ddagrab output_idx), or null.</param>
public sealed record MonitorInfo(int Number, string DeviceName, PixelRect Bounds, bool IsPrimary, int? DdagrabIndex)
{
    public string DisplayName =>
        $"Display {Number}{(IsPrimary ? " (main)" : "")} — {Bounds.Width}×{Bounds.Height}";
}

public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var ddaIndexByMonitor = GetDesktopDuplicationOutputs();
        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var r = info.rcMonitor;
                monitors.Add(new MonitorInfo(
                    0,
                    info.szDevice,
                    new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    ddaIndexByMonitor.TryGetValue(handle, out var index) ? index : null));
            }

            return true;
        }, IntPtr.Zero);

        // Main display first, then left-to-right.
        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .Select((m, i) => m with { Number = i + 1 })
            .ToList();
    }

    /// <summary>
    /// ffmpeg's ddagrab enumerates the outputs of the default (first) GPU. Mirror that here so each monitor maps to
    /// the right output_idx. Monitors on another GPU get no index and are recorded with GDI capture instead.
    /// </summary>
    private static Dictionary<IntPtr, int> GetDesktopDuplicationOutputs()
    {
        var result = new Dictionary<IntPtr, int>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return result;
            try
            {
                if (factory.EnumAdapters(0, out var adapter) != 0) return result;
                try
                {
                    for (uint i = 0; adapter.EnumOutputs(i, out var output) == 0; i++)
                    {
                        try
                        {
                            if (output.GetDesc(out var desc) == 0) result[desc.Monitor] = (int)i;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // No DXGI (very unusual): every monitor falls back to GDI capture.
        }

        return result;
    }
}
