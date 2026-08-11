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
    private nint _font;
    private int _fontDpi;
    private bool _disposed;

    internal GdiCanvas(nint dc)
    {
        _dc = dc;
        Gdi.SetBkMode(_dc, Gdi.TRANSPARENT);
    }

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

    public void FillRoundRect(PixelRect bounds, uint argb, int radius)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

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
