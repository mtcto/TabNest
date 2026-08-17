using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 带逐像素 alpha 的绘制表面，用于让窗口边缘抗锯齿。
///
/// **为什么需要它。** 分组栏的顶部圆角原先靠 <c>SetWindowRgn</c> 成形，而窗口区域
/// 是二值掩码 —— 一个像素要么完全属于窗口、要么完全不属于，圆弧只能是硬像素阶梯，
/// 用户看到的就是左右两个上角有锯齿。这不是画法问题：区域**无法**抗锯齿。
/// 唯一的出路是分层窗口，按逐像素 alpha 与桌面混合，半覆盖的边缘像素半透明呈现。
///
/// **绘制顺序上的关键取舍。** GDI 的 ClearType 文本画进 32 位位图时不写 alpha 通道，
/// 若一开始就把整幅置为透明，文字所在的像素 alpha 会留在 0，在分层窗口上完全看不见 ——
/// 这是分层窗口最经典的坑。因此这里的做法是：
///
///   1. 照常渲染（GDI 文本、GDI+ 图形都不变，完全不关心 alpha）；
///   2. 渲染完成后**单独后处理 alpha 通道**：整幅置为不透明，只在圆角方块内
///      按超采样算出的覆盖率写入 alpha；
///   3. 对部分透明的像素做预乘（UpdateLayeredWindow 要求预乘 alpha）。
///
/// 这样文本绘制路径一行都不用改，也不必和 GDI 的 alpha 行为较劲。
/// </summary>
public sealed class LayeredSurface : IDisposable
{
    /// <summary>圆角覆盖率的每轴采样数。4×4 = 16 个样本，肉眼已看不出台阶。</summary>
    private const int SamplesPerAxis = 4;

    /// <summary>
    /// 最近一次提交中不透明像素的占比，供诊断断言使用。
    ///
    /// 直接测"整幅透明"这个失败本身。试过两种间接判据都是无用的：
    /// 屏幕上"是不是非空白"（透明时读到的是后面的窗口，同样非空白），
    /// 以及"像不像分组栏的背景色"（浅色主题接近白色，屏幕上本就有大片浅色）。
    /// 唯一可靠的是看我们自己写进去的 alpha。
    /// </summary>
    public static double LastCommitOpaqueRatio { get; private set; }

    private readonly nint _hwnd;
    private readonly nint _screenDc;
    private readonly nint _memDc;
    private readonly nint _bitmap;
    private readonly nint _oldBitmap;
    private readonly nint _bits;
    private readonly int _width;
    private readonly int _height;
    private readonly int _cornerRadius;
    private bool _disposed;

    private LayeredSurface(
        nint hwnd, nint screenDc, nint memDc, nint bitmap, nint oldBitmap, nint bits,
        int width, int height, int cornerRadius)
    {
        _hwnd = hwnd;
        _screenDc = screenDc;
        _memDc = memDc;
        _bitmap = bitmap;
        _oldBitmap = oldBitmap;
        _bits = bits;
        _width = width;
        _height = height;
        _cornerRadius = cornerRadius;

        Canvas = new GdiCanvas(memDc);
    }

    public GdiCanvas Canvas { get; }

