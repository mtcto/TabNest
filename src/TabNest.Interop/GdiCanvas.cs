using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>文本对齐与截断方式。</summary>
public enum TextTrim
{
    /// <summary>尾部省略。</summary>
    End,

    /// <summary>中间省略。窗口标题的区分信息常在末尾（文件名、项目名），中间省略更易辨认。</summary>
    Middle,
}

/// <summary>水平对齐。</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 文本样式。
///
/// 用一组具名样式而不是让调用方随便传字号，是为了让整个设置中心的排版有据可依 ——
/// 手写 UI 最容易失控的就是每处各自决定字号，最后凑出十几种大小相近的字。
/// 字号以 96 DPI 下的逻辑像素表示，绘制时按实际 DPI 缩放。
/// </summary>
public readonly record struct TextStyle(int Size, int Weight)
{
    /// <summary>页面标题。</summary>
    public static TextStyle PageTitle => new(20, 600);

    /// <summary>分区标题。</summary>
    public static TextStyle Section => new(14, 600);

    /// <summary>设置项标题、导航项。</summary>
    public static TextStyle Body => new(12, 400);

    /// <summary>强调的正文，用于选中的导航项与卡片标题。</summary>
    public static TextStyle BodyStrong => new(12, 600);

    /// <summary>说明文字。比正文小一号且用弱化颜色，承载"为什么"。</summary>
    public static TextStyle Caption => new(11, 400);
}

/// <summary>
/// GDI 绘图门面。
///
/// 存在的意义是守住"所有 P/Invoke 集中在 TabNest.Interop"这条规则：
/// 上层的渲染代码只用领域类型（<see cref="PixelRect"/>、ARGB）描述要画什么，
/// 不接触任何 HDC、HBRUSH、RECT。
///
/// 非线程安全，且必须在收到 WM_PAINT 的线程上使用。
/// </summary>
public sealed class GdiCanvas : IDisposable
{
    private readonly nint _dc;

    /// <summary>字体缓存。设置中心一帧要画上百段不同样式的文本，每次建字体会明显拖慢绘制。</summary>
    private readonly Dictionary<(TextStyle Style, int Dpi), nint> _fonts = [];

    private nint _font;
    private int _fontDpi;
    private bool _disposed;

    internal GdiCanvas(nint dc)
    {
        _dc = dc;
        Gdi.SetBkMode(_dc, Gdi.TRANSPARENT);
    }

    /// <summary>供托管代码把原生控件的字体设成与自绘文本一致时取用。</summary>
    public nint FontFor(TextStyle style, int dpi) => EnsureStyledFont(style, dpi);

    public void FillRect(PixelRect bounds, uint argb)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var brush = Gdi.CreateSolidBrush(Gdi.ToColorRef(argb));
        var rect = ToRect(bounds);

