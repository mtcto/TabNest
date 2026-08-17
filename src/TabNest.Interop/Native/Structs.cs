using System.Runtime.InteropServices;
using TabNest.Core.Models;

namespace TabNest.Interop.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);

    public static RECT FromPixelRect(PixelRect r) => new()
    {
        Left = r.Left,
        Top = r.Top,
        Right = r.Right,
        Bottom = r.Bottom,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public readonly PixelPoint ToPixelPoint() => new(X, Y);
}

/// <summary>Win32 SIZE。UpdateLayeredWindow 用它传位图尺寸。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int Width;
    public int Height;
}

/// <summary>WM_GETMINMAXINFO 的载荷。用于给可缩放窗口设最小尺寸。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;

    public static WINDOWPLACEMENT Create() => new()
    {
        length = Marshal.SizeOf<WINDOWPLACEMENT>(),
    };
}

/// <summary>
/// 显示器信息。
///
/// 设备名用固定字符缓冲区而非 <c>[MarshalAs(ByValTStr)] string</c>：
/// 后者不是 blittable 类型，源生成的 P/Invoke（LibraryImport）无法封送它。
/// 项目规定所有 P/Invoke 必须走源生成版本，因此结构体必须保持 blittable。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEX
{
    public const int DeviceNameLength = 32;

    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public int dwFlags;
    public fixed char szDevice[DeviceNameLength];

    public static MONITORINFOEX Create() => new()
    {
        cbSize = sizeof(MONITORINFOEX),
    };

    public readonly string GetDeviceName()
    {
        fixed (char* p = szDevice)
        {
            var span = new ReadOnlySpan<char>(p, DeviceNameLength);
            var end = span.IndexOf('\0');
            return new string(end < 0 ? span : span[..end]);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_MANDATORY_LABEL
{
    public SID_AND_ATTRIBUTES Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public nint Sid;
    public uint Attributes;
}
