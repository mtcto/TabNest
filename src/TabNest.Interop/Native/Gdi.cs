using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public long rgbReserved1;
    public long rgbReserved2;
    public long rgbReserved3;
    public long rgbReserved4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LOGFONT
{
    public int lfHeight;
    public int lfWidth;
    public int lfEscapement;
    public int lfOrientation;
    public int lfWeight;
    public byte lfItalic;
    public byte lfUnderline;
    public byte lfStrikeOut;
    public byte lfCharSet;
    public byte lfOutPrecision;
    public byte lfClipPrecision;
    public byte lfQuality;
    public byte lfPitchAndFamily;
    public fixed char lfFaceName[32];

    public void SetFaceName(string name)
    {
        fixed (char* p = lfFaceName)
        {
            var span = new Span<char>(p, 32);
            span.Clear();
            name.AsSpan(0, Math.Min(name.Length, 31)).CopyTo(span);
        }
    }
}

internal static partial class Gdi
{
    private const string Gdi32 = "gdi32.dll";
    private const string User32Lib = "user32.dll";

    // ---- 绘制上下文 ----

    [LibraryImport(User32Lib, SetLastError = true)]
    public static partial nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport(User32Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    public const uint SRCCOPY = 0x00CC0020;

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    // ---- 画刷与图形 ----

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreatePen(int iStyle, int cWidth, uint color);

    public const int PS_SOLID = 0;

    [LibraryImport(User32Lib, SetLastError = true)]
    public static partial int FillRect(nint hDC, ref RECT lprc, nint hbr);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(
        nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Rectangle(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateRoundRectRgn(
        int x1, int y1, int x2, int y2, int w, int h);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateRectRgn(int x1, int y1, int x2, int y2);

    public const int RGN_OR = 2;

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int CombineRgn(nint hrgnDst, nint hrgnSrc1, nint hrgnSrc2, int iMode);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowRgn(
        nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport(Gdi32, EntryPoint = "MoveToEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveToExCore(nint hdc, int x, int y, nint lppt);

    public static bool MoveTo(nint hdc, int x, int y) => MoveToExCore(hdc, x, y, 0);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LineTo(nint hdc, int x, int y);

    public const int NULL_BRUSH = 5;
    public const int NULL_PEN = 8;

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint GetStockObject(int i);

    // ---- 文本 ----

    public const uint DT_LEFT = 0x00000000;
    public const uint DT_CENTER = 0x00000001;
    public const uint DT_RIGHT = 0x00000002;
    public const uint DT_VCENTER = 0x00000004;
    public const uint DT_WORDBREAK = 0x00000010;
    public const uint DT_SINGLELINE = 0x00000020;
    public const uint DT_CALCRECT = 0x00000400;
    public const uint DT_END_ELLIPSIS = 0x00008000;
    public const uint DT_PATH_ELLIPSIS = 0x00004000;
    public const uint DT_NOPREFIX = 0x00000800;
    public const uint DT_EDITCONTROL = 0x00002000;

    public const int TRANSPARENT = 1;

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport(User32Lib, EntryPoint = "DrawTextW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(
        nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [LibraryImport(Gdi32, EntryPoint = "CreateFontIndirectW", SetLastError = true)]
    public static partial nint CreateFontIndirect(ref LOGFONT lplf);

    /// <summary>把字体应用到 Win32 原生控件。原生控件默认用系统位图字体，不设就是 90 年代长相。</summary>
    public const uint WM_SETFONT = 0x0030;

    // ---- 裁剪区 ----
    //
    // 设置中心的内容区可滚动，绘制必须被限制在视口内，
    // 否则滚动到一半的卡片会画到导航栏和标题上去。

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int IntersectClipRect(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int SelectClipRgn(nint hdc, nint hrgn);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int SaveDC(nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RestoreDC(nint hdc, int nSavedDC);

    // ---- 图标 ----

    public const uint DI_NORMAL = 0x0003;

    [LibraryImport(User32Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        nint hdc, int xLeft, int yTop, nint hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    /// <summary>把 ARGB 转成 GDI 的 COLORREF（0x00BBGGRR）。</summary>
    public static uint ToColorRef(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (b << 16) | (g << 8) | r;
    }
}