        try
        {
            Gdi.FillRect(_dc, ref rect, brush);
        }
        finally
        {
            Gdi.DeleteObject(brush);
        }
    }

    /// <summary>
    /// 填充圆角矩形，带抗锯齿。
    ///
    /// 走 GDI+ 而不是 GDI 的 <c>RoundRect</c>：后者完全不做抗锯齿，
    /// 圆角边缘是硬像素阶梯，在开关的胶囊轨道上肉眼可见锯齿。
    /// GDI+ 不可用时（极罕见）退回 GDI，宁可有锯齿也不能不画。
    /// </summary>
    public void FillRoundRect(PixelRect bounds, uint argb, int radius)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        if (radius <= 0)
        {
            FillRect(bounds, argb);
            return;
        }

        if (TryFillRoundRectAntiAliased(bounds, argb, radius))
        {
            return;
        }

        FillRoundRectLegacy(bounds, argb, radius);
    }

    /// <summary>
    /// 描边圆角矩形，带抗锯齿。
    ///
    /// 卡片是圆角填充，若配直角描边，四个角上会露出直线与圆弧的错位 ——
    /// 这正是选中卡片看起来"边框有毛刺"的原因。
    /// </summary>
    public void DrawRoundRectOutline(PixelRect bounds, uint argb, int radius, int thickness = 1)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        if (radius <= 0)
        {
            DrawRectOutline(bounds, argb, thickness);
            return;
        }

        if (!GdiPlus.EnsureInitialized() || GdiPlus.GdipCreateFromHDC(_dc, out var g) != 0)
        {
            DrawRectOutline(bounds, argb, thickness);
            return;
        }

        try
        {
            _ = GdiPlus.GdipSetSmoothingMode(g, GdiPlus.SmoothingAntiAlias);
            _ = GdiPlus.GdipSetPixelOffsetMode(g, GdiPlus.PixelOffsetHalf);

            var w = Math.Max(1, thickness);

            // 画笔以路径为中心向两侧各扩半个宽度，因此把路径向内缩半个笔宽，
            // 描边才会正好落在矩形内侧，不与相邻元素错开半像素。
            var half = w / 2.0f;
            var rect = new RectF(
                bounds.Left + half, bounds.Top + half,
                bounds.Width - w, bounds.Height - w);

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            if (GdiPlus.GdipCreatePen1(argb, w, GdiPlus.UnitPixel, out var pen) != 0)
            {
                return;
            }

            try
            {
                if (TryBuildRoundRectPath(rect, radius, out var path))
                {
                    try
                    {
                        _ = GdiPlus.GdipDrawPath(g, pen, path);
                    }
                    finally
                    {
                        _ = GdiPlus.GdipDeletePath(path);
                    }
                }
            }
            finally
            {
                _ = GdiPlus.GdipDeletePen(pen);
            }
        }
        finally
        {
            _ = GdiPlus.GdipDeleteGraphics(g);
        }
    }

    /// <summary>填充圆形，带抗锯齿。开关的圆钮用它。</summary>
    public void FillCircle(PixelRect bounds, uint argb)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        if (!GdiPlus.EnsureInitialized() || GdiPlus.GdipCreateFromHDC(_dc, out var g) != 0)
        {
            FillRoundRectLegacy(bounds, argb, Math.Min(bounds.Width, bounds.Height) / 2);
            return;
        }

        try
        {
            _ = GdiPlus.GdipSetSmoothingMode(g, GdiPlus.SmoothingAntiAlias);
            _ = GdiPlus.GdipSetPixelOffsetMode(g, GdiPlus.PixelOffsetHalf);

            if (GdiPlus.GdipCreateSolidFill(argb, out var brush) != 0)
            {
                return;
            }

            try
            {
                _ = GdiPlus.GdipFillEllipse(g, brush, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            }
            finally
            {
                _ = GdiPlus.GdipDeleteBrush(brush);
            }
        }
        finally
        {
            _ = GdiPlus.GdipDeleteGraphics(g);
        }
    }

    private readonly record struct RectF(float X, float Y, float Width, float Height);

    private bool TryFillRoundRectAntiAliased(PixelRect bounds, uint argb, int radius)
    {
        if (!GdiPlus.EnsureInitialized() || GdiPlus.GdipCreateFromHDC(_dc, out var g) != 0)
        {
            return false;
        }

        try
        {
            _ = GdiPlus.GdipSetSmoothingMode(g, GdiPlus.SmoothingAntiAlias);
            _ = GdiPlus.GdipSetPixelOffsetMode(g, GdiPlus.PixelOffsetHalf);

            if (GdiPlus.GdipCreateSolidFill(argb, out var brush) != 0)
            {
                return false;
            }

            try
            {
                var rect = new RectF(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

                if (!TryBuildRoundRectPath(rect, radius, out var path))
                {
                    return false;
                }

                try
                {
                    return GdiPlus.GdipFillPath(g, brush, path) == 0;
                }
                finally
                {
                    _ = GdiPlus.GdipDeletePath(path);
                }
            }
            finally
            {
                _ = GdiPlus.GdipDeleteBrush(brush);
            }
        }
        finally
        {
            _ = GdiPlus.GdipDeleteGraphics(g);
        }
    }

    private static bool TryBuildRoundRectPath(RectF r, int radius, out nint path)
    {
        path = 0;

        // 半径不能超过短边的一半，否则四段圆弧会互相穿插，画出蝴蝶形。
        var d = Math.Min(radius * 2.0f, Math.Min(r.Width, r.Height));

        if (d <= 0 || GdiPlus.GdipCreatePath(GdiPlus.FillModeAlternate, out path) != 0)
        {
            return false;
        }

        _ = GdiPlus.GdipAddPathArc(path, r.X, r.Y, d, d, 180, 90);
        _ = GdiPlus.GdipAddPathArc(path, r.X + r.Width - d, r.Y, d, d, 270, 90);
        _ = GdiPlus.GdipAddPathArc(path, r.X + r.Width - d, r.Y + r.Height - d, d, d, 0, 90);
        _ = GdiPlus.GdipAddPathArc(path, r.X, r.Y + r.Height - d, d, d, 90, 90);
        _ = GdiPlus.GdipClosePathFigure(path);

        return true;
    }

    private void FillRoundRectLegacy(PixelRect bounds, uint argb, int radius)
    {
        var brush = Gdi.CreateSolidBrush(Gdi.ToColorRef(argb));
        var oldBrush = Gdi.SelectObject(_dc, brush);
        var oldPen = Gdi.SelectObject(_dc, Gdi.GetStockObject(Gdi.NULL_PEN));

        try
        {
            // RoundRect 的右下边界是开区间，+1 才画得满。
            Gdi.RoundRect(
                _dc, bounds.Left, bounds.Top, bounds.Right + 1, bounds.Bottom + 1,
                radius * 2, radius * 2);
        }
        finally
        {
            Gdi.SelectObject(_dc, oldPen);
            Gdi.SelectObject(_dc, oldBrush);
            Gdi.DeleteObject(brush);
        }
    }

    /// <summary>画叉。用两条线而非字形，避免依赖特定字体是否收录该字符。</summary>
    public void DrawCross(PixelRect bounds, uint argb, int inset)
    {
        var pen = Gdi.CreatePen(Gdi.PS_SOLID, 1, Gdi.ToColorRef(argb));
        var oldPen = Gdi.SelectObject(_dc, pen);

        try
        {
            var l = bounds.Left + inset;
            var t = bounds.Top + inset;
            var r = bounds.Right - inset;
            var b = bounds.Bottom - inset;

            Gdi.MoveTo(_dc, l, t);
            Gdi.LineTo(_dc, r, b);
            Gdi.MoveTo(_dc, r, t);
            Gdi.LineTo(_dc, l, b);
        }
        finally
        {
            Gdi.SelectObject(_dc, oldPen);
            Gdi.DeleteObject(pen);
        }
    }

    public void DrawText(string text, PixelRect bounds, uint argb, int dpi, TextTrim trim = TextTrim.Middle)
    {
        if (string.IsNullOrEmpty(text) || bounds.IsEmpty)
        {
            return;
        }

        var font = EnsureFont(dpi);
        var oldFont = Gdi.SelectObject(_dc, font);
        Gdi.SetTextColor(_dc, Gdi.ToColorRef(argb));

        var rect = ToRect(bounds);
        var format = Gdi.DT_LEFT | Gdi.DT_VCENTER | Gdi.DT_SINGLELINE | Gdi.DT_NOPREFIX
            | (trim is TextTrim.Middle ? Gdi.DT_PATH_ELLIPSIS : Gdi.DT_END_ELLIPSIS);

        try
        {
            Gdi.DrawText(_dc, text, text.Length, ref rect, format);
        }
        finally
        {
            Gdi.SelectObject(_dc, oldFont);
        }
    }

    /// <summary>描边矩形。用于卡片边框与选中态外框。</summary>
    public void DrawRectOutline(PixelRect bounds, uint argb, int thickness = 1)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var brush = Gdi.CreateSolidBrush(Gdi.ToColorRef(argb));

        try
        {
            // 用四条填充而非 Rectangle + 画笔：画笔宽度是以线为中心向两侧扩展的，
            // 高 DPI 下 2px 边框会有半像素落在矩形外，与相邻元素产生 1px 的错位。
            var t = Math.Max(1, thickness);
            Fill(bounds.Left, bounds.Top, bounds.Width, t, brush);
            Fill(bounds.Left, bounds.Bottom - t, bounds.Width, t, brush);
            Fill(bounds.Left, bounds.Top, t, bounds.Height, brush);
            Fill(bounds.Right - t, bounds.Top, t, bounds.Height, brush);
        }
        finally
        {
            Gdi.DeleteObject(brush);
        }

        void Fill(int x, int y, int w, int h, nint b)
        {
            var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            Gdi.FillRect(_dc, ref r, b);
        }
    }

    /// <summary>画一条水平分隔线。</summary>
    public void DrawSeparator(int left, int right, int y, uint argb) =>
        FillRect(new PixelRect(left, y, right, y + 1), argb);

    /// <summary>
    /// 按样式绘制文本。<paramref name="wrap"/> 为真时按宽度换行，用于说明文字。
    /// </summary>
    public void DrawStyledText(
        string text,
        PixelRect bounds,
        uint argb,
        TextStyle style,
        int dpi,
        TextAlign align = TextAlign.Left,
        bool wrap = false,
        bool ellipsis = true)
    {
        if (string.IsNullOrEmpty(text) || bounds.IsEmpty)
        {
            return;
        }

        var font = EnsureStyledFont(style, dpi);
        var oldFont = Gdi.SelectObject(_dc, font);
        Gdi.SetTextColor(_dc, Gdi.ToColorRef(argb));

        var rect = ToRect(bounds);
        var format = Gdi.DT_NOPREFIX | AlignFlag(align);

        if (wrap)
        {
            format |= Gdi.DT_WORDBREAK | Gdi.DT_EDITCONTROL;
        }
        else
        {
            format |= Gdi.DT_SINGLELINE | Gdi.DT_VCENTER;

            if (ellipsis)
            {
                format |= Gdi.DT_END_ELLIPSIS;
            }
        }

        try
        {
            Gdi.DrawText(_dc, text, text.Length, ref rect, format);
        }
        finally
        {
            Gdi.SelectObject(_dc, oldFont);
        }
    }

    /// <summary>
    /// 测量文本在给定宽度下所需的高度。
    ///
    /// 布局必须先测量再定位 —— 说明文字换行后的行数决定了整行控件的高度，
    /// 而行数取决于字体、DPI 与实际文字，写死行高在中英文混排时必然出错。
    /// </summary>
    public int MeasureTextHeight(string text, int width, TextStyle style, int dpi, bool wrap = true)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return 0;
        }

        var font = EnsureStyledFont(style, dpi);
        var oldFont = Gdi.SelectObject(_dc, font);

        var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = 0 };
        var format = Gdi.DT_NOPREFIX | Gdi.DT_CALCRECT
            | (wrap ? Gdi.DT_WORDBREAK | Gdi.DT_EDITCONTROL : Gdi.DT_SINGLELINE);

        try
        {
            Gdi.DrawText(_dc, text, text.Length, ref rect, format);
            return rect.Bottom - rect.Top;
        }
        finally
        {
            Gdi.SelectObject(_dc, oldFont);
        }
    }

    /// <summary>测量单行文本的宽度。用于让选中指示条、徽标贴合文字实际长度。</summary>
    public int MeasureTextWidth(string text, TextStyle style, int dpi)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var font = EnsureStyledFont(style, dpi);
        var oldFont = Gdi.SelectObject(_dc, font);

        var rect = new RECT { Left = 0, Top = 0, Right = 0, Bottom = 0 };

        try
        {
            Gdi.DrawText(
                _dc, text, text.Length, ref rect,
                Gdi.DT_NOPREFIX | Gdi.DT_CALCRECT | Gdi.DT_SINGLELINE);

            return rect.Right - rect.Left;
        }
        finally
        {
            Gdi.SelectObject(_dc, oldFont);
        }
    }

    /// <summary>
    /// 把后续绘制限制在指定矩形内，返回一个还原句柄。
    /// 内容区滚动时必需：否则滚出视口的卡片会画到导航栏和页头上。
    /// </summary>
    public ClipScope PushClip(PixelRect bounds)
    {
        var saved = Gdi.SaveDC(_dc);
        Gdi.IntersectClipRect(_dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return new ClipScope(_dc, saved);
    }

    /// <summary>裁剪区作用域。释放时还原到进入前的状态。</summary>
    public readonly struct ClipScope(nint dc, int saved) : IDisposable
    {
        public void Dispose() => Gdi.RestoreDC(dc, saved);
    }

    private static uint AlignFlag(TextAlign align) => align switch
    {
        TextAlign.Center => Gdi.DT_CENTER,
        TextAlign.Right => Gdi.DT_RIGHT,
        _ => Gdi.DT_LEFT,
    };

    private nint EnsureStyledFont(TextStyle style, int dpi)
    {
        var key = (style, dpi);
        if (_fonts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var logFont = new LOGFONT
        {
            lfHeight = -(int)Math.Round(style.Size * dpi / 96.0, MidpointRounding.AwayFromZero),
            lfWeight = style.Weight,
            lfCharSet = 1,
            lfQuality = 5,
        };

        logFont.SetFaceName("Segoe UI");

        var font = Gdi.CreateFontIndirect(ref logFont);
        _fonts[key] = font;

        return font;
    }

    public void DrawIcon(nint icon, PixelRect bounds)
    {
        if (icon == 0 || bounds.IsEmpty)
        {
            return;
        }

        Gdi.DrawIconEx(
            _dc, bounds.Left, bounds.Top, icon,
            bounds.Width, bounds.Height, 0, 0, Gdi.DI_NORMAL);
    }

    /// <summary>按 DPI 创建并缓存字体。DPI 变化时重建，否则跨屏后字号不对。</summary>
    private nint EnsureFont(int dpi)
    {
        if (_font != 0 && _fontDpi == dpi)
        {
            return _font;
        }

        if (_font != 0)
        {
            Gdi.DeleteObject(_font);
        }

        var logFont = new LOGFONT
        {
            // 负值表示字符高度而非单元格高度，这是文本渲染的惯例。
            lfHeight = -(int)Math.Round(12.0 * dpi / 96.0, MidpointRounding.AwayFromZero),
            lfWeight = 400,
            lfCharSet = 1,  // DEFAULT_CHARSET，保证中文标题正确显示
            lfQuality = 5,  // CLEARTYPE_QUALITY
        };

        logFont.SetFaceName("Segoe UI");

        _font = Gdi.CreateFontIndirect(ref logFont);
        _fontDpi = dpi;

        return _font;
    }

    private static RECT ToRect(PixelRect r) => new()
    {
        Left = r.Left,
        Top = r.Top,
        Right = r.Right,
        Bottom = r.Bottom,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_font != 0)
        {
            Gdi.DeleteObject(_font);
            _font = 0;
        }

        foreach (var font in _fonts.Values)
        {
            Gdi.DeleteObject(font);
        }

        _fonts.Clear();
    }
}