    /// <summary>
    /// 开始一次分层绘制。返回 null 表示无法创建表面，调用方应退回普通绘制。
    /// </summary>
    /// <param name="cornerRadius">顶部两个圆角的半径，0 表示直角。</param>
    public static LayeredSurface? Begin(nint hwnd, int width, int height, int cornerRadius)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = WindowClass.GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }

        var memDc = Gdi.CreateCompatibleDC(screenDc);
        if (memDc == 0)
        {
            _ = WindowClass.ReleaseDC(0, screenDc);
            return null;
        }

        // biHeight 取负值 = 自上而下排列，与屏幕坐标一致，省去逐行翻转。
        var header = new Gdi.BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Gdi.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Gdi.BI_RGB,
        };

        var bitmap = Gdi.CreateDIBSection(
            memDc, in header, Gdi.DIB_RGB_COLORS, out var bits, 0, 0);

        if (bitmap == 0 || bits == 0)
        {
            Gdi.DeleteObject(memDc);
            _ = WindowClass.ReleaseDC(0, screenDc);
            return null;
        }

        var oldBitmap = Gdi.SelectObject(memDc, bitmap);

        return new LayeredSurface(
            hwnd, screenDc, memDc, bitmap, oldBitmap, bits, width, height, cornerRadius);
    }

    /// <summary>把窗口切换为分层窗口。创建时或首次分层绘制前调用一次即可。</summary>
    public static void EnableLayered(nint hwnd)
    {
        var ex = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE);

        if ((ex & User32.WS_EX_LAYERED) == 0)
        {
            _ = User32.SetWindowLongPtr(
                hwnd, User32.GWL_EXSTYLE, (nint)(ex | User32.WS_EX_LAYERED));
        }
    }

    /// <summary>提交到窗口。失败返回 false，调用方可退回普通绘制。</summary>
    public unsafe bool Commit()
    {
        ApplyAlpha();

        var source = default(POINT);
        var size = new SIZE { Width = _width, Height = _height };

        // 位置传 0 表示"不动窗口"，只换内容。窗口位置仍由 SetWindowPos 管。
        var blend = new User32.BLENDFUNCTION
        {
            BlendOp = User32.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = User32.AC_SRC_ALPHA,
        };

        User32.GetWindowRect(_hwnd, out var rect);
        var destination = new POINT { X = rect.Left, Y = rect.Top };

        return User32.UpdateLayeredWindow(
            _hwnd, _screenDc, in destination, in size,
            _memDc, in source, 0, in blend, User32.ULW_ALPHA);
    }

    /// <summary>
    /// 后处理 alpha 通道：整幅不透明，只有顶部两个圆角按覆盖率渐变。
    ///
    /// 覆盖率用 4×4 超采样算，比直接判"圆内／圆外"多出一圈中间值，
    /// 那一圈正是消除锯齿的东西。
    /// </summary>
    private unsafe void ApplyAlpha()
    {
        var pixels = (byte*)_bits;
        var stride = _width * 4;

        // 先整幅置为不透明。GDI 文本不写 alpha，这一步同时把文字区域补成可见。
        for (var y = 0; y < _height; y++)
        {
            var row = pixels + (y * stride);

            for (var x = 0; x < _width; x++)
            {
                row[(x * 4) + 3] = 255;
            }
        }

        if (_cornerRadius <= 0)
        {
            RecordOpaqueRatio(pixels, stride);
            return;
        }

        var r = Math.Min(_cornerRadius, Math.Min(_width / 2, _height));

        for (var y = 0; y < r; y++)
        {
            var row = pixels + (y * stride);

            for (var x = 0; x < r; x++)
            {
                var coverage = CornerCoverage(x, y, r);

                if (coverage < 255)
                {
                    // 左上角
                    Blend(row + (x * 4), coverage);

                    // 右上角，水平镜像
                    Blend(row + ((_width - 1 - x) * 4), coverage);
                }
            }
        }

        RecordOpaqueRatio(pixels, stride);
    }

    /// <summary>统计不透明像素占比，供诊断断言判断内容有没有真的画上去。</summary>
    private unsafe void RecordOpaqueRatio(byte* pixels, int stride)
    {
        var total = 0;
        var opaque = 0;

        // 取样而非全扫：这里每帧都会走到，而判断"有没有内容"不需要逐像素。
        for (var y = 0; y < _height; y += 2)
        {
            var row = pixels + (y * stride);

            for (var x = 0; x < _width; x += 4)
            {
                total++;

                if (row[(x * 4) + 3] > 128)
                {
                    opaque++;
                }
            }
        }

        LastCommitOpaqueRatio = total == 0 ? 0 : (double)opaque / total;
    }

    /// <summary>把一个像素乘上覆盖率：写入 alpha 并预乘 RGB。</summary>
    private static unsafe void Blend(byte* pixel, byte coverage)
    {
        pixel[0] = (byte)(pixel[0] * coverage / 255);
        pixel[1] = (byte)(pixel[1] * coverage / 255);
        pixel[2] = (byte)(pixel[2] * coverage / 255);
        pixel[3] = coverage;
    }

    /// <summary>
    /// 圆角处某像素被窗口覆盖的比例，0~255。
    /// 圆心在 (r, r)：像素中心到圆心的距离小于 r 即在圆内。
    /// </summary>
    private static byte CornerCoverage(int x, int y, int r)
    {
        var inside = 0;

        for (var sy = 0; sy < SamplesPerAxis; sy++)
        {
            for (var sx = 0; sx < SamplesPerAxis; sx++)
            {
                var px = x + ((sx + 0.5) / SamplesPerAxis);
                var py = y + ((sy + 0.5) / SamplesPerAxis);

                var dx = r - px;
                var dy = r - py;

                if ((dx * dx) + (dy * dy) <= (double)r * r)
                {
                    inside++;
                }
            }
        }

        const int total = SamplesPerAxis * SamplesPerAxis;
        return (byte)(inside * 255 / total);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Canvas.Dispose();

        if (_oldBitmap != 0)
        {
            Gdi.SelectObject(_memDc, _oldBitmap);
        }

        Gdi.DeleteObject(_bitmap);
        Gdi.DeleteObject(_memDc);
        _ = WindowClass.ReleaseDC(0, _screenDc);
    }
}
