using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput
{
    public uint GdiplusVersion;
    public nint DebugEventCallback;
    public int SuppressBackgroundThread;
    public int SuppressExternalCodecs;
}

/// <summary>
/// GDI+ 互操作，只取抗锯齿图形所需的最小子集。
///
/// 为什么需要它：GDI 的 <c>RoundRect</c> 不做抗锯齿，圆角边缘是硬像素阶梯，
/// 在开关的胶囊轨道和卡片圆角上肉眼可见锯齿。GDI+ 支持真正的抗锯齿。
///
/// 代价为零：<c>gdiplus.dll</c> 自 Windows XP 起就是系统组件，
/// 不需要随程序分发，也不增加发布体积。Groupy 同样用 GDI+ 画它的界面。
///
/// 文本仍然走 GDI 的 DrawText —— GDI+ 的文本渲染不如 GDI 的 ClearType 清晰，
/// 混用是有意为之：形状用 GDI+，文字用 GDI。
///
/// 颜色格式与我们的主题一致（0xAARRGGBB），无需像 GDI 的 COLORREF 那样换字节序。
/// </summary>
internal static partial class GdiPlus
{
    private const string Lib = "gdiplus.dll";

    /// <summary>SmoothingModeAntiAlias。</summary>
    public const int SmoothingAntiAlias = 4;

    /// <summary>PixelOffsetModeHalf。让填充与像素网格对齐，避免边缘发虚。</summary>
    public const int PixelOffsetHalf = 2;

    /// <summary>FillModeAlternate。</summary>
    public const int FillModeAlternate = 0;

    /// <summary>UnitPixel。</summary>
    public const int UnitPixel = 2;

    [LibraryImport(Lib)]
    public static partial int GdiplusStartup(
        out nint token, ref GdiplusStartupInput input, nint output);

    [LibraryImport(Lib)]
    public static partial int GdiplusShutdown(nint token);

    [LibraryImport(Lib)]
    public static partial int GdipCreateFromHDC(nint hdc, out nint graphics);

    [LibraryImport(Lib)]
    public static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport(Lib)]
    public static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport(Lib)]
    public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport(Lib)]
    public static partial int GdipCreateSolidFill(uint argb, out nint brush);

    [LibraryImport(Lib)]
    public static partial int GdipDeleteBrush(nint brush);

    [LibraryImport(Lib)]
    public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

    [LibraryImport(Lib)]
    public static partial int GdipDeletePen(nint pen);

    [LibraryImport(Lib)]
    public static partial int GdipCreatePath(int fillMode, out nint path);

    [LibraryImport(Lib)]
    public static partial int GdipDeletePath(nint path);

    [LibraryImport(Lib)]
    public static partial int GdipAddPathArc(
        nint path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [LibraryImport(Lib)]
    public static partial int GdipClosePathFigure(nint path);

    [LibraryImport(Lib)]
    public static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport(Lib)]
    public static partial int GdipDrawPath(nint graphics, nint pen, nint path);

    [LibraryImport(Lib)]
    public static partial int GdipFillEllipse(
        nint graphics, nint brush, float x, float y, float width, float height);

    /// <summary>
    /// 进程内一次性初始化。
    ///
    /// GDI+ 要求任何调用之前先 Startup。刻意不做对应的 Shutdown：
    /// 进程退出时系统会回收，而在退出路径上关闭 GDI+ 反而可能与仍在绘制的窗口撞车。
    /// </summary>
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        return GdiplusStartup(out _, ref input, 0) == 0;
    });

    public static bool EnsureInitialized() => Initialized.Value;
}