/// <summary>
/// 双缓冲绘制会话。
///
/// 直接往窗口 DC 上画会让标签逐个出现，产生明显闪烁；
/// 因此先画到内存位图，完成后一次性拷贝过去。
/// </summary>
public sealed class BufferedPaint : IDisposable
{
    private readonly nint _hwnd;
    private readonly nint _windowDc;
    private readonly nint _memDc;
    private readonly nint _bitmap;
    private readonly nint _oldBitmap;
    private readonly int _width;
    private readonly int _height;
    private PAINTSTRUCT _ps;
    private bool _disposed;

    private BufferedPaint(nint hwnd, nint windowDc, PAINTSTRUCT ps, int width, int height)
    {
        _hwnd = hwnd;
        _windowDc = windowDc;
        _ps = ps;
        _width = width;
        _height = height;

        _memDc = Gdi.CreateCompatibleDC(windowDc);
        _bitmap = Gdi.CreateCompatibleBitmap(windowDc, width, height);
        _oldBitmap = Gdi.SelectObject(_memDc, _bitmap);

        Canvas = new GdiCanvas(_memDc);
    }

    public GdiCanvas Canvas { get; }

    /// <summary>开始一次绘制。返回 null 表示无需绘制。</summary>
    public static BufferedPaint? Begin(nint hwnd, int width, int height)
    {
        var dc = Gdi.BeginPaint(hwnd, out var ps);

        if (dc == 0 || width <= 0 || height <= 0)
        {
            if (dc != 0)
            {
                Gdi.EndPaint(hwnd, ref ps);
            }

            return null;
        }

        return new BufferedPaint(hwnd, dc, ps, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Gdi.BitBlt(_windowDc, 0, 0, _width, _height, _memDc, 0, 0, Gdi.SRCCOPY);

        Canvas.Dispose();
        Gdi.SelectObject(_memDc, _oldBitmap);
        Gdi.DeleteObject(_bitmap);
        Gdi.DeleteDC(_memDc);
        Gdi.EndPaint(_hwnd, ref _ps);
    }
}
