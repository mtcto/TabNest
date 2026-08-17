using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 从屏幕上读回一块矩形的像素，用于验证"画出来的东西真的在屏幕上"。
///
/// 分组栏改用分层窗口（逐像素 alpha）之后，最经典的失败是内容整幅透明：
/// GDI 的 ClearType 文本不写 alpha 通道，若位图 alpha 留在 0，窗口在屏幕上
/// 完全看不见 —— 而 <c>IsWindowVisible</c>、窗口矩形、命中测试**全都照常为真**，
/// 常规断言一条都抓不到。只有直接看屏幕像素才能发现。
///
/// **判据必须是"像不像分组栏自己的颜色"，不能是"是不是非空白"。**
/// 分组栏整幅透明时，那个位置读到的是它后面那个窗口的颜色 —— 同样不是空白。
/// 第一版就是这么写的：alpha 全置 0 之后断言照样通过，是个彻底无用的断言。
/// </summary>
public static class ScreenPixels
{
    /// <summary>取样结果。</summary>
    /// <param name="Total">有效取样像素数。</param>
    /// <param name="Matching">与期望颜色相符的像素数。</param>
    public readonly record struct Sample(int Total, int Matching)
    {
        /// <summary>相符像素占比。内容没画上去时会接近 0。</summary>
        public double MatchRatio => Total == 0 ? 0 : (double)Matching / Total;
    }

    /// <summary>
    /// 读回一块屏幕矩形，统计其中有多少像素接近 <paramref name="expectedArgb"/>。
    /// </summary>
    /// <param name="expectedArgb">期望的主色，通常是被测窗口的背景色。</param>
    /// <param name="tolerance">每个通道允许的偏差。文本抗锯齿会让边缘像素偏离主色。</param>
    /// <param name="step">取样步长。验证"有没有画上"不需要逐像素。</param>
    public static Sample Read(PixelRect bounds, uint expectedArgb, int tolerance = 24, int step = 3)
    {
        if (bounds.IsEmpty)
        {
            return new Sample(0, 0);
        }

        var screenDc = WindowClass.GetDC(0);
        if (screenDc == 0)
        {
            return new Sample(0, 0);
        }

        var wantR = (int)((expectedArgb >> 16) & 0xFF);
        var wantG = (int)((expectedArgb >> 8) & 0xFF);
        var wantB = (int)(expectedArgb & 0xFF);

        try
        {
            var total = 0;
            var matching = 0;

            for (var y = bounds.Top; y < bounds.Bottom; y += step)
            {
                for (var x = bounds.Left; x < bounds.Right; x += step)
                {
                    var color = Gdi.GetPixel(screenDc, x, y);

                    // CLR_INVALID：坐标在屏幕外或不可读，不计入。
                    if (color == 0xFFFFFFFF)
                    {
                        continue;
                    }

                    total++;

                    // GetPixel 返回 COLORREF，字节序是 0x00BBGGRR。
                    var r = (int)(color & 0xFF);
                    var g = (int)((color >> 8) & 0xFF);
                    var b = (int)((color >> 16) & 0xFF);

                    if (Math.Abs(r - wantR) <= tolerance
                        && Math.Abs(g - wantG) <= tolerance
                        && Math.Abs(b - wantB) <= tolerance)
                    {
                        matching++;
                    }
                }
            }

            return new Sample(total, matching);
        }
        finally
        {
            _ = WindowClass.ReleaseDC(0, screenDc);
        }
    }
}
