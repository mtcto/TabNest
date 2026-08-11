using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>显示器信息。</summary>
/// <param name="DeviceName">设备名，如 \\.\DISPLAY1。作为显示器标识存进快照 —— 索引会随插拔变化。</param>
/// <param name="Bounds">显示器完整区域。</param>
/// <param name="WorkArea">工作区（扣除任务栏）。</param>
public readonly record struct MonitorFacts(string DeviceName, PixelRect Bounds, PixelRect WorkArea);

public static class MonitorLookup
{
    public static MonitorFacts ForWindow(nint hwnd)
    {
        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    public static MonitorFacts ForPoint(PixelPoint point)
    {
        var pt = new POINT { X = point.X, Y = point.Y };
        var monitor = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    /// <summary>
    /// 窗口的 DPI。
    /// 进程必须是 Per-Monitor V2 感知（见 app.manifest），否则这里拿到的是系统 DPI 而非
    /// 窗口实际所在显示器的 DPI，跨屏时轨道会错位。
    /// </summary>
    public static int DpiForWindow(nint hwnd)
    {
        var dpi = User32.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : (int)dpi;
    }

    private static MonitorFacts Describe(nint monitor)
    {
        if (monitor == 0)
        {
            return new MonitorFacts(string.Empty, PixelRect.Empty, PixelRect.Empty);
        }

        var info = MONITORINFOEX.Create();
        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            return new MonitorFacts(string.Empty, PixelRect.Empty, PixelRect.Empty);
        }

        return new MonitorFacts(
            info.GetDeviceName(),
            info.rcMonitor.ToPixelRect(),
            info.rcWork.ToPixelRect());
    }
}
